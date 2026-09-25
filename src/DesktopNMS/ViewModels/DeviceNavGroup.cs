using System;
using System.Linq;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Infrastructure;

namespace DesktopNMS.ViewModels;

/// <summary>
/// One foldable group in the Device Details sidebar (Health, Hardware,
/// Network, Alerts &amp; logs, Integrations). Which groups are folded is
/// remembered in settings and shared by every device window; the group
/// holding the section that's open never folds, so the highlighted item is
/// always visible.
/// </summary>
public sealed class DeviceNavGroup : ObservableObject
{
    public const string Health = "health";
    public const string Hardware = "hardware";
    public const string Network = "network";
    public const string Logs = "logs";
    public const string Integrations = "integrations";

    private readonly ISettingsStore _settings;
    private readonly Func<bool> _containsSelection;

    public DeviceNavGroup(string key, ISettingsStore settings, Func<bool> containsSelection)
    {
        Key = key;
        _settings = settings;
        _containsSelection = containsSelection;
        ToggleCommand = new RelayCommand(Toggle);
    }

    public string Key { get; }

    public RelayCommand ToggleCommand { get; }

    /// <summary>Folded away by the user - see <see cref="IsExpanded"/> for whether it actually shows its items.</summary>
    public bool IsCollapsed => _settings.Current.CollapsedDeviceNavGroups.Contains(Key, StringComparer.Ordinal);

    public bool IsExpanded => !IsCollapsed || _containsSelection();

    /// <summary>Segoe chevrons: down when open, right when folded.</summary>
    public string ExpanderGlyph => IsExpanded ? "" : "";

    /// <summary>The section just changed, or settings did - recompute.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(IsCollapsed));
        OnPropertyChanged(nameof(IsExpanded));
        OnPropertyChanged(nameof(ExpanderGlyph));
    }

    private void Toggle()
    {
        // The open section's group can't fold - clicking it does nothing
        // rather than appearing to fold and springing straight back open.
        if (_containsSelection())
        {
            return;
        }

        var collapsed = _settings.Current.CollapsedDeviceNavGroups;
        if (!collapsed.Remove(Key))
        {
            collapsed.Add(Key);
        }

        // A display preference nothing else reacts to - see SaveQuietly.
        _settings.SaveQuietly();
        Refresh();
    }
}
