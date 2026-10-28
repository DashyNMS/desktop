using DesktopNMS.Infrastructure;
using DesktopNMS.Services;
using DesktopNMS.Services.Intelligence;

namespace DesktopNMS.ViewModels;

public sealed partial class SettingsViewModel
{
    private McpToolHost? _versionTools;
    private ComponentVersion _mcpVersion = new("MCP server", ComponentVersions.Unavailable, "Reading bundle metadata…");
    private bool _versionsDetached;
    private int _versionRead;
    private string? _copyVersionsStatus;
    public string? CopyVersionsStatus { get => _copyVersionsStatus; private set => SetProperty(ref _copyVersionsStatus, value); }
    public RelayCommand CopyVersionDetailsCommand { get; private set; } = null!;
    private IReadOnlyList<ComponentVersion> _packageVersions = Array.Empty<ComponentVersion>();
    public IReadOnlyList<ComponentVersion> PackageVersions => _packageVersions;

    private IReadOnlyList<ComponentVersion> BuildPackageVersions() => new[]
    {
        new ComponentVersion("Desktop", _updates.CurrentVersion, "Installed",
            "Build: " + ComponentVersions.AssemblyVersion(System.Reflection.Assembly.GetEntryAssembly() ?? typeof(SettingsViewModel).Assembly)),
        new ComponentVersion("Core", ComponentVersions.AssemblyVersion(typeof(DesktopNMS.Core.Sensors.SensorDefinitionStore).Assembly), "Loaded"),
        _mcpVersion with
        {
            State = _mcpVersion.State + (_versionTools?.ReportedVersion is null ? "; not connected" : "; connected"),
            Details = string.Join("\n", new[] { _mcpVersion.Details,
                _versionTools?.ReportedVersion is { } reported
                    ? $"Connected server reports: {reported}. Older servers may omit preview/build identity."
                    : "Network tools have not connected. Opening About does not start them." }.Where(s => s.Length > 0))
        },
        ComponentVersions.Sensor(_sensorDefinitions)
    };

    private void InitialiseComponentVersions(McpToolHost? tools)
    {
        _versionTools = tools;
        if (tools is not null) tools.VersionChanged += OnComponentVersionsChanged;
        CopyVersionDetailsCommand = new RelayCommand(() => CopyVersionsStatus = UiPlatform.Clipboard.SetText(ComponentVersions.Format(PackageVersions))
            ? "Version details copied." : "Clipboard unavailable. Select the version text to copy it.");
        RaiseComponentVersions();
        _ = RefreshComponentVersionsAsync();
    }

    private void OnComponentVersionsChanged(object? sender, EventArgs e) => _dispatcher.InvokeAsync(() =>
    {
        if (!_versionsDetached) RaiseComponentVersions();
    });

    private void RaiseComponentVersions()
    {
        _packageVersions = BuildPackageVersions();
        CopyVersionsStatus = null;
        OnPropertyChanged(nameof(PackageVersions));
    }

    private async Task RefreshComponentVersionsAsync()
    {
        var generation = ++_versionRead;
        var result = await Task.Run(() => ComponentVersions.ReadMcpAsync(McpToolHost.ExecutablePath));
        await _dispatcher.InvokeAsync(() =>
        {
            if (_versionsDetached || generation != _versionRead) return;
            _mcpVersion = result;
            RaiseComponentVersions();
        });
    }

    private Uri ComponentBugReportLink()
    {
        var uri = DesktopNMS.Core.Updates.BugReportLink.Build(_updates.CurrentVersion,
            System.Runtime.InteropServices.RuntimeInformation.OSDescription, _serverInfo?.LocalVersion);
        return ComponentVersions.AddToBugReport(uri, PackageVersions);
    }

    private void DetachComponentVersions()
    {
        _versionsDetached = true;
        if (_versionTools is not null) _versionTools.VersionChanged -= OnComponentVersionsChanged;
    }
}
