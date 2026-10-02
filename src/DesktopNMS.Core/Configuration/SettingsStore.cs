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

    /// <summary>
    /// Writes <see cref="Current"/> to disk without raising <see cref="Changed"/> -
    /// for a display preference only the thing that set it cares about (a
    /// folded panel or sidebar group), so saving it doesn't set every
    /// listener re-applying settings that haven't changed.
    /// </summary>
    void SaveQuietly();

    /// <summary>Swaps in a new settings object and persists it.</summary>
    void Replace(AppSettings settings);
}

/// <summary>JSON-backed settings, by default in %APPDATA%\DashyNMS\settings.json.</summary>
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
    private readonly string _settingsFile;
    private readonly object _sync = new();

    private AppSettings _current = new();
    private bool _loaded;

    public SettingsStore(ILogger<SettingsStore> logger)
        : this(logger, AppPaths.SettingsFile)
    {
    }

    /// <summary>Uses <paramref name="settingsFile"/> instead of the per-user default - for tests, and for hosts that keep their data elsewhere.</summary>
    public SettingsStore(ILogger<SettingsStore> logger, string settingsFile)
    {
        _logger = logger;
        _settingsFile = settingsFile;
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
                if (File.Exists(_settingsFile))
                {
                    var json = File.ReadAllText(_settingsFile);
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
                _logger.LogWarning(ex, "Could not read {Path}; falling back to defaults", _settingsFile);
                TryBackupCorruptFile();
            }

            _current = new AppSettings();
            _current.Normalise();
            return _current;
        }
    }

    public void SaveQuietly() => Write();

    /// <remarks>
    /// Writes first, then raises <see cref="Changed"/>. Folding the write into the
    /// event call (<c>Changed?.Invoke(this, Write())</c>) skipped the write
    /// entirely whenever nothing was subscribed (#191).
    /// </remarks>
    public void Save()
    {
        var saved = Write();
        Changed?.Invoke(this, saved);
    }

    private AppSettings Write()
    {
        AppSettings snapshot;

        lock (_sync)
        {
            _current.Normalise();
            snapshot = _current;

            try
            {
                var json = JsonSerializer.Serialize(snapshot, SerializerOptions);
                WriteAtomic(_settingsFile, json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not write {Path}", _settingsFile);
            }
        }

        return snapshot;
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
            if (File.Exists(_settingsFile))
            {
                var backup = _settingsFile + ".corrupt";
                File.Copy(_settingsFile, backup, overwrite: true);
                _logger.LogInformation("Kept a copy of the unreadable settings file at {Path}", backup);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not back up the unreadable settings file");
        }
    }
}
