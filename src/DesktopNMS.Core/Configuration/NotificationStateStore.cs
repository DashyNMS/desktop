using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.Core.Configuration;

/// <summary>
/// Remembers which alert/state pairs have already produced a toast, so
/// restarting DesktopNMS does not re-announce everything that is outstanding.
/// </summary>
public interface INotificationStateStore
{
    /// <summary>Alert id to the alerts.state value last notified about.</summary>
    IReadOnlyDictionary<int, int> Load();

    void Save(IReadOnlyDictionary<int, int> notifiedStates);
}

public sealed class NotificationStateStore : INotificationStateStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
    };

    private readonly ILogger<NotificationStateStore> _logger;

    public NotificationStateStore(ILogger<NotificationStateStore> logger)
    {
        _logger = logger;
    }

    public IReadOnlyDictionary<int, int> Load()
    {
        try
        {
            if (!File.Exists(AppPaths.NotificationStateFile))
            {
                return new Dictionary<int, int>();
            }

            var json = File.ReadAllText(AppPaths.NotificationStateFile);
            var state = JsonSerializer.Deserialize<PersistedState>(json, SerializerOptions);

            if (state?.Alerts is null)
            {
                return new Dictionary<int, int>();
            }

            var result = new Dictionary<int, int>(state.Alerts.Count);
            foreach (var pair in state.Alerts)
            {
                if (int.TryParse(pair.Key, out var id))
                {
                    result[id] = pair.Value;
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read notification state; starting from empty");
            return new Dictionary<int, int>();
        }
    }

    public void Save(IReadOnlyDictionary<int, int> notifiedStates)
    {
        try
        {
            var state = new PersistedState
            {
                SavedAtUtc = DateTime.UtcNow,
                Alerts = notifiedStates.ToDictionary(
                    pair => pair.Key.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    pair => pair.Value),
            };

            var json = JsonSerializer.Serialize(state, SerializerOptions);
            var temp = AppPaths.NotificationStateFile + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, AppPaths.NotificationStateFile, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not write notification state");
        }
    }

    private sealed class PersistedState
    {
        public DateTime SavedAtUtc { get; set; }

        /// <summary>Alert id (as a string key, because JSON object keys are strings) to state value.</summary>
        public Dictionary<string, int>? Alerts { get; set; }
    }
}
