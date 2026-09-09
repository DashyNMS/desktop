namespace DesktopNMS.Core.Alerting;

/// <summary>
/// Remembers which alerts this app itself just acknowledged or returned to
/// active, so the poll that picks up the resulting state change does not also
/// raise a toast telling you about the thing you just did yourself.
/// </summary>
public interface ISelfActionTracker
{
    /// <summary>Call right after an acknowledge/unacknowledge request succeeds.</summary>
    void Record(int alertId, AlertChangeKind kind);

    /// <summary>
    /// True if this exact alert/kind combination was just self-initiated.
    /// Consumes the record, so it only suppresses the one poll it was meant for.
    /// </summary>
    bool WasSelfInitiated(int alertId, AlertChangeKind kind);
}

public sealed class SelfActionTracker : ISelfActionTracker
{
    // A poll landing more than this long after the action was almost
    // certainly triggered by something else (a second client, the LibreNMS
    // web UI, an auto-clear rule), so it is worth notifying about after all.
    private static readonly TimeSpan Expiry = TimeSpan.FromMinutes(2);

    private readonly Dictionary<int, (AlertChangeKind Kind, DateTimeOffset RecordedAt)> _entries = new();
    private readonly object _lock = new();

    public void Record(int alertId, AlertChangeKind kind)
    {
        lock (_lock)
        {
            _entries[alertId] = (kind, DateTimeOffset.UtcNow);
        }
    }

    public bool WasSelfInitiated(int alertId, AlertChangeKind kind)
    {
        lock (_lock)
        {
            if (!_entries.Remove(alertId, out var entry))
            {
                return false;
            }

            return entry.Kind == kind && DateTimeOffset.UtcNow - entry.RecordedAt < Expiry;
        }
    }
}
