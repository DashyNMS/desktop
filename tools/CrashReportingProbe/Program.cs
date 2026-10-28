using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using DesktopNMS.Diagnostics;
using DesktopNMS.Infrastructure;

var context = new CrashReportContext("2.0.0", "unknown", "unknown");
if (args is ["--crash-child", var profile])
{
    using var reporter = new CrashReporter(context, new LocalTransport());
    reporter.Configure(profile, true);
    AppDomain.CurrentDomain.UnhandledException += (_, failure) =>
    {
        if (failure.ExceptionObject is Exception exception) reporter.Capture(exception, CrashOrigin.Application);
    };
    new Thread(() => typeof(RelayCommand).GetMethod(nameof(RelayCommand.Execute))!.Invoke(
        new RelayCommand(_ => throw new InvalidOperationException("synthetic-private-token https://synthetic-network.invalid synthetic-chat")), [null]))
        { IsBackground = false }.Start();
    Thread.Sleep(TimeSpan.FromSeconds(15));
    return 2;
}

var scratch = Path.Combine(Path.GetTempPath(), "dashynms-crash-probe-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
try
{
    var start = new ProcessStartInfo(Environment.ProcessPath!)
    {
        UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, WorkingDirectory = scratch,
    };
    foreach (var prefix in new[] { "DOTNET_", "COMPlus_" })
    {
        start.Environment[prefix + "DbgEnableMiniDump"] = "0";
        start.Environment[prefix + "EnableCrashReport"] = "0";
    }
    if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    start.ArgumentList.Add("--crash-child");
    start.ArgumentList.Add(scratch);
    using var child = Process.Start(start) ?? throw new InvalidOperationException("The crash probe child did not start.");
    var output = child.StandardOutput.ReadToEndAsync();
    var errors = child.StandardError.ReadToEndAsync();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    try { await child.WaitForExitAsync(timeout.Token); }
    catch (OperationCanceledException) { child.Kill(entireProcessTree: true); throw new InvalidOperationException("The crash probe child timed out."); }
    await Task.WhenAll(output, errors);
    if (child.ExitCode == 0) throw new InvalidOperationException("The child did not terminate from its forced managed exception.");
    var directory = Path.Combine(scratch, "crash-reports");
    var files = Directory.GetFiles(directory, "*.json");
    if (files.Length != 1) throw new InvalidOperationException("The fatal exception did not leave exactly one report.");
    var persisted = File.ReadAllText(files[0]);
    if (persisted.Contains("synthetic-private-token") || persisted.Contains("synthetic-network") || persisted.Contains("synthetic-chat"))
        throw new InvalidOperationException("The persisted report contains exception content.");
    if (!persisted.Contains("src/DesktopNMS.Presentation/Infrastructure/RelayCommand.cs") || !persisted.Contains("lineNumber"))
        throw new InvalidOperationException("The Release build did not preserve safe source locations.");
    if (persisted.Contains("/_/dashynms/") || persisted.Contains("abs_path"))
        throw new InvalidOperationException("The report contains an absolute build path.");
    var transport = new LocalTransport();
    using var restarted = new CrashReporter(context, transport);
    restarted.Configure(scratch, true);
    await restarted.SendPendingAsync();
    await restarted.SendPendingAsync();
    if (transport.Accepted != 1 || Directory.GetFiles(directory, "*.json").Length != 0)
        throw new InvalidOperationException("The restarted reporter did not send the pending report exactly once.");
    Console.WriteLine("Forced managed failure persisted one sanitised report; the next launch sent it once through a local test transport.");
    return 0;
}
finally
{
    var resolved = Path.GetFullPath(scratch);
    if (!Path.GetFileName(resolved).StartsWith("dashynms-crash-probe-", StringComparison.Ordinal) ||
        Path.GetDirectoryName(resolved) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar))
        throw new InvalidOperationException("Unexpected crash probe directory.");
    Directory.Delete(resolved, recursive: true);
}

sealed class LocalTransport : ICrashTransport
{
    public bool IsAvailable => true;
    public int Accepted { get; private set; }
    public Task<CrashSendResult> SendAsync(DiagnosticReport report, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var payload = JsonSerializer.Serialize(report);
        if (payload.Contains("synthetic-private-token") || payload.Contains("synthetic-network") || payload.Contains("synthetic-chat"))
            throw new InvalidOperationException("The report contains exception content.");
        Accepted++;
        return Task.FromResult(new CrashSendResult(CrashSendDisposition.Accepted));
    }
}
