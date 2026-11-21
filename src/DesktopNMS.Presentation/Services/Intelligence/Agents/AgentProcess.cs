using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace DesktopNMS.Services.Intelligence.Agents;

/// <summary>
/// One running agent process (#332): started with exactly these arguments,
/// talking newline-delimited JSON over stdio - or, for an agent that frames
/// its messages with Content-Length headers as LSP does, one message per
/// "line" - with no console window. Its
/// output lines are read as they arrive; its error output is kept only as a
/// short tail in memory, never logged, since it can carry account or network
/// detail. Disposing it kills the process and anything it started.
/// </summary>
public sealed partial class AgentProcess : IAsyncDisposable
{
    private const int MaxLineChars = 8 * 1024 * 1024;
    private const int StderrTailLines = 20;

    private readonly Process _process;
    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Queue<string> _stderr = new();
    private readonly SemaphoreSlim _writing = new(1, 1);
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly bool _framed;
    private readonly TaskCompletionSource _errorsRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    private AgentProcess(Process process, bool framed)
    {
        _process = process;
        _framed = framed;
    }

    /// <summary>Completes with the exit code when the process ends, however it ends.</summary>
    public Task<int> Exited => _exited.Task;

    public bool HasExited => _exited.Task.IsCompleted;

    /// <summary>The last few lines of error output, for diagnosing a failed start in tests - never shown or logged.</summary>
    internal string StderrTail
    {
        get { lock (_stderr) return string.Join("\n", _stderr); }
    }

    /// <summary>
    /// Starts <paramref name="executable"/>. The process inherits this app's
    /// environment - agents need their home folder, credential store and PATH -
    /// plus <paramref name="environment"/>, and the folders of the executable and
    /// the usual install locations ahead on PATH, since a desktop app's PATH
    /// often lacks the shell's.
    /// </summary>
    /// <param name="framed">Messages carry Content-Length headers both ways instead of ending at a newline.</param>
    /// <exception cref="AgentProviderException">It couldn't be started.</exception>
    public static AgentProcess Start(string executable, IEnumerable<string> arguments, string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment = null, bool redirectInput = true, bool framed = false)
    {
        var args = arguments.ToList();
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = redirectInput,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = redirectInput ? new UTF8Encoding(false) : null,
        };

        // A Windows .cmd shim runs through cmd.exe, which re-reads its arguments: only plain ones
        // are passed, and everything else (prompts, instructions, configuration) goes by file or stdin.
        if (IsShim(executable) && args.FirstOrDefault(a => !PlainArgument().IsMatch(a)) is not null)
        {
            throw new AgentProviderException(
                "DashyNMS can't pass its settings safely to this script launcher. Choose the agent's .exe in Settings › Providers instead.",
                Core.Intelligence.IntelligenceFailure.Tools, AgentProviderState.Unavailable);
        }

        foreach (var argument in args) info.ArgumentList.Add(argument);
        info.Environment["PATH"] = SearchPath(executable, info.Environment.TryGetValue("PATH", out var path) ? path : null);
        foreach (var (key, value) in environment ?? new Dictionary<string, string?>())
        {
            if (value is null) info.Environment.Remove(key);
            else info.Environment[key] = value;
        }

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        var started = new AgentProcess(process, framed);
        process.Exited += (_, _) => started.OnExited();
        try
        {
            if (!process.Start()) throw new InvalidOperationException("The process didn't start.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            process.Dispose();
            throw new AgentProviderException("The agent couldn't be started. Check its executable in Settings › Providers.",
                Core.Intelligence.IntelligenceFailure.Tools, AgentProviderState.NotInstalled);
        }

        _ = framed ? started.ReadFramesAsync() : started.ReadOutputAsync();
        _ = started.ReadErrorsAsync();
        if (process.HasExited) started.OnExited();
        return started;
    }

    /// <summary>Output lines as they arrive; the sequence ends when the process closes its output.</summary>
    public IAsyncEnumerable<string> ReadLinesAsync(CancellationToken cancellationToken) => _lines.Reader.ReadAllAsync(cancellationToken);

    /// <summary>Waits for the next output line, or null once output has ended.</summary>
    public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _lines.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    /// <summary>Writes one line to the process's input - a JSON message, or a prompt - or one framed message.</summary>
    public async Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        await _writing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (HasExited) throw new IOException("The agent has exited.");
            if (_framed)
            {
                var header = $"Content-Length: {Encoding.UTF8.GetByteCount(line)}\r\n\r\n";
                await _process.StandardInput.WriteAsync(header.AsMemory(), cancellationToken).ConfigureAwait(false);
                await _process.StandardInput.WriteAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _process.StandardInput.WriteAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
                await _process.StandardInput.WriteAsync("\n".AsMemory(), cancellationToken).ConfigureAwait(false);
            }

            await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writing.Release();
        }
    }

    /// <summary>Closes the process's input: the end of a prompt, or a request to finish.</summary>
    public void CloseInput()
    {
        try
        {
            _process.StandardInput.Close();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            // Already gone.
        }
    }

    /// <summary>Kills the process and its children - Stop, a retired session, or the app closing.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Already exiting.
        }

        try
        {
            await _exited.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Left to the system; nothing more can be done from here.
        }

        _process.Dispose();
        _writing.Dispose();
    }

    private async Task ReadOutputAsync()
    {
        try
        {
            var reader = _process.StandardOutput;
            var line = new StringBuilder();
            var buffer = new char[16 * 1024];
            int read;
            while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                for (var i = 0; i < read; i++)
                {
                    if (buffer[i] == '\n')
                    {
                        var text = line.ToString().TrimEnd('\r');
                        line.Clear();
                        if (text.Length > 0) _lines.Writer.TryWrite(text);
                    }
                    else if (line.Length < MaxLineChars)
                    {
                        line.Append(buffer[i]);
                    }
                }
            }

            if (line.Length > 0) _lines.Writer.TryWrite(line.ToString().TrimEnd('\r'));
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The process went away mid-line.
        }
        finally
        {
            _lines.Writer.TryComplete();
        }
    }

    /// <summary>Content-Length framed output: each message's body becomes one "line".</summary>
    private async Task ReadFramesAsync()
    {
        try
        {
            var stream = _process.StandardOutput.BaseStream;
            while (await ReadFrameAsync(stream).ConfigureAwait(false) is { } message)
            {
                if (message.Length > 0) _lines.Writer.TryWrite(message);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The process went away mid-message.
        }
        finally
        {
            _lines.Writer.TryComplete();
        }
    }

    /// <summary>Reads one framed message, or null at the end of the output. A message too large to keep is drained and comes back empty.</summary>
    internal static async Task<string?> ReadFrameAsync(Stream stream)
    {
        var length = -1;
        var header = new StringBuilder();
        var one = new byte[1];
        while (true)
        {
            if (await stream.ReadAsync(one).ConfigureAwait(false) == 0) return null;
            if (one[0] != (byte)'\n')
            {
                if (header.Length < 1024) header.Append((char)one[0]);
                continue;
            }

            var text = header.ToString().TrimEnd('\r');
            header.Clear();
            if (text.Length == 0)
            {
                if (length >= 0) break;
                continue;
            }

            var colon = text.IndexOf(':');
            if (colon > 0 && text[..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(text[(colon + 1)..].Trim(), out var parsed) && parsed >= 0)
            {
                length = parsed;
            }
        }

        var body = new byte[Math.Min(length, MaxLineChars)];
        await stream.ReadExactlyAsync(body).ConfigureAwait(false);
        if (length <= MaxLineChars) return Encoding.UTF8.GetString(body);

        // Larger than anything DashyNMS reads from an agent: dropped rather than kept in memory.
        var left = length - body.Length;
        while (left > 0)
        {
            var read = await stream.ReadAsync(body.AsMemory(0, Math.Min(body.Length, left))).ConfigureAwait(false);
            if (read == 0) return null;
            left -= read;
        }

        return string.Empty;
    }

    private async Task ReadErrorsAsync()
    {
        try
        {
            string? line;
            while ((line = await _process.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                lock (_stderr)
                {
                    _stderr.Enqueue(line.Length > 500 ? line[..500] : line);
                    while (_stderr.Count > StderrTailLines) _stderr.Dequeue();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
        finally
        {
            _errorsRead.TrySetResult();
        }
    }

    /// <summary>
    /// The error output's last lines once it has all been read - it's read
    /// apart from the output, so its final lines can still be arriving when
    /// the output ends - waiting no more than a moment.
    /// </summary>
    internal async Task<string> StderrTailAsync()
    {
        await Task.WhenAny(_errorsRead.Task, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
        return StderrTail;
    }

    private void OnExited()
    {
        int code;
        try
        {
            code = _process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            code = -1;
        }

        _exited.TrySetResult(code);
    }

    internal static bool IsShim(string executable)
        => OperatingSystem.IsWindows() && (executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || executable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase));

    /// <summary>Arguments cmd.exe can't misread: no quotes, carets, percent signs, ampersands, pipes or brackets.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_\-.,:/\\=+@~ ]*$")]
    private static partial Regex PlainArgument();

    /// <summary>The executable's own folder and the usual install folders, ahead of the inherited PATH.</summary>
    internal static string SearchPath(string executable, string? inherited)
    {
        var folders = new List<string>();
        if (Path.GetDirectoryName(executable) is { Length: > 0 } own) folders.Add(own);
        folders.AddRange(AgentExecutables.CommonFolders());
        folders.AddRange((inherited ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        return string.Join(Path.PathSeparator, folders.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal));
    }
}
