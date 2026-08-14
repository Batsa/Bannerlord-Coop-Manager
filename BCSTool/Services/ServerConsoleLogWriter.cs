using System.Diagnostics;
using System.IO;
using System.Text;

namespace BCSTool.Services;

/// <summary>
/// Persists the raw character stream read from the Bannerlord ConPTY output.
/// Keeping the VT/ANSI control data makes the file a lossless record of what
/// reached Bannerlord Coop Manager; diagnostic tools can strip or replay it later.
/// </summary>
internal sealed class ServerConsoleLogWriter : IDisposable
{
    private const int RetainedLogCount = 10;
    private const long MaximumSegmentBytes = 64L * 1024L * 1024L;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);
    private static readonly Encoding LogEncoding =
        new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false);

    private readonly object _sync = new();
    private readonly string _directory;
    private readonly DateTime _timestamp;
    private readonly long _maximumSegmentBytes;
    private readonly Action<string>? _rotationWarning;
    private readonly List<string> _segmentFilePaths = [];
    private StreamWriter _writer;
    private int _nextSegmentSuffix;
    private long _currentSegmentBytes;
    private long _lastFlushTimestamp;
    private bool _disposed;

    public string FilePath { get; }


    private ServerConsoleLogWriter(
        string directory,
        DateTime timestamp,
        long maximumSegmentBytes,
        Action<string>? rotationWarning,
        string filePath,
        StreamWriter writer,
        int nextSegmentSuffix)
    {
        _directory = directory;
        _timestamp = timestamp;
        _maximumSegmentBytes = maximumSegmentBytes;
        _rotationWarning = rotationWarning;
        FilePath = filePath;
        _writer = writer;
        _nextSegmentSuffix = nextSegmentSuffix;
        _segmentFilePaths.Add(filePath);
        _lastFlushTimestamp = Stopwatch.GetTimestamp();
    }


    internal IReadOnlyList<string> SegmentFilePaths
    {
        get
        {
            lock (_sync)
            {
                return _segmentFilePaths.ToArray();
            }
        }
    }


    public static ServerConsoleLogWriter Create(
        string logDirectory,
        DateTime timestamp,
        Action<string>? rotationWarning = null)
    {
        return Create(
            logDirectory,
            timestamp,
            MaximumSegmentBytes,
            rotationWarning);
    }


    internal static ServerConsoleLogWriter CreateForTesting(
        string logDirectory,
        DateTime timestamp,
        long maximumSegmentBytes,
        Action<string>? rotationWarning = null)
    {
        return Create(
            logDirectory,
            timestamp,
            maximumSegmentBytes,
            rotationWarning);
    }


    private static ServerConsoleLogWriter Create(
        string logDirectory,
        DateTime timestamp,
        long maximumSegmentBytes,
        Action<string>? rotationWarning)
    {
        if (string.IsNullOrWhiteSpace(logDirectory))
        {
            throw new ArgumentException(
                "Server log directory cannot be empty.",
                nameof(logDirectory));
        }

        if (maximumSegmentBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumSegmentBytes),
                "Server log segment size must be positive.");
        }

        var directory = Path.GetFullPath(logDirectory);
        Directory.CreateDirectory(directory);

        var (filePath, writer, segmentSuffix) =
            CreateSegment(
                directory,
                timestamp,
                minimumSuffix: 0);

        var result =
            new ServerConsoleLogWriter(
                directory,
                timestamp,
                maximumSegmentBytes,
                rotationWarning,
                filePath,
                writer,
                segmentSuffix + 1);

        result.RotateOldLogs();

        return result;
    }


    public void Append(string chunk)
    {
        if (string.IsNullOrEmpty(chunk))
            return;

        lock (_sync)
        {
            ThrowIfDisposed();

            var chunkBytes = LogEncoding.GetByteCount(chunk);
            if (
                _currentSegmentBytes > 0 &&
                chunkBytes >
                _maximumSegmentBytes - _currentSegmentBytes)
            {
                StartNextSegment();
            }

            _writer.Write(chunk);
            _currentSegmentBytes += chunkBytes;

            if (
                Stopwatch.GetElapsedTime(
                    _lastFlushTimestamp) >=
                FlushInterval)
            {
                _writer.Flush();
                _lastFlushTimestamp = Stopwatch.GetTimestamp();
            }
        }
    }


    public void Flush()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            _writer.Flush();
            _lastFlushTimestamp = Stopwatch.GetTimestamp();
        }
    }


    private void StartNextSegment()
    {
        _writer.Flush();
        _writer.Dispose();

        var (filePath, writer, segmentSuffix) =
            CreateSegment(
                _directory,
                _timestamp,
                _nextSegmentSuffix);

        _writer = writer;
        _nextSegmentSuffix = segmentSuffix + 1;
        _currentSegmentBytes = 0;
        _segmentFilePaths.Add(filePath);
        _lastFlushTimestamp = Stopwatch.GetTimestamp();

        RotateOldLogs();
    }


    private void RotateOldLogs()
    {
        var allLogs =
            Directory.EnumerateFiles(
                    _directory,
                    "coop-server-*.log",
                    SearchOption.TopDirectoryOnly)
                .ToArray();

        var retainedLogs =
            _segmentFilePaths
                .Where(File.Exists)
                .TakeLast(RetainedLogCount)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var remainingSlots =
            RetainedLogCount - retainedLogs.Count;

        foreach (
            var oldLog in
            allLogs
                .Where(path => !retainedLogs.Contains(path))
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ThenByDescending(file => file.Name, StringComparer.OrdinalIgnoreCase)
                .Take(remainingSlots))
        {
            retainedLogs.Add(oldLog.FullName);
        }

        foreach (
            var oldLog in
            allLogs)
        {
            if (retainedLogs.Contains(oldLog))
            {
                continue;
            }

            try
            {
                File.Delete(oldLog);
            }
            catch (Exception ex)
                when (ex is IOException or UnauthorizedAccessException)
            {
                _rotationWarning?.Invoke(
                    Path.GetFileName(oldLog) +
                    ": " +
                    ex.Message);
            }
        }
    }


    private static (string FilePath, StreamWriter Writer, int SegmentSuffix) CreateSegment(
        string directory,
        DateTime timestamp,
        int minimumSuffix)
    {
        var (filePath, segmentSuffix) =
            CreateUniqueLogPath(
                directory,
                timestamp,
                minimumSuffix);

        var stream =
            new FileStream(
                filePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 65536,
                FileOptions.SequentialScan);

        var writer =
            new StreamWriter(
                stream,
                LogEncoding,
                bufferSize: 65536,
                leaveOpen: false);

        return (filePath, writer, segmentSuffix);
    }


    private static (string FilePath, int SegmentSuffix) CreateUniqueLogPath(
        string directory,
        DateTime timestamp,
        int minimumSuffix)
    {
        var stem =
            "coop-server-" +
            timestamp.ToString(
                "yyyyMMdd-HHmmss");

        for (var suffix = minimumSuffix; ; suffix++)
        {
            var name =
                suffix == 0
                    ? stem + ".log"
                    : stem + "-" + suffix + ".log";

            var path =
                Path.Combine(
                    directory,
                    name);

            if (!File.Exists(path))
                return (path, suffix);
        }
    }


    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            _disposed,
            this);
    }


    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;

            _disposed = true;
            _writer.Dispose();
        }
    }
}
