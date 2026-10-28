using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopNMS.Diagnostics;

/// <summary>An eligible process interval, including tray time; absence of an exit is unknown, never a confirmed crash.</summary>
public sealed class SessionHealth : IDisposable
{
    private readonly object _gate = new();
    private readonly CrashReporter _reporter;
    private readonly string _directory;
    private readonly TimeProvider _time;
    private readonly Func<bool> _sampleDuration;
    private State? _state;
    private long _started;
    private bool _enabled, _disposed;
    private string Marker => Path.Combine(_directory, "session-health.json");
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 8,
    };

    public SessionHealth(CrashReporter reporter, string dataDirectory, TimeProvider? timeProvider = null, Func<bool>? sampleDuration = null)
    {
        _reporter = reporter; _directory = Path.GetFullPath(dataDirectory);
        _time = timeProvider ?? TimeProvider.System;
        _sampleDuration = sampleDuration ?? (() => Random.Shared.Next(10) == 0);
        _reporter.ManagedFailureObserved += OnManagedFailure;
    }

    public void Configure(bool enabled)
    {
        lock (_gate)
        {
            if (_disposed) return;
            enabled &= _reporter.IsAvailable;
            if (!enabled) { _enabled = false; _state = null; Delete(); return; }
            if (_enabled) return;
            _enabled = true;
            if (!Recover()) return;
            _started = _time.GetTimestamp();
            _state = new(1, 2, Guid.NewGuid(), _time.GetUtcNow(), _reporter.Context, false, _sampleDuration(), null, null, null);
            if (!Save()) _state = null; // No durable start means no reliable recovery/denominator.
        }
    }

    private void OnManagedFailure(CrashOrigin origin)
    {
        lock (_gate)
        {
            if (!_enabled || _disposed || _state is null || !Enum.IsDefined(origin)) return;
            if (origin == CrashOrigin.Application) Finish(SessionOutcome.ManagedCrash);
            else if (!_state.HadError) { _state = _state with { HadError = true }; Save(); }
            // A UI exception alone is not proof of process termination. A later
            // Application-origin capture refines it if the exception really escapes.
        }
    }

    private void Finish(SessionOutcome outcome)
    {
        if (_state is null) return;
        var duration = _time.GetElapsedTime(_started).TotalMilliseconds;
        _state = _state with { Final = outcome, Ended = _time.GetUtcNow(), Duration =
            _state.SampleDuration && duration is >= 0 and <= 7_776_000_000 ? duration : null };
        Save(); // Preserve the known terminal state before attempting the bounded queue.
        Enqueue(_state);
        _state = null;
    }

    private bool Enqueue(State state)
    {
        var outcome = state.Final ?? SessionOutcome.Abnormal;
        var report = new SessionHealthReport(1, state.Id, state.Ended ?? _time.GetUtcNow(), state.Context,
            outcome, outcome == SessionOutcome.Abnormal ? null : state.Duration);
        if (!_reporter.RecordSessionHealth(report)) return false;
        Delete();
        return true;
    }

    private bool Recover()
    {
        try
        {
            if (!File.Exists(Marker)) return true;
            var info = new FileInfo(Marker);
            if (!SafeDirectory() || (info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length is <= 0 or > 2048) { Delete(); return true; }
            var state = JsonSerializer.Deserialize<State>(File.ReadAllBytes(Marker), Options);
            if (state is null || state.Schema != 1 || state.ConsentVersion != 2 || state.Id == Guid.Empty ||
                state.Started > _time.GetUtcNow().AddMinutes(5) || (state.Ended ?? state.Started) < _time.GetUtcNow().AddDays(-7) ||
                ((state.Final is null) != (state.Ended is null)) || state.Ended < state.Started || state.Ended > _time.GetUtcNow().AddMinutes(5) ||
                !CrashPayload.IsValid(new SessionHealthReport(1, state.Id, state.Ended ?? _time.GetUtcNow(), state.Context,
                    state.Final ?? SessionOutcome.Abnormal, state.Duration))) { Delete(); return true; }
            return Enqueue(state);
        }
        catch (Exception) { Delete(); return true; }
    }

    private bool SafeDirectory() => Directory.Exists(_directory) && (File.GetAttributes(_directory) & FileAttributes.ReparsePoint) == 0;
    private bool Save()
    {
        if (_state is null) return false;
        try
        {
            Directory.CreateDirectory(_directory);
            if (!SafeDirectory()) return false;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(_state, Options);
            if (bytes.Length > 2048) return false;
            var temporary = Marker + ".tmp";
            File.Delete(temporary);
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { file.Write(bytes); file.Flush(flushToDisk: true); }
            File.Move(temporary, Marker, overwrite: true);
            return true;
        }
        catch (Exception) { return false; }
    }
    private void Delete()
    {
        try { if (SafeDirectory()) { File.Delete(Marker); File.Delete(Marker + ".tmp"); } } catch (Exception) { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_enabled && _state is not null) Finish(_state.HadError ? SessionOutcome.Errored : SessionOutcome.Healthy);
            _disposed = true; _enabled = false;
            _reporter.ManagedFailureObserved -= OnManagedFailure;
        }
    }

    private sealed record State(int Schema, int ConsentVersion, Guid Id, DateTimeOffset Started,
        CrashReportContext Context, bool HadError, bool SampleDuration, SessionOutcome? Final, DateTimeOffset? Ended, double? Duration);
}
