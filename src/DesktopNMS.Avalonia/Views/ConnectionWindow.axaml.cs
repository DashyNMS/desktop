using Avalonia.Input.Platform;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DesktopNMS.Core.Api;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

public partial class ConnectionWindow : Window
{
    private readonly ConnectionViewModel? _viewModel;

    /// <summary>A token on the clipboard, waiting for "Use it".</summary>
    private string? _copiedToken;

    /// <summary>For the designer.</summary>
    public ConnectionWindow()
    {
        InitializeComponent();
    }

    public ConnectionWindow(ConnectionViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        viewModel.RequestClose += OnRequestClose;

        // A pasted token with stray whitespace or a leading "Bearer " goes in as
        // just the token (#261), as the WPF app's TokenBoxes.CleanPastes does.
        TokenBox.TextChanged += (_, _) =>
        {
            if (TokenBox.Text is { } text && ApiTokenText.TryExtract(text, out var token) && token != text)
            {
                TokenBox.Text = token;
            }

            if (!string.IsNullOrEmpty(TokenBox.Text))
            {
                CopiedTokenOffer.IsVisible = false;
            }
        };

        // The clipboard is only read when the person acts - clicking into the
        // token box, or coming back to the window from LibreNMS - never in the
        // background (#261).
        viewModel.TokenCreated += OnTokenCreated;
        Closed += (_, _) => viewModel.TokenCreated -= OnTokenCreated;
        TokenBox.GotFocus += (_, _) => _ = OfferCopiedTokenAsync();
        Activated += (_, _) => _ = OfferCopiedTokenAsync();

        // Land on whichever field is still empty.
        Opened += (_, _) => (string.IsNullOrWhiteSpace(ServerBox.Text) ? ServerBox : TokenBox).Focus();
    }

    /// <summary>True once signed in; closing any other way is a cancel.</summary>
    public bool Succeeded { get; private set; }

    private async Task OfferCopiedTokenAsync()
    {
        if (_viewModel?.ShowsTokenEntry != true || !string.IsNullOrEmpty(TokenBox.Text) || Clipboard is not { } clipboard)
        {
            return;
        }

        string? text;
        try
        {
            text = await clipboard.TryGetTextAsync().ConfigureAwait(true);
        }
        catch (Exception)
        {
            // Another app holds the clipboard.
            return;
        }

        _copiedToken = text is not null && ApiTokenText.TryExtract(text, out var token) ? token : null;
        CopiedTokenOffer.IsVisible = _copiedToken is not null && string.IsNullOrEmpty(TokenBox.Text);
    }

    private void OnUseCopiedToken(object? sender, RoutedEventArgs e)
    {
        if (_copiedToken is { } token)
        {
            TokenBox.Text = token;
        }

        CopiedTokenOffer.IsVisible = false;
    }

    private void OnTokenCreated(object? sender, string token) => TokenBox.Text = token;

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private void OnRequestClose(object? sender, bool success)
    {
        _viewModel!.RequestClose -= OnRequestClose;
        Succeeded = success;
        Close();
    }
}
