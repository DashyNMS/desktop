using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        _viewModel = viewModel;

        InitializeComponent();
        DesktopNMS.Infrastructure.TokenBoxes.CleanPastes(ServerTokenBox);

        DataContext = viewModel;
        _viewModel.RequestClose += OnRequestClose;
        Closed += (_, _) => _viewModel.Detach();
    }

    /// <summary>PasswordBox does not expose a bindable password, by design - see ConnectionWindow's identical pattern for the LibreNMS token.</summary>
    private void OnUnimusTokenChanged(object sender, RoutedEventArgs e)
        => _viewModel.UnimusTokenInput = ((PasswordBox)sender).Password;

    private void OnGraylogPasswordChanged(object sender, RoutedEventArgs e)
        => _viewModel.GraylogPasswordInput = ((PasswordBox)sender).Password;

    private void OnServerTokenChanged(object sender, RoutedEventArgs e)
        => _viewModel.ServerTokenInput = ((PasswordBox)sender).Password;

    /// <summary>Three Shift+clicks on the About logo within this window start the penguins.</summary>
    private static readonly TimeSpan SecretClickWindow = TimeSpan.FromSeconds(2);

    private readonly List<DateTime> _secretClicks = new();

    private void OnAboutLogoMouseDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0)
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
                SmileAndWave.Play(SmileAndWave.ActualWidth);
            }
        }
        catch (Exception)
        {
            // Cosmetic only - never let it affect the window.
        }
    }

    /// <summary>Any click ends the penguins early. Not handled, so the click still does whatever it was for.</summary>
    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (SmileAndWave.IsPlaying)
        {
            SmileAndWave.Dismiss();
        }
    }

    /// <summary>Esc ends the penguins early - and only that, rather than also cancelling the window.</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && SmileAndWave.IsPlaying)
        {
            SmileAndWave.Dismiss();
            e.Handled = true;
        }
    }

    private void OnRequestClose(object? sender, bool saved)
    {
        _viewModel.RequestClose -= OnRequestClose;
        DialogResult = saved;
    }
}
