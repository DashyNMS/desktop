using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using DesktopNMS.ViewModels;
using DesktopNMS.Views.Intelligence;
using FluentIcons.Avalonia;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(DesktopNMS.Avalonia.Tests.TestApplication))]

namespace DesktopNMS.Avalonia.Tests;

public sealed class TestApplication : Application
{
    public override void Initialize() => Styles.Add(new global::Avalonia.Themes.Fluent.FluentTheme());
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApplication>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public sealed class IntelligenceObjectMenuTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void First_right_click_opens_a_new_or_existing_menu_and_targets_the_clicked_interface(bool existing)
    {
        var row = new TopRowViewModel(12, 42, "Switch", "Ethernet1", "", "", 0, null);
        var label = new TextBlock { Text = "Interface" };
        var button = new Button { Content = label, DataContext = row, Width = 200, Height = 60 };
        if (existing) button.ContextMenu = new ContextMenu { Items = { new MenuItem { Header = "Open device" } } };
        var window = new Window { Content = button, Width = 300, Height = 150 };
        IntelligenceChatRequest? opened = null;
        Attach(window, request => opened = request);
        window.Show();
        try
        {
            window.MouseDown(new Point(100, 50), MouseButton.Right);
            window.MouseUp(new Point(100, 50), MouseButton.Right);
            var menu = button.GetVisualDescendants().OfType<Control>().Prepend(button).Select(control => control.ContextMenu).FirstOrDefault(menu => menu is not null);
            Assert.NotNull(menu);
            Assert.True(menu.IsOpen);
            var item = Assert.IsType<MenuItem>(menu.Items[0]);
            Assert.Equal("Ask Intelligence", item.Header);
            Assert.IsType<SymbolIcon>(item.Icon);
            item.Command!.Execute(null);
            Assert.Equal(new DesktopNMS.Core.Intelligence.IntelligenceObjectReference("port", 42, 12), opened!.Target.Identity);
            menu.Close();
            button.DataContext = new TopRowViewModel(13, 43, "Other switch", "Ethernet2", "", "", 0, null);
            window.MouseDown(new Point(100, 50), MouseButton.Right);
            window.MouseUp(new Point(100, 50), MouseButton.Right);
            Assert.True(menu.IsOpen);
            Assert.Single(menu.Items.OfType<MenuItem>(), item => Equals(item.Header, "Ask Intelligence"));
            item.Command!.Execute(null);
            Assert.Equal(43, opened!.Target.Id);
            menu.Close();
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Keyboard_request_opens_a_fresh_menu_but_busy_actions_are_disabled()
    {
        var button = new Button { Content = "Device", DataContext = new DevicePickerItem(12, "Switch") };
        var window = new Window { Content = button };
        IntelligenceObjectMenu.Attach(window,
            value => IntelligenceObjectTargets.From(value) is { } target ? new(target, "https://nms.example.net", "account") : null,
            () => true, () => false, _ => throw new InvalidOperationException("Busy action must not run"));
        window.Show();
        try
        {
            button.RaiseEvent(new ContextRequestedEventArgs());
            var menu = Assert.IsType<ContextMenu>(button.ContextMenu);
            Assert.True(menu.IsOpen);
            var item = Assert.IsType<MenuItem>(menu.Items[0]);
            Assert.False(item.Command!.CanExecute(null));
            Assert.False(item.IsEffectivelyEnabled);
            menu.Close();
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Unknown_objects_keep_their_existing_menu_without_an_intelligence_action()
    {
        var button = new Button { Content = "Unknown", DataContext = new object(), ContextMenu = new ContextMenu { Items = { new MenuItem { Header = "Existing" } } } };
        var window = new Window { Content = button };
        Attach(window, _ => throw new InvalidOperationException("Unknown object must not open a chat"));
        window.Show();
        try
        {
            button.RaiseEvent(new ContextRequestedEventArgs());
            Assert.True(button.ContextMenu.IsOpen);
            Assert.Equal("Existing", Assert.IsType<MenuItem>(Assert.Single(button.ContextMenu.Items)).Header);
            button.ContextMenu.Close();
        }
        finally { window.Close(); }
    }

    private static void Attach(Window window, Action<IntelligenceChatRequest> open) => IntelligenceObjectMenu.Attach(window,
        value => IntelligenceObjectTargets.From(value) is { } target ? new(target, "https://nms.example.net", "account") : null,
        () => true, () => true, request => { open(request); return Task.CompletedTask; });
}
