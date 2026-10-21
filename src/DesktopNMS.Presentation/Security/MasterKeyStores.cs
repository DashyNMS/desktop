using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;

namespace DesktopNMS.Security;

/// <summary>Where the per-user key lives on macOS and Linux (#154).</summary>
public interface IMasterKeyStore
{
    /// <summary>"the macOS Keychain", for the log.</summary>
    string Name { get; }

    /// <summary>The key; no key and no error when the keychain has none; an error when it couldn't be asked.</summary>
    MasterKeyLookup Read();

    /// <summary>Stores the key; returns an error when it couldn't.</summary>
    string? Write(byte[] key);
}

public readonly record struct MasterKeyLookup(byte[]? Key, string? Error);

/// <summary>
/// macOS: a generic password in the login Keychain, through /usr/bin/security.
/// The key goes in on stdin (<c>security -i</c>), never on a command line other
/// processes could read.
/// </summary>
public sealed class MacKeychainKeyStore(ICommandRunner runner) : IMasterKeyStore
{
    private const string Tool = "/usr/bin/security";

    /// <summary>security's "The specified item could not be found in the keychain."</summary>
    private const int NotFound = 44;

    public string Name => "the macOS Keychain";

    public MasterKeyLookup Read()
    {
        var result = runner.Run(Tool, ["find-generic-password", "-s", KeyNames.Service, "-a", KeyNames.Account, "-w"]);
        return result switch
        {
            { ExitCode: 0 } => KeyNames.Decode(result.Output, Name),
            { ExitCode: NotFound } => new MasterKeyLookup(null, null),
            _ => new MasterKeyLookup(null, $"{Name} couldn't be read ({result.Describe()})."),
        };
    }

    public string? Write(byte[] key)
    {
        var command = $"add-generic-password -U -s {KeyNames.Service} -a {KeyNames.Account} -l \"{KeyNames.Label}\" -w {Convert.ToBase64String(key)}\n";
        var result = runner.Run(Tool, ["-i"], command);
        return result.ExitCode == 0 ? null : $"{Name} couldn't store the key ({result.Describe()}).";
    }
}

/// <summary>
/// Linux: the freedesktop Secret Service (GNOME Keyring, KWallet), through
/// secret-tool from libsecret. The key goes in on stdin.
/// </summary>
public sealed class SecretServiceKeyStore(ICommandRunner runner) : IMasterKeyStore
{
    private const string Tool = "secret-tool";

    public string Name => "the Secret Service";

    public MasterKeyLookup Read()
    {
        var result = runner.Run(Tool, ["lookup", "service", KeyNames.Service, "account", KeyNames.Account]);
        return result switch
        {
            { Started: false } => new MasterKeyLookup(null, "secret-tool isn't installed (libsecret-tools), so secrets can't be kept."),
            { ExitCode: 0 } when result.Output.Trim().Length > 0 => KeyNames.Decode(result.Output, Name),

            // Nothing stored: exit 1 and nothing said. Anything on stderr means it couldn't ask.
            { ExitCode: 1 } when result.Error.Trim().Length == 0 => new MasterKeyLookup(null, null),
            _ => new MasterKeyLookup(null, $"{Name} couldn't be read ({result.Describe()})."),
        };
    }

    public string? Write(byte[] key)
    {
        var result = runner.Run(Tool, ["store", $"--label={KeyNames.Label}", "service", KeyNames.Service, "account", KeyNames.Account], Convert.ToBase64String(key));
        return result switch
        {
            { Started: false } => "secret-tool isn't installed (libsecret-tools), so secrets can't be kept.",
            { ExitCode: 0 } => null,
            _ => $"{Name} couldn't store the key ({result.Describe()}).",
        };
    }
}

internal static class KeyNames
{
    public const string Service = "DashyNMS";
    public const string Account = "data-key";
    public const string Label = "DashyNMS data key";

    public static MasterKeyLookup Decode(string output, string store)
    {
        try
        {
            var key = Convert.FromBase64String(output.Trim());
            return key.Length == 32 ? new(key, null) : new(null, $"The DashyNMS key in {store} isn't one DashyNMS made.");
        }
        catch (FormatException)
        {
            return new(null, $"The DashyNMS key in {store} isn't one DashyNMS made.");
        }
    }
}

/// <summary>Runs a command-line tool - a seam, so the key stores can be tested without a keychain.</summary>
public interface ICommandRunner
{
    CommandResult Run(string file, string[] arguments, string? input = null);
}

/// <param name="Started">False when the tool isn't there to run.</param>
public sealed record CommandResult(bool Started, int ExitCode, string Output, string Error)
{
    /// <summary>"exit 1: Cannot autolaunch D-Bus" - for the log; never includes the output, which may be the key.</summary>
    public string Describe() => Started
        ? $"exit {ExitCode}" + (Error.Trim() is { Length: > 0 } error ? ": " + error.Split('\n')[0] : string.Empty)
        : "couldn't be started";
}

public sealed class ProcessCommandRunner : ICommandRunner
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public CommandResult Run(string file, string[] arguments, string? input = null)
    {
        var start = new ProcessStartInfo(file)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        Process? process;
        try
        {
            process = Process.Start(start);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new CommandResult(false, -1, string.Empty, string.Empty);
        }

        if (process is null) return new CommandResult(false, -1, string.Empty, string.Empty);
        using (process)
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (input is not null) process.StandardInput.Write(input);
            process.StandardInput.Close();

            // A keychain prompt waits for the person; past that, give up rather than hang.
            if (!process.WaitForExit(Timeout))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return new CommandResult(true, -1, string.Empty, "timed out");
            }

            Task.WaitAll(output, error);
            return new CommandResult(true, process.ExitCode, output.Result, error.Result);
        }
    }
}
