using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;
using DesktopNMS.Infrastructure;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.ViewModels;

/// <summary>One selectable behaviour in the maintenance dialog - wraps <see cref="MaintenanceBehavior"/> with a ToString() override, the established workaround for this app's ComboBox closed-display quirk.</summary>
public sealed class MaintenanceBehaviorOption
{
    public MaintenanceBehaviorOption(MaintenanceBehavior value) => Value = value;

    public MaintenanceBehavior Value { get; }

    public override string ToString() => Value.ToDisplayString();
}

/// <summary>
/// Backs the "Schedule maintenance" dialog (issue #38) - create-only, since
/// LibreNMS's versioned API has no route to list, edit or cancel a
/// maintenance window once scheduled (only <c>maintenance_device</c> to
/// create one and <c>device_under_maintenance</c> to check the current
/// state - see <see cref="IDevicesApi.IsUnderMaintenanceAsync"/>'s remarks).
/// Ending a window early is only possible from LibreNMS's own web UI.
/// </summary>
public sealed class MaintenanceScheduleViewModel : ObservableObject
{
    public static readonly IReadOnlyList<MaintenanceBehaviorOption> BehaviorOptions = new[]
    {
        new MaintenanceBehaviorOption(MaintenanceBehavior.SkipAlerts),
        new MaintenanceBehaviorOption(MaintenanceBehavior.MuteAlerts),
        new MaintenanceBehaviorOption(MaintenanceBehavior.RunAlerts),
    };

    private readonly ILibreNmsClient _client;
    private readonly ILogger<MaintenanceScheduleViewModel> _logger;

    private int _deviceId;
    private string _deviceName = string.Empty;
    private string? _title;
    private string? _notes;
    private bool _startNow = true;
    private DateTime _scheduledDate = DateTime.Today;
    private string _scheduledTime = "09:00";
    private string _durationHoursText = "1";
    private string _durationMinutesText = "0";
    private MaintenanceBehaviorOption _selectedBehavior = BehaviorOptions[0];
    private bool _isBusy;
    private string? _errorMessage;

    public MaintenanceScheduleViewModel(ILibreNmsClient client, ILogger<MaintenanceScheduleViewModel> logger)
    {
        _client = client;
        _logger = logger;

        SaveCommand = new AsyncRelayCommand(SaveAsync, CanSave);
    }

    /// <summary>Called by <see cref="Services.WindowService.ShowScheduleMaintenanceDialog"/> before the dialog is shown.</summary>
    public void Initialize(int deviceId, string deviceName)
    {
        _deviceId = deviceId;
        _deviceName = deviceName;
    }

    public string Title => $"Schedule maintenance - {_deviceName}";

    public event EventHandler<bool>? RequestClose;

    /// <summary>Placeholder text for the name field - blank sends nothing, and LibreNMS falls back to the device's own display name.</summary>
    public string TitlePlaceholder => _deviceName;

    public string? MaintenanceTitle
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public string? Notes
    {
        get => _notes;
        set => SetProperty(ref _notes, value);
    }

    public bool StartNow
    {
        get => _startNow;
        set
        {
            if (SetProperty(ref _startNow, value))
            {
                OnPropertyChanged(nameof(IsScheduledForLater));
            }
        }
    }

    public bool IsScheduledForLater
    {
        get => !_startNow;
        set => StartNow = !value;
    }

    public DateTime ScheduledDate
    {
        get => _scheduledDate;
        set => SetProperty(ref _scheduledDate, value);
    }

    /// <summary>24-hour "HH:mm" - a plain text field rather than a time picker control, matching this app's other simple inputs.</summary>
    public string ScheduledTime
    {
        get => _scheduledTime;
        set => SetProperty(ref _scheduledTime, value);
    }

    /// <summary>Whole hours, as free text rather than a numeric control - this app's established pattern for a plain numeric field (see SettingsViewModel's colour fields).</summary>
    public string DurationHoursText
    {
        get => _durationHoursText;
        set
        {
            if (SetProperty(ref _durationHoursText, value ?? string.Empty))
            {
                SaveCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string DurationMinutesText
    {
        get => _durationMinutesText;
        set
        {
            if (SetProperty(ref _durationMinutesText, value ?? string.Empty))
            {
                SaveCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public IReadOnlyList<MaintenanceBehaviorOption> Behaviors => BehaviorOptions;

    public MaintenanceBehaviorOption SelectedBehavior
    {
        get => _selectedBehavior;
        set => SetProperty(ref _selectedBehavior, value);
    }

    public AsyncRelayCommand SaveCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                SaveCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_errorMessage);

    private bool CanSave() =>
        !IsBusy
        && int.TryParse(DurationHoursText, out var hours) && hours >= 0
        && int.TryParse(DurationMinutesText, out var minutes) && minutes is >= 0 and < 60
        && (hours > 0 || minutes > 0);

    private async Task SaveAsync()
    {
        ErrorMessage = null;

        if (!int.TryParse(DurationHoursText, out var hours) || hours < 0
            || !int.TryParse(DurationMinutesText, out var minutes) || minutes < 0 || minutes >= 60
            || (hours == 0 && minutes == 0))
        {
            ErrorMessage = "Enter a duration of at least a minute - hours as a whole number, minutes from 0 to 59.";
            return;
        }

        string? start = null;
        if (!StartNow)
        {
            if (!TimeSpan.TryParse(ScheduledTime, out var timeOfDay))
            {
                ErrorMessage = "Enter the scheduled time as HH:mm, e.g. 09:00 or 17:30.";
                return;
            }

            // Server-local wall-clock time, unspecified kind - same convention
            // every LibreNMS timestamp this app reads already uses
            // (LibreNmsDateTimeConverter): no timezone conversion either way.
            start = ScheduledDate.Date.Add(timeOfDay).ToString("yyyy-MM-dd HH:mm:00", System.Globalization.CultureInfo.InvariantCulture);
        }

        IsBusy = true;

        try
        {
            var request = new DeviceMaintenanceRequest
            {
                Title = string.IsNullOrWhiteSpace(MaintenanceTitle) ? null : MaintenanceTitle,
                Notes = string.IsNullOrWhiteSpace(Notes) ? null : Notes,
                Start = start,
                Duration = $"{hours}:{minutes:D2}",
                Behavior = (int)SelectedBehavior.Value,
            };

            var message = await _client.Devices.ScheduleMaintenanceAsync(_deviceId, request).ConfigureAwait(true);
            ConfirmationMessage = message;
            RequestClose?.Invoke(this, true);
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogWarning(ex, "Could not schedule maintenance for device {DeviceId}", _deviceId);
            ErrorMessage = ex.ToUserMessage();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>LibreNMS's own confirmation text (e.g. "Device x will begin maintenance mode at ... for 2h") - shown by the caller after the dialog closes successfully.</summary>
    public string? ConfirmationMessage { get; private set; }
}
