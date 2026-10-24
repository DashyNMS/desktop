using DesktopNMS.Core.Sensors;
using DesktopNMS.Infrastructure;

namespace DesktopNMS.ViewModels;

public sealed partial class SettingsViewModel
{
    private SensorDefinitionStore? _sensorDefinitions;
    private SensorDefinitionOptions? _definitionDraft;
    private bool _isSyncingDefinitions;
    private SensorDefinitionOptions DefinitionDraft => _definitionDraft ?? _sensorDefinitions?.Options ?? new();

    private void InitialiseSensorDefinitions(SensorDefinitionStore? definitions)
    {
        _sensorDefinitions = definitions;
        if (definitions is not null) definitions.Changed += OnSensorDefinitionsChanged;
        SelectSensorDefinitionsSectionCommand = new RelayCommand(() => SelectedSection = SettingsSection.SensorDefinitions);
        UpdateSensorDefinitionsCommand = new AsyncRelayCommand(UpdateSensorDefinitionsAsync,
            () => _sensorDefinitions?.Options.Enabled == true && !IsSyncingDefinitions && (_definitionDraft is null || _definitionDraft == _sensorDefinitions.Options));
    }

    public bool HasSensorDefinitions => _sensorDefinitions is not null;
    public bool IsSensorDefinitionsSectionSelected => SelectedSection == SettingsSection.SensorDefinitions;
    public RelayCommand SelectSensorDefinitionsSectionCommand { get; private set; } = null!;
    public AsyncRelayCommand UpdateSensorDefinitionsCommand { get; private set; } = null!;
    public bool SensorDefinitionsEnabled
    {
        get => DefinitionDraft.Enabled;
        set { _definitionDraft = DefinitionDraft with { Enabled = value }; RefreshSensorDefinitions(); }
    }
    public bool SensorDefinitionsCustomRepository
    {
        get => DefinitionDraft.UseCustomRepository;
        set { _definitionDraft = DefinitionDraft with { UseCustomRepository = value }; RefreshSensorDefinitions(); }
    }
    public string SensorDefinitionsRepository
    {
        get => DefinitionDraft.CustomRepository;
        set { _definitionDraft = DefinitionDraft with { CustomRepository = value }; RefreshSensorDefinitions(); }
    }
    public string SensorDefinitionsSource => DefinitionDraft.UseCustomRepository ? DefinitionDraft.CustomRepository : SensorDefinitionOptions.OfficialRepository;
    public string SensorDefinitionsStatus => _sensorDefinitions?.Status ?? "Sensor definitions aren't available in this build.";
    public string SensorDefinitionsVersion => _sensorDefinitions?.Version ?? "Not downloaded";
    public string SensorDefinitionsPath => _sensorDefinitions?.Paths.DefinitionsDirectory ?? "";
    public string SensorDefinitionsLastSync => _sensorDefinitions?.LastSuccessfulCheck?.ToLocalTime().ToString("g") ?? "Not checked";
    public bool IsSyncingDefinitions { get => _isSyncingDefinitions; private set { if (SetProperty(ref _isSyncingDefinitions, value)) { UpdateSensorDefinitionsCommand.RaiseCanExecuteChanged(); SaveCommand?.RaiseCanExecuteChanged(); } } }

    private void OnSensorDefinitionsChanged(object? sender, EventArgs e) => _dispatcher.InvokeAsync(RefreshSensorDefinitions);

    private void RefreshSensorDefinitions()
    {
        foreach (var name in new[] { nameof(SensorDefinitionsEnabled), nameof(SensorDefinitionsCustomRepository), nameof(SensorDefinitionsRepository),
                     nameof(SensorDefinitionsSource), nameof(SensorDefinitionsStatus), nameof(SensorDefinitionsVersion), nameof(SensorDefinitionsPath), nameof(SensorDefinitionsLastSync) })
            OnPropertyChanged(name);
        UpdateSensorDefinitionsCommand.RaiseCanExecuteChanged();
    }

    private async Task<bool> ApplySensorDefinitionsAsync()
    {
        if (_sensorDefinitions is null || _definitionDraft is null || _definitionDraft == _sensorDefinitions.Options) return true;
        try
        {
            await _sensorDefinitions.ConfigureAsync(_definitionDraft);
            _definitionDraft = null;
            if (_sensorDefinitions.Options.Enabled) _ = _sensorDefinitions.SynchroniseAsync();
            RefreshSensorDefinitions();
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            SelectedSection = SettingsSection.SensorDefinitions;
            RefreshSensorDefinitions();
            _windows.ShowError("Sensor definitions", ex.Message);
            return false;
        }
    }

    private async Task UpdateSensorDefinitionsAsync()
    {
        if (_sensorDefinitions is null) return;
        IsSyncingDefinitions = true;
        try { await _sensorDefinitions.SynchroniseAsync(force: true); }
        finally { IsSyncingDefinitions = false; RefreshSensorDefinitions(); }
    }

    private void DetachSensorDefinitions()
    {
        if (_sensorDefinitions is not null) _sensorDefinitions.Changed -= OnSensorDefinitionsChanged;
    }
}
