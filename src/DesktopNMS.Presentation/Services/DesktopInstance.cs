using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace DesktopNMS.Services;

/// <summary>One monitor per profile, with the shipped Windows activation contract preserved.</summary>
public sealed class DesktopInstance : IDisposable
{
    private readonly string _mutexName;
    private readonly string _signalName;
    private readonly CancellationTokenSource _stop = new();
    private Mutex? _mutex;
    private EventWaitHandle? _signal;
    private Task? _listener;
    private bool _ownsMutex;
    private bool _disposed;

    public DesktopInstance(string profile, bool legacyWindowsIdentity)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(profile))))[..24];
        var identity = legacyWindowsIdentity && OperatingSystem.IsWindows() ? "DashyNMS" : "DashyNMS." + hash;
        _mutexName = (OperatingSystem.IsWindows() ? @"Local\" : "") + identity + ".SingleInstance";
        _signalName = OperatingSystem.IsWindows() ? @"Local\" + identity + ".ShowWindow" : "dashynms-show-" + hash;
    }

    /// <summary>Called and disposed on the app's main thread, which owns the mutex.</summary>
    public bool TryClaim(Action showWindow)
    {
        _mutex = new Mutex(false, _mutexName);
        try { _ownsMutex = _mutex.WaitOne(0); }
        catch (AbandonedMutexException) { _ownsMutex = true; }
        if (!_ownsMutex) { _mutex.Dispose(); _mutex = null; return false; }
        var token = _stop.Token;
        if (OperatingSystem.IsWindows())
        {
            var signal = _signal = new EventWaitHandle(false, EventResetMode.AutoReset, _signalName);
            _listener = Task.Run(() =>
            {
                try
                {
                    var handles = new WaitHandle[] { signal, token.WaitHandle };
                    while (WaitHandle.WaitAny(handles) == 0) showWindow();
                }
                catch (ObjectDisposedException) { }
            });
        }
        else
        {
            _listener = ListenAsync(showWindow, token);
        }
        return true;
    }

    private async Task ListenAsync(Action showWindow, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(_signalName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                var message = new byte[1];
                if (await server.ReadAsync(message, token).ConfigureAwait(false) == 1 && message[0] == 1) showWindow();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (IOException) { if (!token.IsCancellationRequested) await Task.Delay(100, token).ConfigureAwait(false); }
        }
    }

    public void SignalExisting()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (EventWaitHandle.TryOpenExisting(_signalName, out var signal)) { using (signal) signal.Set(); }
            }
            else
            {
                using var client = new NamedPipeClientStream(".", _signalName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
                client.Connect(1000); client.WriteByte(1); client.Flush();
            }
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        try { _listener?.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        _signal?.Dispose(); _stop.Dispose();
        if (_ownsMutex) _mutex?.ReleaseMutex();
        _mutex?.Dispose();
    }
}
