using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using DesktopNMS.Diagnostics;

var context = new CrashReportContext("2.0.0", "linux", "x64");
if (args.Length == 2 && args[0].StartsWith("--child-", StringComparison.Ordinal))
{
    using var reporter = new CrashReporter(context, new LocalTransport());
    reporter.ConfigureMetrics(args[1], true, true, true, true);
    using var session = new SessionHealth(reporter, args[1], sampleDuration: () => true);
    session.Configure(true);
    AppDomain.CurrentDomain.UnhandledException += (_, failure) =>
    { if (failure.ExceptionObject is Exception error) reporter.Capture(error, CrashOrigin.Application); };
    Console.WriteLine("READY");
    if (args[0] == "--child-clean") return 0;
    if (args[0] == "--child-fatal")
        new Thread(() => throw new InvalidOperationException("synthetic-private-session-canary")) { IsBackground = false }.Start();
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return 2;
}

var root = Path.Combine(Path.GetTempPath(), "dashynms-session-probe-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    foreach (var (scenario, expected) in new[] { ("clean", SessionOutcome.Healthy), ("fatal", SessionOutcome.ManagedCrash), ("kill", SessionOutcome.Abnormal) })
    {
        var profile = Path.Combine(root, scenario);
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root };
        foreach (var prefix in new[] { "DOTNET_", "COMPlus_" })
        { start.Environment[prefix + "DbgEnableMiniDump"] = "0"; start.Environment[prefix + "EnableCrashReport"] = "0"; }
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--child-" + scenario); start.ArgumentList.Add(profile);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start session probe child.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try
        {
            if (await child.StandardOutput.ReadLineAsync().WaitAsync(timeout.Token) != "READY") throw new InvalidOperationException("Child did not save its eligible session.");
            var output = child.StandardOutput.ReadToEndAsync(); var errors = child.StandardError.ReadToEndAsync();
            if (scenario == "kill") child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync(timeout.Token); await Task.WhenAll(output, errors);
        }
        catch { if (!child.HasExited) child.Kill(entireProcessTree: true); throw; }
        if (scenario == "clean" ? child.ExitCode != 0 : child.ExitCode == 0) throw new InvalidOperationException("Unexpected child exit.");
        var sink = new LocalTransport();
        using var reporter = new CrashReporter(context, sink);
        reporter.ConfigureMetrics(profile, true, true, true, true);
        using var recovered = new SessionHealth(reporter, profile, sampleDuration: () => true);
        recovered.Configure(true);
        await reporter.SendPendingAsync();
        var result = sink.Reports.OfType<SessionHealthReport>().Single();
        if (result.Outcome != expected || (expected == SessionOutcome.Abnormal && result.DurationMilliseconds is not null)) throw new InvalidOperationException("Session termination was misclassified.");
        if (scenario == "fatal" && sink.Reports.OfType<CrashReport>().Count() != 1) throw new InvalidOperationException("Managed error path was not preserved.");
        recovered.Configure(false);
        Console.WriteLine($"{scenario}: {result.Outcome}; sanitised recovery and next-launch delivery passed.");
    }
    return 0;
}
finally { Directory.Delete(root, recursive: true); }

sealed class LocalTransport : ICrashTransport
{
    public bool IsAvailable => true;
    public List<DiagnosticReport> Reports { get; } = [];
    public Task<CrashSendResult> SendAsync(DiagnosticReport report, CancellationToken cancellationToken)
    {
        if (JsonSerializer.Serialize(report).Contains("synthetic-private-session-canary")) throw new InvalidOperationException("Report leaked error content.");
        Reports.Add(report); return Task.FromResult(new CrashSendResult(CrashSendDisposition.Accepted));
    }
}
