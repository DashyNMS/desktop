using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.Core.Configuration;

public interface ISettingsStore
{
    /// <summary>The live settings object. Mutate it, then call <see cref="Save"/>.</summary>
    AppSettings Current { get; }

    /// <summary>Raised after <see cref="Save"/> or <see cref="Replace"/> changes the settings.</summary>
    event EventHandler<AppSettings>? Changed;

    /// <summary>Reads settings from disk, falling back to defaults on any problem.</summary>
    AppSettings Load();

    /// <summary>Writes <see cref="Current"/> to disk.</summary>
    void Save();

    /// <summary>Swaps in a new settings object and persists it.</summary>
    void Replace(AppSettings settings);
}

/// <summary>JSON-backed settings in %APPDATA%\DesktopNMS\settings.json.</summary>
public sealed class SettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ILogger<SettingsStore> _logger;
    private readonly object _sync = new();

    private AppSettings _current = new();
    private bool _loaded;

    public SettingsStore(ILogger<SettingsStore> logger)
    {
        _logger = logger;
    }

    public AppSettings Current
    {
        get
        {
            if (!_loaded)
            {
                Load();
            }

            return _current;
        }
    }

    public event EventHandler<AppSettings>? Changed;

    public AppSettings Load()
    {
        lock (_sync)
        {
            _loaded = true;

            try
            {
                if (File.Exists(AppPaths.SettingsFile))
                {
                    var json = File.ReadAllText(AppPaths.SettingsFile);
                    var loaded = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);
                    if (loaded is not null)
                    {
                        loaded.Normalise();
                        _current = loaded;
                        return _current;
                    }
                }
            }
            catch (Exception ex)
            {
                // A corrupt settings file must not stop the app starting.
                _logger.LogWarning(ex, "Could not read {Path}; falling back to defaults", AppPaths.SettingsFile);
                TryBackupCorruptFile();
            }

            _current = new AppSettings();
            _current.Normalise();
            return _current;
        }
    }

    public void Save()
    {
        AppSettings snapshot;

        lock (_sync)
        {
            _current.Normalise();
            snapshot = _current;

            try
            {
                var json = JsonSerializer.Serialize(snapshot, SerializerOptions);
                WriteAtomic(AppPaths.SettingsFile, json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not write {Path}", AppPaths.SettingsFile);
            }
        }

        Changed?.Invoke(this, snapshot);
    }

    public void Replace(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_sync)
        {
            settings.Normalise();
            _current = settings;
            _loaded = true;
        }

        Save();
    }

    /// <summary>Write to a temporary file and move it into place, so a crash cannot truncate settings.</summary>
    private static void WriteAtomic(string path, string contents)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, contents);
        File.Move(temp, path, overwrite: true);
    }

    private void TryBackupCorruptFile()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var backup = AppPaths.SettingsFile + ".corrupt";
                File.Copy(AppPaths.SettingsFile, backup, overwrite: true);
                _logger.LogInformation("Kept a copy of the unreadable settings file at {Path}", backup);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not back up the unreadable settings file");
        }
    }
}
