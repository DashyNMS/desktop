using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using DesktopNMS.Infrastructure;

namespace DesktopNMS.ViewModels;

/// <summary>One stop in a device window's history - the device, its name when it was left, and the section it was on.</summary>
public sealed record DeviceHistoryEntry(int DeviceId, string Name, DeviceDetailSection Section);

/// <summary>A breadcrumb before the current device - clicking it goes back to that one.</summary>
public sealed record DeviceCrumb(int Index, string Name);

/// <summary>
/// A Device Details window's back/forward history (#58), like a browser's:
/// following a neighbour link opens that device in the same window, and
/// this remembers the way back - the devices before the current one show
/// as a breadcrumb trail, each clickable, with back and forward arrows.
/// The window's host (see <see cref="Services.WindowService"/>) does the
/// actual switching when <see cref="NavigateRequested"/> fires, and moves
/// this along with <see cref="Visit"/> or <see cref="GoTo"/>.
/// </summary>
public sealed class DeviceBrowseHistory : ObservableObject
{
    private readonly List<DeviceHistoryEntry> _entries = new();
    private int _index;

    public DeviceBrowseHistory(int deviceId)
    {
        _entries.Add(new DeviceHistoryEntry(deviceId, string.Empty, DeviceDetailSection.Overview));

        Crumbs = new ObservableCollection<DeviceCrumb>();
        BackCommand = new RelayCommand(() => RequestGoTo(_index - 1), () => CanGoBack);
        ForwardCommand = new RelayCommand(() => RequestGoTo(_index + 1), () => CanGoForward);
        GoToCrumbCommand = new RelayCommand(parameter =>
        {
            if (parameter is DeviceCrumb crumb)
            {
                RequestGoTo(crumb.Index);
            }
        });
    }

    /// <summary>A back/forward arrow or a breadcrumb asked for the entry at this index - the host switches to it and calls <see cref="GoTo"/>.</summary>
    public event EventHandler<int>? NavigateRequested;

    public DeviceHistoryEntry Current => _entries[_index];

    public IReadOnlyList<DeviceHistoryEntry> Entries => _entries;

    /// <summary>The devices before the current one, oldest first.</summary>
    public ObservableCollection<DeviceCrumb> Crumbs { get; }

    public bool CanGoBack => _index > 0;

    public bool CanGoForward => _index < _entries.Count - 1;

    /// <summary>Anything to show - the arrows and trail stay out of the way until the window has been somewhere else.</summary>
    public bool HasHistory => _entries.Count > 1;

    public RelayCommand BackCommand { get; }

    public RelayCommand ForwardCommand { get; }

    public RelayCommand GoToCrumbCommand { get; }

    /// <summary>Records the current device's name and section as it's left, so going back returns to the same place.</summary>
    public void UpdateCurrent(string name, DeviceDetailSection section) =>
        _entries[_index] = _entries[_index] with { Name = name, Section = section };

    /// <summary>Moves on to a new device - anything that was forward of here is dropped, as in a browser.</summary>
    public void Visit(int deviceId)
    {
        _entries.RemoveRange(_index + 1, _entries.Count - _index - 1);
        _entries.Add(new DeviceHistoryEntry(deviceId, string.Empty, DeviceDetailSection.Overview));
        _index = _entries.Count - 1;
        Raise();
    }

    /// <summary>Moves back or forward to an existing entry.</summary>
    public void GoTo(int index)
    {
        _index = Math.Clamp(index, 0, _entries.Count - 1);
        Raise();
    }

    public void RequestGoTo(int index)
    {
        if (index >= 0 && index < _entries.Count && index != _index)
        {
            NavigateRequested?.Invoke(this, index);
        }
    }

    private void Raise()
    {
        Crumbs.Clear();
        foreach (var (entry, i) in _entries.Take(_index).Select((e, i) => (e, i)))
        {
            Crumbs.Add(new DeviceCrumb(i, string.IsNullOrWhiteSpace(entry.Name) ? $"device {entry.DeviceId}" : entry.Name));
        }

        OnPropertyChanged(nameof(Current));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        OnPropertyChanged(nameof(HasHistory));
        BackCommand.RaiseCanExecuteChanged();
        ForwardCommand.RaiseCanExecuteChanged();
    }
}
