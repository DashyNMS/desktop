using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.CustomMaps;
using DesktopNMS.Core.Graylog;
using DesktopNMS.Core.Models;
using DesktopNMS.Core.Security;
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
    Devices,
    Maps,
    Window,
    Appearance,
    Server,
    Integrations,
    Graylog,
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

/// <summary>A choice in Settings → Maps → Default map.</summary>
public sealed record DefaultMapOption(string Value, string Label)
{
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
    private readonly ICustomMapStore _customMaps;
    private readonly IStartupRegistration _startup;
    private readonly IAlertNotificationService _notifications;
    private readonly IUpdateCheckService _updates;
    private readonly IWindowService _windows;
    private readonly ISessionService _session;
    private readonly ILibreNmsClient _client;
    private readonly IUnimusApi _unimus;
    private readonly IUnimusTokenProtector _unimusTokens;
    private readonly IUnimusDeviceResolver _unimusResolver;
    private readonly IGraylogApi _graylog;
    private readonly IGraylogPasswordProtector _graylogPasswords;
    private readonly AppSettings _draft;

    private SettingsSection _selectedSection = SettingsSection.Polling;
    private bool _isCheckingForUpdates;
    private string _updateStatusText = "Checking for updates...";
    private GitHubRelease? _latestRelease;
    private bool _isNewerVersionAvailable;
    private SystemInfo? _serverInfo;
    private bool _isRefreshingServerInfo;
    private string? _serverInfoStatusText;

    private bool _hasStoredUnimusToken;
    private string _unimusTokenInput = string.Empty;
    private bool _isTestingUnimusConnection;
    private string? _unimusTestStatusText;
    private bool? _unimusTestSucceeded;

    private bool _hasStoredGraylogPassword;
    private string _graylogPasswordInput = string.Empty;
    private bool _isTestingGraylogConnection;
    private string? _graylogTestStatusText;
    private bool? _graylogTestSucceeded;

    public SettingsViewModel(
        ISettingsStore store,
        IStartupRegistration startup,
        IAlertNotificationService notifications,
        IUpdateCheckService updates,
        IWindowService windows,
        ISessionService session,
        ILibreNmsClient client,
        IUnimusApi unimus,
        IUnimusTokenProtector unimusTokens,
        IUnimusDeviceResolver unimusResolver,
        IGraylogApi graylog,
        IGraylogPasswordProtector graylogPasswords,
        ICustomMapStore customMaps)
    {
        _store = store;
        _customMaps = customMaps;
        _startup = startup;
        _notifications = notifications;
        _updates = updates;
        _windows = windows;
        _session = session;
        _client = client;
        _unimus = unimus;
        _unimusTokens = unimusTokens;
        _unimusResolver = unimusResolver;
        _graylog = graylog;
        _graylogPasswords = graylogPasswords;
        _serverInfo = session.ServerInfo;
        _draft = store.Current.Clone();
        _hasStoredUnimusToken = unimusTokens.HasStoredToken;
        _hasStoredGraylogPassword = graylogPasswords.HasStoredPassword;

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
        SelectDevicesSectionCommand = new RelayCommand(() => SelectedSection = SettingsSection.Devices);
        SelectWindowSectionCommand = new RelayCommand(() => SelectedSection = SettingsSection.Window);
        SelectAppearanceSectionCommand = new RelayCommand(() => SelectedSection = SettingsSection.Appearance);
        SelectServerSectionCommand = new RelayCommand(() => SelectedSection = SettingsSection.Server);
        SelectIntegrationsSectionCommand = new RelayCommand(() => SelectedSection = SettingsSection.Integrations);
        SelectGraylogSectionCommand = new RelayCommand(() => SelectedSection = SettingsSection.Graylog);
        SelectMapsSectionCommand = new RelayCommand(() => SelectedSection = SettingsSection.Maps);
        ResetMapTileUrlCommand = new RelayCommand(() => MapTileUrl = null);
        SelectAboutSectionCommand = new RelayCommand(() => SelectedSection = SettingsSection.About);

        RefreshServerInfoCommand = new AsyncRelayCommand(RefreshServerInfoAsync, () => !IsRefreshingServerInfo);

        CheckForUpdatesCommand = new AsyncRelayCommand(() => CheckForUpdatesAsync(notifyIfNewer: false));
        ViewLatestReleaseCommand = new RelayCommand(
            () => _windows.OpenUrl(new Uri(_latestRelease!.HtmlUrl!)),
            () => _latestRelease?.HtmlUrl is not null);
        ViewReleasesPageCommand = new RelayCommand(
            () => _windows.OpenUrl(new Uri("https://github.com/DashyNMS/desktop/releases")));
        ReportBugCommand = new RelayCommand(
            () => _windows.OpenUrl(BugReportLink.Build(
                _updates.CurrentVersion.ToString(),
                System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                _serverInfo?.LocalVersion)));

        TestUnimusConnectionCommand = new AsyncRelayCommand(TestUnimusConnectionAsync, () => !IsTestingUnimusConnection && !string.IsNullOrWhiteSpace(UnimusUrl));
        ClearUnimusTokenCommand = new RelayCommand(ClearUnimusToken, () => HasStoredUnimusToken || !string.IsNullOrEmpty(UnimusTokenInput));
        TestGraylogConnectionCommand = new AsyncRelayCommand(TestGraylogConnectionAsync, () => !IsTestingGraylogConnection && !string.IsNullOrWhiteSpace(GraylogServer));
        ClearGraylogPasswordCommand = new RelayCommand(ClearGraylogPassword, () => HasStoredGraylogPassword || !string.IsNullOrEmpty(GraylogPasswordInput));

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

    public RelayCommand SelectDevicesSectionCommand { get; }

    public RelayCommand SelectWindowSectionCommand { get; }

    public RelayCommand SelectAppearanceSectionCommand { get; }

    public RelayCommand SelectServerSectionCommand { get; }

    public RelayCommand SelectIntegrationsSectionCommand { get; }

    public RelayCommand SelectGraylogSectionCommand { get; }

    public RelayCommand SelectMapsSectionCommand { get; }

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
                OnPropertyChanged(nameof(IsDevicesSectionSelected));
                OnPropertyChanged(nameof(IsWindowSectionSelected));
                OnPropertyChanged(nameof(IsAppearanceSectionSelected));
                OnPropertyChanged(nameof(IsServerSectionSelected));
                OnPropertyChanged(nameof(IsIntegrationsSectionSelected));
                OnPropertyChanged(nameof(IsGraylogSectionSelected));
                OnPropertyChanged(nameof(IsMapsSectionSelected));
                OnPropertyChanged(nameof(IsAboutSectionSelected));
            }
        }
    }

    public bool IsPollingSectionSelected => SelectedSection == SettingsSection.Polling;

    public bool IsAlertDisplaySectionSelected => SelectedSection == SettingsSection.AlertDisplay;

    public bool IsHealthThresholdsSectionSelected => SelectedSection == SettingsSection.HealthThresholds;

    public bool IsNotificationsSectionSelected => SelectedSection == SettingsSection.Notifications;

    public bool IsDevicesSectionSelected => SelectedSection == SettingsSection.Devices;

    public bool IsWindowSectionSelected => SelectedSection == SettingsSection.Window;

    public bool IsAppearanceSectionSelected => SelectedSection == SettingsSection.Appearance;

    public bool IsServerSectionSelected => SelectedSection == SettingsSection.Server;

    public bool IsIntegrationsSectionSelected => SelectedSection == SettingsSection.Integrations;

    public bool IsGraylogSectionSelected => SelectedSection == SettingsSection.Graylog;

    public bool IsMapsSectionSelected => SelectedSection == SettingsSection.Maps;

    public bool IsAboutSectionSelected => SelectedSection == SettingsSection.About;

    // ------------------------------------------------------------- appearance

    public bool IsDarkTheme
    {
        get => _draft.Theme == AppTheme.Dark;
        set
        {
            if (value)
            {
                SetDraftTheme(AppTheme.Dark);
            }
        }
    }

    public bool IsLightTheme
    {
        get => _draft.Theme == AppTheme.Light;
        set
        {
            if (value)
            {
                SetDraftTheme(AppTheme.Light);
            }
        }
    }

    /// <summary>"Match Windows" (#81) - resolved each time DashyNMS starts.</summary>
    public bool IsSystemTheme
    {
        get => _draft.Theme == AppTheme.System;
        set
        {
            if (value)
            {
                SetDraftTheme(AppTheme.System);
            }
        }
    }

    /// <summary>
    /// Only true when the palette the draft would give differs from the one
    /// running - so the notice doesn't show before anything has changed, or
    /// for switching to "Match Windows" when Windows already matches.
    /// </summary>
    public bool ThemeChangeRequiresRestart => _draft.Theme.Resolve(ThemeState.WindowsUsesLightTheme()) != ThemeState.Effective;

    private void SetDraftTheme(AppTheme theme)
    {
        if (_draft.Theme == theme)
        {
            return;
        }

        _draft.Theme = theme;
        OnPropertyChanged(nameof(IsDarkTheme));
        OnPropertyChanged(nameof(IsLightTheme));
        OnPropertyChanged(nameof(IsSystemTheme));
        OnPropertyChanged(nameof(ThemeChangeRequiresRestart));
    }

    /// <summary>"#RRGGBB" - kept in sync with <see cref="AccentRed"/>/<see cref="AccentGreen"/>/<see cref="AccentBlue"/> and the live preview swatch.</summary>
    public string AccentColorHex
    {
        get => _draft.AccentColor;
        set
        {
            if (AccentTheme.TryParseColor(value, out var color))
            {
                SetDraftAccentColor(color);
            }

            // An unparsable in-progress value (e.g. "#3B") is left alone
            // rather than reverted, so the text box does not fight someone
            // mid-keystroke.
        }
    }

    public byte AccentRed
    {
        get => CurrentAccentColor.R;
        set => SetDraftAccentColor(Color.FromRgb(value, CurrentAccentColor.G, CurrentAccentColor.B));
    }

    public byte AccentGreen
    {
        get => CurrentAccentColor.G;
        set => SetDraftAccentColor(Color.FromRgb(CurrentAccentColor.R, value, CurrentAccentColor.B));
    }

    public byte AccentBlue
    {
        get => CurrentAccentColor.B;
        set => SetDraftAccentColor(Color.FromRgb(CurrentAccentColor.R, CurrentAccentColor.G, value));
    }

    /// <summary>
    /// Typed-entry counterpart to <see cref="AccentRed"/>/<see cref="AccentGreen"/>/
    /// <see cref="AccentBlue"/> for the text box next to each slider - same
    /// "leave an unparsable in-progress value alone" behaviour as
    /// <see cref="AccentColorHex"/>, rather than fighting someone mid-keystroke
    /// or throwing on an out-of-range number.
    /// </summary>
    public string AccentRedText
    {
        get => AccentRed.ToString(CultureInfo.InvariantCulture);
        set
        {
            if (TryParseChannel(value, out var channel))
            {
                AccentRed = channel;
            }
        }
    }

    public string AccentGreenText
    {
        get => AccentGreen.ToString(CultureInfo.InvariantCulture);
        set
        {
            if (TryParseChannel(value, out var channel))
            {
                AccentGreen = channel;
            }
        }
    }

    public string AccentBlueText
    {
        get => AccentBlue.ToString(CultureInfo.InvariantCulture);
        set
        {
            if (TryParseChannel(value, out var channel))
            {
                AccentBlue = channel;
            }
        }
    }

    private static bool TryParseChannel(string? text, out byte value)
        => byte.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    /// <summary>A live swatch for the settings dialog itself - the rest of the app only repaints once Save applies the draft.</summary>
    public Brush AccentPreviewBrush => new SolidColorBrush(CurrentAccentColor);

    private Color CurrentAccentColor =>
        AccentTheme.TryParseColor(_draft.AccentColor, out var color) ? color : Colors.DodgerBlue;

    private void SetDraftAccentColor(Color color)
    {
        var hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        if (string.Equals(_draft.AccentColor, hex, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _draft.AccentColor = hex;
        OnPropertyChanged(nameof(AccentColorHex));
        OnPropertyChanged(nameof(AccentRed));
        OnPropertyChanged(nameof(AccentGreen));
        OnPropertyChanged(nameof(AccentBlue));
        OnPropertyChanged(nameof(AccentRedText));
        OnPropertyChanged(nameof(AccentGreenText));
        OnPropertyChanged(nameof(AccentBlueText));
        OnPropertyChanged(nameof(AccentPreviewBrush));
    }

    /// <summary>Off shows DashyNMS's own icon in the shell header instead of the connected server's logo - see <see cref="AppSettings.ShowServerLogo"/>.</summary>
    public bool ShowServerLogo
    {
        get => _draft.ShowServerLogo;
        set
        {
            if (_draft.ShowServerLogo == value)
            {
                return;
            }

            _draft.ShowServerLogo = value;
            OnPropertyChanged();
        }
    }

    // ----------------------------------------------------------------- server

    public AsyncRelayCommand RefreshServerInfoCommand { get; }

    public string ServerUrlText => _session.Connection?.WebRoot.ToString() ?? "-";

    /// <summary>
    /// False only if the very first fetch (at sign-in) somehow never
    /// completed - Settings can still be opened while signed out, and this
    /// keeps the section from showing a wall of "-" in that case.
    /// </summary>
    public bool HasServerInfo => _serverInfo is not null;

    public string ServerVersionText => Blank(_serverInfo?.LocalVersion);

    public string ServerBranchText => Blank(_serverInfo?.LocalBranch);

    /// <summary>Shortened to the first 10 characters, matching how GitHub and most git tooling abbreviate a commit SHA.</summary>
    public string ServerCommitText
    {
        get
        {
            var sha = _serverInfo?.LocalCommit;
            return string.IsNullOrWhiteSpace(sha) ? "-" : sha.Length > 10 ? sha[..10] : sha;
        }
    }

    public string ServerDateText => Blank(_serverInfo?.LocalDate);

    public string DatabaseVersionText => Blank(_serverInfo?.DatabaseVersion);

    public string DatabaseSchemaText => Blank(_serverInfo?.DatabaseSchema);

    public string PhpVersionText => Blank(_serverInfo?.PhpVersion);

    public string PythonVersionText => Blank(_serverInfo?.PythonVersion);

    public string RrdToolVersionText => Blank(_serverInfo?.RrdToolVersion);

    public string NetSnmpVersionText => Blank(_serverInfo?.NetSnmpVersion);

    public bool IsRefreshingServerInfo
    {
        get => _isRefreshingServerInfo;
        private set
        {
            if (SetProperty(ref _isRefreshingServerInfo, value))
            {
                RefreshServerInfoCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Only ever an error - a successful refresh just updates the fields above silently, matching how the rest of this dialog does not narrate its own successes.</summary>
    public string? ServerInfoStatusText
    {
        get => _serverInfoStatusText;
        private set => SetProperty(ref _serverInfoStatusText, value);
    }

    public bool HasServerInfoStatus => ServerInfoStatusText is not null;

    /// <summary>
    /// Settings resolves as a fresh instance every time the dialog opens
    /// (see App.xaml.cs), so this section already shows whatever
    /// ISessionService captured at sign-in without needing a live call of
    /// its own - this exists for the case that matters: confirming a server
    /// upgrade without having to close Settings, sign out, and back in.
    /// </summary>
    private async Task RefreshServerInfoAsync()
    {
        IsRefreshingServerInfo = true;
        ServerInfoStatusText = null;

        try
        {
            _serverInfo = await _client.System.GetAsync().ConfigureAwait(true);
        }
        catch (LibreNmsApiException ex)
        {
            ServerInfoStatusText = ex.ToUserMessage();
        }
        catch (Exception)
        {
            ServerInfoStatusText = "Could not refresh the server's info. Check your connection.";
        }
        finally
        {
            IsRefreshingServerInfo = false;
        }

        OnPropertyChanged(nameof(HasServerInfo));
        OnPropertyChanged(nameof(ServerVersionText));
        OnPropertyChanged(nameof(ServerBranchText));
        OnPropertyChanged(nameof(ServerCommitText));
        OnPropertyChanged(nameof(ServerDateText));
        OnPropertyChanged(nameof(DatabaseVersionText));
        OnPropertyChanged(nameof(DatabaseSchemaText));
        OnPropertyChanged(nameof(PhpVersionText));
        OnPropertyChanged(nameof(PythonVersionText));
        OnPropertyChanged(nameof(RrdToolVersionText));
        OnPropertyChanged(nameof(NetSnmpVersionText));
        OnPropertyChanged(nameof(HasServerInfoStatus));
    }

    private static string Blank(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value!;

    // ------------------------------------------------------------------ integrations (Unimus, issue #115)

    public bool UnimusEnabled
    {
        get => _draft.Unimus.Enabled;
        set
        {
            if (_draft.Unimus.Enabled == value)
            {
                return;
            }

            _draft.Unimus.Enabled = value;
            OnPropertyChanged();
        }
    }

    public string? UnimusUrl
    {
        get => _draft.Unimus.Url;
        set
        {
            if (_draft.Unimus.Url == value)
            {
                return;
            }

            _draft.Unimus.Url = value;
            OnPropertyChanged();
            TestUnimusConnectionCommand.RaiseCanExecuteChanged();
        }
    }

    public bool UnimusAllowUntrustedCertificate
    {
        get => _draft.Unimus.AllowUntrustedCertificate;
        set
        {
            if (_draft.Unimus.AllowUntrustedCertificate == value)
            {
                return;
            }

            _draft.Unimus.AllowUntrustedCertificate = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// LibreNMS's own discovery domain suffix - see
    /// <see cref="UnimusSettings.MyDomain"/> for why this has to be entered
    /// here rather than read from LibreNMS itself.
    /// </summary>
    public string? UnimusMyDomain
    {
        get => _draft.Unimus.MyDomain;
        set
        {
            if (_draft.Unimus.MyDomain == value)
            {
                return;
            }

            _draft.Unimus.MyDomain = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Fed by the token PasswordBox's code-behind (PasswordBox has no
    /// bindable Password, the same reason ConnectionWindow's own token field
    /// works this way). Left blank on save keeps whatever token is already
    /// stored - see <see cref="Save"/> - so reopening Settings never forces
    /// re-entering a token that's already working.
    /// </summary>
    public string UnimusTokenInput
    {
        get => _unimusTokenInput;
        set
        {
            if (SetProperty(ref _unimusTokenInput, value ?? string.Empty))
            {
                ClearUnimusTokenCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasStoredUnimusToken
    {
        get => _hasStoredUnimusToken;
        private set
        {
            if (SetProperty(ref _hasStoredUnimusToken, value))
            {
                OnPropertyChanged(nameof(UnimusTokenStatusText));
                ClearUnimusTokenCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string UnimusTokenStatusText => HasStoredUnimusToken ? "A token is saved." : "No token saved yet.";

    public RelayCommand ClearUnimusTokenCommand { get; }

    private void ClearUnimusToken()
    {
        UnimusTokenInput = string.Empty;
        HasStoredUnimusToken = false;
        _unimusTokens.Clear();
        _unimus.Clear();
        _unimusResolver.Clear();
        UnimusTestStatusText = null;
    }

    public AsyncRelayCommand TestUnimusConnectionCommand { get; }

    public bool IsTestingUnimusConnection
    {
        get => _isTestingUnimusConnection;
        private set
        {
            if (SetProperty(ref _isTestingUnimusConnection, value))
            {
                TestUnimusConnectionCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string? UnimusTestStatusText
    {
        get => _unimusTestStatusText;
        private set
        {
            if (SetProperty(ref _unimusTestStatusText, value))
            {
                OnPropertyChanged(nameof(HasUnimusTestStatus));
            }
        }
    }

    public bool HasUnimusTestStatus => !string.IsNullOrEmpty(_unimusTestStatusText);

    /// <summary>Null before a test has run, then whether the last one succeeded - drives the status text's colour.</summary>
    public bool? UnimusTestSucceeded
    {
        get => _unimusTestSucceeded;
        private set => SetProperty(ref _unimusTestSucceeded, value);
    }

    /// <summary>
    /// Tries the URL/token currently in the form - whatever is typed in
    /// <see cref="UnimusTokenInput"/>, or the already-stored token if that's
    /// blank - without touching the live <see cref="IUnimusApi"/> singleton
    /// until Save is actually clicked.
    /// </summary>
    private async Task TestUnimusConnectionAsync()
    {
        UnimusTestStatusText = null;

        if (!UnimusConnection.TryParseWebRoot(UnimusUrl, out var webRoot, out var urlError) || webRoot is null)
        {
            UnimusTestSucceeded = false;
            UnimusTestStatusText = urlError;
            return;
        }

        var token = string.IsNullOrEmpty(UnimusTokenInput) ? _unimusTokens.Load() : UnimusTokenInput;
        if (string.IsNullOrWhiteSpace(token))
        {
            UnimusTestSucceeded = false;
            UnimusTestStatusText = "Enter an API token first.";
            return;
        }

        IsTestingUnimusConnection = true;

        using var probe = new UnimusApi(Microsoft.Extensions.Logging.Abstractions.NullLogger<UnimusApi>.Instance);
        probe.Configure(new UnimusConnection(webRoot, token, UnimusAllowUntrustedCertificate));

        try
        {
            await probe.TestConnectionAsync().ConfigureAwait(true);
            UnimusTestSucceeded = true;
            UnimusTestStatusText = "Connected to Unimus successfully.";
        }
        catch (UnimusApiException ex)
        {
            UnimusTestSucceeded = false;
            UnimusTestStatusText = ex.ToUserMessage();
        }
        finally
        {
            IsTestingUnimusConnection = false;
        }
    }

    // ------------------------------------------------------------------ integrations (Graylog, issue #114)

    /// <summary>LibreNMS's <c>graylog.version</c> choices, with its own labels.</summary>
    public IReadOnlyList<DefaultMapOption> GraylogVersionOptions { get; } = new[]
    {
        new DefaultMapOption(GraylogSettings.Version21, "2.1 or newer"),
        new DefaultMapOption(GraylogSettings.Version20, "Less than 2.1"),
        new DefaultMapOption(GraylogSettings.VersionOther, "Other"),
    };

    /// <summary>LibreNMS's <c>graylog.device-page.loglevel</c> choices - each includes every more severe level.</summary>
    public IReadOnlyList<GraylogLevelOption> GraylogLogLevelOptions { get; } = GraylogLevelOption.All;

    public bool GraylogEnabled
    {
        get => _draft.Graylog.Enabled;
        set
        {
            if (_draft.Graylog.Enabled == value)
            {
                return;
            }

            _draft.Graylog.Enabled = value;
            OnPropertyChanged();
        }
    }

    public string? GraylogServer
    {
        get => _draft.Graylog.Server;
        set
        {
            if (_draft.Graylog.Server == value)
            {
                return;
            }

            _draft.Graylog.Server = value;
            OnPropertyChanged();
            TestGraylogConnectionCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Text so it can be blank (the scheme's default port); anything that isn't a whole number counts as blank.</summary>
    public string GraylogPortText
    {
        get => _draft.Graylog.Port?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        set
        {
            int? port = int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
            if (_draft.Graylog.Port == port)
            {
                return;
            }

            _draft.Graylog.Port = port;
            OnPropertyChanged();
        }
    }

    public DefaultMapOption SelectedGraylogVersion
    {
        get => GraylogVersionOptions.FirstOrDefault(o => o.Value == _draft.Graylog.Version) ?? GraylogVersionOptions[0];
        set
        {
            if (value is null || _draft.Graylog.Version == value.Value)
            {
                return;
            }

            _draft.Graylog.Version = value.Value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsGraylogBaseUriVisible));
        }
    }

    /// <summary>LibreNMS only shows Base URI when the version is "Other".</summary>
    public bool IsGraylogBaseUriVisible => _draft.Graylog.Version == GraylogSettings.VersionOther;

    public string? GraylogBaseUri
    {
        get => _draft.Graylog.BaseUri;
        set
        {
            if (_draft.Graylog.BaseUri == value)
            {
                return;
            }

            _draft.Graylog.BaseUri = value;
            OnPropertyChanged();
        }
    }

    public string? GraylogUsername
    {
        get => _draft.Graylog.Username;
        set
        {
            if (_draft.Graylog.Username == value)
            {
                return;
            }

            _draft.Graylog.Username = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Fed by the password PasswordBox's code-behind, like <see cref="UnimusTokenInput"/>; blank on save keeps the stored password.</summary>
    public string GraylogPasswordInput
    {
        get => _graylogPasswordInput;
        set
        {
            if (SetProperty(ref _graylogPasswordInput, value ?? string.Empty))
            {
                ClearGraylogPasswordCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasStoredGraylogPassword
    {
        get => _hasStoredGraylogPassword;
        private set
        {
            if (SetProperty(ref _hasStoredGraylogPassword, value))
            {
                OnPropertyChanged(nameof(GraylogPasswordStatusText));
                ClearGraylogPasswordCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string GraylogPasswordStatusText => HasStoredGraylogPassword ? "A password is saved." : "No password saved yet.";

    public RelayCommand ClearGraylogPasswordCommand { get; }

    private void ClearGraylogPassword()
    {
        GraylogPasswordInput = string.Empty;
        HasStoredGraylogPassword = false;
        _graylogPasswords.Clear();
        _graylog.Clear();
        GraylogTestStatusText = null;
    }

    public bool GraylogAllowUntrustedCertificate
    {
        get => _draft.Graylog.AllowUntrustedCertificate;
        set
        {
            if (_draft.Graylog.AllowUntrustedCertificate == value)
            {
                return;
            }

            _draft.Graylog.AllowUntrustedCertificate = value;
            OnPropertyChanged();
        }
    }

    public string? GraylogTimezone
    {
        get => _draft.Graylog.Timezone;
        set
        {
            if (_draft.Graylog.Timezone == value)
            {
                return;
            }

            _draft.Graylog.Timezone = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(GraylogTimezoneStatusText));
            OnPropertyChanged(nameof(IsGraylogTimezoneInvalid));
        }
    }

    public bool IsGraylogTimezoneInvalid =>
        !string.IsNullOrWhiteSpace(_draft.Graylog.Timezone) && GraylogQuery.FindTimeZone(_draft.Graylog.Timezone) is null;

    public string GraylogTimezoneStatusText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_draft.Graylog.Timezone))
            {
                return "Blank shows times in this PC's own time zone.";
            }

            var zone = GraylogQuery.FindTimeZone(_draft.Graylog.Timezone);
            return zone is null
                ? "Not a time zone Windows recognises - times will show in this PC's own time zone."
                : $"Times will show in {zone.DisplayName}.";
        }
    }

    public GraylogLevelOption SelectedGraylogLogLevel
    {
        get => GraylogLevelOption.For(_draft.Graylog.DeviceLogLevel);
        set
        {
            if (value is null || _draft.Graylog.DeviceLogLevel == value.Level)
            {
                return;
            }

            _draft.Graylog.DeviceLogLevel = value.Level ?? GraylogSettings.DefaultLogLevel;
            OnPropertyChanged();
        }
    }

    /// <summary>Text for the same reason as <see cref="GraylogPortText"/>; anything that isn't a positive whole number keeps the previous value.</summary>
    public string GraylogRowCountText
    {
        get => _draft.Graylog.DeviceRowCount.ToString(CultureInfo.InvariantCulture);
        set
        {
            if (!int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rows) || rows < 1)
            {
                return;
            }

            rows = Math.Min(rows, GraylogSettings.MaxRowCount);
            if (_draft.Graylog.DeviceRowCount == rows)
            {
                return;
            }

            _draft.Graylog.DeviceRowCount = rows;
            OnPropertyChanged();
        }
    }

    public string? GraylogQueryField
    {
        get => _draft.Graylog.QueryField;
        set
        {
            var queryField = string.IsNullOrWhiteSpace(value) ? GraylogSettings.DefaultQueryField : value.Trim();
            if (_draft.Graylog.QueryField == queryField)
            {
                return;
            }

            _draft.Graylog.QueryField = queryField;
            OnPropertyChanged();
        }
    }

    public bool GraylogMatchAnyAddress
    {
        get => _draft.Graylog.MatchAnyAddress;
        set
        {
            if (_draft.Graylog.MatchAnyAddress == value)
            {
                return;
            }

            _draft.Graylog.MatchAnyAddress = value;
            OnPropertyChanged();
        }
    }

    public AsyncRelayCommand TestGraylogConnectionCommand { get; }

    public bool IsTestingGraylogConnection
    {
        get => _isTestingGraylogConnection;
        private set
        {
            if (SetProperty(ref _isTestingGraylogConnection, value))
            {
                TestGraylogConnectionCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string? GraylogTestStatusText
    {
        get => _graylogTestStatusText;
        private set
        {
            if (SetProperty(ref _graylogTestStatusText, value))
            {
                OnPropertyChanged(nameof(HasGraylogTestStatus));
            }
        }
    }

    public bool HasGraylogTestStatus => !string.IsNullOrEmpty(_graylogTestStatusText);

    public bool? GraylogTestSucceeded
    {
        get => _graylogTestSucceeded;
        private set => SetProperty(ref _graylogTestSucceeded, value);
    }

    /// <summary>Tries what's in the form without touching the live <see cref="IGraylogApi"/> until Save - the same approach as <see cref="TestUnimusConnectionAsync"/>.</summary>
    private async Task TestGraylogConnectionAsync()
    {
        GraylogTestStatusText = null;

        var password = string.IsNullOrEmpty(GraylogPasswordInput) ? _graylogPasswords.Load() : GraylogPasswordInput;
        var connection = GraylogConnection.FromSettings(_draft.Graylog, password, out var error);
        if (connection is null)
        {
            GraylogTestSucceeded = false;
            GraylogTestStatusText = error;
            return;
        }

        IsTestingGraylogConnection = true;

        using var probe = new GraylogApi(Microsoft.Extensions.Logging.Abstractions.NullLogger<GraylogApi>.Instance);
        probe.Configure(connection);

        try
        {
            var streams = await probe.TestConnectionAsync().ConfigureAwait(true);
            GraylogTestSucceeded = true;
            GraylogTestStatusText = streams == 1
                ? "Connected to Graylog - 1 stream available."
                : $"Connected to Graylog - {streams} streams available.";
        }
        catch (GraylogApiException ex)
        {
            GraylogTestSucceeded = false;
            GraylogTestStatusText = ex.ToUserMessage();
        }
        finally
        {
            IsTestingGraylogConnection = false;
        }
    }

    /// <summary>Saves a newly-typed password and reconfigures the live <see cref="IGraylogApi"/> - mirrors <c>App.ConfigureGraylogIfEnabled</c> for the running app.</summary>
    private void ApplyGraylogConfiguration()
    {
        if (!string.IsNullOrEmpty(GraylogPasswordInput))
        {
            _graylogPasswords.Save(GraylogPasswordInput);
        }

        if (!GraylogEnabled)
        {
            _graylog.Clear();
            return;
        }

        var password = string.IsNullOrEmpty(GraylogPasswordInput) ? _graylogPasswords.Load() : GraylogPasswordInput;
        var connection = GraylogConnection.FromSettings(_draft.Graylog, password, out _);
        if (connection is null)
        {
            _graylog.Clear();
            return;
        }

        _graylog.Configure(connection);
    }

    // ------------------------------------------------------------------ about

    public AsyncRelayCommand CheckForUpdatesCommand { get; }

    public RelayCommand ViewLatestReleaseCommand { get; }

    public RelayCommand ViewReleasesPageCommand { get; }

    /// <summary>Opens a new GitHub issue with the app, Windows and LibreNMS versions filled in (#149) - see <see cref="BugReportLink"/>.</summary>
    public RelayCommand ReportBugCommand { get; }

    public string CurrentVersionText => $"Version {_updates.CurrentVersion}";

    /// <summary>
    /// Also treat GitHub pre-release ("preview") tags as an available update.
    /// Re-checks immediately on toggle - like the accent colour swatch above,
    /// this does not wait for Save so flipping it and seeing the effect is
    /// one action, not two.
    /// </summary>
    public bool IncludePreviewBuilds
    {
        get => _draft.IncludePreviewBuilds;
        set
        {
            if (_draft.IncludePreviewBuilds == value)
            {
                return;
            }

            _draft.IncludePreviewBuilds = value;
            OnPropertyChanged();
            _ = CheckForUpdatesAsync(notifyIfNewer: false);
        }
    }

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

    public string? LatestReleaseNotes => string.IsNullOrWhiteSpace(_latestRelease?.Body)
        ? "No release notes were provided for this version."
        : _latestRelease.Body;

    private async Task CheckForUpdatesAsync(bool notifyIfNewer)
    {
        IsCheckingForUpdates = true;
        UpdateStatusText = "Checking for updates...";

        var result = await _updates.CheckAsync(notifyIfNewer, IncludePreviewBuilds).ConfigureAwait(true);

        _latestRelease = result.LatestRelease;
        IsNewerVersionAvailable = result.IsNewerVersionAvailable;

        UpdateStatusText = !result.Succeeded
            ? "Could not check for updates. Check your internet connection."
            : result.IsNewerVersionAvailable
                ? result.LatestRelease!.Prerelease
                    ? $"Preview {result.LatestRelease!.TagName} is available."
                    : $"Version {result.LatestRelease!.TagName} is available."
                : "You're up to date.";

        OnPropertyChanged(nameof(HasLatestRelease));
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
        new StartupTabOption(StartupTab.Groups, "Device Groups"),
        new StartupTabOption(StartupTab.Locations, "Locations"),
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

    /// <summary>
    /// Inverted from <see cref="AppSettings.SuppressBulkAlertActionConfirmation"/>
    /// so the checkbox reads as "on" by default - that flag only becomes true
    /// once someone ticks "don't ask me again" on the prompt itself, and this
    /// is where it gets turned back on.
    /// </summary>
    public bool ConfirmBulkAlertActions
    {
        get => !_draft.SuppressBulkAlertActionConfirmation;
        set
        {
            var suppress = !value;
            if (_draft.SuppressBulkAlertActionConfirmation == suppress)
            {
                return;
            }

            _draft.SuppressBulkAlertActionConfirmation = suppress;
            OnPropertyChanged();
        }
    }

    /// <summary>See <see cref="AppSettings.ShowAlertTabBadge"/>.</summary>
    public bool ShowAlertTabBadge
    {
        get => _draft.ShowAlertTabBadge;
        set
        {
            if (_draft.ShowAlertTabBadge == value)
            {
                return;
            }

            _draft.ShowAlertTabBadge = value;
            OnPropertyChanged();
        }
    }

    /// <summary>See <see cref="AppSettings.AlertTabBadgeIncludesAcknowledged"/>.</summary>
    public bool AlertTabBadgeIncludesAcknowledged
    {
        get => _draft.AlertTabBadgeIncludesAcknowledged;
        set
        {
            if (_draft.AlertTabBadgeIncludesAcknowledged == value)
            {
                return;
            }

            _draft.AlertTabBadgeIncludesAcknowledged = value;
            OnPropertyChanged();
        }
    }

    // ------------------------------------------------------ health thresholds

    /// <summary>
    /// When true, reverts to the app-only "current deciding system": these
    /// thresholds always win and a sensor's own LibreNMS-configured limit is
    /// ignored. When false (default), a sensor's own limit wins per-boundary
    /// wherever it sets one, and these values only fill the gaps.
    /// </summary>
    public bool OverrideSensorLimitsWithAppThresholds
    {
        get => _draft.OverrideSensorLimitsWithAppThresholds;
        set
        {
            if (_draft.OverrideSensorLimitsWithAppThresholds == value)
            {
                return;
            }

            _draft.OverrideSensorLimitsWithAppThresholds = value;
            OnPropertyChanged();
        }
    }

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

    public double SignalWarningThreshold
    {
        get => _draft.SignalThresholds.WarningThreshold;
        set => SetThreshold(value, _draft.SignalThresholds.WarningThreshold, v => _draft.SignalThresholds.WarningThreshold = v);
    }

    public double SignalCriticalThreshold
    {
        get => _draft.SignalThresholds.CriticalThreshold;
        set => SetThreshold(value, _draft.SignalThresholds.CriticalThreshold, v => _draft.SignalThresholds.CriticalThreshold = v);
    }

    public double SignalIgnoreAtOrAbove
    {
        get => _draft.SignalThresholds.IgnoreAtOrAbove;
        set => SetThreshold(value, _draft.SignalThresholds.IgnoreAtOrAbove, v => _draft.SignalThresholds.IgnoreAtOrAbove = v);
    }

    public double SignalIgnoreAtOrBelow
    {
        get => _draft.SignalThresholds.IgnoreAtOrBelow;
        set => SetThreshold(value, _draft.SignalThresholds.IgnoreAtOrBelow, v => _draft.SignalThresholds.IgnoreAtOrBelow = v);
    }

    public double TemperatureLowCritical
    {
        get => _draft.TemperatureThresholds.LowCritical;
        set => SetThreshold(value, _draft.TemperatureThresholds.LowCritical, v => _draft.TemperatureThresholds.LowCritical = v);
    }

    public double TemperatureLowWarning
    {
        get => _draft.TemperatureThresholds.LowWarning;
        set => SetThreshold(value, _draft.TemperatureThresholds.LowWarning, v => _draft.TemperatureThresholds.LowWarning = v);
    }

    public double TemperatureHighWarning
    {
        get => _draft.TemperatureThresholds.HighWarning;
        set => SetThreshold(value, _draft.TemperatureThresholds.HighWarning, v => _draft.TemperatureThresholds.HighWarning = v);
    }

    public double TemperatureHighCritical
    {
        get => _draft.TemperatureThresholds.HighCritical;
        set => SetThreshold(value, _draft.TemperatureThresholds.HighCritical, v => _draft.TemperatureThresholds.HighCritical = v);
    }

    public double FanSpeedLowCritical
    {
        get => _draft.FanSpeedThresholds.LowCritical;
        set => SetThreshold(value, _draft.FanSpeedThresholds.LowCritical, v => _draft.FanSpeedThresholds.LowCritical = v);
    }

    public double FanSpeedLowWarning
    {
        get => _draft.FanSpeedThresholds.LowWarning;
        set => SetThreshold(value, _draft.FanSpeedThresholds.LowWarning, v => _draft.FanSpeedThresholds.LowWarning = v);
    }

    public double FanSpeedHighWarning
    {
        get => _draft.FanSpeedThresholds.HighWarning;
        set => SetThreshold(value, _draft.FanSpeedThresholds.HighWarning, v => _draft.FanSpeedThresholds.HighWarning = v);
    }

    public double FanSpeedHighCritical
    {
        get => _draft.FanSpeedThresholds.HighCritical;
        set => SetThreshold(value, _draft.FanSpeedThresholds.HighCritical, v => _draft.FanSpeedThresholds.HighCritical = v);
    }

    private void SetThreshold(double value, double current, Action<double> setter, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (current == value)
        {
            return;
        }

        setter(value);
        OnPropertyChanged(propertyName);
    }

    // -------------------------------------------------------------- devices

    public bool ShowRecentlyViewedDevices
    {
        get => _draft.ShowRecentlyViewedDevices;
        set
        {
            if (_draft.ShowRecentlyViewedDevices == value)
            {
                return;
            }

            _draft.ShowRecentlyViewedDevices = value;
            OnPropertyChanged();
        }
    }

    /// <summary>See <see cref="AppSettings.EnablePinnedDevices"/> (#98).</summary>
    public bool EnablePinnedDevices
    {
        get => _draft.EnablePinnedDevices;
        set
        {
            if (_draft.EnablePinnedDevices == value)
            {
                return;
            }

            _draft.EnablePinnedDevices = value;
            OnPropertyChanged();
        }
    }

    public int RecentlyViewedDeviceCount
    {
        get => _draft.RecentlyViewedDeviceCount;
        set
        {
            if (_draft.RecentlyViewedDeviceCount == value)
            {
                return;
            }

            _draft.RecentlyViewedDeviceCount = value;
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

    // ----------------------------------------------------------------- maps

    private IReadOnlyList<DefaultMapOption>? _defaultMapOptions;

    /// <summary>What the Maps tab opens on: Network, Geographical, or any saved custom map (as "custom:{id}").</summary>
    public IReadOnlyList<DefaultMapOption> DefaultMapOptions => _defaultMapOptions ??= new[]
        {
            new DefaultMapOption(AppSettings.DefaultMapNetwork, "Network"),
            new DefaultMapOption(AppSettings.DefaultMapGeographical, "Geographical"),
        }
        .Concat(_customMaps.List().Select(m => new DefaultMapOption(AppSettings.DefaultMapCustomPrefix + m.Id, "Custom: " + m.Name)))
        .ToList();

    public DefaultMapOption SelectedDefaultMap
    {
        get => DefaultMapOptions.FirstOrDefault(o => o.Value == _draft.DefaultMap) ?? DefaultMapOptions[0];
        set
        {
            if (value is null || _draft.DefaultMap == value.Value)
            {
                return;
            }

            _draft.DefaultMap = value.Value;
            OnPropertyChanged();
        }
    }

    /// <summary>Blank means OpenStreetMap's standard tiles - LibreNMS's own default.</summary>
    public string? MapTileUrl
    {
        get => _draft.MapTileUrl;
        set
        {
            var trimmed = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (_draft.MapTileUrl == trimmed)
            {
                return;
            }

            _draft.MapTileUrl = trimmed;
            OnPropertyChanged();
            OnPropertyChanged(nameof(MapTileUrlStatusText));
            OnPropertyChanged(nameof(MapTileUrlIsInvalid));
        }
    }

    public bool MapTileUrlIsInvalid =>
        _draft.MapTileUrl is not null && Core.Topology.TileUrlTemplate.Normalise(_draft.MapTileUrl) is null;

    /// <summary>What the setting resolves to, so a LibreNMS-style host-only value visibly becomes a full address.</summary>
    public string MapTileUrlStatusText =>
        _draft.MapTileUrl is null
            ? $"Using OpenStreetMap: {Core.Topology.TileUrlTemplate.Default}"
            : Core.Topology.TileUrlTemplate.Normalise(_draft.MapTileUrl) is { } template
                ? $"Tiles will load from: {template}"
                : "Not a usable tile address - it needs {z}, {x} and {y} (or just a host, like LibreNMS's leaflet.tile_url). OpenStreetMap will be used instead.";

    public RelayCommand ResetMapTileUrlCommand { get; }

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

        // Keep the window placement, filter chips, dashboard layout,
        // recently-viewed list and pinned devices the running app has, rather
        // than the copies taken when this dialog opened - a device viewed or
        // pinned while Settings was open would otherwise be silently
        // discarded on save.
        _draft.Window = _store.Current.Window;
        _draft.Filter = _store.Current.Filter;
        _draft.DashboardWidgets = _store.Current.DashboardWidgets;
        _draft.RecentlyViewedDevices = _store.Current.RecentlyViewedDevices;
        _draft.PinnedDevices = _store.Current.PinnedDevices;

        ApplyUnimusConfiguration();
        ApplyGraylogConfiguration();

        _store.Replace(_draft);
        RequestClose?.Invoke(this, true);
    }

    /// <summary>
    /// Persists a newly-typed token (if any) and reconfigures the live
    /// <see cref="IUnimusApi"/> singleton to match the saved settings -
    /// mirrors <c>App.ConfigureUnimusIfEnabled</c>'s logic for the
    /// already-running app, since that method only runs once at startup.
    /// </summary>
    private void ApplyUnimusConfiguration()
    {
        if (!string.IsNullOrEmpty(UnimusTokenInput))
        {
            _unimusTokens.Save(UnimusTokenInput);
        }

        // Whatever changed here (enabled/disabled, URL, token, mydomain) can
        // change matching outcomes - never leave a stale resolution behind.
        _unimusResolver.Clear();

        if (!UnimusEnabled
            || !UnimusConnection.TryParseWebRoot(UnimusUrl, out var webRoot, out _)
            || webRoot is null)
        {
            _unimus.Clear();
            return;
        }

        var token = string.IsNullOrEmpty(UnimusTokenInput) ? _unimusTokens.Load() : UnimusTokenInput;
        if (string.IsNullOrWhiteSpace(token))
        {
            _unimus.Clear();
            return;
        }

        _unimus.Configure(new UnimusConnection(webRoot, token, UnimusAllowUntrustedCertificate));
    }
}
