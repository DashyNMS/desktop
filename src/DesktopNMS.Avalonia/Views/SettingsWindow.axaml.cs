using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DesktopNMS.Core.Api;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

public partial class SettingsWindow : Window
{
    private static readonly TimeSpan SecretClickWindow = TimeSpan.FromSeconds(2);

    private readonly SettingsViewModel? _viewModel;
    private readonly List<DateTime> _secretClicks = new();

    /// <summary>For the designer.</summary>
    public SettingsWindow()
    {
        InitializeComponent();
    }

    public SettingsWindow(SettingsViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        viewModel.RequestClose += OnRequestClose;
        Closed += (_, _) => viewModel.Detach();

        AboutLogo.PointerPressed += OnAboutLogoPressed;

        // Any click ends the penguins early - tunnelling and not handled, so
        // the click still does whatever it was for. Esc ends them too, and only
        // that, rather than also cancelling the window.
        AddHandler(PointerPressedEvent, (_, _) =>
        {
            if (SmileAndWave.IsPlaying)
            {
                SmileAndWave.Dismiss();
            }
        }, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape && SmileAndWave.IsPlaying)
            {
                SmileAndWave.Dismiss();
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);

        // A pasted "Bearer …" token goes in as just the token (#261).
        ServerTokenBox.TextChanged += (_, _) =>
        {
            if (ServerTokenBox.Text is { } text && ApiTokenText.TryExtract(text, out var token) && token != text)
            {
                ServerTokenBox.Text = token;
            }
        };
    }

    /// <summary>True once saved; closing any other way discards the changes.</summary>
    public bool Saved { get; private set; }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    /// <summary>Three shift-clicks on the About logo, close together.</summary>
    private void OnAboutLogoPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            _secretClicks.Clear();
            return;
        }

        var now = DateTime.UtcNow;
        _secretClicks.RemoveAll(t => now - t > SecretClickWindow);
        _secretClicks.Add(now);

        if (_secretClicks.Count >= 3)
        {
            _secretClicks.Clear();
            SmileAndWave.Play();
        }
    }

    private void OnRequestClose(object? sender, bool saved)
    {
        _viewModel!.RequestClose -= OnRequestClose;
        Saved = saved;
        Close();
    }
}
