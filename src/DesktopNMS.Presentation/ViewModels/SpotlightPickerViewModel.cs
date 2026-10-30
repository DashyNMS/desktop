using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Services;
using DesktopNMS.Core.Spotlights;
using DesktopNMS.Infrastructure;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.ViewModels;

/// <summary>
/// Choosing something to spotlight: Devices, Group or Location, then a
/// name. Shared by Intelligence's context sidebar and the Event log and
/// Graylog cards (#552), so both pick the same way; what happens to the
/// choice is up to the owner's <c>add</c>.
/// </summary>
public sealed class SpotlightPickerViewModel : ObservableObject
{
    private readonly IDeviceCache _devices;
    private readonly ILibreNmsClient _client;
    private readonly ISettingsStore _settings;
    private readonly ILogger _logger;
    private readonly Func<SpotlightOption, bool> _add;
    private readonly Func<bool> _canEdit;

    private SpotlightKind _kind = SpotlightKind.Device;
    private SpotlightOption? _selection;
    private bool _isOpen;
    private bool _loadedScopes;
    private List<SpotlightOption> _groups = new();
    private List<SpotlightOption> _locations = new();

    /// <param name="add">Spotlights the choice; false leaves the picker open (a full Spotlight, say).</param>
    /// <param name="canEdit">Whether the Spotlight can change just now - Intelligence locks it mid-answer.</param>
    public SpotlightPickerViewModel(IDeviceCache devices, ILibreNmsClient client, ISettingsStore settings, ILogger logger, Func<SpotlightOption, bool> add, Func<bool>? canEdit = null)
    {
        _devices = devices;
        _client = client;
        _settings = settings;
        _logger = logger;
        _add = add;
        _canEdit = canEdit ?? (() => true);

        OpenCommand = new AsyncRelayCommand(OpenAsync, () => _canEdit());
        CloseCommand = new RelayCommand(() => IsOpen = false);
        AddCommand = new RelayCommand(() =>
        {
            if (_selection is { } picked && _add(picked))
            {
                Selection = null;
                IsOpen = false;
            }
        }, () => _canEdit() && _selection is not null);
        PickDevicesCommand = new RelayCommand(() => Kind = SpotlightKind.Device);
        PickGroupCommand = new RelayCommand(() => Kind = SpotlightKind.Group);
        PickLocationCommand = new RelayCommand(() => Kind = SpotlightKind.Location);
    }

    public ObservableCollection<SpotlightOption> Options { get; } = new();

    public AsyncRelayCommand OpenCommand { get; }

    public RelayCommand CloseCommand { get; }

    public RelayCommand AddCommand { get; }

    public RelayCommand PickDevicesCommand { get; }

    public RelayCommand PickGroupCommand { get; }

    public RelayCommand PickLocationCommand { get; }

    public bool IsOpen
    {
        get => _isOpen;
        set => SetProperty(ref _isOpen, value);
    }

    public SpotlightKind Kind
    {
        get => _kind;
        set
        {
            if (SetProperty(ref _kind, value))
            {
                OnPropertyChanged(nameof(IsPickingDevices));
                OnPropertyChanged(nameof(IsPickingGroup));
                OnPropertyChanged(nameof(IsPickingLocation));
                Fill();
            }
        }
    }

    public bool IsPickingDevices => _kind == SpotlightKind.Device;

    public bool IsPickingGroup => _kind == SpotlightKind.Group;

    public bool IsPickingLocation => _kind == SpotlightKind.Location;

    public SpotlightOption? Selection
    {
        get => _selection;
        set
        {
            if (SetProperty(ref _selection, value))
            {
                AddCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>The owner's lock changed: re-check the commands, and close if it can't be edited now.</summary>
    public void RefreshCanEdit()
    {
        if (!_canEdit())
        {
            IsOpen = false;
        }

        OpenCommand.RaiseCanExecuteChanged();
        AddCommand.RaiseCanExecuteChanged();
    }

    private async Task OpenAsync()
    {
        if (!_canEdit())
        {
            return;
        }

        IsOpen = true;
        if (!_loadedScopes)
        {
            _loadedScopes = true;
            try
            {
                var groups = await _client.DeviceGroups.ListAsync(CancellationToken.None).ConfigureAwait(true);
                _groups = groups.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).Select(g => new SpotlightOption(SpotlightKind.Group, g.Id, g.Name)).ToList();
                var locations = await _client.Locations.ListAsync(CancellationToken.None).ConfigureAwait(true);
                _locations = locations.OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase).Select(l => new SpotlightOption(SpotlightKind.Location, l.Id, l.Name)).ToList();
            }
            catch (Exception ex) when (ex is LibreNmsApiException or OperationCanceledException)
            {
                _logger.LogDebug(ex, "Couldn't list groups and locations for Spotlight");
                _loadedScopes = false;
            }
        }

        Fill();
    }

    private void Fill()
    {
        var style = _settings.Current.DeviceNameStyle;
        var options = _kind switch
        {
            SpotlightKind.Group => _groups,
            SpotlightKind.Location => _locations,
            _ => _devices.All
                .Select(d => new SpotlightOption(SpotlightKind.Device, d.DeviceId, style.Resolve(d, d.Hostname)))
                .OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };

        Options.Clear();
        foreach (var option in options)
        {
            Options.Add(option);
        }

        Selection = null;
    }
}
