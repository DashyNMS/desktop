using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Threading;
using DesktopNMS.Core.Api;

namespace DesktopNMS.Views;

/// <summary>
/// Turns a button or menu item off once LibreNMS has refused the token the
/// write it makes (#51), with the reason as its tooltip:
/// <code>&lt;Button Command="{Binding DeleteCommand}" views:PermissionGate.Requires="DeleteDevices" /&gt;</code>
/// Nothing changes until a refusal - see <see cref="ApiPermissions"/> - and a
/// new connection turns everything back on. The WPF app's PermissionGate (#250):
/// here the override sits above the control's own values and is lifted
/// afterwards, so its tooltip binding comes back untouched.
/// </summary>
public sealed class PermissionGate
{
    public static readonly AttachedProperty<ApiPermission?> RequiresProperty =
        AvaloniaProperty.RegisterAttached<PermissionGate, Control, ApiPermission?>("Requires");

    /// <summary>The live connection's permissions - set once at start-up.</summary>
    public static ApiPermissions? Source { get; set; }

    /// <summary>A gated control's overrides, lifted when the permission is no longer refused.</summary>
    private static readonly ConditionalWeakTable<Control, Gate> Gates = new();

    static PermissionGate()
    {
        RequiresProperty.Changed.AddClassHandler<Control>((control, _) => Track(control));
    }

    public static ApiPermission? GetRequires(Control element) => element.GetValue(RequiresProperty);

    public static void SetRequires(Control element, ApiPermission? value) => element.SetValue(RequiresProperty, value);

    private static void Track(Control control)
    {
        var gate = Gates.GetValue(control, c => new Gate(c));
        if (GetRequires(control) is null)
        {
            gate.Stop();
        }
        else
        {
            gate.Start();
        }
    }

    private sealed class Gate
    {
        private readonly WeakReference<Control> _control;
        private readonly EventHandler _onChanged;
        private IDisposable? _enabled;
        private IDisposable? _tip;
        private IDisposable? _showOnDisabled;
        private bool _listening;

        public Gate(Control control)
        {
            _control = new WeakReference<Control>(control);
            _onChanged = (_, _) => Dispatcher.UIThread.Post(Apply);
            control.AttachedToVisualTree += (_, _) => Start();
            control.DetachedFromVisualTree += (_, _) => Stop();
        }

        public void Start()
        {
            if (!_listening && Source is { } source)
            {
                source.Changed += _onChanged;
                _listening = true;
            }

            Apply();
        }

        public void Stop()
        {
            if (_listening && Source is { } source)
            {
                source.Changed -= _onChanged;
                _listening = false;
            }
        }

        private void Apply()
        {
            if (!_control.TryGetTarget(out var control))
            {
                return;
            }

            var permission = GetRequires(control);
            var refused = permission is { } p && Source?.IsRefused(p) == true;

            if (refused)
            {
                // Keep the reason current: the rule editor swaps between create and edit.
                _tip?.Dispose();
                _tip = control.SetValue(ToolTip.TipProperty, Source!.Reason(permission!.Value), BindingPriority.Animation);
                _enabled ??= control.SetValue(InputElement.IsEnabledProperty, false, BindingPriority.Animation);
                _showOnDisabled ??= control.SetValue(ToolTip.ShowOnDisabledProperty, true, BindingPriority.Animation);
            }
            else
            {
                _enabled?.Dispose();
                _tip?.Dispose();
                _showOnDisabled?.Dispose();
                _enabled = _tip = _showOnDisabled = null;
            }
        }
    }
}
