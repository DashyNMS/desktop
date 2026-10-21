using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using DesktopNMS.Core.Intelligence;
using DesktopNMS.Infrastructure;

namespace DesktopNMS.ViewModels;

/// <summary>One message in the Intelligence conversation.</summary>
public sealed class IntelligenceMessageViewModel : ObservableObject
{
    private string _text = string.Empty;
    private bool _isStreaming;
    private string? _restoredActivity;
    private bool _isCopied;
    private IUiTimer? _copyFeedbackTimer;
    private bool _isActivityExpanded;
    private readonly HashSet<string> _observedToolsAwaitingFinish = new();
    private readonly List<IntelligenceImage> _savedImages = new();

    private IntelligenceMessageViewModel(bool isUser)
    {
        IsUser = isUser;
        _isStreaming = !isUser;
        Outcome = isUser ? IntelligenceReplyOutcome.Completed : IntelligenceReplyOutcome.Generating;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public static IntelligenceMessageViewModel User(string text) => new(isUser: true) { _text = text };

    public static IntelligenceMessageViewModel Assistant(string? replyStyle = null, string? question = null) => new(isUser: false) { ReplyStyle = replyStyle, SubmittedQuestion = question };

    /// <summary>The presentation chosen for this reply, kept stable when settings change later.</summary>
    public string? ReplyStyle { get; private init; }

    public bool IsConversational => ReplyStyle is { } detail && IntelligenceDetail.IsConversational(detail);

    public DateTimeOffset? CreatedAt { get; private init; }
    public string TimeLabel => CreatedAt?.ToLocalTime().ToString("HH:mm") ?? string.Empty;
    public string TimeTooltip => CreatedAt?.ToLocalTime().ToString("f") ?? string.Empty;
    public bool CanCopyAnswer => IsAssistant && !string.IsNullOrWhiteSpace(Text);
    public string CopyMarkdown => IntelligenceInlineMarkup.ToReadableMarkdown(Text, InlineReferences).Trim();
    public string CopyAnswerLabel => _isCopied ? "Copied" : "Copy answer";
    public IntelligenceReplyOutcome Outcome { get; private set; } = IntelligenceReplyOutcome.Generating;
    public string? SubmittedQuestion { get; private init; }
    public string? FailureText { get; private set; }
    public bool HasFailureText => !string.IsNullOrEmpty(FailureText);
    public bool CanRecoverQuestion => IsAssistant && !IsStreaming
        && (Outcome is IntelligenceReplyOutcome.Failed or IntelligenceReplyOutcome.Stopped or IntelligenceReplyOutcome.Interrupted)
        && !string.IsNullOrWhiteSpace(SubmittedQuestion);
    public string OutcomeLabel => Outcome switch
    {
        IntelligenceReplyOutcome.Generating => "Answer in progress",
        IntelligenceReplyOutcome.Stopped => "Stopped",
        IntelligenceReplyOutcome.Interrupted => "Interrupted",
        IntelligenceReplyOutcome.Failed => "Failed",
        _ => _failedLookups > 0 ? "Completed · some lookups failed" : "Completed",
    };
    public string CompletionGlyph => Outcome == IntelligenceReplyOutcome.Stopped ? "■"
        : _failedLookups > 0 || Outcome is IntelligenceReplyOutcome.Failed or IntelligenceReplyOutcome.Interrupted ? "!" : "✓";
    public void Finish(IntelligenceReplyOutcome outcome, string? failureText = null)
    {
        Outcome = outcome;
        FailureText = failureText;
        _currentStep = null;
        IsStreaming = false;
        OnPropertyChanged(nameof(Outcome));
        OnPropertyChanged(nameof(FailureText));
        OnPropertyChanged(nameof(HasFailureText));
        OnPropertyChanged(nameof(CanRecoverQuestion));
        RaiseActivity();
    }
    public bool IsActivityExpanded { get => _isActivityExpanded; set => SetProperty(ref _isActivityExpanded, value); }
    public ObservableCollection<IntelligenceToolObservationViewModel> ToolObservations { get; } = new();
    public bool HasToolObservations => ToolObservations.Count > 0;

    public void MarkCopied()
    {
        _isCopied = true;
        OnPropertyChanged(nameof(CopyAnswerLabel));
        if (_copyFeedbackTimer is null)
        {
            _copyFeedbackTimer = UiThread.CreateTimer(TimeSpan.FromSeconds(2));
            _copyFeedbackTimer.Tick += (_, _) =>
            {
                _copyFeedbackTimer.Stop();
                _isCopied = false;
                OnPropertyChanged(nameof(CopyAnswerLabel));
            };
        }
        _copyFeedbackTimer.Stop();
        _copyFeedbackTimer.Start();
    }

    public void RecordToolObservation(IntelligenceToolObservation observation)
    {
        if (ToolObservations.Count < 48) ToolObservations.Add(new(observation));
        _observedToolsAwaitingFinish.Add(observation.Tool);
        OnPropertyChanged(nameof(HasToolObservations));
    }

    public SavedIntelligenceMessage Save() => new()
    {
        IsUser = IsUser,
        CreatedAt = CreatedAt,
        Text = Text,
        Activity = ActivityText,
        ReplyStyle = ReplyStyle,
        StatusCards = _statusSnapshots.ToList(),
        PriorReferences = _priorReferences.Values.Where(reference => Text.Contains("[[" + reference.Key + "]]", StringComparison.Ordinal)).ToList(),
        ToolObservations = ToolObservations.Select(item => item.Data).ToList(),
        IsIncomplete = IsStreaming,
        Outcome = Outcome,
        SubmittedQuestion = SubmittedQuestion,
        FailureText = FailureText,
        Images = _savedImages.ToList(),
    };

    public static IntelligenceMessageViewModel Restore(SavedIntelligenceMessage saved)
    {
        var message = new IntelligenceMessageViewModel(saved.IsUser)
        {
            _text = saved.Text + (saved.IsIncomplete && !saved.IsUser ? "\n\n(Interrupted when DashyNMS closed.)" : string.Empty),
            _isStreaming = false,
            _restoredActivity = saved.IsIncomplete ? "Interrupted" : saved.Activity,
            ReplyStyle = saved.ReplyStyle,
            CreatedAt = saved.CreatedAt,
            Outcome = saved.IsIncomplete || saved.Outcome == IntelligenceReplyOutcome.Generating ? IntelligenceReplyOutcome.Interrupted : saved.Outcome ?? IntelligenceReplyOutcome.Completed,
            SubmittedQuestion = saved.SubmittedQuestion,
            FailureText = saved.FailureText,
        };
        foreach (var image in saved.Images) message.AddImage(image);
        message.SeedInlineReferences(saved.PriorReferences);
        foreach (var observation in saved.ToolObservations) message.ToolObservations.Add(new(observation));
        message._failedLookups = saved.ToolObservations.Count(item => item.IsError);
        foreach (var card in saved.StatusCards) message.AddStatusCard(card);
        return message;
    }

    public bool IsUser { get; }

    public bool IsAssistant => !IsUser;

    private readonly List<IntelligenceStatusCard> _statusSnapshots = new();
    private readonly Dictionary<string, IntelligenceInlineReference> _inlineReferences = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IntelligenceInlineReference> _priorReferences = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<IntelligenceInlineReference> InlineReferences => _inlineReferences.Values
        .Concat(_priorReferences.Values.Where(reference => !_inlineReferences.ContainsKey(reference.Key))).ToArray();

    public void SeedInlineReferences(IEnumerable<IntelligenceInlineReference> references)
    {
        foreach (var reference in references)
        {
            if (_priorReferences.Count >= 2000) break;
            // A previous lookup may resolve the name, but cannot claim a current operational state.
            _priorReferences.TryAdd(reference.Key, reference with { Status = "", Tone = "unknown", Detail = "Name verified earlier in this chat; current status not supplied by this reply" });
        }
        OnPropertyChanged(nameof(InlineReferences));
    }

    public void AddStatusCard(IntelligenceStatusCard card)
    {
        // Accumulate pages. The first observed status for an object in this reply
        // stays fixed rather than changing when a later page arrives.
        if (_statusSnapshots.Count >= 48) return;
        _statusSnapshots.Add(card);
        foreach (var reference in IntelligenceInlineReferences.FromSnapshot(card))
        {
            if (_inlineReferences.Count >= 2000) break;
            _inlineReferences.TryAdd(reference.Key, reference);
        }
        OnPropertyChanged(nameof(InlineReferences));
    }

    public string Text
    {
        get => _text;
        set
        {
            var wasEmpty = _text.Length == 0;
            if (SetProperty(ref _text, value))
            {
                OnPropertyChanged(nameof(CanCopyAnswer));
                if (wasEmpty != (_text.Length == 0)) RaiseActivity();
            }
        }
    }

    public bool IsStreaming
    {
        get => _isStreaming;
        set
        {
            if (SetProperty(ref _isStreaming, value))
            {
                if (!value && Outcome == IntelligenceReplyOutcome.Generating) Outcome = IntelligenceReplyOutcome.Completed;
                OnPropertyChanged(nameof(CanRecoverQuestion));
                RaiseActivity();
            }
        }
    }

    /// <summary>Looking something up, or waiting for the first words - drives the heartbeat.</summary>
    public bool IsWorking => IsAssistant && _isStreaming;

    /// <summary>Graphs the answer drew on, shown above its text.</summary>
    /// <remarks>The image bytes; the view decodes them, and leaves out any it can't read (#249).</remarks>
    public ObservableCollection<byte[]> Images { get; } = new();

    public bool HasImages => Images.Count > 0;

    public void AddImage(IntelligenceImage image)
    {
        Images.Add(image.Data);
        _savedImages.Add(image);
        OnPropertyChanged(nameof(HasImages));
    }

    // What was looked up for this answer, in the order first looked at.
    private readonly List<string> _lookedAt = new();
    private string? _currentStep;
    private int _failedLookups;

    /// <summary>
    /// One quiet line above the answer: the step in hand while it works
    /// ("Checking alerts…"), then what it looked at ("Looked at alerts and
    /// device health").
    /// </summary>
    public string? ActivityText => Outcome is IntelligenceReplyOutcome.Stopped or IntelligenceReplyOutcome.Interrupted or IntelligenceReplyOutcome.Failed ? OutcomeLabel
        : _currentStep is { } step
        ? step + "…"
        : _lookedAt.Count > 0
            ? "Looked at " + JoinNames(_lookedAt) + (_failedLookups > 0 ? $" ({_failedLookups} couldn't be read)" : string.Empty)
            : IsWorking ? "Thinking…" : _restoredActivity;

    public bool HasActivity => ActivityText is not null;

    public void ToolStarted(string tool)
    {
        _currentStep = Step(tool);
        RaiseActivity();
    }

    public void ToolFinished(string tool, bool isError)
    {
        if (!_observedToolsAwaitingFinish.Remove(tool))
        {
            // Hosted web search reports activity events rather than a local tool payload.
            RecordToolObservation(new(tool, DateTimeOffset.UtcNow, isError ? "Lookup failed." : "Lookup completed.", isError));
            _observedToolsAwaitingFinish.Remove(tool);
        }
        var subject = Subject(tool);
        if (!_lookedAt.Contains(subject))
        {
            _lookedAt.Add(subject);
        }

        if (isError)
        {
            _failedLookups++;
        }

        _currentStep = null;
        RaiseActivity();
    }

    private void RaiseActivity()
    {
        OnPropertyChanged(nameof(ActivityText));
        OnPropertyChanged(nameof(HasActivity));
        OnPropertyChanged(nameof(IsWorking));
        OnPropertyChanged(nameof(CompletionGlyph));
        OnPropertyChanged(nameof(OutcomeLabel));
    }

    private static string JoinNames(List<string> names) => names.Count switch
    {
        1 => names[0],
        2 => names[0] + " and " + names[1],
        _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
    };

    /// <summary>What a tool is doing, as a person would say it.</summary>
    private static string Step(string tool) => tool switch
    {
        "web_search" => "Searching the web",
        "active_alerts" => "Checking alerts",
        "alert_details" => "Reading the alert",
        "alert_rules" => "Reading alert rules",
        "config_backup" or "config_diff" or "recent_config_changes" => "Checking config backups",
        "device_details" => "Reading device details",
        "device_events" or "network_events" => "Reading the event log",
        "device_health" => "Checking device health",
        "device_neighbours" => "Finding neighbours",
        "device_sensors" or "health_overview" => "Reading sensors",
        "find_device" => "Finding the device",
        "graph" => "Drawing the graph",
        "list_groups_and_locations" => "Listing groups and locations",
        "network_overview" => "Getting the network overview",
        "port_status" => "Checking the port",
        "search_logs" => "Searching Graylog",
        "top_devices" or "top_interfaces" or "top_errors" => "Ranking devices and ports",
        "trace_endpoint" => "Tracing the address",
        "what_changed" => "Finding what changed",
        _ => "Looking it up",
    };

    /// <summary>What a tool looked at, for "Looked at …".</summary>
    internal static string Subject(string tool) => tool switch
    {
        "web_search" => "the web",
        "active_alerts" or "alert_details" => "alerts",
        "alert_rules" => "alert rules",
        "config_backup" or "config_diff" or "recent_config_changes" => "config backups",
        "device_details" or "find_device" => "devices",
        "graph" => "graphs",
        "device_events" or "network_events" => "the event log",
        "device_health" => "device health",
        "device_neighbours" => "neighbours",
        "device_sensors" or "health_overview" => "sensors",
        "list_groups_and_locations" => "groups and locations",
        "network_overview" => "the network overview",
        "port_status" => "ports",
        "search_logs" => "Graylog",
        "top_devices" or "top_interfaces" or "top_errors" => "the busiest devices and ports",
        "trace_endpoint" => "where the address is plugged in",
        "what_changed" => "recent changes",
        _ => "the network",
    };
}
