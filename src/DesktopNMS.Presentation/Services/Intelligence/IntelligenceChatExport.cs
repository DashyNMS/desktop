using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using DesktopNMS.Core.Intelligence;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Services.Intelligence;

/// <summary>A graph an answer drew on, as it was received.</summary>
public sealed record ExportedChatImage(byte[] Data, string MimeType)
{
    /// <summary>The file extension for the Markdown export's images folder.</summary>
    public string Extension => MimeType.ToLowerInvariant() switch
    {
        "image/jpeg" or "image/jpg" => "jpg",
        "image/gif" => "gif",
        "image/webp" => "webp",
        "image/svg+xml" => "svg",
        _ => "png",
    };
}

/// <summary>One message as exported: who said it, when, and what the reader saw.</summary>
/// <param name="Markdown">An answer's readable Markdown, with device and alert references already written out as names. A question's plain text.</param>
/// <param name="Note">Why an answer may be incomplete, or null when it finished.</param>
public sealed record ExportedChatMessage(bool IsUser, DateTimeOffset? At, string Markdown, IReadOnlyList<ExportedChatImage> Images, string? Note);

/// <summary>
/// An Intelligence chat ready to save as Markdown or PDF (#315). It holds only
/// what the conversation shows: the questions, the answers and their graphs.
/// Lookups, tool records, the model's history and sign-in details stay out.
/// </summary>
public sealed record ExportedChat(string Title, DateTimeOffset ExportedAt, IReadOnlyList<ExportedChatMessage> Messages)
{
    public const string UserLabel = "You";
    public const string AssistantLabel = "Dashy";

    public static ExportedChat From(string title, IEnumerable<IntelligenceMessageViewModel> messages, DateTimeOffset exportedAt)
    {
        var exported = new List<ExportedChatMessage>();
        foreach (var message in messages)
        {
            if (message.IsUser)
            {
                if (!string.IsNullOrWhiteSpace(message.Text))
                    exported.Add(new(true, message.CreatedAt, message.Text.Trim(), Array.Empty<ExportedChatImage>(), null));
                continue;
            }

            var images = message.ImageFiles.Select(image => new ExportedChatImage(image.Data, image.MimeType)).ToArray();
            var note = Note(message);
            var markdown = message.CopyMarkdown;
            if (markdown.Length == 0 && images.Length == 0 && note is null) continue;
            exported.Add(new(false, message.CreatedAt, markdown, images, note));
        }

        return new(string.IsNullOrWhiteSpace(title) ? "Intelligence chat" : title.Trim(), exportedAt, exported);
    }

    /// <summary>
    /// An answer still being written is exported as far as it has got, and
    /// says so. One that was stopped, interrupted or failed says that too, so
    /// a reader doesn't take a partial answer as the whole of it.
    /// </summary>
    private static string? Note(IntelligenceMessageViewModel message) => message.Outcome switch
    {
        IntelligenceReplyOutcome.Generating => "This answer was still being written when the chat was exported. Only the part written so far is included.",
        IntelligenceReplyOutcome.Stopped => "This answer was stopped before it finished.",
        IntelligenceReplyOutcome.Interrupted => "This answer was interrupted before it finished.",
        IntelligenceReplyOutcome.Failed => message.HasFailureText ? "This answer failed: " + message.FailureText : "This answer failed.",
        _ => null,
    };

    /// <summary>A heading for a message: "You · 3 Nov 2026, 14:05".</summary>
    public static string Heading(ExportedChatMessage message)
        => (message.IsUser ? UserLabel : AssistantLabel) + (message.At is { } at ? " · " + at.ToLocalTime().ToString("d MMM yyyy, HH:mm") : string.Empty);

    /// <summary>The line under the title.</summary>
    public string Summary => $"Exported from DashyNMS Intelligence on {ExportedAt.ToLocalTime():d MMM yyyy, HH:mm}. "
        + (Messages.Count == 1 ? "1 message." : $"{Messages.Count} messages.");
}

/// <summary>Writes a chat as a PDF. The front end supplies it, as PDF drawing needs a graphics library.</summary>
public interface IIntelligenceChatPdfWriter
{
    void Write(ExportedChat chat, Stream output);
}

/// <summary>Saves an exported chat to a file the user picked. Nothing is sent anywhere.</summary>
public sealed class IntelligenceChatExporter
{
    private readonly IIntelligenceChatPdfWriter? _pdf;

    public IntelligenceChatExporter(IIntelligenceChatPdfWriter? pdf = null) => _pdf = pdf;

    public bool CanWritePdf => _pdf is not null;

    /// <summary>A file name from the chat title, without characters any of the three systems refuse.</summary>
    public static string FileStem(string title)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }).ToHashSet();
        var cleaned = new string(title.Select(c => invalid.Contains(c) || char.IsControl(c) ? ' ' : c).ToArray());
        cleaned = string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim('.', ' ');
        if (cleaned.Length > 80) cleaned = cleaned[..80].TrimEnd('.', ' ');
        return cleaned.Length == 0 ? "Intelligence chat" : cleaned;
    }

    /// <summary>
    /// The .md file, with any graphs in a folder beside it ("Chat-images"),
    /// linked by relative paths so the file and folder can be moved together.
    /// </summary>
    public void SaveMarkdown(ExportedChat chat, string path)
    {
        var folderName = Path.GetFileNameWithoutExtension(path) + "-images";
        var folder = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".", folderName);
        var markdown = IntelligenceChatMarkdown.Write(chat, (answer, index, image) =>
        {
            var name = $"answer-{answer}-graph-{index}.{image.Extension}";
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, name), image.Data);
            return Uri.EscapeDataString(folderName) + "/" + name;
        });
        File.WriteAllText(path, markdown, new UTF8Encoding(false));
    }

    public void SavePdf(ExportedChat chat, string path)
    {
        if (_pdf is null) throw new InvalidOperationException("PDF export isn't available.");

        // Written to a temporary file first, so a failure part-way through
        // never leaves a broken PDF where the user expects their export.
        var temporary = path + ".partial";
        try
        {
            using (var stream = File.Create(temporary))
            {
                _pdf.Write(chat, stream);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

/// <summary>The Markdown export: a title, then each message under its own heading.</summary>
public static partial class IntelligenceChatMarkdown
{
    /// <param name="saveImage">Saves one graph - the answer's number, the graph's number in it, the image - and returns its relative link.</param>
    public static string Write(ExportedChat chat, Func<int, int, ExportedChatImage, string> saveImage)
    {
        var text = new StringBuilder();
        text.Append("# ").Append(EscapeLine(chat.Title)).Append("\n\n");
        text.Append('*').Append(chat.Summary).Append("*\n");

        var answer = 0;
        foreach (var message in chat.Messages)
        {
            text.Append("\n---\n\n## ").Append(EscapeLine(ExportedChat.Heading(message))).Append("\n\n");
            if (message.IsUser)
            {
                text.Append(EscapeQuestion(message.Markdown)).Append('\n');
                continue;
            }

            answer++;
            if (message.Note is { } note) text.Append("> **Note:** ").Append(EscapeLine(note)).Append("\n\n");
            for (var i = 0; i < message.Images.Count; i++)
                text.Append("![Graph ").Append(i + 1).Append("](").Append(saveImage(answer, i + 1, message.Images[i])).Append(")\n\n");
            if (message.Markdown.Length > 0) text.Append(DemoteHeadings(message.Markdown)).Append('\n');
        }

        return text.ToString().TrimEnd() + "\n";
    }

    /// <summary>
    /// An answer's own headings move two levels down, under its message
    /// heading, so the document outline stays chat, then message, then section.
    /// </summary>
    internal static string DemoteHeadings(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var inCode = false;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inCode = !inCode;
                continue;
            }

            if (!inCode && HeadingLine().Match(lines[i]) is { Success: true } heading)
                lines[i] = new string('#', Math.Min(6, heading.Groups[1].Length + 2)) + heading.Groups[2].Value;
        }

        return string.Join('\n', lines);
    }

    /// <summary>A question is plain text, so anything Markdown would read as formatting is escaped.</summary>
    internal static string EscapeQuestion(string question)
    {
        var lines = question.Replace("\r\n", "\n").Split('\n').Select(line =>
        {
            // Line starts that would become a heading, list or rule ("> " is already escaped).
            var escaped = SymbolStart().Replace(EscapeLine(line), "$1\\$2");
            return NumberStart().Replace(escaped, "$1\\$2");
        });

        // A single line break would join lines in most viewers.
        return string.Join("  \n", lines);
    }

    private static string EscapeLine(string text) => InlineSpecial().Replace(text, match => "\\" + match.Value);

    [GeneratedRegex(@"^(#{1,6})(\s.*)$")]
    private static partial Regex HeadingLine();

    [GeneratedRegex(@"[\\`*_\[\]<>|]")]
    private static partial Regex InlineSpecial();

    [GeneratedRegex(@"^(\s*)([#+-])")]
    private static partial Regex SymbolStart();

    [GeneratedRegex(@"^(\s*\d+)([.)])")]
    private static partial Regex NumberStart();
}
