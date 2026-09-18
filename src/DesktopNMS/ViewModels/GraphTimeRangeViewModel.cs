using System;
using DesktopNMS.Core.Models;
using DesktopNMS.Infrastructure;

namespace DesktopNMS.ViewModels;

/// <summary>
/// Backs the shared time-range picker (issue #13) - one instance per
/// graph-hosting view, reused as-is so every graph in the app offers the
/// same 1h/1d/1w/1m/1y/custom choice rather than each inventing its own.
/// </summary>
public sealed class GraphTimeRangeViewModel : ObservableObject
{
    private GraphTimeRangePreset _preset;
    private DateTime _customFrom;
    private DateTime _customTo;

    /// <param name="initialPreset">Defaults to Day - every existing caller relied on that default.</param>
    /// <param name="initialCustomFrom">Only meaningful when <paramref name="initialPreset"/> is Custom (a Dashboard Graph widget persists this - see issue #12); otherwise defaults as before.</param>
    public GraphTimeRangeViewModel(GraphTimeRangePreset initialPreset = GraphTimeRangePreset.Day, DateTime? initialCustomFrom = null, DateTime? initialCustomTo = null)
    {
        _preset = initialPreset;
        _customFrom = initialCustomFrom ?? DateTime.Now.AddDays(-1);
        _customTo = initialCustomTo ?? DateTime.Now;


        SetHourCommand = new RelayCommand(() => Preset = GraphTimeRangePreset.Hour);
        SetDayCommand = new RelayCommand(() => Preset = GraphTimeRangePreset.Day);
        SetWeekCommand = new RelayCommand(() => Preset = GraphTimeRangePreset.Week);
        SetMonthCommand = new RelayCommand(() => Preset = GraphTimeRangePreset.Month);
        SetYearCommand = new RelayCommand(() => Preset = GraphTimeRangePreset.Year);
        SetCustomCommand = new RelayCommand(() => Preset = GraphTimeRangePreset.Custom);
    }

    /// <summary>Raised whenever the effective range changes - a graph-hosting view model reloads on this.</summary>
    public event EventHandler? Changed;

    public GraphTimeRangePreset Preset
    {
        get => _preset;
        private set
        {
            if (SetProperty(ref _preset, value))
            {
                OnPropertyChanged(nameof(IsCustom));
                OnPropertyChanged(nameof(IsHourSelected));
                OnPropertyChanged(nameof(IsDaySelected));
                OnPropertyChanged(nameof(IsWeekSelected));
                OnPropertyChanged(nameof(IsMonthSelected));
                OnPropertyChanged(nameof(IsYearSelected));
                OnPropertyChanged(nameof(IsCustomSelected));
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public bool IsCustom => Preset == GraphTimeRangePreset.Custom;

    /// <summary>Drives each preset button's "selected" look - plain Buttons rather than ToggleButtons/RadioButtons, since these are read-only/derived, not something the control itself should be able to set.</summary>
    public bool IsHourSelected => Preset == GraphTimeRangePreset.Hour;

    public bool IsDaySelected => Preset == GraphTimeRangePreset.Day;

    public bool IsWeekSelected => Preset == GraphTimeRangePreset.Week;

    public bool IsMonthSelected => Preset == GraphTimeRangePreset.Month;

    public bool IsYearSelected => Preset == GraphTimeRangePreset.Year;

    public bool IsCustomSelected => Preset == GraphTimeRangePreset.Custom;

    public DateTime CustomFrom
    {
        get => _customFrom;
        set
        {
            if (SetProperty(ref _customFrom, value) && IsCustom)
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public DateTime CustomTo
    {
        get => _customTo;
        set
        {
            if (SetProperty(ref _customTo, value) && IsCustom)
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public RelayCommand SetHourCommand { get; }

    public RelayCommand SetDayCommand { get; }

    public RelayCommand SetWeekCommand { get; }

    public RelayCommand SetMonthCommand { get; }

    public RelayCommand SetYearCommand { get; }

    public RelayCommand SetCustomCommand { get; }

    public GraphTimeRange ToTimeRange() => Preset == GraphTimeRangePreset.Custom
        ? GraphTimeRange.Custom(CustomFrom, CustomTo)
        : Preset switch
        {
            GraphTimeRangePreset.Hour => GraphTimeRange.LastHour,
            GraphTimeRangePreset.Week => GraphTimeRange.LastWeek,
            GraphTimeRangePreset.Month => GraphTimeRange.LastMonth,
            GraphTimeRangePreset.Year => GraphTimeRange.LastYear,
            _ => GraphTimeRange.LastDay,
        };
}
