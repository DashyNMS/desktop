using System.Windows;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

public partial class ConnectionWindow : Window
{
    private readonly ConnectionViewModel _viewModel;

    public ConnectionWindow(ConnectionViewModel viewModel)
    {
        _viewModel = viewModel;

        InitializeComponent();
        DesktopNMS.Infrastructure.TokenBoxes.CleanPastes(TokenBox);

        DataContext = viewModel;
        _viewModel.RequestClose += OnRequestClose;

        // A token "Sign in with LibreNMS" made but couldn't sign in with: in the box, to try again.
        _viewModel.TokenCreated += (_, token) => TokenBox.Password = token;

        Loaded += (_, _) =>
        {
            // Land on whichever field is still empty.
            if (string.IsNullOrWhiteSpace(ServerBox.Text) || !_viewModel.UsesToken)
            {
                ServerBox.Focus();
            }
            else
            {
                TokenBox.Focus();
            }
        };
    }

    /// <summary>A token on the clipboard, waiting for "Use it".</summary>
    private string? _copiedToken;

    /// <summary>Clicking into the token box: offer a token copied from LibreNMS.</summary>
    private void OnTokenBoxFocused(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e) => OfferCopiedToken();

    /// <summary>
    /// Coming back to the window (from LibreNMS's API Tokens page, say) is the
    /// user acting too - the clipboard is only ever read then, or on focus,
    /// never in the background (#261).
    /// </summary>
    protected override void OnActivated(System.EventArgs e)
    {
        base.OnActivated(e);
        if (IsLoaded)
        {
            OfferCopiedToken();
        }
    }

    private void OfferCopiedToken()
    {
        // Only while the box is showing and still empty - a token already typed in stays as it is.
        var token = _viewModel.ShowsTokenEntry && string.IsNullOrEmpty(TokenBox.Password)
            ? DesktopNMS.Infrastructure.TokenBoxes.CopiedToken()
            : null;
        _copiedToken = token;
        CopiedTokenOffer.Visibility = token is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnUseCopiedToken(object sender, RoutedEventArgs e)
    {
        if (_copiedToken is { } token)
        {
            TokenBox.Password = token;
        }

        CopiedTokenOffer.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// PasswordBox does not expose a bindable password, by design, so the value
    /// is pushed to the view model here instead.
    /// </summary>
    private void OnTokenChanged(object sender, RoutedEventArgs e)
    {
        _viewModel.ApiToken = TokenBox.Password;
        if (TokenBox.Password.Length > 0)
        {
            CopiedTokenOffer.Visibility = Visibility.Collapsed;
        }
    }

    private void OnRequestClose(object? sender, bool success)
    {
        _viewModel.RequestClose -= OnRequestClose;
        DialogResult = success;
    }
}
