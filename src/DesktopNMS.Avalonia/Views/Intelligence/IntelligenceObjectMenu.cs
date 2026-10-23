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
    {
        window.AddHandler(Control.ContextRequestedEvent, (_, e) =>
        {
            if (e.Source is not Control source) return;
            var ancestors = source.GetVisualAncestors().OfType<Control>().Prepend(source).ToArray();
            if (ancestors.Any(c => c is DataGridColumnHeader)) return;
            var host = ancestors.FirstOrDefault(c => c.ContextMenu is not null);
            var target = ancestors.Select(c => intelligence.ObjectRequest(c.Tag is SpotlightOption ? c.Tag : c.DataContext, server())).FirstOrDefault(t => t is not null);
            if (target is null || !intelligence.ShowObjectActions)
            {
                if (host?.ContextMenu?.Items.OfType<MenuItem>().FirstOrDefault(i => Equals(i.Tag, ItemTag)) is { } old) old.IsVisible = false;
                return;
            }
            host ??= ancestors.FirstOrDefault(c => c is DataGridRow) ?? source;
            host.ContextMenu ??= new ContextMenu();
            var menu = host.ContextMenu;
            var item = menu.Items.OfType<MenuItem>().FirstOrDefault(i => Equals(i.Tag, ItemTag));
            if (item is null)
            {
                item = new MenuItem { Header = "Ask Intelligence", Tag = ItemTag, Icon = new SymbolIcon { Symbol = Symbol.Sparkle } };
                menu.Items.Insert(0, item);
            }
            item.IsVisible = true;
            item.Command = new AsyncRelayCommand(async () => { show(); await intelligence.StartObjectChatAsync(target); }, () => intelligence.CanStartObjectChat);
            ToolTip.SetTip(item, intelligence.IsBusy ? "Available when the current answer finishes" : "Start a new chat with this object in Spotlight");
        }, RoutingStrategies.Tunnel);
    }
}
