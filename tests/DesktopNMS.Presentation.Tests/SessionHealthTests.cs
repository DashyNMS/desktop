using DesktopNMS.Core.Configuration;
using DesktopNMS.Diagnostics;
using Xunit;

namespace DesktopNMS.Presentation.Tests;

public sealed class SessionHealthTests
{
    [Theory]
    [InlineData(0, DiagnosticsSharingPreference.ErrorsAndUsage, false, false)]
    [InlineData(99, DiagnosticsSharingPreference.ErrorsAndUsage, false, false)]
    [InlineData(1, DiagnosticsSharingPreference.ErrorsAndUsage, true, false)]
    [InlineData(2, DiagnosticsSharingPreference.ErrorsAndUsage, true, true)]
    [InlineData(2, DiagnosticsSharingPreference.UsageOnly, true, false)]
    [InlineData(2, DiagnosticsSharingPreference.ErrorsOnly, false, false)]
    [InlineData(2, DiagnosticsSharingPreference.None, false, false)]
    public void Only_the_explicit_both_category_population_is_eligible(int version, DiagnosticsSharingPreference choice, bool metrics, bool sessions)
    {
        var settings = new AppSettings { DiagnosticsSharing = choice, DiagnosticsConsentVersion = version };
        Assert.Equal(metrics, DiagnosticsConsent.Metrics(settings));
        Assert.Equal(sessions, DiagnosticsConsent.Sessions(settings));
    }

    [Theory]
    [InlineData(false, SessionOutcome.Healthy)]
    [InlineData(true, SessionOutcome.Errored)]
    public async Task Clean_exit_keeps_healthy_and_handled_errors_distinct(bool error, SessionOutcome expected)
    {
        using var f = new Fixture();
        using (var session = new SessionHealth(f.Reporter, f.Path, f.Clock, () => true))
        {
            session.Configure(true);
            if (error) f.Reporter.Capture(new Exception("private-content-canary"), CrashOrigin.UiThread);
            f.Clock.Advance(TimeSpan.FromSeconds(10));
        }
        await f.Reporter.SendPendingAsync();
        var report = Assert.Single(f.Sink.Reports.OfType<SessionHealthReport>());
        Assert.Equal(expected, report.Outcome);
        Assert.Equal(10000, report.DurationMilliseconds);
        Assert.False(File.Exists(System.IO.Path.Combine(f.Path, "session-health.json")));
    }

    [Fact]
    public async Task A_real_fatal_origin_refines_an_already_captured_UI_error_without_duplicate_error_reports()
    {
        using var f = new Fixture();
        using var session = new SessionHealth(f.Reporter, f.Path, f.Clock, () => true);
        session.Configure(true);
        var error = new Exception("private-content-canary");
        f.Reporter.Capture(error, CrashOrigin.UiThread);
        f.Reporter.Capture(error, CrashOrigin.Application);
        await f.Reporter.SendPendingAsync();
        Assert.Single(f.Sink.Reports.OfType<CrashReport>());
        Assert.Equal(SessionOutcome.ManagedCrash, Assert.Single(f.Sink.Reports.OfType<SessionHealthReport>()).Outcome);
    }

    [Fact]
    public async Task Missing_exit_recovers_as_unknown_without_inventing_a_duration_or_crash()
    {
        using var f = new Fixture();
        using var previous = new SessionHealth(f.Reporter, f.Path, f.Clock, () => true);
        previous.Configure(true);
        using var restarted = new SessionHealth(f.Reporter, f.Path, f.Clock, () => true);
        restarted.Configure(true); // Simulate a restart with the durable active marker.
        await f.Reporter.SendPendingAsync();
        var report = Assert.Single(f.Sink.Reports.OfType<SessionHealthReport>());
        Assert.Equal(SessionOutcome.Abnormal, report.Outcome);
        Assert.Null(report.DurationMilliseconds);
        previous.Configure(false);
        restarted.Configure(false);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Withdrawing_either_choice_removes_sessions_but_keeps_the_other_category(bool withdrawErrors)
    {
        using var f = new Fixture();
        using var session = new SessionHealth(f.Reporter, f.Path, f.Clock, () => true);
        session.Configure(true);
        f.Reporter.Capture(new Exception(), CrashOrigin.Application);
        f.Reporter.RecordUsage();
        session.Configure(false);
        f.Reporter.ConfigureMetrics(f.Path, !withdrawErrors, withdrawErrors, withdrawErrors, false);
        await f.Reporter.SendPendingAsync();
        var remaining = Assert.Single(f.Sink.Reports);
        if (withdrawErrors) Assert.IsType<UsageReport>(remaining);
        else Assert.IsType<CrashReport>(remaining);
        Assert.False(File.Exists(System.IO.Path.Combine(f.Path, "session-health.json")));
    }

    [Fact]
    public void Recovery_state_is_bounded_sanitised_and_malformed_state_is_replaced()
    {
        using var f = new Fixture();
        Directory.CreateDirectory(f.Path);
        var path = System.IO.Path.Combine(f.Path, "session-health.json");
        File.WriteAllText(path, new string('x', 4096));
        using var session = new SessionHealth(f.Reporter, f.Path);
        session.Configure(true);
        Assert.True(new FileInfo(path).Length <= 2048);
        Assert.DoesNotContain("private-", File.ReadAllText(path));
        session.Configure(false);
    }

    [Theory]
    [InlineData(-8)]
    [InlineData(1)]
    public async Task Expired_or_future_markers_are_dropped_without_inventing_outcomes(int days)
    {
        using var f = new Fixture();
        using var session = new SessionHealth(f.Reporter, f.Path, f.Clock);
        session.Configure(true);
        var marker = System.IO.Path.Combine(f.Path, "session-health.json");
        var state = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(marker))!;
        state["started"] = f.Clock.GetUtcNow().AddDays(days);
        File.WriteAllText(marker, state.ToJsonString());
        using var restarted = new SessionHealth(f.Reporter, f.Path, f.Clock);
        restarted.Configure(true);
        await f.Reporter.SendPendingAsync();
        Assert.Empty(f.Sink.Reports);
        session.Configure(false);
        restarted.Configure(false);
    }

    [Fact]
    public void Reconsent_creates_a_fresh_interval_identifier()
    {
        using var f = new Fixture();
        using var session = new SessionHealth(f.Reporter, f.Path, f.Clock);
        var marker = System.IO.Path.Combine(f.Path, "session-health.json");
        session.Configure(true);
        var first = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(marker))!["id"]!.GetValue<Guid>();
        session.Configure(false);
        session.Configure(true);
        var second = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(marker))!["id"]!.GetValue<Guid>();
        Assert.NotEqual(Guid.Empty, first);
        Assert.NotEqual(first, second);
        session.Configure(false);
    }

    private sealed class Fixture : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dashynms-session-tests-" + Guid.NewGuid().ToString("N"));
        public Sink Sink { get; } = new();
        public Clock Clock { get; } = new();
        public CrashReporter Reporter { get; }
        public Fixture()
        {
            Reporter = new(new("2.0.0", "linux", "x64"), Sink, Clock);
            Reporter.ConfigureMetrics(Path, true, true, true, true);
        }
        public void Dispose() { Reporter.Dispose(); if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) { _ticks += by.Ticks; _now += by; }
    }
    private sealed class Sink : ICrashTransport
    {
        public bool IsAvailable => true;
        public List<DiagnosticReport> Reports { get; } = [];
        public Task<CrashSendResult> SendAsync(DiagnosticReport report, CancellationToken cancellationToken)
        { Reports.Add(report); return Task.FromResult(new CrashSendResult(CrashSendDisposition.Accepted)); }
    }
}
