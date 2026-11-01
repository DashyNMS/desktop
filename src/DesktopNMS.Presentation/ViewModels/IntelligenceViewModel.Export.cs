using System;
using DesktopNMS.Infrastructure;
using DesktopNMS.Services.Intelligence;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.ViewModels;

/// <summary>Saving the conversation as Markdown or PDF (#315).</summary>
public sealed partial class IntelligenceViewModel
{
    private readonly IntelligenceChatExporter? _exporter;

    /// <summary>Save as Markdown…</summary>
    public RelayCommand ExportMarkdownCommand { get; }

    /// <summary>Save as PDF…</summary>
    public RelayCommand ExportPdfCommand { get; }

    /// <summary>
    /// Any conversation with messages, saved or temporary. An answer still
    /// being written can be exported: it's marked as incomplete.
    /// </summary>
    public bool CanExportChat => _exporter is not null && ShowConversation && Messages.Count > 0;

    /// <summary>The selected chat's name, or what a temporary chat is called.</summary>
    internal string ExportTitle => IsSingleChatMode ? "Temporary chat"
        : Chats?.SelectedChat?.Title is { Length: > 0 } title ? title : "Intelligence chat";

    private void RaiseExportCommands()
    {
        // Raised from RaiseCommands too, which can run before the constructor reaches these.
        ExportMarkdownCommand?.RaiseCanExecuteChanged();
        ExportPdfCommand?.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanExportChat));
    }

    private void ExportChat(bool pdf)
    {
        if (_exporter is null || !CanExportChat) return;

        // Taken before the dialog opens, so the file holds what was on screen when Save as was chosen.
        var chat = ExportedChat.From(ExportTitle, Messages, DateTimeOffset.Now);
        var extension = pdf ? ".pdf" : ".md";
        var path = UiPlatform.FileDialogs.PickFileToSave(new FileDialogOptions(
            pdf ? "PDF files (*.pdf)|*.pdf" : "Markdown files (*.md)|*.md",
            IntelligenceChatExporter.FileStem(chat.Title) + extension,
            pdf ? "Save chat as PDF" : "Save chat as Markdown",
            extension));
        if (path is null) return;

        try
        {
            if (pdf) _exporter.SavePdf(chat, path);
            else _exporter.SaveMarkdown(chat, path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't export the Intelligence chat as {Format}", pdf ? "PDF" : "Markdown");
            _windows.ShowError("Couldn't save the chat",
                "DashyNMS couldn't write the file. Check that the folder still exists and that you can save files there, then try again.");
        }
    }
}
