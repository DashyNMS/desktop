using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DesktopNMS.Infrastructure;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views.Intelligence;

/// <summary>
/// The Intelligence conversation (#327): each question and answer, following
/// the latest as it streams in unless the person has scrolled up to read.
/// </summary>
public partial class IntelligenceTranscript : UserControl
{
    /// <summary>How near the bottom still counts as following the latest.</summary>
    private const double FollowSlack = 24;

    /// <summary>A graph's PNG bytes as an image; nothing if they won't decode.</summary>
    public static readonly IValueConverter BytesToBitmap = new FuncValueConverter<byte[]?, Bitmap?>(Decode);

    private readonly HashSet<IntelligenceMessageViewModel> _observedMessages = new();
    private IntelligenceViewModel? _model;
    private bool _followLatest = true;
    private bool _scrollPending;

    public IntelligenceTranscript()
    {
        InitializeComponent();
        Transcript.ScrollChanged += OnTranscriptScrollChanged;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_model is not null)
        {
            _model.Messages.CollectionChanged -= OnMessagesChanged;
        }

        _model = DataContext as IntelligenceViewModel;
        if (_model is not null)
        {
            _model.Messages.CollectionChanged += OnMessagesChanged;
        }

        _followLatest = true;
        SynchronizeMessageSubscriptions();
        ScheduleScrollToLatest();
    }

    private static Bitmap? Decode(byte[]? data)
    {
        if (data is not { Length: > 0 })
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(data);
            return new Bitmap(stream);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void OnSourceLinkActivated(object? sender, Uri uri) => _model?.OpenSource(uri);

    /// <summary>A graph at full size, in a window of its own.</summary>
    private void OnGraphClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: byte[] data } button || Decode(data) is not { } image)
        {
            return;
        }

        var screen = TopLevel.GetTopLevel(this) is Window owner ? owner.Screens.ScreenFromWindow(owner)?.WorkingArea : null;
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var window = new Window
        {
            Title = "Graph",
            Icon = (TopLevel.GetTopLevel(this) as Window)?.Icon,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.WidthAndHeight,
            Background = Brushes.White,
            Content = new Image
            {
                Source = image,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(12),
                MaxWidth = (screen?.Width ?? 1600) / scaling * 0.9,
                MaxHeight = (screen?.Height ?? 1000) / scaling * 0.85,
            },
        };
        window.Closed += (_, _) => button.Focus();
        if (TopLevel.GetTopLevel(this) is Window parent)
        {
            window.Show(parent);
        }
        else
        {
            window.Show();
        }
    }

    /// <summary>The whole answer, as the Markdown it came in - tables and lists intact.</summary>
    private void OnCopyAnswerClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: IntelligenceMessageViewModel { CanCopyAnswer: true } message }
            && UiPlatform.Clipboard.SetText(message.CopyMarkdown))
        {
            message.MarkCopied();
        }
    }

    /// <summary>Follow the conversation as it grows, including the answer as it streams in.</summary>
    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SynchronizeMessageSubscriptions();
        if (e.Action == NotifyCollectionChangedAction.Reset || e.NewItems?.OfType<IntelligenceMessageViewModel>().Any(message => message.IsUser) == true)
        {
            _followLatest = true;
        }

        if (_followLatest)
        {
            ScheduleScrollToLatest();
        }
        else
        {
            JumpToLatestButton.IsVisible = true;
        }
    }

    private void SynchronizeMessageSubscriptions()
    {
        var messages = _model?.Messages.ToHashSet() ?? new HashSet<IntelligenceMessageViewModel>();
        foreach (var message in _observedMessages.Where(message => !messages.Contains(message)).ToArray())
        {
            message.PropertyChanged -= OnMessagePropertyChanged;
            _observedMessages.Remove(message);
        }

        foreach (var message in messages.Where(message => !_observedMessages.Contains(message)))
        {
            message.PropertyChanged += OnMessagePropertyChanged;
            _observedMessages.Add(message);
        }
    }

    private void OnMessagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(IntelligenceMessageViewModel.Text) or nameof(IntelligenceMessageViewModel.HasImages)))
        {
            return;
        }

        if (_followLatest)
        {
            ScheduleScrollToLatest();
        }
        else
        {
            JumpToLatestButton.IsVisible = true;
        }
    }

    private double DistanceFromBottom => Transcript.Extent.Height - Transcript.Viewport.Height - Transcript.Offset.Y;

    private void OnTranscriptScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        // Scrolled by the person (the content didn't grow): follow again only once back at the bottom.
        if (e.ExtentDelta.Y == 0 && e.OffsetDelta.Y != 0)
        {
            _followLatest = DistanceFromBottom <= FollowSlack;
        }

        if (e.ExtentDelta.Y != 0 && _followLatest)
        {
            ScheduleScrollToLatest();
        }

        JumpToLatestButton.IsVisible = !_followLatest && DistanceFromBottom > FollowSlack;
    }

    private void ScheduleScrollToLatest()
    {
        if (!_followLatest || _scrollPending)
        {
            return;
        }

        _scrollPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _scrollPending = false;
            if (_followLatest)
            {
                Transcript.ScrollToEnd();
                JumpToLatestButton.IsVisible = false;
            }
        }, DispatcherPriority.Loaded);
    }

    private void OnJumpToLatestClick(object? sender, RoutedEventArgs e)
    {
        _followLatest = true;
        ScheduleScrollToLatest();
    }
}
