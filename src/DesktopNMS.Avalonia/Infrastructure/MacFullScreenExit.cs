using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace DesktopNMS.Infrastructure;

/// <summary>Observes AppKit completion rather than Avalonia's requested WindowState.</summary>
internal sealed class MacFullScreenExit : IDisposable
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private static readonly Dictionary<IntPtr, Action> Callbacks = new();
    private static readonly NotificationCallback Callback = OnNotification;
    private static readonly Lazy<IntPtr> ObserverClass = new(CreateObserverClass);
    private readonly IntPtr _center;
    private IntPtr _observer;

    public MacFullScreenExit(Window window, Action completed)
    {
        var handle = window.TryGetPlatformHandle();
        if (handle is not { HandleDescriptor: "NSWindow" } || handle.Handle == IntPtr.Zero)
            throw new InvalidOperationException("Expected a macOS NSWindow handle.");

        _center = Send(objc_getClass("NSNotificationCenter"), sel_registerName("defaultCenter"));
        _observer = Send(Send(ObserverClass.Value, sel_registerName("alloc")), sel_registerName("init"));
        Callbacks.Add(_observer, completed);
        var name = SendString(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"),
            "NSWindowDidExitFullScreenNotification");
        AddObserver(_center, sel_registerName("addObserver:selector:name:object:"), _observer,
            sel_registerName("fullscreenExited:"), name, handle.Handle);
    }

    public void Dispose()
    {
        if (_observer == IntPtr.Zero) return;
        SendArgument(_center, sel_registerName("removeObserver:"), _observer);
        Callbacks.Remove(_observer);
        Send(_observer, sel_registerName("release"));
        _observer = IntPtr.Zero;
    }

    private static IntPtr CreateObserverClass()
    {
        var type = objc_allocateClassPair(objc_getClass("NSObject"), "DashyNMSFullScreenExitObserver", 0);
        if (type == IntPtr.Zero || !class_addMethod(type, sel_registerName("fullscreenExited:"),
                Marshal.GetFunctionPointerForDelegate(Callback), "v@:@"))
            throw new InvalidOperationException("Could not register fullscreen notification observer.");
        objc_registerClassPair(type);
        return type;
    }

    private static void OnNotification(IntPtr self, IntPtr selector, IntPtr notification)
    {
        if (Callbacks.TryGetValue(self, out var completed)) completed();
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void NotificationCallback(IntPtr self, IntPtr selector, IntPtr notification);

    [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC)] private static extern IntPtr objc_allocateClassPair(IntPtr superclass, string name, nuint extraBytes);
    [DllImport(ObjC)] private static extern void objc_registerClassPair(IntPtr type);
    [DllImport(ObjC)] [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool class_addMethod(IntPtr type, IntPtr selector, IntPtr implementation, string types);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendString(IntPtr receiver, IntPtr selector, string value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendArgument(IntPtr receiver, IntPtr selector, IntPtr argument);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void AddObserver(IntPtr receiver, IntPtr selector, IntPtr observer, IntPtr callback, IntPtr name, IntPtr window);
}
