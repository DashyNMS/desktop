using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DesktopNMS.Core.Api;

namespace DesktopNMS.Views;

/// <summary>
/// Turns a button or menu item off once LibreNMS has refused the token the
/// write it makes (#51), with the reason as its tooltip:
/// <code>&lt;Button Command="{Binding DeleteCommand}" views:PermissionGate.Requires="DeleteDevices" /&gt;</code>
/// Nothing changes until a refusal - see <see cref="ApiPermissions"/> - and
/// a new connection turns everything back on. Not for a control that binds
/// IsEnabled itself: this sets it locally while the permission is refused.
/// </summary>
public static class PermissionGate
{
    public static readonly DependencyProperty RequiresProperty = DependencyProperty.RegisterAttached(
        "Requires",
        typeof(ApiPermission?),
        typeof(PermissionGate),
        new PropertyMetadata(null, OnRequiresChanged));

    /// <summary>The control's own tooltip, put back when the permission is no longer refused.</summary>
    private static readonly DependencyProperty SavedToolTipProperty = DependencyProperty.RegisterAttached(
        "SavedToolTip",
        typeof(object),
        typeof(PermissionGate),
        new PropertyMetadata(null));

    /// <summary>Stands for "no tooltip of its own" - a property's default can't be UnsetValue.</summary>
    private static readonly object NoToolTip = new();

    private static readonly DependencyProperty IsGatedProperty = DependencyProperty.RegisterAttached(
        "IsGated",
        typeof(bool),
        typeof(PermissionGate),
        new PropertyMetadata(false));

    /// <summary>The live connection's permissions - set once at startup.</summary>
    public static ApiPermissions? Source { get; set; }

    public static ApiPermission? GetRequires(DependencyObject element) => (ApiPermission?)element.GetValue(RequiresProperty);

    public static void SetRequires(DependencyObject element, ApiPermission? value) => element.SetValue(RequiresProperty, value);

    private static void OnRequiresChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        if (e.OldValue is null && e.NewValue is not null)
        {
            element.Loaded += OnLoaded;
            element.Unloaded += OnUnloaded;
            if (element.IsLoaded)
            {
                Watch(element);
            }
        }
        else if (e.NewValue is null)
        {
            element.Loaded -= OnLoaded;
            element.Unloaded -= OnUnloaded;
        }

        Apply(element);
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        var element = (FrameworkElement)sender;
        Watch(element);
        Apply(element);
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (Source is { } source)
        {
            source.Changed -= ((FrameworkElement)sender).GetChangedHandler();
        }
    }

    private static void Watch(FrameworkElement element)
    {
        if (Source is { } source)
        {
            var handler = element.GetChangedHandler();
            source.Changed -= handler;
            source.Changed += handler;
        }
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FrameworkElement, EventHandler> Handlers = new();

    /// <summary>One handler per element, so subscribing again on a reload replaces rather than doubles it.</summary>
    private static EventHandler GetChangedHandler(this FrameworkElement element) =>
        Handlers.GetValue(element, e => (_, _) => e.Dispatcher.BeginInvoke(() => Apply(e)));

    private static void Apply(FrameworkElement element)
    {
        var refused = GetRequires(element) is { } permission && Source?.IsRefused(permission) == true;
        var gated = (bool)element.GetValue(IsGatedProperty);

        if (refused && !gated)
        {
            var own = element.ReadLocalValue(FrameworkElement.ToolTipProperty);
            element.SetValue(SavedToolTipProperty, own == DependencyProperty.UnsetValue ? NoToolTip : own);
            element.SetValue(IsGatedProperty, true);
            element.IsEnabled = false;
            element.ToolTip = ApiPermissions.Describe(GetRequires(element)!.Value);
            ToolTipService.SetShowOnDisabled(element, true);
        }
        else if (!refused && gated)
        {
            element.SetValue(IsGatedProperty, false);
            element.ClearValue(UIElement.IsEnabledProperty);
            element.ClearValue(ToolTipService.ShowOnDisabledProperty);

            switch (element.GetValue(SavedToolTipProperty))
            {
                case BindingExpressionBase expression:
                    BindingOperations.SetBinding(element, FrameworkElement.ToolTipProperty, expression.ParentBindingBase);
                    break;
                case var saved when saved == NoToolTip:
                    element.ClearValue(FrameworkElement.ToolTipProperty);
                    break;
                case var saved:
                    element.ToolTip = saved;
                    break;
            }

            element.ClearValue(SavedToolTipProperty);
        }
        else if (refused)
        {
            // The rule editor swaps between create and edit: keep the reason current.
            element.ToolTip = ApiPermissions.Describe(GetRequires(element)!.Value);
        }
    }
}
