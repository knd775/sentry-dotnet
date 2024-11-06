/*
 * dotnet-gcdump needs .NET 6 or later:
 * https://www.nuget.org/packages/dotnet-gcdump#supportedframeworks-body-tab
 *
 * Also `GC.GetGCMemoryInfo()` is not available in NetFX or NetStandard
 */
#if MEMORY_DUMP_SUPPORTED

using Sentry.Extensibility;
using Sentry.Internal.Extensions;

namespace Sentry.Internal;

internal sealed class MemoryMonitor : IDisposable
{
    private readonly long _totalMemory;

    private readonly SentryOptions _options;
    private readonly HeapDumpOptions _dumpOptions;

    private readonly CancellationTokenSource _cancellationTokenSource = new();

    private readonly Action _onCaptureDump; // Just for testing purposes
    private readonly Action<string> _onDumpCollected;

    public MemoryMonitor(SentryOptions options, Action<string> onDumpCollected, Action? onCaptureDump = null)
    {
        if (options.HeapDumpOptions is null)
        {
            throw new ArgumentException("No heap dump options provided", nameof(options));
        }

        _options = options;
        _dumpOptions = options.HeapDumpOptions;
        _onDumpCollected = onDumpCollected;
        _onCaptureDump = onCaptureDump ?? CaptureMemoryDump;

        _totalMemory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;

        // Since we're not awaiting the task, the continuation will happen elsewhere but that's OK - all we care about
        // is that any exceptions get logged as soon as possible.
        GarbageCollectionMonitor.Start(CheckMemoryUsage, _cancellationTokenSource.Token)
            .ContinueWith(
                t => _options.LogError(t.Exception!, "Garbage collection monitor failed"),
                TaskContinuationOptions.OnlyOnFaulted // guarantees that the exception is not null
            );
    }

    internal void CheckMemoryUsage()
    {
        var eventTime = DateTimeOffset.UtcNow;
        if (!_dumpOptions.Debouncer.CanProcess(eventTime))
        {
            return;
        }

        var usedMemory = Environment.WorkingSet;
        if (!_dumpOptions.Trigger(usedMemory, _totalMemory))
        {
            return;
        }

        _dumpOptions.Debouncer.RecordOccurence(eventTime);

        var usedMemoryPercentage = ((double)usedMemory / _totalMemory) * 100;
        _options.LogDebug("Auto heap dump triggered: Total: {0:N0} bytes, Used: {1:N0} bytes ({2:N2}%)",
            _totalMemory, usedMemory, usedMemoryPercentage);
        _onCaptureDump();
    }

    internal void CaptureMemoryDump()
    {
        if (_options.DisableFileWrite)
        {
            _options.LogDebug("File write has been disabled via the options. Unable to create memory dump.");
            return;
        }

        var dumpFile = TryGetDumpLocation();
        if (dumpFile is null)
        {
            return;
        }

        var processId = Environment.ProcessId;
        _options.LogInfo("Creating a memory dump for Process ID: {0}", processId);

        // Check which patth to use for dotnet-gcdump. If it's been bundled with the application, it will be available
        // in the `dotnet-gcdump` folder of the application directory. Otherwise we assume it has been installed globally.
        var bundledToolPath = Path.Combine(AppContext.BaseDirectory, "dotnet-gcdump", "dotnet-gcdump.dll");
        var arguments = $"collect -v -p {processId} -o '{dumpFile}'";
        var command = _options.FileSystem.FileExists(bundledToolPath)
            ? $"dotnet {bundledToolPath} {arguments}"
            : $"dotnet-gcdump {arguments}";

        _options.LogDebug($"Starting process: {command}");
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmd.exe" : "/bin/bash",
            Arguments = $"-c \"{command}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        process.Start();
        while (!process.StandardOutput.EndOfStream)
        {
            if (process.StandardOutput.ReadLine() is { } line)
            {
                _options.LogDebug($"gcdump: {line}");
            }
        }
#if NET8_0_OR_GREATER
        process.WaitForExit(TimeSpan.FromSeconds(5));
#else
        process.WaitForExit(5000);
#endif

        if (!_options.FileSystem.FileExists(dumpFile))
        {
            // if this happens, hopefully there would be more information in the standard output from the process above
            _options.LogError("Unexpected error creating memory dump. Use debug-level to see output of dotnet-gcdump.");
        }

        _onDumpCollected(dumpFile);
    }

    internal string? TryGetDumpLocation()
    {
        try
        {
            var rootPath = _options.CacheDirectoryPath ??
                           Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var directoryPath = Path.Combine(rootPath, "Sentry", _options.Dsn!.GetHashString());
            var fileSystem = _options.FileSystem;

            if (!fileSystem.CreateDirectory(directoryPath))
            {
                _options.LogWarning("Failed to create a directory for memory dump ({0}).", directoryPath);
                return null;
            }
            _options.LogDebug("Created directory for heap dump ({0}).", directoryPath);

            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var processId = Environment.ProcessId;
            var filePath = Path.Combine(directoryPath, $"{timestamp}_{processId}.gcdump");
            if (fileSystem.FileExists(filePath))
            {
                _options.LogWarning("Duplicate dump file detected.");
                return null;
            }

            return filePath;
        }
        // If there's no write permission or the platform doesn't support this, we handle simply log and bug out
        catch (Exception ex)
        {
            _options.LogError(ex, "Failed to resolve appropriate memory dump location.");
            return null;
        }
    }

    public void Dispose()
    {
        // Important no exceptions can be thrown from this method as it's called when disposing the Hub
        _cancellationTokenSource.Cancel();
        _cancellationTokenSource.Dispose();
    }
}

#endif