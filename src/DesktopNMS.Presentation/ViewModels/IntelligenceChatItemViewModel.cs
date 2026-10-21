using System;
using System.Linq;
using DesktopNMS.Core.Intelligence;

namespace DesktopNMS.ViewModels;

/// <summary>One saved chat in the chat list.</summary>
public sealed class IntelligenceChatItemViewModel(SavedIntelligenceChat data) : ObservableObject
{
    public SavedIntelligenceChat Data { get; } = data;
    public string Title => Data.Title;
    public bool IsPinned => Data.IsPinned;
    public string PinLabel => IsPinned ? "Unpin chat" : "Pin chat";
    private IntelligenceChatActivityGroup _activityGroup = IntelligenceChatActivityGroups.ForDate(
        DateOnly.FromDateTime(data.UpdatedAt.ToLocalTime().DateTime), DateOnly.FromDateTime(DateTime.Now), data.IsPinned);
    public IntelligenceChatActivityGroup ActivityGroup => _activityGroup;
    public void RefreshActivityGroup(DateOnly today) => SetProperty(ref _activityGroup,
        IntelligenceChatActivityGroups.ForDate(DateOnly.FromDateTime(Data.UpdatedAt.ToLocalTime().DateTime), today, IsPinned), nameof(ActivityGroup));
    private bool _isRenaming;
    public bool IsRenaming { get => _isRenaming; set => SetProperty(ref _isRenaming, value); }
    public string LastActivity => Data.Messages.Count == 0 ? string.Empty
        : Data.UpdatedAt.ToLocalTime().ToString("g");
    public string Preview
    {
        get
        {
            var message = Data.Messages.LastOrDefault(message => !string.IsNullOrWhiteSpace(message.Text));
            if (message is null) return "Start a conversation";
            var references = message.StatusCards.SelectMany(IntelligenceInlineReferences.FromSnapshot)
                .Concat(message.PriorReferences).DistinctBy(reference => reference.Key).ToArray();
            return IntelligenceInlineMarkup.ToPreviewText(message.Text, references);
        }
    }
    public void Refresh()
    {
        RefreshActivityGroup(DateOnly.FromDateTime(DateTime.Now));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Preview));
        OnPropertyChanged(nameof(IsPinned));
        OnPropertyChanged(nameof(PinLabel));
        OnPropertyChanged(nameof(LastActivity));
    }
}
