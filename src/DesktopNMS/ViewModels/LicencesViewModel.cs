using System;
using System.Collections.Generic;
using System.Linq;
using DesktopNMS.Core.Licences;
using DesktopNMS.Infrastructure;

namespace DesktopNMS.ViewModels;

/// <summary>
/// Settings › About › Open-source licences (#266): everything third-party
/// desktop ships, each with its copyright and licence, and the trademark line
/// - as DashyNMS Mobile's licences page.
/// </summary>
public sealed class LicencesViewModel
{
    public LicencesViewModel(Action<Uri> openUrl)
    {
        Items = DesktopOpenSourceNotices.All.Select(n => new LicenceItemViewModel(n, openUrl)).ToList();
    }

    public IReadOnlyList<LicenceItemViewModel> Items { get; }

    public string Trademarks => OpenSourceNotices.Trademarks;
}

/// <summary>One component: its notice, and its licence text once opened - read only then.</summary>
public sealed class LicenceItemViewModel : ObservableObject
{
    private bool _isExpanded;
    private string? _licenceText;

    public LicenceItemViewModel(OpenSourceNotice notice, Action<Uri> openUrl)
    {
        Notice = notice;
        ToggleCommand = new RelayCommand(Toggle, () => HasLicenceText);
        OpenCommand = new RelayCommand(() => openUrl(notice.Link));
    }

    public OpenSourceNotice Notice { get; }

    public string Name => Notice.Name;

    public string Use => Notice.Use;

    public string Copyright => Notice.Copyright;

    /// <summary>"MIT · github.com/dotnet/wpf".</summary>
    public string LicenceLine => Notice.Licence + " · " + Notice.Link.Host + Notice.Link.AbsolutePath.TrimEnd('/');

    public bool HasLicenceText => Notice.LicenceFile is not null;

    public bool IsExpanded
    {
        get => _isExpanded;
        private set
        {
            if (SetProperty(ref _isExpanded, value))
            {
                OnPropertyChanged(nameof(ToggleText));
            }
        }
    }

    public string? LicenceText
    {
        get => _licenceText;
        private set => SetProperty(ref _licenceText, value);
    }

    public string ToggleText => IsExpanded ? "Hide licence" : "Show licence";

    public RelayCommand ToggleCommand { get; }

    public RelayCommand OpenCommand { get; }

    private void Toggle()
    {
        if (!IsExpanded && LicenceText is null && Notice.LicenceFile is { } file)
        {
            LicenceText = OpenSourceNotices.ReadLicence(file) ?? "The licence text couldn't be read. It's at " + Notice.Link + ".";
        }

        IsExpanded = !IsExpanded;
    }
}
