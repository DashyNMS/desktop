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
/// talking newline-delimited JSON over stdio, with no console window. Its
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
    private int _disposed;

    private AgentProcess(Process process)
    {
        _process = process;
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
    /// <exception cref="AgentProviderException">It couldn't be started.</exception>
    public static AgentProcess Start(string executable, IEnumerable<string> arguments, string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment = null, bool redirectInput = true)
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
                "DashyNMS can't pass its settings safely to this script launcher. Choose the agent's .exe in Settings › Intelligence instead.",
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
        var started = new AgentProcess(process);
        process.Exited += (_, _) => started.OnExited();
        try
        {
            if (!process.Start()) throw new InvalidOperationException("The process didn't start.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            process.Dispose();
            throw new AgentProviderException("The agent couldn't be started. Check its executable in Settings › Intelligence.",
                Core.Intelligence.IntelligenceFailure.Tools, AgentProviderState.NotInstalled);
        }

        _ = started.ReadOutputAsync();
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

    /// <summary>Writes one line to the process's input - a JSON message, or a prompt.</summary>
    public async Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        await _writing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (HasExited) throw new IOException("The agent has exited.");
            await _process.StandardInput.WriteAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _process.StandardInput.WriteAsync("\n".AsMemory(), cancellationToken).ConfigureAwait(false);
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
