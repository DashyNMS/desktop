using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
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
        viewModel.Applied += OnApplied;
        viewModel.PropertyChanged += OnSettingsSectionChanged;
        Closed += (_, _) =>
        {
            viewModel.RequestClose -= OnRequestClose;
            viewModel.Applied -= OnApplied;
            viewModel.PropertyChanged -= OnSettingsSectionChanged;
            viewModel.Detach();
        };

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

    /// <summary>True once any changes have been saved, including through Apply.</summary>
    public bool Saved { get; private set; }

    private void OnSettingsSectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.SelectedSection)) SettingsContent.Offset = default;
    }

    private void OnApplied(object? sender, EventArgs e) => Saved = true;

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
        Saved |= saved;
        Close();
    }

    /// <summary>A row in Settings › Providers was clicked: show that provider's details, unless the click was on its bin.</summary>
    private void OnProviderRowPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual { } source && source.FindAncestorOfType<Button>(includeSelf: true) is not null) return;
        SelectProvider(sender);
    }

    /// <summary>Enter or Space chooses a focused provider row, as a click does.</summary>
    private void OnProviderRowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space) || !ReferenceEquals(e.Source, sender)) return;
        SelectProvider(sender);
        e.Handled = true;
    }

    private void SelectProvider(object? sender)
    {
        if (sender is Control { DataContext: IntelligenceProviderEntryViewModel entry } && DataContext is SettingsViewModel settings)
        {
            settings.IntelligenceProviders.Selected = entry;
        }
    }
}
