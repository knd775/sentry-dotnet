/*
 * dotnet-gcdump needs .NET 6 or later:
 * https://www.nuget.org/packages/dotnet-gcdump#supportedframeworks-body-tab
 *
 * Also `GC.GetGCMemoryInfo()` is not available in NetFX or NetStandard
 */
#if NET6_0_OR_GREATER && !(IOS || ANDROID)

using Sentry.Extensibility;
using Sentry.Internal.Extensions;

namespace Sentry.Internal;

internal class MemoryMonitor : IDisposable
{
    private readonly SentryOptions _options;
    private readonly long _totalMemory;
    internal readonly long _thresholdBytes;
    private bool _dumpTriggered;
    private readonly CancellationTokenSource _cancellationTokenSource = new();

    private Action<string> OnDumpCollected { get; }

    public MemoryMonitor(SentryOptions options, short thresholdPercentage, Action<string> onDumpCollected)
    {
        if (thresholdPercentage is < 0 or > 100)
        {
            throw new ArgumentException("Must be a value between 0 and 100", nameof(thresholdPercentage));
        }

        _options = options;
        OnDumpCollected = onDumpCollected;

        _totalMemory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        var portion = (double)thresholdPercentage / 100;
        _thresholdBytes = (long)Math.Ceiling(portion * _totalMemory);
        _options.LogInfo("Automatic heap dump enabled if memory usage exceeds {0:N0} bytes ({1}%)", _thresholdBytes, thresholdPercentage);

        GarbageCollectionMonitor.Start(CheckMemoryUsage, _cancellationTokenSource.Token);
    }

    private void CheckMemoryUsage()
    {
        // Get the memory used by the application
        var usedMemory = Environment.WorkingSet;
        // var usedMemory = System.Diagnostics.Process.GetCurrentProcess().PagedMemorySize64;

        // Calculate the percentage of memory used
        // var usedMemoryPercentage = GC.GetGCMemoryInfo().MemoryLoadBytes;
        var usedMemoryPercentage = ((double)usedMemory / _totalMemory) * 100;

        // Trigger the event if the threshold is exceeded
        if (usedMemory > _thresholdBytes && !_dumpTriggered)
        {
            _dumpTriggered = true;
            _options.LogDebug("Total Memory: {0:N0} bytes", _totalMemory);
            _options.LogDebug("Threshold: {0:N0} bytes", _thresholdBytes);
            _options.LogDebug("Memory used: {0:N0} bytes ({1:N2}%)", usedMemory, usedMemoryPercentage);
            CaptureMemoryDump();
        }
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

        var command = $"dotnet-gcdump collect -p {processId} -o '{dumpFile}'";
        var startInfo = new ProcessStartInfo
        {
            FileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmd.exe" : "/bin/bash",
            Arguments = $"-c \"{command}\"",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var process = new Process { StartInfo = startInfo };
        process.Start();
        while (!process.StandardOutput.EndOfStream)
        {
            if (process.StandardOutput.ReadLine() is { } line)
            {
                _options.LogDebug(line);
            }
        }

        if (!_options.FileSystem.FileExists(dumpFile))
        {
            // if this happens, hopefully there would be more information in the standard output from gcdump above
            _options.LogError("Unexpected error creating memory dump. Check debug logs for more information.");
        }

        OnDumpCollected(dumpFile);
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
        _cancellationTokenSource.Cancel();
    }
}

#endif