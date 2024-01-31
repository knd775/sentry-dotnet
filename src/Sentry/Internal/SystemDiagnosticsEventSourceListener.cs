using System.Diagnostics.Tracing;

namespace Sentry.Internal;

internal class SystemDiagnosticsEventSourceListener : EventListener
{
    private readonly ExperimentalMetricsOptions _metricsOptions;
    private readonly Lazy<IMetricAggregator> _metricsAggregator;
    private IMetricAggregator MetricsAggregator => _metricsAggregator.Value;
    private static SystemDiagnosticsEventSourceListener? DefaultListener;
    private volatile bool _initialized = false;

    private SystemDiagnosticsEventSourceListener(ExperimentalMetricsOptions metricsOptions)
        : this(metricsOptions, () => SentrySdk.Metrics)
    {
    }

    /// <summary>
    /// Overload for testing purposes - allows us to supply a mock IMetricAggregator
    /// </summary>
    internal SystemDiagnosticsEventSourceListener(ExperimentalMetricsOptions metricsOptions, Func<IMetricAggregator> metricsAggregatorResolver)
    {
        _metricsOptions = metricsOptions;
        _metricsAggregator = new Lazy<IMetricAggregator>(metricsAggregatorResolver);
        _initialized = true;
    }

    internal static void InitializeDefaultListener(ExperimentalMetricsOptions metricsOptions)
    {
        var oldListener = Interlocked.Exchange(
            ref DefaultListener,
            new SystemDiagnosticsEventSourceListener(metricsOptions)
            );
        oldListener?.Dispose();
    }

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        // In a multi-threaded application, it's possible for this method to be called before constructor initialization
        // completes, which is why we check _initialized... otherwise _metricsOptions might be null
        if (_initialized && _metricsOptions.CaptureSystemDiagnosticsEventSourceNames.ContainsMatch(eventSource.Name))
        {
            EnableEvents(eventSource, EventLevel.LogAlways);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
#if NET5_0_OR_GREATER
        DateTimeOffset eventTime = eventData.TimeStamp.ToUniversalTime();
#else
        DateTimeOffset eventTime = DateTime.UtcNow;
#endif
        var name = eventData.EventName ?? eventData.EventSource.Name + eventData.EventId.ToString();
        Dictionary<string, string> tags = new()
        {
            ["EventSource"] = eventData.EventSource.Name,
            ["EventId"] = eventData.EventId.ToString(),
            ["Level"] = eventData.Level.ToString(),
            ["Opcode"] = eventData.Opcode.ToString()
        };
        if (eventData.Message is { } message)
        {
            tags.Add("Message", message);
        }
        MetricsAggregator.Increment(name, 1, MeasurementUnit.None, tags, eventTime);
    }
}