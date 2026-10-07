using System.Windows;
using System.Windows.Controls;
using DesktopNMS.Core.Api;

namespace DesktopNMS.Infrastructure;

/// <summary>
/// The API token boxes' paste (#261): a LibreNMS token pasted with stray
/// whitespace or a leading "Bearer " goes in as just the token, read by
/// Core's <see cref="ApiTokenText"/> so both apps accept the same things.
/// Anything that isn't a token pastes as it is.
/// </summary>
public static class TokenBoxes
{
    public static void CleanPastes(PasswordBox box) => DataObject.AddPastingHandler(box, OnPasting);

    private static void OnPasting(object sender, DataObjectPastingEventArgs e)
    {
        if (sender is not PasswordBox box
            || e.SourceDataObject.GetData(DataFormats.UnicodeText) is not string text
            || !ApiTokenText.TryExtract(text, out var token)
            || token == text)
        {
            return;
        }

        e.CancelCommand();
        box.Password = token;
    }

    /// <summary>
    /// A token on the clipboard, if that's what's there - read only when the
    /// user acts (focuses the box, comes back to the window), never in the
    /// background. Null for anything else, or a clipboard another app holds.
    /// </summary>
    public static string? CopiedToken()
    {
        try
        {
            return Clipboard.ContainsText() && ApiTokenText.TryExtract(Clipboard.GetText(), out var token) ? token : null;
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            return null;
        }
    }
}
