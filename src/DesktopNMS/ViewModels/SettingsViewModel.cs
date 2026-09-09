using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using DesktopNMS.Core.Updates;
using DesktopNMS.Infrastructure;
using DesktopNMS.Services;

namespace DesktopNMS.ViewModels;

/// <summary>Which section of the settings dialog is showing.</summary>
public enum SettingsSection
{
    Polling,
    AlertDisplay,
    HealthThresholds,
    Notifications,
    Window,
    About,
}

/// <summary>A selectable device-naming option shown in the settings combo box.</summary>
public sealed class DeviceNameOption
{
    public DeviceNameOption(DeviceNameStyle value, string label, string description)
    {
        Value = value;
        Label = label;
        Description = description;
    }

    public DeviceNameStyle Value { get; }

    public string Label { get; }

    public string Description { get; }

    public override string ToString() => Label;
}

/// <summary>A selectable startup-tab option shown in the settings combo box.</summary>
public sealed class StartupTabOption
{
    public StartupTabOption(StartupTab value, string label)
    {
        Value = value;
        Label = label;
    }

    public StartupTab Value { get; }

    public string Label { get; }

    public override string ToString() => Label;
}

/// <summary>A selectable stickiness option shown in the settings combo boxes.</summary>
public sealed class PersistenceOption
{
    public PersistenceOption(ToastPersistence value, string label, string description)
    {
        Value = value;
        Label = label;
        Description = description;
    }

    public ToastPersistence Value { get; }

    public string Label { get; }

    public string Description { get; }

    public override string ToString() => Label;
}

/// <summary>
/// Settings dialog. Edits a copy so Cancel really cancels.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly ISettingsStore _store;
    private readonly IStartupRegistration _startup;
    private readonly IAlertNotificationService _notifications;
    private readonly IUpdateCheckService _updates;
    private readonly IWindowService _windows;
    private readonly AppSettings _draft;

    private SettingsSection _selectedSection = SettingsSection.Polling;
    private bool _isCheckingForUpdates;
    private string _updateStatusText = "Checking for updates...";
    private GitHubRelease? _latestRelease;
    private bool _isNewerVersionAvailable;

    public SettingsViewModel(
        ISettingsStore store,
        IStartupRegistration startup,
        IAlertNotificationService notifications,
        IUpdateCheckService updates,
        IWindowService windows)
    {
        _store = store;
        _startup = startup;
        _notifications = notifications;
        _updates = updates;
        _windows = windows;
        _draft = store.Current.Clone();

        // The registry is the source of truth for auto-start, not the settings file.
        _draft.StartWithWindows = startup.IsEnabled;

        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke(this, false));
        PreviewCriticalCommand = new RelayCommand(() => _notifications.ShowPreview(AlertSeverity.Critical));
        PreviewWarningCommand = new RelayCommand(() => _notifications.ShowPreview(AlertSeverity.Warning));

        SelectPollingSectionCommand = new RelayCommand(() => SelectedSection = SettingsSection.Polling);
        SelectAlertDisplaySectionCommand = new RelayCommand(() => SelectedSection = SettingsSection.AlertDisplay);
        SelectHealthThresholdsSectionCommand = new RelayCommand(() => SelectedSection = SettingsSection.HealthThresholds);
        SelectNotificationsSectionCommand = new RelayCommand(() => SelectedSection = SettingsSection.Notifications);
        SelectWindowSectionCommand = new RelayCommand(() => SelectedSection = SettingsSection.Window);
        SelectAboutSectionCommand = new RelayCommand(() => SelectedSection = SettingsSection.About);

        CheckForUpdatesCommand = new AsyncRelayCommand(() => CheckForUpdatesAsync(notifyIfNewer: false));
        ViewLatestReleaseCommand = new RelayCommand(
            () => _windows.OpenUrl(new Uri(_latestRelease!.HtmlUrl!)),
            () => _latestRelease?.HtmlUrl is not null);
        ViewReleasesPageCommand = new RelayCommand(
            () => _windows.OpenUrl(new Uri("https://github.com/DashyNMS/desktop/releases")));

        _ = CheckForUpdatesAsync(notifyIfNewer: false);
    }

    public event EventHandler<bool>? RequestClose;

    public RelayCommand SaveCommand { get; }

    public RelayCommand CancelCommand { get; }

    public RelayCommand PreviewCriticalCommand { get; }

    public RelayCommand PreviewWarningCommand { get; }

    // --------------------------------------------------------------- sections

    public RelayCommand SelectPollingSectionCommand { get; }

    public RelayCommand SelectAlertDisplaySectionCommand { get; }

    public RelayCommand SelectHealthThresholdsSectionCommand { get; }

    public RelayCommand SelectNotificationsSectionCommand { get; }

    public RelayCommand SelectWindowSectionCommand { get; }

    public RelayCommand SelectAboutSectionCommand { get; }

    public SettingsSection SelectedSection
    {
        get => _selectedSection;
        set
        {
            if (SetProperty(ref _selectedSection, value))
            {
                OnPropertyChanged(nameof(IsPollingSectionSelected));
                OnPropertyChanged(nameof(IsAlertDisplaySectionSelected));
                OnPropertyChanged(nameof(IsHealthThresholdsSectionSelected));
                OnPropertyChanged(nameof(IsNotificationsSectionSelected));
                OnPropertyChanged(nameof(IsWindowSectionSelected));
                OnPropertyChanged(nameof(IsAboutSectionSelected));
            }
        }
    }

    public bool IsPollingSectionSelected => SelectedSection == SettingsSection.Polling;

    public bool IsAlertDisplaySectionSelected => SelectedSection == SettingsSection.AlertDisplay;

    public bool IsHealthThresholdsSectionSelected => SelectedSection == SettingsSection.HealthThresholds;

    public bool IsNotificationsSectionSelected => SelectedSection == SettingsSection.Notifications;

    public bool IsWindowSectionSelected => SelectedSection == SettingsSection.Window;

    public bool IsAboutSectionSelected => SelectedSection == SettingsSection.About;

    // ------------------------------------------------------------------ about

    public AsyncRelayCommand CheckForUpdatesCommand { get; }

    public RelayCommand ViewLatestReleaseCommand { get; }

    public RelayCommand ViewReleasesPageCommand { get; }

    public string CurrentVersionText => $"Version {_updates.CurrentVersion}";

    public bool IsCheckingForUpdates
    {
        get => _isCheckingForUpdates;
        private set => SetProperty(ref _isCheckingForUpdates, value);
    }

    public string UpdateStatusText
    {
        get => _updateStatusText;
        private set => SetProperty(ref _updateStatusText, value);
    }

    public bool IsNewerVersionAvailable
    {
        get => _isNewerVersionAvailable;
        private set
        {
            if (SetProperty(ref _isNewerVersionAvailable, value))
            {
                ViewLatestReleaseCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasLatestRelease => _latestRelease is not null;

    public string? LatestReleaseTitle => _latestRelease is null
        ? null
        : string.IsNullOrWhiteSpace(_latestRelease.Name) ? _latestRelease.TagName : _latestRelease.Name;

    public string? LatestReleaseNotes => string.IsNullOrWhiteSpace(_latestRelease?.Body)
        ? "No release notes were provided for this version."
        : _latestRelease.Body;

    private async Task CheckForUpdatesAsync(bool notifyIfNewer)
    {
        IsCheckingForUpdates = true;
        UpdateStatusText = "Checking for updates...";

        var result = await _updates.CheckAsync(notifyIfNewer).ConfigureAwait(true);

        _latestRelease = result.LatestRelease;
        IsNewerVersionAvailable = result.IsNewerVersionAvailable;

        UpdateStatusText = !result.Succeeded
            ? "Could not check for updates. Check your internet connection."
            : result.IsNewerVersionAvailable
                ? $"Version {result.LatestRelease!.TagName} is available."
                : "You're up to date.";

        OnPropertyChanged(nameof(HasLatestRelease));
        OnPropertyChanged(nameof(LatestReleaseTitle));
        OnPropertyChanged(nameof(LatestReleaseNotes));
        ViewLatestReleaseCommand.RaiseCanExecuteChanged();

        IsCheckingForUpdates = false;
    }

    /// <summary>0-23, for the quiet-hours pickers.</summary>
    public IReadOnlyList<int> Hours { get; } = Enumerable.Range(0, 24).ToArray();

    public IReadOnlyList<StartupTabOption> StartupTabOptions { get; } = new[]
    {
        new StartupTabOption(StartupTab.Dashboard, "Dashboard"),
        new StartupTabOption(StartupTab.Devices, "Devices"),
        new StartupTabOption(StartupTab.Health, "Health"),
        new StartupTabOption(StartupTab.Alerts, "Alerts"),
    };

    public IReadOnlyList<DeviceNameOption> DeviceNameOptions { get; } = new[]
    {
        new DeviceNameOption(
            DeviceNameStyle.Hostname,
            "Hostname",
            "The polling address LibreNMS uses. Always available, because the alerts endpoint returns it."),
        new DeviceNameOption(
            DeviceNameStyle.SysName,
            "sysName",
            "The name the device reports over SNMP. Needs the device list, which DashyNMS fetches in the background."),
        new DeviceNameOption(
            DeviceNameStyle.DisplayName,
            "LibreNMS display name",
            "The display name configured in LibreNMS, falling back to sysName then hostname."),
    };

    public IReadOnlyList<PersistenceOption> PersistenceOptions { get; } = new[]
    {
        new PersistenceOption(
            ToastPersistence.Transient,
            "Fade after a few seconds",
            "Standard Windows behaviour: the toast shows briefly, then waits in the notification centre."),
        new PersistenceOption(
            ToastPersistence.UntilDismissed,
            "Stay on screen until dismissed",
            "The toast stays put until you act on it or close it."),
        new PersistenceOption(
            ToastPersistence.UntilDismissedWithAlarm,
            "Stay on screen with a repeating sound",
            "As above, plus a looping alarm tone. Hard to miss, and hard to ignore."),
    };

    // ------------------------------------------------------------- connection

    public int PollIntervalSeconds
    {
        get => _draft.PollIntervalSeconds;
        set
        {
            if (_draft.PollIntervalSeconds == value)
            {
                return;
            }

            _draft.PollIntervalSeconds = value;
            OnPropertyChanged();
        }
    }

    public int TimeoutSeconds
    {
        get => _draft.TimeoutSeconds;
        set
        {
            if (_draft.TimeoutSeconds == value)
            {
                return;
            }

            _draft.TimeoutSeconds = value;
            OnPropertyChanged();
        }
    }

    public bool AllowUntrustedCertificate
    {
        get => _draft.AllowUntrustedCertificate;
        set
        {
            if (_draft.AllowUntrustedCertificate == value)
            {
                return;
            }

            _draft.AllowUntrustedCertificate = value;
            OnPropertyChanged();
        }
    }

    public bool ServerTimestampsAreUtc
    {
        get => _draft.ServerTimestampsAreUtc;
        set
        {
            if (_draft.ServerTimestampsAreUtc == value)
            {
                return;
            }

            _draft.ServerTimestampsAreUtc = value;
            OnPropertyChanged();
        }
    }

    public DeviceNameOption DeviceName
    {
        get => FindDeviceName(_draft.DeviceNameStyle);
        set
        {
            if (value is null || _draft.DeviceNameStyle == value.Value)
            {
                return;
            }

            _draft.DeviceNameStyle = value.Value;
            OnPropertyChanged();
        }
    }

    public bool LoadAlertFaults
    {
        get => _draft.LoadAlertFaults;
        set
        {
            if (_draft.LoadAlertFaults == value)
            {
                return;
            }

            _draft.LoadAlertFaults = value;
            OnPropertyChanged();
        }
    }

    public bool IncludeRecoveredAlerts
    {
        get => _draft.IncludeRecoveredAlerts;
        set
        {
            if (_draft.IncludeRecoveredAlerts == value)
            {
                return;
            }

            _draft.IncludeRecoveredAlerts = value;
            OnPropertyChanged();
        }
    }

    // ------------------------------------------------------ health thresholds

    public double DbmWarningThreshold
    {
        get => _draft.DbmThresholds.WarningThreshold;
        set
        {
            if (_draft.DbmThresholds.WarningThreshold == value)
            {
                return;
            }

            _draft.DbmThresholds.WarningThreshold = value;
            OnPropertyChanged();
        }
    }

    public double DbmCriticalThreshold
    {
        get => _draft.DbmThresholds.CriticalThreshold;
        set
        {
            if (_draft.DbmThresholds.CriticalThreshold == value)
            {
                return;
            }

            _draft.DbmThresholds.CriticalThreshold = value;
            OnPropertyChanged();
        }
    }

    public double DbmIgnoreAtOrAbove
    {
        get => _draft.DbmThresholds.IgnoreAtOrAbove;
        set
        {
            if (_draft.DbmThresholds.IgnoreAtOrAbove == value)
            {
                return;
            }

            _draft.DbmThresholds.IgnoreAtOrAbove = value;
            OnPropertyChanged();
        }
    }

    public double DbmIgnoreAtOrBelow
    {
        get => _draft.DbmThresholds.IgnoreAtOrBelow;
        set
        {
            if (_draft.DbmThresholds.IgnoreAtOrBelow == value)
            {
                return;
            }

            _draft.DbmThresholds.IgnoreAtOrBelow = value;
            OnPropertyChanged();
        }
    }

    // --------------------------------------------------------------- window

    public bool MinimiseToTrayOnClose
    {
        get => _draft.MinimiseToTrayOnClose;
        set
        {
            if (_draft.MinimiseToTrayOnClose == value)
            {
                return;
            }

            _draft.MinimiseToTrayOnClose = value;
            OnPropertyChanged();
        }
    }

    public bool StartMinimised
    {
        get => _draft.StartMinimised;
        set
        {
            if (_draft.StartMinimised == value)
            {
                return;
            }

            _draft.StartMinimised = value;
            OnPropertyChanged();
        }
    }

    public bool StartWithWindows
    {
        get => _draft.StartWithWindows;
        set
        {
            if (_draft.StartWithWindows == value)
            {
                return;
            }

            _draft.StartWithWindows = value;
            OnPropertyChanged();
        }
    }

    public StartupTabOption SelectedStartupTab
    {
        get => FindStartupTab(_draft.StartupTab);
        set
        {
            if (value is null || _draft.StartupTab == value.Value)
            {
                return;
            }

            _draft.StartupTab = value.Value;
            OnPropertyChanged();
        }
    }

    // -------------------------------------------------------- notifications

    public bool NotificationsEnabled
    {
        get => _draft.Notifications.Enabled;
        set
        {
            if (_draft.Notifications.Enabled == value)
            {
                return;
            }

            _draft.Notifications.Enabled = value;
            OnPropertyChanged();
        }
    }

    public bool CriticalEnabled
    {
        get => _draft.Notifications.Critical.Enabled;
        set
        {
            if (_draft.Notifications.Critical.Enabled == value)
            {
                return;
            }

            _draft.Notifications.Critical.Enabled = value;
            OnPropertyChanged();
        }
    }

    public PersistenceOption CriticalPersistence
    {
        get => Find(_draft.Notifications.Critical.Persistence);
        set
        {
            if (value is null || _draft.Notifications.Critical.Persistence == value.Value)
            {
                return;
            }

            _draft.Notifications.Critical.Persistence = value.Value;
            OnPropertyChanged();
        }
    }

    public bool CriticalPlaySound
    {
        get => _draft.Notifications.Critical.PlaySound;
        set
        {
            if (_draft.Notifications.Critical.PlaySound == value)
            {
                return;
            }

            _draft.Notifications.Critical.PlaySound = value;
            OnPropertyChanged();
        }
    }

    public bool WarningEnabled
    {
        get => _draft.Notifications.Warning.Enabled;
        set
        {
            if (_draft.Notifications.Warning.Enabled == value)
            {
                return;
            }

            _draft.Notifications.Warning.Enabled = value;
            OnPropertyChanged();
        }
    }

    public PersistenceOption WarningPersistence
    {
        get => Find(_draft.Notifications.Warning.Persistence);
        set
        {
            if (value is null || _draft.Notifications.Warning.Persistence == value.Value)
            {
                return;
            }

            _draft.Notifications.Warning.Persistence = value.Value;
            OnPropertyChanged();
        }
    }

    public bool WarningPlaySound
    {
        get => _draft.Notifications.Warning.PlaySound;
        set
        {
            if (_draft.Notifications.Warning.PlaySound == value)
            {
                return;
            }

            _draft.Notifications.Warning.PlaySound = value;
            OnPropertyChanged();
        }
    }

    public bool NotifyOnRecovery
    {
        get => _draft.Notifications.NotifyOnRecovery;
        set
        {
            if (_draft.Notifications.NotifyOnRecovery == value)
            {
                return;
            }

            _draft.Notifications.NotifyOnRecovery = value;
            OnPropertyChanged();
        }
    }

    public bool NotifyOnAcknowledge
    {
        get => _draft.Notifications.NotifyOnAcknowledge;
        set
        {
            if (_draft.Notifications.NotifyOnAcknowledge == value)
            {
                return;
            }

            _draft.Notifications.NotifyOnAcknowledge = value;
            OnPropertyChanged();
        }
    }

    public bool SuppressOnFirstPoll
    {
        get => _draft.Notifications.SuppressOnFirstPoll;
        set
        {
            if (_draft.Notifications.SuppressOnFirstPoll == value)
            {
                return;
            }

            _draft.Notifications.SuppressOnFirstPoll = value;
            OnPropertyChanged();
        }
    }

    public int MaxToastsPerPoll
    {
        get => _draft.Notifications.MaxToastsPerPoll;
        set
        {
            if (_draft.Notifications.MaxToastsPerPoll == value)
            {
                return;
            }

            _draft.Notifications.MaxToastsPerPoll = value;
            OnPropertyChanged();
        }
    }

    public bool QuietHoursEnabled
    {
        get => _draft.Notifications.QuietHoursEnabled;
        set
        {
            if (_draft.Notifications.QuietHoursEnabled == value)
            {
                return;
            }

            _draft.Notifications.QuietHoursEnabled = value;
            OnPropertyChanged();
        }
    }

    public int QuietHoursStartHour
    {
        get => _draft.Notifications.QuietHoursStartHour;
        set
        {
            if (_draft.Notifications.QuietHoursStartHour == value)
            {
                return;
            }

            _draft.Notifications.QuietHoursStartHour = value;
            OnPropertyChanged();
        }
    }

    public int QuietHoursEndHour
    {
        get => _draft.Notifications.QuietHoursEndHour;
        set
        {
            if (_draft.Notifications.QuietHoursEndHour == value)
            {
                return;
            }

            _draft.Notifications.QuietHoursEndHour = value;
            OnPropertyChanged();
        }
    }

    public bool QuietHoursAllowCritical
    {
        get => _draft.Notifications.QuietHoursAllowCritical;
        set
        {
            if (_draft.Notifications.QuietHoursAllowCritical == value)
            {
                return;
            }

            _draft.Notifications.QuietHoursAllowCritical = value;
            OnPropertyChanged();
        }
    }

    private StartupTabOption FindStartupTab(StartupTab value)
    {
        foreach (var option in StartupTabOptions)
        {
            if (option.Value == value)
            {
                return option;
            }
        }

        return StartupTabOptions[0];
    }

    private DeviceNameOption FindDeviceName(DeviceNameStyle value)
    {
        foreach (var option in DeviceNameOptions)
        {
            if (option.Value == value)
            {
                return option;
            }
        }

        return DeviceNameOptions[0];
    }

    private PersistenceOption Find(ToastPersistence value)
    {
        foreach (var option in PersistenceOptions)
        {
            if (option.Value == value)
            {
                return option;
            }
        }

        return PersistenceOptions[0];
    }

    private void Save()
    {
        _startup.SetEnabled(_draft.StartWithWindows);

        // Keep the window placement and filter chips the running app has, rather
        // than the copies taken when this dialog opened.
        _draft.Window = _store.Current.Window;
        _draft.Filter = _store.Current.Filter;

        _store.Replace(_draft);
        RequestClose?.Invoke(this, true);
    }
}
