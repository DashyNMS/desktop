using System.Text.Json;
using DesktopNMS.Core.Intelligence;
using DesktopNMS.Diagnostics;
using Xunit;

namespace DesktopNMS.Presentation.Tests;

public sealed class IntelligenceRequestTimingTests
{
    [Theory]
    [InlineData(MetricOutcome.Success)]
    [InlineData(MetricOutcome.Failed)]
    [InlineData(MetricOutcome.Cancelled)]
    public void One_logical_request_records_first_text_and_total_once(MetricOutcome outcome)
    {
        var clock = new Clock();
        var sink = new Sink();
        var timing = new IntelligenceRequestTiming(sink, clock);
        clock.Advance(100);
        timing.FirstResponse();
        clock.Advance(400);
        timing.FirstResponse(); // More deltas or another internal tool round.
        clock.Advance(500);
        timing.Complete(outcome);
        timing.Complete(outcome);
        timing.FirstResponse(); // Late callbacks cannot revive a completed request.
        Assert.Equal(3, sink.Points.Count);
        Assert.Equal(100, Assert.Single(sink.Points, p => p.Name == MetricName.IntelligenceFirstResponse).Value);
        Assert.Equal(1000, Assert.Single(sink.Points, p => p.Name == MetricName.IntelligenceDuration).Value);
        Assert.Equal(1, Assert.Single(sink.Points, p => p.Name == MetricName.IntelligenceRequests).Value);
        Assert.All(sink.Points, p => { Assert.Equal(outcome, p.Outcome); Assert.Equal(MetricArea.Intelligence, p.Area); });
    }

    [Fact]
    public void Failure_before_first_text_has_no_first_response_measurement()
    {
        var sink = new Sink();
        new IntelligenceRequestTiming(sink, new Clock()).Complete(MetricOutcome.AuthFailure);
        Assert.Equal(2, sink.Points.Count);
        Assert.DoesNotContain(sink.Points, p => p.Name == MetricName.IntelligenceFirstResponse);
    }

    [Theory]
    [InlineData(IntelligenceFailure.SignIn, MetricOutcome.AuthFailure)]
    [InlineData(IntelligenceFailure.PlanUsage, MetricOutcome.PlanLimit)]
    [InlineData(IntelligenceFailure.Model, MetricOutcome.ModelFailure)]
    [InlineData(IntelligenceFailure.Tools, MetricOutcome.ToolFailure)]
    [InlineData(IntelligenceFailure.Network, MetricOutcome.NetworkFailure)]
    public void Failure_categories_never_include_exception_text(IntelligenceFailure source, MetricOutcome outcome)
    {
        var error = new IntelligenceException("private-prompt-canary private-token-canary", source);
        var sink = new Sink();
        new IntelligenceRequestTiming(sink, new Clock()).Complete(IntelligenceRequestTiming.Failure(error, false));
        Assert.All(sink.Points, p => Assert.Equal(outcome, p.Outcome));
        Assert.DoesNotContain("private-", JsonSerializer.Serialize(sink.Points));
    }

    [Fact]
    public void Cancellation_is_distinct_from_timeout_and_signin_and_network_are_bounded()
    {
        Assert.Equal(MetricOutcome.Cancelled, IntelligenceRequestTiming.Failure(new OperationCanceledException(), true));
        Assert.Equal(MetricOutcome.Timeout, IntelligenceRequestTiming.Failure(new OperationCanceledException(), false));
        Assert.Equal(MetricOutcome.AuthFailure, IntelligenceRequestTiming.Failure(new ChatGptSignInException("private"), false));
        Assert.Equal(MetricOutcome.NetworkFailure, IntelligenceRequestTiming.Failure(new HttpRequestException("private"), false));
        Assert.Equal(MetricOutcome.Failed, IntelligenceRequestTiming.Failure(new Exception("private"), false));
    }

    [Fact]
    public void New_consent_and_withdrawal_do_not_retroactively_capture_requests()
    {
        var sink = new Sink { IsEnabled = false };
        var beforeConsent = new IntelligenceRequestTiming(sink, new Clock());
        sink.IsEnabled = true;
        beforeConsent.FirstResponse();
        beforeConsent.Complete(MetricOutcome.Success);
        Assert.Empty(sink.Points);
        var beforeWithdrawal = new IntelligenceRequestTiming(sink, new Clock());
        beforeWithdrawal.FirstResponse();
        sink.ConsentGeneration++;
        beforeWithdrawal.Complete(MetricOutcome.Success);
        Assert.Empty(sink.Points);
    }

    private sealed class Clock : TimeProvider
    {
        private long _ms;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _ms;
        public void Advance(long ms) => _ms += ms;
    }
    private sealed class Sink : IAppMetrics
    {
        public bool IsEnabled { get; set; } = true;
        public long ConsentGeneration { get; set; }
        public List<MetricObservation> Points { get; } = [];
        public void Record(MetricObservation observation) => Points.Add(observation);
    }
}
