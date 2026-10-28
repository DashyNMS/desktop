using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopNMS.Diagnostics;

public enum MetricUpdateChannel { Stable, Preview }
public sealed record UpdateMetricContext(string Source, string Target, MetricUpdateChannel Channel)
{
    internal static string Version(string? value)
    {
        value = value?.TrimStart('v', 'V').Split('+')[0];
        return value is { Length: > 0 and <= 32 }
            ? CrashPayload.SafeContext(new(value, "unknown", "unknown")).Release : "unknown";
    }
    internal bool IsValid => Source == Version(Source) && Target == Version(Target) && Enum.IsDefined(Channel);
}

public sealed class UpdateMetrics(IAppMetrics? metrics, string directory, string sourceVersion)
{
    private readonly object _gate = new();
    private readonly string _source = UpdateMetricContext.Version(sourceVersion);
    private string Marker => Path.Combine(directory, "pending-update-metrics.json");
    private static readonly JsonSerializerOptions Options = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public UpdateTiming Begin(MetricArea stage, string? target, bool preview, MetricMode mode)
        => new(metrics, stage, new(_source, UpdateMetricContext.Version(target), preview ? MetricUpdateChannel.Preview : MetricUpdateChannel.Stable), mode);

    public void MarkPending(UpdateTiming timing)
    {
        lock (_gate)
        {
            if (!timing.IsCurrent || timing.Context.Target == "unknown") return;
            try
            {
                Directory.CreateDirectory(directory);
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return;
                // No identity or path is persisted; a single bounded target is enough.
                if (File.Exists(Marker) && (File.GetAttributes(Marker) & FileAttributes.ReparsePoint) != 0) return;
                File.WriteAllBytes(Marker, JsonSerializer.SerializeToUtf8Bytes(new Pending(1, DateTimeOffset.UtcNow, timing.Context), Options));
            }
            catch (Exception) { }
        }
    }

    public void Withdraw()
    {
        lock (_gate) { try { if (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0) File.Delete(Marker); } catch (Exception) { } }
    }

    public bool ConfirmStartup(string currentVersion)
    {
        lock (_gate)
        {
            try
            {
                if (metrics?.IsEnabled != true) { Withdraw(); return false; }
                var generation = metrics.ConsentGeneration;
                if (!File.Exists(Marker)) return false;
                var file = new FileInfo(Marker);
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 ||
                    (file.Attributes & FileAttributes.ReparsePoint) != 0 || file.Length is <= 0 or > 1024) { Withdraw(); return false; }
                var pending = JsonSerializer.Deserialize<Pending>(File.ReadAllBytes(Marker), Options);
                if (pending is null || pending.Schema != 1 || pending.Context is null || !pending.Context.IsValid ||
                    pending.Context.Target == "unknown" || pending.Timestamp < DateTimeOffset.UtcNow.AddDays(-7) ||
                    pending.Timestamp > DateTimeOffset.UtcNow.AddMinutes(5)) { Withdraw(); return false; }
                var current = UpdateMetricContext.Version(currentVersion);
                if (current == "unknown" || current == pending.Context.Source) return true; // Old app running is not proof of failure.
                Withdraw();
                metrics.Record(new(MetricName.UpdateOutcome, 1,
                    Outcome: current == pending.Context.Target ? MetricOutcome.Confirmed : MetricOutcome.DifferentVersion,
                    Area: MetricArea.UpdateStartup, Update: pending.Context), generation);
                return true;
            }
            catch (Exception) { Withdraw(); return false; }
        }
    }

    public void ConfirmUntrackedStartup(string? previousVersion, string currentVersion)
    {
        if (metrics?.IsEnabled != true) return;
        var generation = metrics.ConsentGeneration;
        var source = UpdateMetricContext.Version(previousVersion);
        var target = UpdateMetricContext.Version(currentVersion);
        if (source == "unknown" || target == "unknown" || !DesktopNMS.Core.Updates.ReleaseVersion.IsNewer(target, source)) return;
        metrics.Record(new(MetricName.UpdateOutcome, 1, MetricMode.Untracked, MetricOutcome.Confirmed,
            MetricArea.UpdateStartup, new(source, target, target.Contains('-') ? MetricUpdateChannel.Preview : MetricUpdateChannel.Stable)), generation);
    }

    private sealed record Pending(int Schema, DateTimeOffset Timestamp, UpdateMetricContext Context);
}

public sealed class UpdateTiming
{
    private readonly IAppMetrics? _metrics;
    private readonly long _generation;
    private readonly long _started = System.Diagnostics.Stopwatch.GetTimestamp();
    private readonly MetricArea _stage;
    private readonly MetricMode _mode;
    private int _completed;
    public UpdateMetricContext Context { get; private set; }
    public bool IsCurrent => _metrics?.IsEnabled == true && _metrics.ConsentGeneration == _generation;
    internal UpdateTiming(IAppMetrics? metrics, MetricArea stage, UpdateMetricContext context, MetricMode mode)
    {
        _metrics = metrics?.IsEnabled == true ? metrics : null;
        _generation = _metrics?.ConsentGeneration ?? 0;
        _stage = stage; Context = context; _mode = mode;
    }
    public void Complete(MetricOutcome outcome, string? target = null)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0 || !IsCurrent) return;
        if (target is not null) Context = Context with { Target = UpdateMetricContext.Version(target) };
        _metrics!.Record(new(MetricName.UpdateOutcome, 1, _mode, outcome, _stage, Context), _generation);
        if (_stage == MetricArea.UpdateAvailability) return;
        _metrics.Record(new(MetricName.UpdateDuration, System.Diagnostics.Stopwatch.GetElapsedTime(_started).TotalMilliseconds,
            _mode, outcome, _stage, Context), _generation);
    }
}
