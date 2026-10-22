using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.Infrastructure;

/// <summary>
/// Gives DashyNMS a durable identity Windows can recognise for toast
/// notifications, by creating a real Start Menu shortcut carrying the same
/// AppUserModelID the toast library uses internally.
/// </summary>
/// <remarks>
/// <para>
/// <c>Microsoft.Toolkit.Uwp.Notifications</c>' modern <c>ToastNotificationManagerCompat</c>
/// deliberately supports unpackaged apps with no Start Menu shortcut: it derives
/// an ad-hoc AppUserModelID from the running executable's path (backslashes
/// replaced with forward slashes, since an AUMID may not contain a backslash).
/// That is enough for the WinRT <c>ToastNotifier.Show</c> call to succeed
/// without throwing, but it does not guarantee the shell actually renders or
/// records the toast: without a shortcut Windows has no independent
/// confirmation that "this AUMID" corresponds to a real, discoverable
/// application, and on some builds it silently declines to deliver a toast
/// from an identity it cannot otherwise place, and never creates the
/// <c>Notifications\Settings</c> entry that Settings' per-app list is built
/// from.
/// </para>
/// <para>
/// This class closes that gap without abandoning the modern activation model:
/// it creates a Start Menu shortcut whose target is the current executable and
/// whose <c>System.AppUserModel.ID</c> property is set to that same forward-
/// slashed path, computed the same way the library does. Activation still goes
/// through <c>ToastNotificationManagerCompat.OnActivated</c>, unchanged.
/// </para>
/// </remarks>
internal static class ToastIdentity
{
    private const string ShortcutFileName = "DashyNMS.lnk";

    private static readonly Guid AppUserModelIdFmtId = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
    private const uint AppUserModelIdPropertyId = 5;

    /// <summary>
    /// The AppUserModelID <c>ToastNotificationManagerCompat</c> derives for the
    /// current process. AUMIDs cannot contain a backslash, so the library
    /// substitutes forward slashes; matching that exactly is what lets the
    /// shortcut's identity line up with the one actually used to send toasts.
    /// </summary>
    public static string CurrentProcessAumid { get; } = ResolveProcessPath().Replace('\\', '/');

    /// <summary>
    /// Creates or refreshes the Start Menu shortcut so it points at the
    /// currently running executable. Safe to call on every launch: it is a
    /// no-op once the shortcut already matches, and self-heals after the app
    /// moves (a new build output folder, a fresh publish location, and so on).
    /// </summary>
    public static void EnsureStartMenuShortcut(ILogger logger)
    {
        try
        {
            var exePath = ResolveProcessPath();
            var shortcutPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                ShortcutFileName);

            if (File.Exists(shortcutPath) && TargetMatches(shortcutPath, exePath))
            {
                return;
            }

            CreateShortcut(shortcutPath, exePath, CurrentProcessAumid);
            logger.LogInformation(
                "Created/updated the Start Menu shortcut at {Path} so Windows recognises DashyNMS as a real app for notifications",
                shortcutPath);
        }
        catch (Exception ex)
        {
            // Toasts fall back to tray balloons if this never succeeds; it must
            // not stop the app starting.
            logger.LogWarning(ex, "Could not create the Start Menu shortcut; toast notifications may not be recognised by Windows");
        }
    }

    private static string ResolveProcessPath()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrEmpty(path))
        {
            throw new InvalidOperationException("Could not determine the running executable's path.");
        }

        return path;
    }

    private static bool TargetMatches(string shortcutPath, string exePath)
    {
        IShellLinkW? link = null;
        try
        {
            link = (IShellLinkW)new ShellLinkCoClass();
            ((IPersistFile)link).Load(shortcutPath, 0 /* STGM_READ */);

            var buffer = new StringBuilder(260);
            link.GetPath(buffer, buffer.Capacity, out _, 0); // discards the WIN32_FIND_DATAW; only the path is wanted

            return string.Equals(buffer.ToString(), exePath, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // A shortcut that cannot be read is as good as absent.
            return false;
        }
        finally
        {
            if (link is not null)
            {
                Marshal.ReleaseComObject(link);
            }
        }
    }

    private static void CreateShortcut(string shortcutPath, string exePath, string aumid)
    {
        var link = (IShellLinkW)new ShellLinkCoClass();

        try
        {
            link.SetPath(exePath);
            link.SetWorkingDirectory(Path.GetDirectoryName(exePath) ?? string.Empty);
            link.SetDescription("DashyNMS - LibreNMS alert monitor");

            var propertyStore = (IPropertyStore)link;
            var key = new PropertyKey(AppUserModelIdFmtId, AppUserModelIdPropertyId);

            using (var value = PropVariant.FromString(aumid))
            {
                var variant = value;
                propertyStore.SetValue(ref key, ref variant);
            }

            propertyStore.Commit();

            ((IPersistFile)link).Save(shortcutPath, true);
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }
    }

    // ---------------------------------------------------------------- COM interop
    //
    // Calling into the Shell Link COM object to write a .lnk with an
    // AppUserModelID property. This mirrors the pattern Microsoft's own
    // desktop-toast samples use for unpackaged Win32 apps.

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkCoClass
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        // The native signature's third parameter is WIN32_FIND_DATAW*, a pointer
        // to a fixed-size (~592 byte) struct that the callee fills in - NOT a
        // handle. Declaring it as anything smaller (an IntPtr, in an earlier
        // version of this file) makes the marshaller pass a pointer to too
        // little stack space; Shell32 then writes the full struct into it
        // regardless, corrupting the stack and crashing the process with an
        // access violation. The struct's contents are never used here, but its
        // real size must be reserved.
        void GetPath(
            [Out][MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile,
            int cchMaxPath,
            out Win32FindDataW pfd,
            uint fFlags);

        void GetIDList(out IntPtr ppidl);

        void SetIDList(IntPtr pidl);

        void GetDescription([Out][MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);

        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);

        void GetWorkingDirectory([Out][MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);

        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);

        void GetArguments([Out][MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);

        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);

        void GetHotkey(out short pwHotkey);

        void SetHotkey(short wHotkey);

        void GetShowCmd(out int piShowCmd);

        void SetShowCmd(int iShowCmd);

        void GetIconLocation([Out][MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);

        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);

        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);

        void Resolve(IntPtr hwnd, uint fFlags);

        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    /// <summary>
    /// The full native WIN32_FIND_DATAW layout. Only its size matters here -
    /// <see cref="IShellLinkW.GetPath"/> writes into it whether or not the
    /// caller cares about the contents, so the struct must be declared at its
    /// real size or the write corrupts adjacent memory.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Win32FindDataW
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint Reserved0;
        public uint Reserved1;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string FileName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string AlternateFileName;
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);

        [PreserveSig]
        int IsDirty();

        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);

        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);

        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);

        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;

        public PropertyKey(Guid formatId, uint propertyId)
        {
            FormatId = formatId;
            PropertyId = propertyId;
        }
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    private interface IPropertyStore
    {
        void GetCount(out uint propertyCount);

        void GetAt(uint propertyIndex, out PropertyKey key);

        void GetValue(ref PropertyKey key, out PropVariant value);

        void SetValue(ref PropertyKey key, ref PropVariant value);

        void Commit();
    }

    /// <summary>
    /// Minimal PROPVARIANT: just enough to carry a VT_LPWSTR string in and back
    /// out, which is all an AppUserModelID property needs.
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant : IDisposable
    {
        private const ushort VT_LPWSTR = 31;

        [FieldOffset(0)]
        public ushort VariantType;

        [FieldOffset(8)]
        public IntPtr PointerValue;

        public static PropVariant FromString(string value) => new()
        {
            VariantType = VT_LPWSTR,
            PointerValue = Marshal.StringToCoTaskMemUni(value),
        };

        public void Dispose() => PropVariantClear(ref this);

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PropVariant propVariant);
    }
}
