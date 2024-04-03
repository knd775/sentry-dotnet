using Sentry.Extensibility;
using Sentry.Internal.Extensions;
using Sentry.Internal.OpenTelemetry;

namespace Sentry.Internal.Tracing;

/// <summary>
/// Helper class to convert <see cref="System.Diagnostics"/> events to Sentry Spans.
/// </summary>
internal class ActivitySpanProcessor
{
    private readonly IHub _hub;

    private Action<ISpan, System.Diagnostics.Activity>? _beforeFinish;
    private readonly Instrumenter _instrumenter;

    // ReSharper disable once MemberCanBePrivate.Global - Used by tests
    internal readonly ConcurrentDictionary<ActivitySpanId, ISpan> _map = new();
    private readonly SentryOptions? _options;
    private readonly Lazy<IDictionary<string, object>> _resourceAttributes;

    private static readonly long PruningInterval = TimeSpan.FromSeconds(5).Ticks;
    internal long _lastPruned = 0;
    private readonly Lazy<Hub?> _realHub;

    static ActivitySpanProcessor()
    {
#if !NET5_0_OR_GREATER
        if (Activity.DefaultIdFormat == ActivityIdFormat.W3C)
        {
            return;
        }

        // TODO: Could customers potentially be relying on the Hierarchical format? If so, this will get us in trouble.
        //
        // Another option would be to warn customers and have them set this themselves (that would be more deliberate).
        //
        // Finally, we can override the ActivityIdFormat for each trace by using ActivitySource.CreateActivity instead
        // of ActivitySource.StartActivity. That's a bit more fragile and will only work for spans created by the SDK
        // (not for anything users instrument themselves).
        //
        // Activity.SpanId only gets a non-zero value if the activity ID format is W3C (the default since net5.0). The
        // default is Hierarchical on .NET Framework, .NET Core 3.1 and below, which won't work with Sentry tracing.
        Debug.WriteLine("Setting Activity.DefaultIdFormat to W3C.");
        Activity.DefaultIdFormat = ActivityIdFormat.W3C;
#endif
    }

    internal ActivitySpanProcessor(IHub hub, Instrumenter instrumenter)
        : this(hub, null, null, instrumenter)
    {
    }

    internal ActivitySpanProcessor(IHub hub, Action<ISpan, System.Diagnostics.Activity>? beforeFinish,
        Func<IDictionary<string, object>>? resourceAttributeResolver)
        : this(hub, beforeFinish, resourceAttributeResolver, Instrumenter.OpenTelemetry)
    {
    }

    private ActivitySpanProcessor(IHub hub, Action<ISpan, System.Diagnostics.Activity>? beforeFinish,
        Func<IDictionary<string, object>>? resourceAttributeResolver, Instrumenter instrumenter)
    {
        _hub = hub;
        _beforeFinish = beforeFinish;
        _instrumenter = instrumenter;
        _realHub = new Lazy<Hub?>(() =>
            _hub switch
            {
                Hub thisHub => thisHub,
                HubAdapter when SentrySdk.CurrentHub is Hub sdkHub => sdkHub,
                _ => null
            });

        _options = hub.GetSentryOptions();

        if (_options is null)
        {
            throw new InvalidOperationException(
                "The Sentry SDK has not been initialised. To use tracing you need to initialize the Sentry SDK.");
        }

        // Resource attributes are consistent between spans, but not available during construction.
        // Thus, get a single instance lazily.
        resourceAttributeResolver ??= () => new Dictionary<string, object>();
        _resourceAttributes = new Lazy<IDictionary<string, object>>(resourceAttributeResolver);
    }

    internal ISpan? GetMappedSpan(ActivitySpanId spanId) => _map.GetValueOrDefault(spanId);

    /// <inheritdoc />
    public void OnStart(System.Diagnostics.Activity data)
    {
        if (data.ParentSpanId != default && _map.TryGetValue(data.ParentSpanId, out var parentSpan))
        {
            // We can find the parent span - start a child span.
            var context = new SpanContext(
                data.OperationName,
                data.SpanId.AsSentrySpanId(),
                data.ParentSpanId.AsSentrySpanId(),
                data.TraceId.AsSentryId(),
                data.DisplayName,
                null,
                null)
            {
                Instrumenter = _instrumenter
            };

            var span = (SpanTracer)parentSpan.StartChild(context);
            span.StartTimestamp = data.StartTimeUtc;
            // Used to filter out spans that are not recorded when finishing a transaction.
            data.BindSentrySpan(span);
            span.IsFiltered = () => span.GetActivity()
                is { IsAllDataRequested: false, Recorded: false };
            _map[data.SpanId] = span;
        }
        else
        {
#if HAS_DIAGNOSTICS_7_OR_GREATER
            // If a parent exists, then copy its sampling decision.
            bool? isSampled = data.HasRemoteParent ? data.Recorded : null;
#else
            bool? isSampled = null;
#endif

            // No parent span found - start a new transaction
            var transactionContext = new TransactionContext(data.DisplayName,
                data.OperationName,
                data.SpanId.AsSentrySpanId(),
                data.ParentSpanId.AsSentrySpanId(),
                data.TraceId.AsSentryId(),
                data.DisplayName, null, isSampled, isSampled)
            {
                Instrumenter = _instrumenter
            };

            var baggageHeader = data.Baggage.AsBaggageHeader();
            var dynamicSamplingContext = baggageHeader.CreateDynamicSamplingContext();
            var transaction = (TransactionTracer)_hub.StartTransaction(
                transactionContext, new Dictionary<string, object?>(), dynamicSamplingContext
                );
            transaction.StartTimestamp = data.StartTimeUtc;
            _hub.ConfigureScope(scope => scope.Transaction = transaction);
            data.BindSentrySpan(transaction);
            _map[data.SpanId] = transaction;
        }

        // Housekeeping
        PruneFilteredSpans();
    }

    public void OnEnd(System.Diagnostics.Activity data)
    {
        // Make a dictionary of the attributes (aka "tags") for faster lookup when used throughout the processor.
        var attributes = data.TagObjects.ToDict();

        var url =
            attributes.TryGetTypedValue(OtelSemanticConventions.AttributeUrlFull, out string? tempUrl) ? tempUrl
            : attributes.TryGetTypedValue(OtelSemanticConventions.AttributeHttpUrl, out string? fallbackUrl) ? fallbackUrl // Falling back to pre-1.5.0
            : null;

        if (!string.IsNullOrEmpty(url) && (_options?.IsSentryRequest(url) ?? false))
        {
            _options?.DiagnosticLogger?.LogDebug($"Ignoring Activity {data.SpanId} for Sentry request.");

            if (_map.TryRemove(data.SpanId, out var removed))
            {
                if (removed is SpanTracer spanTracerToRemove)
                {
                    spanTracerToRemove.IsSentryRequest = true;
                }

                if (removed is TransactionTracer transactionTracer)
                {
                    transactionTracer.IsSentryRequest = true;
                }
            }

            return;
        }

        if (!_map.TryGetValue(data.SpanId, out var span))
        {
            _options?.DiagnosticLogger?.LogError($"Span not found for SpanId: {data.SpanId}. Did OnStart run? We might have a bug in the SDK.");
            return;
        }

        var (operation, description, source) = ParseOtelSpanDescription(data, attributes);
        span.Operation = operation;
        span.Description = description;

        if (span is TransactionTracer transaction)
        {
            transaction.Name = description;
            transaction.NameSource = source;

            // Use the end timestamp from the activity data.
            transaction.EndTimestamp = data.StartTimeUtc + data.Duration;

            // Transactions set otel attributes (and resource attributes) as context.
            transaction.Contexts["otel"] = GetOtelContext(attributes);
        }
        else
        {
            // Use the end timestamp from the activity data.
            ((SpanTracer)span).EndTimestamp = data.StartTimeUtc + data.Duration;

            // Spans set otel attributes in extras (passed to Sentry as "data" on the span).
            // Resource attributes do not need to be set, as they would be identical as those set on the transaction.
            span.SetExtras(attributes);
            span.SetExtra("otel.kind", data.Kind);
        }

        // In ASP.NET Core the middleware finishes up (and the scope gets popped) before the activity is ended.  So we
        // need to restore the scope here (it's saved by our middleware when the request starts)
        var activityScope = GetSavedScope(data);
        if (activityScope is { } savedScope)
        {
            var hub = _realHub.Value;
            hub?.RestoreScope(savedScope);
        }
        GenerateSentryErrorsFromOtelSpan(data, attributes);
        _beforeFinish?.Invoke(span, data);
        if (data.GetException() is { } exception)
        {
            span.Finish(exception);
        }
        else
        {
            // TODO: Does this override a status that we might be setting manually? This logic worked for OTel spans but
            // might need to be more sophisticated for ActivityTraceSpans... alternatively we need to be more
            // sophisticated about how we set status in the first place (leveraging attributes that will be applied
            // appropriately by this GetSpanStatus method).
            var status = GetSpanStatus(data.Status, attributes);
            span.Finish(status);
        }

        _map.TryRemove(data.SpanId, out _);

        // Housekeeping
        PruneFilteredSpans();
    }

    /// <summary>
    /// Clean up items that may have been filtered out.
    /// See https://github.com/getsentry/sentry-dotnet/pull/3198
    /// </summary>
    internal void PruneFilteredSpans(bool force = false)
    {
        if (!force && !NeedsPruning())
        {
            return;
        }

        foreach (var mappedItem in _map)
        {
            var (spanId, span) = mappedItem;
            if (span.GetActivity() is { Recorded: false, IsAllDataRequested: false })
            {
                _map.TryRemove(spanId, out _);
            }
        }
    }

    private bool NeedsPruning()
    {
        var lastPruned = Interlocked.Read(ref _lastPruned);
        if (lastPruned > DateTime.UtcNow.Ticks - PruningInterval)
        {
            return false;
        }

        var thisPruned = DateTime.UtcNow.Ticks;
        Interlocked.CompareExchange(ref _lastPruned, thisPruned, lastPruned);
        // May be false if another thread gets there first
        return Interlocked.Read(ref _lastPruned) == thisPruned;
    }

    private static Scope? GetSavedScope(System.Diagnostics.Activity? activity)
    {
        while (activity is not null)
        {
            if (activity.GetFused<Scope>() is { } savedScope)
            {
                return savedScope;
            }
            activity = activity.Parent;
        }
        return null;
    }

    internal static SpanStatus GetSpanStatus(ActivityStatusCode status, IDictionary<string, object?> attributes)
    {
        // See https://github.com/open-telemetry/opentelemetry-dotnet/discussions/4703
        if (attributes.TryGetValue(OtelSpanAttributeConstants.StatusCodeKey, out var statusCode)
            && statusCode is OtelStatusTags.ErrorStatusCodeTagValue
           )
        {
            return GetErrorSpanStatus(attributes);
        }
        return status switch
        {
            ActivityStatusCode.Unset => SpanStatus.Ok,
            ActivityStatusCode.Ok => SpanStatus.Ok,
            ActivityStatusCode.Error => GetErrorSpanStatus(attributes),
            _ => SpanStatus.UnknownError
        };
    }

    private static SpanStatus GetErrorSpanStatus(IDictionary<string, object?> attributes)
    {
        if (attributes.TryGetTypedValue("http.status_code", out int httpCode))
        {
            return SpanStatusConverter.FromHttpStatusCode(httpCode);
        }

        if (attributes.TryGetTypedValue("rpc.grpc.status_code", out int grpcCode))
        {
            return SpanStatusConverter.FromGrpcStatusCode(grpcCode);
        }

        return SpanStatus.UnknownError;
    }

    private static (string operation, string description, TransactionNameSource source) ParseOtelSpanDescription(
        System.Diagnostics.Activity activity,
         IDictionary<string, object?> attributes)
    {
        // This function should loosely match the JavaScript implementation at:
        // https://github.com/getsentry/sentry-javascript/blob/3487fa3af7aa72ac7fdb0439047cb7367c591e77/packages/opentelemetry-node/src/utils/parseOtelSpanDescription.ts
        // However, it should also follow the OpenTelemetry semantic conventions specification, as indicated.

        // HTTP span
        // https://opentelemetry.io/docs/specs/otel/trace/semantic_conventions/http/
        if (attributes.TryGetTypedValue(OtelSemanticConventions.AttributeHttpMethod, out string httpMethod))
        {
            if (activity.Kind == ActivityKind.Client)
            {
                // Per OpenTelemetry spec, client spans use only the method.
                return ("http.client", httpMethod, TransactionNameSource.Custom);
            }

            if (attributes.TryGetTypedValue(OtelSemanticConventions.AttributeHttpRoute, out string httpRoute))
            {
                // A route exists.  Use the method and route.
                return ("http.server", $"{httpMethod} {httpRoute}", TransactionNameSource.Route);
            }

            if (attributes.TryGetTypedValue(OtelSemanticConventions.AttributeHttpTarget, out string httpTarget))
            {
                // A target exists.  Use the method and target.  If the target is "/" we can treat it like a route.
                var source = httpTarget == "/" ? TransactionNameSource.Route : TransactionNameSource.Url;
                return ("http.server", $"{httpMethod} {httpTarget}", source);
            }

            // Some other type of HTTP server span.  Pass it through with the original name.
            return ("http.server", activity.DisplayName, TransactionNameSource.Custom);
        }

        // DB span
        // https://opentelemetry.io/docs/specs/otel/trace/semantic_conventions/database/
        if (attributes.ContainsKey(OtelSemanticConventions.AttributeDbSystem))
        {
            if (attributes.TryGetTypedValue(OtelSemanticConventions.AttributeDbStatement, out string dbStatement))
            {
                // We have a database statement.  Use it.
                return ("db", dbStatement, TransactionNameSource.Task);
            }

            // Some other type of DB span.  Pass it through with the original name.
            return ("db", activity.DisplayName, TransactionNameSource.Task);
        }

        // RPC span
        // https://opentelemetry.io/docs/specs/otel/trace/semantic_conventions/rpc/
        if (attributes.ContainsKey(OtelSemanticConventions.AttributeRpcService))
        {
            return ("rpc", activity.DisplayName, TransactionNameSource.Route);
        }

        // Messaging span
        // https://opentelemetry.io/docs/specs/otel/trace/semantic_conventions/messaging/
        if (attributes.ContainsKey(OtelSemanticConventions.AttributeMessagingSystem))
        {
            return ("message", activity.DisplayName, TransactionNameSource.Route);
        }

        // FaaS (Functions/Lambda) span
        // https://opentelemetry.io/docs/specs/otel/trace/semantic_conventions/faas/
        if (attributes.TryGetTypedValue(OtelSemanticConventions.AttributeFaasTrigger, out string faasTrigger))
        {
            return (faasTrigger, activity.DisplayName, TransactionNameSource.Route);
        }

        // Default - pass through unmodified.
        return (activity.OperationName, activity.DisplayName, TransactionNameSource.Custom);
    }

    private Dictionary<string, object?> GetOtelContext(IDictionary<string, object?> attributes)
    {
        var otelContext = new Dictionary<string, object?>();
        if (attributes.Count > 0)
        {
            otelContext.Add("attributes", attributes);
        }

        var resourceAttributes = _resourceAttributes.Value;
        if (resourceAttributes.Count > 0)
        {
            otelContext.Add("resource", resourceAttributes);
        }

        return otelContext;
    }

    private void GenerateSentryErrorsFromOtelSpan(System.Diagnostics.Activity activity, IDictionary<string, object?> spanAttributes)
    {
        // https://develop.sentry.dev/sdk/performance/opentelemetry/#step-7-define-generatesentryerrorsfromotelspan
        // https://opentelemetry.io/docs/specs/otel/trace/semantic_conventions/exceptions/
        foreach (var @event in activity.Events.Where(e => e.Name == OtelSemanticConventions.AttributeExceptionEventName))
        {
            var eventAttributes = @event.Tags.ToDict();
            // This would be where we would ideally implement full exception capture. That's not possible at the
            // moment since the full exception isn't yet available via the OpenTelemetry API.
            // See https://github.com/open-telemetry/opentelemetry-dotnet/issues/2439#issuecomment-1577314568
            // if (!eventAttributes.TryGetTypedValue("exception", out Exception exception))
            // {
            //      continue;
            // }

            // At the moment, OTEL only gives us `exception.type`, `exception.message`, and `exception.stacktrace`...
            // So the best we can do is a poor man's exception (no accurate symbolication or anything)
            if (!eventAttributes.TryGetTypedValue(OtelSemanticConventions.AttributeExceptionType, out string exceptionType))
            {
                continue;
            }
            eventAttributes.TryGetTypedValue(OtelSemanticConventions.AttributeExceptionMessage, out string message);
            eventAttributes.TryGetTypedValue(OtelSemanticConventions.AttributeExceptionStacktrace, out string stackTrace);

            Exception exception;
            try
            {
                var type = Type.GetType(exceptionType)!;
                exception = (Exception)Activator.CreateInstance(type, message)!;
                exception.SetSentryMechanism("SentrySpanProcessor.ErrorSpan");
            }
            catch
            {
                _options?.DiagnosticLogger?.LogError($"Failed to create poor man's exception for type : {exceptionType}");
                continue;
            }

            // TODO: Validate that our `DuplicateEventDetectionEventProcessor` prevents this from doubling exceptions
            // that are also caught by other means, such as our AspNetCore middleware, etc.
            // (When options.RecordException = true is set on AddAspNetCoreInstrumentation...)
            // Also, in such cases - how will we get the otel scope and trace context on the other one?

            var sentryEvent = new SentryEvent(exception, @event.Timestamp);
            var otelContext = GetOtelContext(spanAttributes);
            otelContext.Add("stack_trace", stackTrace);
            sentryEvent.Contexts["otel"] = otelContext;
            _hub.CaptureEvent(sentryEvent, scope =>
            {
                var trace = scope.Contexts.Trace;
                trace.SpanId = activity.SpanId.AsSentrySpanId();
                trace.ParentSpanId = activity.ParentSpanId.AsSentrySpanId();
                trace.TraceId = activity.TraceId.AsSentryId();
            });
        }
    }
}