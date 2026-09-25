using System.Net.Sockets;
using System.Security.Authentication;

namespace DesktopNMS.Core.Api;

/// <summary>
/// Backup address failover for the LibreNMS server: after
/// <see cref="FailuresBeforeSwitch"/> requests in a row can't reach the
/// server at all (timeout, DNS, refused, unreachable), the transport dials
/// the backup address instead - the same server by another route, so the
/// URL, its hostname and the certificate check all stay as they are. It
/// stays on the backup until switched back by hand (<see cref="FailBack"/>);
/// it never flips back and forth on its own.
/// </summary>
public sealed class ServerFailover
{
    public const int FailuresBeforeSwitch = 2;

    private readonly object _gate = new();
    private int _consecutiveFailures;

    /// <summary>The backup address, as shown - null when none is set.</summary>
    public string? BackupAddress { get; private set; }

    public bool IsOnBackup { get; private set; }

    /// <summary>When it last switched to the backup.</summary>
    public DateTimeOffset? SwitchedAt { get; private set; }

    public bool HasBackup => BackupAddress is not null;

    /// <summary>Raised on switching to the backup or back - the transport drops its pooled connections so the next request dials the right address.</summary>
    public event EventHandler? Changed;

    /// <summary>A new connection: its backup address, and whether it starts on it (the main address was unreachable when signing in).</summary>
    public void Configure(string? backupAddress, bool startOnBackup = false)
    {
        bool changed;
        lock (_gate)
        {
            var backup = string.IsNullOrWhiteSpace(backupAddress) ? null : backupAddress.Trim();
            var onBackup = startOnBackup && backup is not null;
            changed = onBackup != IsOnBackup || !string.Equals(backup, BackupAddress, StringComparison.OrdinalIgnoreCase);

            BackupAddress = backup;
            IsOnBackup = onBackup;
            SwitchedAt = onBackup ? DateTimeOffset.Now : null;
            _consecutiveFailures = 0;
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>A request got an answer (any answer, errors included) - the server is reachable.</summary>
    public void RecordSuccess()
    {
        lock (_gate)
        {
            _consecutiveFailures = 0;
        }
    }

    /// <summary>A request couldn't reach the server. True when this was the one that switched to the backup - worth trying the request again there.</summary>
    public bool RecordUnreachable()
    {
        lock (_gate)
        {
            if (IsOnBackup || BackupAddress is null)
            {
                return false;
            }

            if (++_consecutiveFailures < FailuresBeforeSwitch)
            {
                return false;
            }

            IsOnBackup = true;
            SwitchedAt = DateTimeOffset.Now;
            _consecutiveFailures = 0;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Back to the main address - by hand only. False if it wasn't on the backup.</summary>
    public bool FailBack()
    {
        lock (_gate)
        {
            if (!IsOnBackup)
            {
                return false;
            }

            IsOnBackup = false;
            SwitchedAt = null;
            _consecutiveFailures = 0;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// The request never got an answer from LibreNMS: it timed out, the name
    /// wouldn't resolve, the connection was refused or had no route, or the
    /// secure connection couldn't be set up - something else answering at
    /// that address, say, that doesn't serve this name (TLS alert 112), as a
    /// public address can while the public side is down. Not an HTTP error
    /// from a server that did answer (401, 500, ...).
    /// </summary>
    public static bool IsUnreachable(LibreNmsApiException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);

        if (ex.StatusCode is not null || ex.InnerException is null)
        {
            return false;
        }

        for (var current = ex.InnerException; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case TimeoutException:
                case OperationCanceledException:
                case AuthenticationException:
                    return true;
                case SocketException socket when socket.SocketErrorCode is
                    SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData
                    or SocketError.ConnectionRefused or SocketError.NetworkUnreachable
                    or SocketError.HostUnreachable or SocketError.TimedOut or SocketError.NetworkDown
                    or SocketError.ConnectionReset or SocketError.ConnectionAborted:
                    return true;
                case HttpRequestException http when http.HttpRequestError is
                    HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError or HttpRequestError.SecureConnectionError:
                    return true;
            }
        }

        return false;
    }
}
