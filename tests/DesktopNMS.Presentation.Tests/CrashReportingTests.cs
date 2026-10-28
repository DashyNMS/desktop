using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using DesktopNMS.Diagnostics;
using DesktopNMS.Infrastructure;
using Xunit;

namespace DesktopNMS.Presentation.Tests;

public sealed class CrashReportingTests
{
    private static readonly CrashReportContext Context = new("2.0.0-preview.9", "windows", "X64");

    [Theory]
    [InlineData("2.0.0-preview.9+e78b753352bde8f01d08d4c420ee118dfbf192b2")]
    [InlineData("2.0.0+e78b753352bde8f01d08d4c420ee118dfbf192b2")]
    public async Task Reports_preserve_the_build_version_including_commit_metadata(string release)
    {
        using var folder = new TemporaryFolder();
        var handler = new RecordingHandler();
        using var reporter = new CrashReporter(Context with { Release = release },
            new SentryCrashTransport("https://publickey@collector.invalid/123", handler));
        reporter.Configure(folder.Path, true, true);
        reporter.Capture(new InvalidOperationException(), CrashOrigin.Application);
        reporter.RecordUsage();
        await reporter.SendPendingAsync();
        Assert.Equal(2, handler.Bodies.Count);
        Assert.All(handler.Bodies, body =>
        {
            using var payload = JsonDocument.Parse(body.Split('\n', StringSplitOptions.RemoveEmptyEntries)[2]);
            var root = payload.RootElement;
            Assert.Equal(release, root.TryGetProperty("items", out var metrics)
                ? metrics[0].GetProperty("attributes").GetProperty("sentry.release").GetProperty("value").GetString()
                : root.GetProperty("release").GetString());
        });
    }

    [Fact]
    public async Task Inactive_and_unavailable_reporters_do_not_capture_or_send()
    {
        using var folder = new TemporaryFolder();
        var transport = new FakeTransport();
        using var reporter = new CrashReporter(Context, transport);
        reporter.Capture(new InvalidOperationException("private"), CrashOrigin.Application);
        await reporter.SendPendingAsync();
        Assert.Empty(Directory.EnumerateFileSystemEntries(folder.Path));
        Assert.Empty(transport.Reports);

        using var invalid = new SentryCrashTransport("http://key@collector.invalid/1", new RecordingHandler());
        using var unavailable = new CrashReporter(Context, invalid);
        unavailable.Configure(folder.Path, true);
        unavailable.Capture(new Exception("private"), CrashOrigin.Command);
        await unavailable.SendPendingAsync();
        Assert.False(unavailable.IsAvailable);
        Assert.Empty(Directory.EnumerateFileSystemEntries(folder.Path));
    }

    [Fact]
    public async Task Only_the_allowlisted_payload_reaches_the_HTTP_endpoint()
    {
        using var folder = new TemporaryFolder();
        var handler = new RecordingHandler();
        using var reporter = new CrashReporter(Context, new SentryCrashTransport("https://publickey@collector.invalid/sentry/123", handler));
        reporter.Configure(folder.Path, true);
        var exception = SensitiveException();
        reporter.Capture(exception, CrashOrigin.Application);
        reporter.Capture(exception, CrashOrigin.UiThread);
        Assert.Single(Pending(folder.Path));
        Assert.Empty(handler.Bodies);
        await reporter.SendPendingAsync();
        var body = Assert.Single(handler.Bodies);
        Assert.Equal("https://collector.invalid/sentry/api/123/envelope/", Assert.Single(handler.Urls));
        Assert.Equal("application/x-sentry-envelope", Assert.Single(handler.ContentTypes));
        Assert.DoesNotContain("publickey", body);
        AssertSafe(body);
        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        using var item = JsonDocument.Parse(lines[1]);
        Assert.Equal("event", item.RootElement.GetProperty("type").GetString());
        Assert.Equal(Encoding.UTF8.GetByteCount(lines[2]), item.RootElement.GetProperty("length").GetInt32());
        using var payload = JsonDocument.Parse(lines[2]);
        Assert.Equal(new[] { "event_id", "exception", "level", "platform", "release", "tags", "timestamp" },
            payload.RootElement.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal("fatal", payload.RootElement.GetProperty("level").GetString());
        Assert.Equal("2.0.0-preview.9", payload.RootElement.GetProperty("release").GetString());
        var frames = payload.RootElement.GetProperty("exception").GetProperty("values").EnumerateArray()
            .SelectMany(value => value.GetProperty("stacktrace").GetProperty("frames").EnumerateArray()).ToArray();
        Assert.Contains(frames, frame => frame.GetProperty("module").GetString() == "DesktopNMS.Presentation" &&
            frame.GetProperty("function").GetString() == "DesktopNMS.Infrastructure.RelayCommand.Execute");
        Assert.DoesNotContain("CrashReportingTests.cs", body);
        Assert.DoesNotContain("filename", body);
        Assert.Empty(Pending(folder.Path));
    }

    [Fact]
    public async Task Offline_reports_survive_restart_then_send_once()
    {
        using var folder = new TemporaryFolder();
        var unavailable = new FakeTransport { Result = new(CrashSendDisposition.Retry) };
        using (var reporter = new CrashReporter(Context, unavailable))
        {
            reporter.Configure(folder.Path, true);
            reporter.Capture(SensitiveException(), CrashOrigin.UiThread);
            await reporter.SendPendingAsync();
            Assert.Single(Pending(folder.Path));
            AssertSafe(File.ReadAllText(Assert.Single(Pending(folder.Path))));
        }
        var online = new FakeTransport();
        using var restarted = new CrashReporter(Context, online);
        restarted.Configure(folder.Path, true);
        await restarted.SendPendingAsync();
        await restarted.SendPendingAsync();
        Assert.Single(online.Reports);
        Assert.Empty(Pending(folder.Path));
    }

    [Fact]
    public async Task Disabling_cancels_inflight_work_and_prevents_stale_requeue_or_replay()
    {
        using var folder = new TemporaryFolder();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource<CrashSendResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken activeToken = default;
        var transport = new FakeTransport
        {
            Send = (_, cancellation) => { activeToken = cancellation; started.SetResult(); return finished.Task; },
        };
        using (var reporter = new CrashReporter(Context, transport))
        {
            reporter.Configure(folder.Path, true);
            reporter.Capture(new Exception("first"), CrashOrigin.Application);
            reporter.Capture(new Exception("second"), CrashOrigin.Application);
            var send = reporter.SendPendingAsync();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            reporter.Configure(folder.Path, false);
            Assert.True(activeToken.IsCancellationRequested);
            Assert.Empty(Pending(folder.Path));
            finished.SetResult(new(CrashSendDisposition.Retry));
            await send;
            reporter.Capture(new Exception("after opt-out"), CrashOrigin.Command);
            await reporter.SendPendingAsync();
            Assert.Single(transport.Reports);
            Assert.Empty(Pending(folder.Path));
        }
        using var restarted = new CrashReporter(Context, new FakeTransport());
        restarted.Configure(folder.Path, false);
        await restarted.SendPendingAsync();
        Assert.Empty(Pending(folder.Path));
    }

    [Fact]
    public async Task Queue_is_bounded_and_invalid_or_expired_entries_are_removed()
    {
        using var folder = new TemporaryFolder();
        var clock = new Clock();
        using var reporter = new CrashReporter(Context, new FakeTransport(), clock);
        reporter.Configure(folder.Path, true);
        for (var index = 0; index < 15; index++)
        {
            reporter.Capture(new Exception("not persisted " + index), CrashOrigin.Application);
            clock.Advance(TimeSpan.FromSeconds(1));
        }
        Assert.Equal(10, Pending(folder.Path).Length);
        Assert.All(Pending(folder.Path), path => Assert.InRange(new FileInfo(path).Length, 1, 32 * 1024));
        var queue = System.IO.Path.Combine(folder.Path, "crash-reports");
        File.WriteAllText(System.IO.Path.Combine(queue, "corrupt.json"), "{broken");
        File.WriteAllText(System.IO.Path.Combine(queue, "oversized.json"), new string('x', 33 * 1024));
        var unknown = Assert.Single(Pending(folder.Path).Take(1));
        File.WriteAllText(unknown, File.ReadAllText(unknown).Replace("\"schema\":1", "\"schema\":99"));
        clock.Advance(TimeSpan.FromDays(8));
        await reporter.SendPendingAsync();
        Assert.Empty(Pending(folder.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unknown_cached_fields_or_report_kinds_are_rejected_instead_of_forwarded(bool unknownKind)
    {
        using var folder = new TemporaryFolder();
        var transport = new FakeTransport();
        using var reporter = new CrashReporter(Context, transport);
        reporter.Configure(folder.Path, true);
        reporter.Capture(new Exception("test"), CrashOrigin.Application);
        var path = Assert.Single(Pending(folder.Path));
        var json = File.ReadAllText(path);
        File.WriteAllText(path, unknownKind ? json.Replace("\"kind\":\"error\"", "\"kind\":\"secret-token-canary\"") :
            json.Insert(1, "\"message\":\"secret-token-canary\","));
        await reporter.SendPendingAsync();
        Assert.Empty(transport.Reports);
        Assert.Empty(Pending(folder.Path));
    }

    [Fact]
    public async Task Rejected_reports_are_dropped_and_rate_limits_delay_further_requests()
    {
        using var folder = new TemporaryFolder();
        var clock = new Clock();
        var handler = new RecordingHandler
        {
            Response = () =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(2));
                response.Headers.TryAddWithoutValidation("X-Sentry-Rate-Limits", "180:error:organization");
                return response;
            },
        };
        using var reporter = new CrashReporter(Context, new SentryCrashTransport("https://publickey@collector.invalid/123", handler), clock);
        reporter.Configure(folder.Path, true);
        reporter.Capture(new Exception("test"), CrashOrigin.Application);
        await reporter.SendPendingAsync();
        await reporter.SendPendingAsync();
        Assert.Single(handler.Bodies);
        Assert.Single(Pending(folder.Path));
        clock.Advance(TimeSpan.FromSeconds(179));
        await reporter.SendPendingAsync();
        Assert.Single(handler.Bodies);
        handler.Response = () => new(HttpStatusCode.BadRequest);
        clock.Advance(TimeSpan.FromSeconds(2));
        await reporter.SendPendingAsync();
        Assert.Equal(2, handler.Bodies.Count);
        Assert.Empty(Pending(folder.Path));
    }

    [Fact]
    public async Task Nonstandard_context_values_and_external_exception_types_are_not_uploaded()
    {
        using var folder = new TemporaryFolder();
        var handler = new RecordingHandler();
        using var reporter = new CrashReporter(new("secret-token-canary", "private-host-canary", "chat-text-canary"),
            new SentryCrashTransport("https://publickey@collector.invalid/123", handler));
        reporter.Configure(folder.Path, true);
        reporter.Capture(new ExternalFailure("secret-token-canary"), CrashOrigin.Application);
        await reporter.SendPendingAsync();
        var body = Assert.Single(handler.Bodies);
        AssertSafe(body);
        Assert.Contains("ExternalException", body);
        Assert.DoesNotContain(nameof(ExternalFailure), body);
    }

    [Fact]
    public async Task Command_errors_pump_without_requiring_a_restart()
    {
        using var folder = new TemporaryFolder();
        var handler = new RecordingHandler();
        using var reporter = new CrashReporter(Context, new SentryCrashTransport("https://publickey@collector.invalid/123", handler));
        reporter.Configure(folder.Path, true);
        reporter.Capture(SensitiveException(), CrashOrigin.Command);
        await reporter.SendPendingAsync();
        var body = Assert.Single(handler.Bodies);
        Assert.Contains("\"level\":\"error\"", body);
        AssertSafe(body);
    }

    [Fact]
    public async Task Usage_only_records_one_fixed_app_start_and_no_error_data()
    {
        using var folder = new TemporaryFolder();
        var handler = new RecordingHandler();
        using var reporter = new CrashReporter(Context, new SentryCrashTransport("https://publickey@collector.invalid/123", handler));
        reporter.RecordUsage();
        reporter.Configure(folder.Path, sendErrors: false, sendUsage: true);
        reporter.RecordUsage();
        reporter.RecordUsage();
        reporter.Capture(SensitiveException(), CrashOrigin.Command);
        Assert.Single(Pending(folder.Path));
        await reporter.SendPendingAsync();
        var body = Assert.Single(handler.Bodies);
        AssertSafe(body);
        using var payload = JsonDocument.Parse(body.Split('\n', StringSplitOptions.RemoveEmptyEntries)[2]);
        using var item = JsonDocument.Parse(body.Split('\n', StringSplitOptions.RemoveEmptyEntries)[1]);
        Assert.Equal("trace_metric", item.RootElement.GetProperty("type").GetString());
        Assert.Equal(1, item.RootElement.GetProperty("item_count").GetInt32());
        Assert.Equal("application/vnd.sentry.items.trace-metric+json", item.RootElement.GetProperty("content_type").GetString());
        Assert.Equal("never", payload.RootElement.GetProperty("ingest_settings").GetProperty("infer_ip").GetString());
        Assert.Equal("never", payload.RootElement.GetProperty("ingest_settings").GetProperty("infer_user_agent").GetString());
        var metric = Assert.Single(payload.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("dashynms.app_start", metric.GetProperty("name").GetString());
        Assert.Equal("counter", metric.GetProperty("type").GetString());
        Assert.Equal(1, metric.GetProperty("value").GetInt32());
        Assert.Equal(new[] { "attributes", "name", "timestamp", "trace_id", "type", "value" },
            metric.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal(new[] { "architecture", "operating_system", "sentry.release", "sentry.sdk.name", "sentry.sdk.version", "sentry.timestamp.sequence" },
            metric.GetProperty("attributes").EnumerateObject().Select(property => property.Name).Order());
        reporter.Configure(folder.Path, false, false);
        reporter.Configure(folder.Path, false, true);
        reporter.RecordUsage();
        await reporter.SendPendingAsync();
        Assert.Single(handler.Bodies);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Disabling_one_category_purges_only_its_pending_reports(bool errors, bool usage)
    {
        using var folder = new TemporaryFolder();
        var transport = new FakeTransport();
        using var reporter = new CrashReporter(Context, transport);
        reporter.Configure(folder.Path, true, true);
        reporter.Capture(SensitiveException(), CrashOrigin.Application);
        reporter.RecordUsage();
        Assert.Equal(2, Pending(folder.Path).Length);
        reporter.Configure(folder.Path, errors, usage);
        Assert.Single(Pending(folder.Path));
        await reporter.SendPendingAsync();
        var report = Assert.Single(transport.Reports);
        Assert.Equal(errors, report is CrashReport);
        Assert.Equal(usage, report is UsageReport);
        reporter.Configure(folder.Path, false, false);
        reporter.RecordUsage();
        reporter.Capture(SensitiveException(), CrashOrigin.Application);
        Assert.Empty(Pending(folder.Path));
    }

    [Fact]
    public async Task Partial_opt_out_cancels_the_active_error_but_retains_allowed_usage()
    {
        using var folder = new TemporaryFolder();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = new TaskCompletionSource<CrashSendResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken token = default;
        var transport = new FakeTransport
        {
            Send = (report, cancellation) =>
            {
                if (report is UsageReport) return Task.FromResult(new CrashSendResult(CrashSendDisposition.Accepted));
                token = cancellation;
                started.SetResult();
                return blocked.Task;
            },
        };
        using var reporter = new CrashReporter(Context, transport);
        reporter.Configure(folder.Path, true, true);
        reporter.Capture(new Exception("error"), CrashOrigin.Application);
        reporter.RecordUsage();
        var send = reporter.SendPendingAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        reporter.Configure(folder.Path, false, true);
        Assert.True(token.IsCancellationRequested);
        blocked.SetResult(new(CrashSendDisposition.Retry));
        await send;
        Assert.Single(Pending(folder.Path));
        await reporter.SendPendingAsync();
        Assert.Equal(2, transport.Reports.Count);
        Assert.IsType<UsageReport>(transport.Reports[1]);
        Assert.Empty(Pending(folder.Path));
    }

    [Fact]
    public async Task Previously_queued_launch_is_sent_as_a_metric_with_its_original_time()
    {
        using var folder = new TemporaryFolder();
        var clock = new Clock();
        using (var previous = new CrashReporter(Context, new FakeTransport(), clock))
        {
            previous.Configure(folder.Path, false, true);
            previous.RecordUsage();
        }
        var original = clock.GetUtcNow();
        clock.Advance(TimeSpan.FromDays(6));
        var handler = new RecordingHandler();
        using var restarted = new CrashReporter(Context,
            new SentryCrashTransport("https://publickey@collector.invalid/123", handler), clock);
        restarted.Configure(folder.Path, false, true);
        await restarted.SendPendingAsync();
        Assert.Empty(Pending(folder.Path));
        var lines = Assert.Single(handler.Bodies).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        using var item = JsonDocument.Parse(lines[1]);
        using var payload = JsonDocument.Parse(lines[2]);
        Assert.Equal("trace_metric", item.RootElement.GetProperty("type").GetString());
        Assert.Equal(original.ToUnixTimeMilliseconds() / 1000.0,
            payload.RootElement.GetProperty("items")[0].GetProperty("timestamp").GetDouble());
    }

    [Theory]
    [InlineData(true, "trace_metric", true)]
    [InlineData(true, "error", false)]
    [InlineData(false, "error", true)]
    [InlineData(false, "trace_metric", false)]
    [InlineData(true, "", true)]
    public async Task Successful_delivery_reads_only_the_relevant_rate_limit(bool usage, string category, bool limited)
    {
        var handler = new RecordingHandler
        {
            Response = () =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Headers.TryAddWithoutValidation("X-Sentry-Rate-Limits", $"180:{category}:organization");
                return response;
            },
        };
        using var transport = new SentryCrashTransport("https://publickey@collector.invalid/123", handler);
        var context = Context with { Architecture = "x64" };
        DiagnosticReport report = usage
            ? new UsageReport(1, Guid.NewGuid(), DateTimeOffset.UtcNow, context)
            : new CrashReport(1, Guid.NewGuid(), DateTimeOffset.UtcNow, context, CrashOrigin.Application,
                [new CrashException("System.Exception", [])]);
        var result = await transport.SendAsync(report, CancellationToken.None);
        Assert.Equal(CrashSendDisposition.Accepted, result.Disposition);
        Assert.Equal(limited ? TimeSpan.FromSeconds(180) : (TimeSpan?)null, result.RetryAfter);
    }

    [Fact]
    public async Task Maximum_metrics_batch_fits_the_envelope_limit_and_has_only_bounded_attributes()
    {
        var handler = new RecordingHandler();
        using var transport = new SentryCrashTransport("https://publickey@collector.invalid/123", handler);
        var now = DateTimeOffset.UtcNow;
        var report = new MetricsReport(1, Guid.NewGuid(), now,
            new CrashReportContext("2.0.0-" + new string('a', 90), "windows", "arm64"),
            Enumerable.Range(0, AppMetrics.MaximumPoints).Select(_ => new MetricPoint(now,
                new(MetricName.IntelligenceFirstResponse, 1234, MetricMode.Background, MetricOutcome.NetworkFailure, MetricArea.DashboardEditor))).ToArray());
        var result = await transport.SendAsync(report, CancellationToken.None);
        Assert.Equal(CrashSendDisposition.Accepted, result.Disposition);
        var body = Assert.Single(handler.Bodies);
        Assert.True(Encoding.UTF8.GetByteCount(body) <= 32768);
        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        using var item = JsonDocument.Parse(lines[1]);
        using var payload = JsonDocument.Parse(lines[2]);
        Assert.Equal("trace_metric", item.RootElement.GetProperty("type").GetString());
        Assert.Equal(AppMetrics.MaximumPoints, item.RootElement.GetProperty("item_count").GetInt32());
        var points = payload.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(AppMetrics.MaximumPoints, points.Length);
        Assert.All(points, point =>
        {
            Assert.Equal("distribution", point.GetProperty("type").GetString());
            Assert.Equal("millisecond", point.GetProperty("unit").GetString());
            Assert.Equal("dashynms.intelligence.first_response", point.GetProperty("name").GetString());
            Assert.Equal(new[] { "architecture", "area", "mode", "operating_system", "outcome", "sentry.release", "sentry.sdk.name", "sentry.sdk.version", "sentry.timestamp.sequence" },
                point.GetProperty("attributes").EnumerateObject().Select(property => property.Name).Order());
        });
        Assert.Equal(Enumerable.Range(0, AppMetrics.MaximumPoints).Select(i => (long)i),
            points.Select(point => point.GetProperty("attributes").GetProperty("sentry.timestamp.sequence").GetProperty("value").GetInt64()));
    }

    [Fact]
    public async Task Withdrawing_metrics_cancels_active_delivery_and_removes_queued_metrics()
    {
        using var folder = new TemporaryFolder();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = new TaskCompletionSource<CrashSendResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken token = default;
        var transport = new FakeTransport
        {
            Send = (_, cancellation) => { token = cancellation; started.SetResult(); return blocked.Task; },
        };
        using var reporter = new CrashReporter(Context, transport);
        reporter.ConfigureMetrics(folder.Path, false, true, true);
        reporter.RecordMetrics([new(DateTimeOffset.UtcNow, new(MetricName.FeatureUse, 1))]);
        var send = reporter.SendPendingAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        reporter.ConfigureMetrics(folder.Path, false, true, false);
        Assert.True(token.IsCancellationRequested);
        blocked.SetResult(new(CrashSendDisposition.Retry));
        await send;
        Assert.Empty(Pending(folder.Path));
        Assert.False(reporter.RecordMetrics([new(DateTimeOffset.UtcNow, new(MetricName.FeatureUse, 1))]));
    }

    [Theory]
    [InlineData(MetricName.ApiRequestCount, "dashynms.api.requests", "counter", "none")]
    [InlineData(MetricName.ApiDuration, "dashynms.api.duration", "distribution", "millisecond")]
    public async Task API_counts_and_timings_use_non_error_envelopes(MetricName metricName, string name, string type, string unit)
    {
        var handler = new RecordingHandler();
        using var transport = new SentryCrashTransport("https://publickey@collector.invalid/123", handler);
        var now = DateTimeOffset.UtcNow;
        var report = new MetricsReport(1, Guid.NewGuid(), now, Context with { Architecture = "x64" },
            [new(now, new(metricName, 1, Outcome: MetricOutcome.Timeout, Area: MetricArea.DeviceList))]);
        Assert.Equal(CrashSendDisposition.Accepted, (await transport.SendAsync(report, default)).Disposition);
        var lines = Assert.Single(handler.Bodies).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        using var item = JsonDocument.Parse(lines[1]);
        using var payload = JsonDocument.Parse(lines[2]);
        Assert.Equal("trace_metric", item.RootElement.GetProperty("type").GetString());
        var point = Assert.Single(payload.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(name, point.GetProperty("name").GetString());
        Assert.Equal(type, point.GetProperty("type").GetString());
        Assert.Equal(unit, point.GetProperty("unit").GetString());
        Assert.Equal("timeout", point.GetProperty("attributes").GetProperty("outcome").GetProperty("value").GetString());
        Assert.Equal("devicelist", point.GetProperty("attributes").GetProperty("area").GetProperty("value").GetString());
    }

    [Fact]
    public async Task Maximum_update_batch_fits_and_has_only_sanitised_version_dimensions()
    {
        var handler = new RecordingHandler();
        using var transport = new SentryCrashTransport("https://publickey@collector.invalid/123", handler);
        var now = DateTimeOffset.UtcNow;
        var version = "2.0.0-" + new string('a', 26);
        var report = new MetricsReport(1, Guid.NewGuid(), now,
            new("2.0.0-" + new string('a', 90), "windows", "arm64"),
            Enumerable.Range(0, AppMetrics.MaximumPoints).Select(_ => new MetricPoint(now,
                new(MetricName.UpdateDuration, 86400000, MetricMode.Notification, MetricOutcome.DifferentVersion,
                    MetricArea.UpdateAvailability, new(version, version, MetricUpdateChannel.Preview)))).ToArray());
        Assert.Equal(CrashSendDisposition.Accepted, (await transport.SendAsync(report, default)).Disposition);
        var body = Assert.Single(handler.Bodies);
        Assert.True(Encoding.UTF8.GetByteCount(body) <= 32768);
        using var payload = JsonDocument.Parse(body.Split('\n', StringSplitOptions.RemoveEmptyEntries)[2]);
        var attributes = payload.RootElement.GetProperty("items")[0].GetProperty("attributes");
        Assert.Equal(version, attributes.GetProperty("update.source").GetProperty("value").GetString());
        Assert.Equal(version, attributes.GetProperty("update.target").GetProperty("value").GetString());
        Assert.Equal("preview", attributes.GetProperty("update.channel").GetProperty("value").GetString());
        Assert.Equal(12, attributes.EnumerateObject().Count());
    }

    [Theory]
    [InlineData(SessionOutcome.Healthy, "healthy", true)]
    [InlineData(SessionOutcome.Errored, "errored", true)]
    [InlineData(SessionOutcome.ManagedCrash, "managedcrash", true)]
    [InlineData(SessionOutcome.Abnormal, "abnormal", false)]
    public async Task Sessions_use_metrics_with_a_fresh_interval_ID_and_no_unknown_duration(SessionOutcome outcome, string dimension, bool duration)
    {
        var handler = new RecordingHandler();
        using var transport = new SentryCrashTransport("https://publickey@collector.invalid/123", handler);
        var id = Guid.NewGuid();
        var report = new SessionHealthReport(1, id, DateTimeOffset.UtcNow, Context with { Architecture = "x64" }, outcome, duration ? 10000 : null);
        Assert.Equal(CrashSendDisposition.Accepted, (await transport.SendAsync(report, default)).Disposition);
        var lines = Assert.Single(handler.Bodies).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        using var item = JsonDocument.Parse(lines[1]);
        Assert.Equal("trace_metric", item.RootElement.GetProperty("type").GetString());
        Assert.Equal(duration ? 2 : 1, item.RootElement.GetProperty("item_count").GetInt32());
        using var payload = JsonDocument.Parse(lines[2]);
        Assert.Equal("never", payload.RootElement.GetProperty("ingest_settings").GetProperty("infer_ip").GetString());
        var points = payload.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal("dashynms.session.outcome", points[0].GetProperty("name").GetString());
        Assert.Equal(1, points[0].GetProperty("value").GetDouble());
        foreach (var point in points)
        {
            Assert.Equal(id.ToString("N"), point.GetProperty("trace_id").GetString());
            var attributes = point.GetProperty("attributes");
            Assert.Equal(9, attributes.EnumerateObject().Count());
            Assert.Equal(dimension, attributes.GetProperty("outcome").GetProperty("value").GetString());
            Assert.Equal("process", attributes.GetProperty("mode").GetProperty("value").GetString());
        }
        if (duration)
        {
            Assert.Equal("dashynms.session.duration", points[1].GetProperty("name").GetString());
            Assert.Equal("distribution", points[1].GetProperty("type").GetString());
            Assert.Equal("millisecond", points[1].GetProperty("unit").GetString());
        }
    }

    private static Exception SensitiveException()
    {
        var exception = new InvalidOperationException("secret-token-canary private-host-canary chat-text-canary C:\\Users\\private-user-canary\\private.txt",
            new IOException("https://private-host-canary/api?token=secret-token-canary"));
        exception.Source = "private-source-canary";
        exception.Data["private-key-canary"] = "secret-token-canary";
        var command = new RelayCommand(_ => throw exception);
        try { typeof(RelayCommand).GetMethod(nameof(RelayCommand.Execute))!.Invoke(command, [null]); }
        catch (TargetInvocationException thrown) { return thrown.InnerException!; }
        throw new InvalidOperationException("The synthetic command did not fail.");
    }

    private static void AssertSafe(string text)
    {
        foreach (var canary in new[] { "secret-token-canary", "private-host-canary", "chat-text-canary", "private-user-canary", "private-source-canary", "private-key-canary" })
            Assert.DoesNotContain(canary, text);
    }

    private static string[] Pending(string root)
    {
        var directory = System.IO.Path.Combine(root, "crash-reports");
        return Directory.Exists(directory) ? Directory.GetFiles(directory, "*.json") : [];
    }

    private sealed class ExternalFailure(string message) : Exception(message);

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan interval) => _now += interval;
    }

    private sealed class FakeTransport : ICrashTransport
    {
        public bool IsAvailable => true;
        public List<DiagnosticReport> Reports { get; } = [];
        public CrashSendResult Result { get; set; } = new(CrashSendDisposition.Accepted);
        public Func<DiagnosticReport, CancellationToken, Task<CrashSendResult>>? Send { get; init; }
        public Task<CrashSendResult> SendAsync(DiagnosticReport report, CancellationToken cancellationToken)
        {
            Reports.Add(report);
            return Send?.Invoke(report, cancellationToken) ?? Task.FromResult(Result);
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        public List<string> Urls { get; } = [];
        public List<string?> ContentTypes { get; } = [];
        public Func<HttpResponseMessage> Response { get; set; } = () => new(HttpStatusCode.OK);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            Urls.Add(request.RequestUri!.ToString());
            ContentTypes.Add(request.Content.Headers.ContentType?.MediaType);
            return Response();
        }
    }

    private sealed class TemporaryFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dashynms-crash-tests-" + Guid.NewGuid().ToString("N"));
        public TemporaryFolder() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            if (System.IO.Path.GetFileName(Path).StartsWith("dashynms-crash-tests-", StringComparison.Ordinal)) Directory.Delete(Path, recursive: true);
        }
    }
}
