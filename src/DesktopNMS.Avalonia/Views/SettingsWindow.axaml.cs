using Avalonia.Controls;
using Avalonia.Interactivity;
using DesktopNMS.Core.Api;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel? _viewModel;

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

    private void OnRequestClose(object? sender, bool saved)
    {
        _viewModel!.RequestClose -= OnRequestClose;
        Saved = saved;
        Close();
    }
}
