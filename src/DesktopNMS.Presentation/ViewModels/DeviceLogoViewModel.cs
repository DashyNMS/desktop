using System;
using System.Threading;
using System.Threading.Tasks;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Services;
using DesktopNMS.Infrastructure;

namespace DesktopNMS.ViewModels;

/// <summary>
/// The device's own logo for the device details header, in place of the
/// device-type symbol once it has loaded (#555). Asks Core's
/// <see cref="IDeviceLogoCache"/> for it only while
/// <see cref="AppSettings.ShowDeviceLogos"/> is on, and only for the device
/// being shown, so nothing is fetched in bulk. Until the logo arrives - or if
/// there isn't one - <see cref="HasLogo"/> stays false and the header keeps the
/// symbol, so it never goes blank or jumps.
/// </summary>
/// <remarks>
/// Gives the file, not an image: this project has no UI framework, so the
/// Avalonia app turns <see cref="Logo"/> into an image (Views.DeviceLogoTile).
/// </remarks>
public sealed class DeviceLogoViewModel : ObservableObject, IDisposable
{
    private readonly IDeviceLogoCache? _cache;
    private readonly ISettingsStore _settings;
    private readonly IUiDispatcher _dispatcher;

    private CancellationTokenSource? _loading;
    private string? _icon;

    /// <summary>The icon <see cref="Logo"/> is for, or being fetched for - so a device poll with the same icon doesn't fetch again.</summary>
    private string? _requestedIcon;
    private DeviceLogo? _logo;
    private bool _disposed;

    /// <param name="cache">Null where the app has no logo cache (a test, or a front end without one): the symbol always shows.</param>
    public DeviceLogoViewModel(IDeviceLogoCache? cache, ISettingsStore settings, IUiDispatcher dispatcher)
    {
        _cache = cache;
        _settings = settings;
        _dispatcher = dispatcher;
        _settings.Changed += OnSettingsChanged;
    }

    /// <summary>The cached logo file to show, or null for the device-type symbol.</summary>
    public DeviceLogo? Logo
    {
        get => _logo;
        private set
        {
            if (SetProperty(ref _logo, value))
            {
                OnPropertyChanged(nameof(HasLogo));
            }
        }
    }

    public bool HasLogo => Logo is not null;

    /// <summary>The device's <c>icon</c> (hpe.svg), each time its details arrive; a new one fetches its logo.</summary>
    public void Show(string? icon)
    {
        _icon = string.IsNullOrWhiteSpace(icon) ? null : icon;
        Refresh();
    }

    private void OnSettingsChanged(object? sender, AppSettings settings) => _dispatcher.Post(Refresh);

    private void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        if (_cache is null || _icon is null || !_settings.Current.ShowDeviceLogos)
        {
            CancelLoad();
            _requestedIcon = null;
            Logo = null;
            return;
        }

        if (string.Equals(_icon, _requestedIcon, StringComparison.Ordinal))
        {
            return;
        }

        CancelLoad();
        _requestedIcon = _icon;
        _loading = new CancellationTokenSource();
        _ = LoadAsync(_icon, _loading.Token);
    }

    private async Task LoadAsync(string icon, CancellationToken cancellationToken)
    {
        // GetAsync never throws: null is "no logo", and the symbol stays.
        var logo = await _cache!.GetAsync(icon, cancellationToken).ConfigureAwait(false);
        _dispatcher.Post(() =>
        {
            if (!cancellationToken.IsCancellationRequested && !_disposed)
            {
                Logo = logo;
            }
        });
    }

    private void CancelLoad()
    {
        if (_loading is { } loading)
        {
            _loading = null;
            loading.Cancel();
            loading.Dispose();
        }
    }

    /// <summary>Stops a fetch still under way - the device's details were closed or moved on.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _settings.Changed -= OnSettingsChanged;
        CancelLoad();
    }
}
