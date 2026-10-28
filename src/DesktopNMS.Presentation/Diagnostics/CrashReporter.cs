using System.Runtime.CompilerServices;
using System.Text.Json;

namespace DesktopNMS.Diagnostics;

public sealed class CrashReporter : ICrashReporter, IDisposable
{
    private readonly CrashReportContext _context;
    private readonly ICrashTransport _transport;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _sender = new(1, 1);
    private readonly ConditionalWeakTable<Exception, object> _captured = new();
    private CancellationTokenSource _consent = new();
    private string? _directory;
    private bool _enabled;
    private bool _sendErrors;
    private bool _sendUsage;
    private bool _sendMetrics;
    private bool _sendSessionHealth;
    private bool _usageRecorded;
    private bool _disposed;
    private long _generation;
    private DateTimeOffset _nextErrorAttempt;
    private DateTimeOffset _nextUsageAttempt;
    private int _pumpScheduled;

    public CrashReporter(CrashReportContext context, ICrashTransport transport, TimeProvider? timeProvider = null)
    {
        _context = context;
        _transport = transport;
        _time = timeProvider ?? TimeProvider.System;
    }

    public bool IsAvailable => _transport.IsAvailable;
    public CrashReportContext Context => CrashPayload.SafeContext(_context);
    public event Action<CrashOrigin>? ManagedFailureObserved;

    public void Configure(string dataDirectory, bool sendErrors, bool sendUsage = false)
        => ConfigureMetrics(dataDirectory, sendErrors, sendUsage, false);

    public void ConfigureMetrics(string dataDirectory, bool sendErrors, bool sendUsage, bool sendMetrics, bool sendSessionHealth = false)
    {
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                var directory = Path.Combine(Path.GetFullPath(dataDirectory), "crash-reports");
                sendErrors &= IsAvailable;
                sendUsage &= IsAvailable;
                sendMetrics &= sendUsage;
                sendSessionHealth &= sendMetrics && sendErrors;
                var enabled = sendErrors || sendUsage;
                if (_directory == directory && _sendErrors == sendErrors && _sendUsage == sendUsage && _sendMetrics == sendMetrics && _sendSessionHealth == sendSessionHealth)
                {
                    if (!enabled) Purge(directory);
                    return;
                }
                _enabled = false;
                _sendErrors = sendErrors;
                _sendUsage = sendUsage;
                _sendMetrics = sendMetrics;
                _sendSessionHealth = sendSessionHealth;
                _generation++;
                _consent.Cancel();
                _consent.Dispose();
                _consent = new();
                if (_directory is not null && _directory != directory) Purge(_directory);
                _directory = directory;
                _nextErrorAttempt = default;
                _nextUsageAttempt = default;
                if (!enabled) { Purge(directory); return; }
                Directory.CreateDirectory(directory);
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return;
                _enabled = true;
                ReadQueue();
            }
            catch (Exception) { _enabled = false; }
        }
    }

    public void Capture(Exception exception, CrashOrigin origin)
    {
        var send = false;
        Action<CrashOrigin>? observed = null;
        try
        {
            lock (_gate)
            {
                if (!_enabled || !_sendErrors || _disposed) return;
                observed = ManagedFailureObserved;
                if (_captured.TryGetValue(exception, out _)) return;
                try
                {
                    var report = CrashPayload.Create(exception, origin, _context, _time.GetUtcNow());
                    if (!CrashPayload.IsValid(report)) return;
                    if (!Persist(report)) return;
                    _captured.Add(exception, new object());
                    ReadQueue();
                    send = origin == CrashOrigin.Command;
                }
                catch (Exception) { }
            }
        }
        finally { try { observed?.Invoke(origin); } catch (Exception) { } }
        if (send && Interlocked.CompareExchange(ref _pumpScheduled, 1, 0) == 0) _ = PumpAsync();
    }

    public void RecordUsage()
    {
        lock (_gate)
        {
            if (!_enabled || !_sendUsage || _disposed || _usageRecorded) return;
            try
            {
                var report = new UsageReport(1, Guid.NewGuid(), _time.GetUtcNow(), CrashPayload.SafeContext(_context));
                if (!Persist(report)) return;
                _usageRecorded = true;
                ReadQueue();
            }
            catch (Exception) { }
        }
    }

    internal long ConsentGeneration { get { lock (_gate) return _generation; } }

    public bool RecordMetrics(IReadOnlyList<MetricPoint> points, long? expectedGeneration = null)
    {
        lock (_gate)
        {
            if (!_enabled || !_sendMetrics || _disposed ||
                (expectedGeneration is { } generation && generation != _generation)) return false;
            try
            {
                var report = new MetricsReport(1, Guid.NewGuid(), points.Min(point => point.Timestamp), CrashPayload.SafeContext(_context), points);
                return CrashPayload.IsValid(report) && Persist(report);
            }
            catch (Exception) { return false; }
        }
    }

    public bool RecordSessionHealth(SessionHealthReport report)
    {
        lock (_gate)
        {
            if (!_enabled || !_sendSessionHealth || _disposed || !CrashPayload.IsValid(report)) return false;
            try
            {
                // Recovery can retry the same terminal session after an interrupted save.
                if (ReadQueue().Any(item => item.Report is SessionHealthReport && item.Report.EventId == report.EventId)) return true;
                return Persist(report);
            }
            catch (Exception) { return false; }
        }
    }

    private bool Persist(DiagnosticReport report)
    {
        var pending = ReadQueue();
        if (pending.Count >= 10)
        {
            Delete(pending[0].Path);
            if (File.Exists(pending[0].Path)) return false;
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(report, CrashPayload.JsonOptions);
        if (bytes.Length > CrashPayload.MaximumBytes) return false;
        var path = Path.Combine(_directory!, report.EventId.ToString("N") + ".json");
        var temporary = path + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path);
            return true;
        }
        finally { Delete(temporary); }
    }

    private async Task PumpAsync()
    {
        try { await SendPendingAsync().ConfigureAwait(false); }
        finally { Volatile.Write(ref _pumpScheduled, 0); }
    }

    public async Task SendPendingAsync(CancellationToken cancellationToken = default)
    {
        var acquired = false;
        try
        {
            await _sender.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;
            while (!cancellationToken.IsCancellationRequested)
            {
                Task<CrashSendResult> request;
                DiagnosticReport report;
                string path;
                long generation;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12), _time);
                CancellationTokenSource linked;
                lock (_gate)
                {
                    if (!_enabled || _disposed) return;
                    var now = _time.GetUtcNow();
                    var pending = ReadQueue().Where(item => now >=
                        (item.Report is CrashReport ? _nextErrorAttempt : _nextUsageAttempt)).ToList();
                    if (pending.Count == 0) return;
                    (path, report) = pending[0];
                    generation = _generation;
                    linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _consent.Token, timeout.Token);
                    try { request = _transport.SendAsync(report, linked.Token); }
                    catch (Exception) { linked.Dispose(); return; }
                }
                CrashSendResult result;
                using (linked)
                {
                    try { result = await request.WaitAsync(linked.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                    catch (Exception) { result = new(CrashSendDisposition.Retry); }
                }
                lock (_gate)
                {
                    if (_disposed || !_enabled || generation != _generation) return;
                    if (result.Disposition == CrashSendDisposition.Retry)
                    {
                        var delay = result.RetryAfter ?? TimeSpan.FromMinutes(1);
                        SetNextAttempt(report, delay, result.RetryAllCategories);
                        continue;
                    }
                    Delete(path);
                    if (File.Exists(path)) return;
                    if (result.RetryAfter is { } backoff && backoff > TimeSpan.Zero) SetNextAttempt(report, backoff, result.RetryAllCategories);
                    if (report is not CrashReport) SetNextAttempt(report, TimeSpan.FromMinutes(1));
                }
            }
        }
        catch (Exception) { }
        finally { if (acquired) _sender.Release(); }
    }

    private void SetNextAttempt(DiagnosticReport report, TimeSpan delay, bool allCategories = false)
    {
        var next = _time.GetUtcNow() + TimeSpan.FromSeconds(Math.Clamp(delay.TotalSeconds, 1, 86400));
        if (allCategories || report is CrashReport) _nextErrorAttempt = next > _nextErrorAttempt ? next : _nextErrorAttempt;
        if (allCategories || report is not CrashReport) _nextUsageAttempt = next > _nextUsageAttempt ? next : _nextUsageAttempt;
    }

    private List<(string Path, DiagnosticReport Report)> ReadQueue()
    {
        var queue = new List<(string, DiagnosticReport)>();
        if (_directory is null || !Directory.Exists(_directory) ||
            (File.GetAttributes(_directory) & FileAttributes.ReparsePoint) != 0) return queue;
        foreach (var partial in Directory.EnumerateFiles(_directory, "*.tmp")) Delete(partial);
        foreach (var path in Directory.EnumerateFiles(_directory, "*.json"))
        {
            try
            {
                var file = new FileInfo(path);
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0 || file.Length is <= 0 or > CrashPayload.MaximumBytes)
                { Delete(path); continue; }
                var report = JsonSerializer.Deserialize<DiagnosticReport>(File.ReadAllBytes(path), CrashPayload.JsonOptions);
                var now = _time.GetUtcNow();
                if (report is null || !CrashPayload.IsValid(report) ||
                    (report is CrashReport && !_sendErrors) || (report is UsageReport && !_sendUsage) || (report is MetricsReport && !_sendMetrics) || (report is SessionHealthReport && !_sendSessionHealth) || report.Timestamp < now.AddDays(-7) ||
                    report.Timestamp > now.AddMinutes(5) ||
                    (report is MetricsReport metrics && metrics.Points.Any(point => point.Timestamp > now.AddMinutes(5))) ||
                    Path.GetFileName(path) != report.EventId.ToString("N") + ".json")
                { Delete(path); continue; }
                queue.Add((path, report));
            }
            catch (Exception) { Delete(path); }
        }
        queue.Sort((left, right) => left.Item2.Timestamp.CompareTo(right.Item2.Timestamp));
        while (queue.Count > 10) { Delete(queue[0].Item1); queue.RemoveAt(0); }
        return queue;
    }

    private static void Purge(string directory)
    {
        if (!Directory.Exists(directory) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return;
        foreach (var path in Directory.EnumerateFiles(directory)) Delete(path);
    }

    private static void Delete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _enabled = false;
            _generation++;
            _consent.Cancel();
            _consent.Dispose();
            if (_transport is IDisposable disposable) disposable.Dispose();
        }
    }
}
