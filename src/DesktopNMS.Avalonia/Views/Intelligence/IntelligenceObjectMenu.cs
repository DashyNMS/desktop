using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DesktopNMS.Infrastructure;
using DesktopNMS.ViewModels;
using FluentIcons.Avalonia;
using FluentIcons.Common;

namespace DesktopNMS.Views.Intelligence;

/// <summary>Adds an object action to existing row menus, or supplies a menu for cards.</summary>
internal static class IntelligenceObjectMenu
{
    private const string ItemTag = "AskIntelligence";

    public static void Attach(Window window, IntelligenceViewModel intelligence, Func<string?> server, Action show)
        => Attach(window, value => intelligence.ObjectRequest(value, server()), () => intelligence.ShowObjectActions,
            () => intelligence.CanStartObjectChat, async target => { show(); await intelligence.StartObjectChatAsync(target); });

    internal static void Attach(Window window, Func<object?, IntelligenceChatRequest?> request,
        Func<bool> showActions, Func<bool> canStart, Func<IntelligenceChatRequest, Task> start)
    {
        window.AddHandler(Control.ContextRequestedEvent, (sender, e) =>
        {
            if (e.Source is not Control source) return;
            var ancestors = source.GetVisualAncestors().OfType<Control>().Prepend(source).ToArray();
            if (ancestors.Any(c => c is DataGridColumnHeader)) return;
            var host = ancestors.FirstOrDefault(c => c.ContextMenu is not null);
            var target = ancestors.Select(c => request(c.Tag is SpotlightOption ? c.Tag : c.DataContext)).FirstOrDefault(t => t is not null);
            if (target is null || !showActions())
            {
                if (host?.ContextMenu?.Items.OfType<MenuItem>().FirstOrDefault(i => Equals(i.Tag, ItemTag)) is { } old) old.IsVisible = false;
                return;
            }
            host ??= ancestors.FirstOrDefault(c => c is DataGridRow or Button or ListBoxItem) ?? source;
            var created = host.ContextMenu is null;
            host.ContextMenu ??= new ContextMenu();
            var menu = host.ContextMenu;
            var item = menu.Items.OfType<MenuItem>().FirstOrDefault(i => Equals(i.Tag, ItemTag));
            if (item is null)
            {
                item = new MenuItem { Header = "Ask Intelligence", Tag = ItemTag, Icon = new SymbolIcon { Symbol = Symbol.Sparkle } };
                menu.Items.Insert(0, item);
            }
            item.IsVisible = true;
            item.Command = new AsyncRelayCommand(() => start(target), canStart);
            ToolTip.SetTip(item, !canStart() ? "Available when the current answer finishes" : "Start a new chat with this object in Spotlight");
            if (created)
            {
                // Routed handlers are collected before dispatch. A menu attached during
                // this request cannot receive it, so open the first request ourselves.
                menu.Placement = e.TryGetPosition(host, out _) ? PlacementMode.Pointer : PlacementMode.Bottom;
                menu.Open(host);
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
    }
}
