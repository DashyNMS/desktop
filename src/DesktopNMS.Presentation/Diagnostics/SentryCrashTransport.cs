using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace DesktopNMS.Diagnostics;

public sealed class SentryCrashTransport : ICrashTransport, IDisposable
{
    private readonly HttpClient _client;
    private readonly Uri? _endpoint;
    private readonly string? _publicKey;
    private long _metricSequence = -1;

    public SentryCrashTransport(string dsn) : this(dsn, new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseDefaultCredentials = false,
    }) { }

    public SentryCrashTransport(string dsn, HttpMessageHandler handler)
    {
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(10) };
        if (!Uri.TryCreate(dsn, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrEmpty(uri.UserInfo) || uri.UserInfo.Contains(':') ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return;
        var separator = uri.AbsolutePath.LastIndexOf('/');
        var project = uri.AbsolutePath[(separator + 1)..];
        if (project.Length == 0 || !project.All(char.IsAsciiDigit) ||
            !uri.UserInfo.All(char.IsAsciiLetterOrDigit)) return;
        _publicKey = uri.UserInfo;
        _endpoint = new UriBuilder(uri)
        {
            UserName = "", Password = "", Path = uri.AbsolutePath[..(separator + 1)] + "api/" + project + "/envelope/",
        }.Uri;
    }

    public bool IsAvailable => _endpoint is not null;

    public async Task<CrashSendResult> SendAsync(DiagnosticReport report, CancellationToken cancellationToken)
    {
        if (_endpoint is null || !CrashPayload.IsValid(report)) return new(CrashSendDisposition.Rejected);
        using var body = new MemoryStream();
        WriteJsonLine(body, new { event_id = report.EventId.ToString("N") });
        var usage = report is UsageReport;
        var payload = usage ? UsageMetric((UsageReport)report) : CrashEvent((CrashReport)report);
        if (usage)
            WriteJsonLine(body, new { type = "trace_metric", item_count = 1,
                content_type = "application/vnd.sentry.items.trace-metric+json", length = payload.Length });
        else
            WriteJsonLine(body, new { type = "event", length = payload.Length });
        body.Write(payload);
        body.WriteByte((byte)'\n');
        if (body.Length > CrashPayload.MaximumBytes) return new(CrashSendDisposition.Rejected);
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        request.Headers.TryAddWithoutValidation("X-Sentry-Auth", "Sentry sentry_version=7,sentry_key=" + _publicKey);
        request.Content = new ByteArrayContent(body.ToArray());
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-sentry-envelope");
        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return new(CrashSendDisposition.Accepted, RetryDelay(response, usage ? "trace_metric" : "error"));
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return new(CrashSendDisposition.Retry, RetryDelay(response, usage ? "trace_metric" : "error") ?? TimeSpan.FromMinutes(1));
            return new((int)response.StatusCode >= 500 ? CrashSendDisposition.Retry : CrashSendDisposition.Rejected);
        }
        catch (HttpRequestException) { return new(CrashSendDisposition.Retry); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(CrashSendDisposition.Retry); }
    }

    // Keep the on-disk usage schema unchanged: old queued launches use this path too.
    // The random report ID supplies a local propagation context, never an installation ID.
    private byte[] UsageMetric(UsageReport report) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        version = 2,
        ingest_settings = new { infer_ip = "never", infer_user_agent = "never" },
        items = new[]
        {
            new
            {
                timestamp = report.Timestamp.ToUnixTimeMilliseconds() / 1000.0,
                trace_id = report.EventId.ToString("N"),
                name = "dashynms.app_start", type = "counter", value = 1,
                attributes = new Dictionary<string, object>
                {
                    ["sentry.release"] = new { type = "string", value = report.Context.Release },
                    ["operating_system"] = new { type = "string", value = report.Context.OperatingSystem },
                    ["architecture"] = new { type = "string", value = report.Context.Architecture },
                    ["sentry.sdk.name"] = new { type = "string", value = "dashynms.envelope" },
                    ["sentry.sdk.version"] = new { type = "string", value = "1.0.0" },
                    ["sentry.timestamp.sequence"] = new { type = "integer", value = Interlocked.Increment(ref _metricSequence) },
                },
            },
        },
    });

    private static byte[] CrashEvent(CrashReport report) => JsonSerializer.SerializeToUtf8Bytes(new
        {
            event_id = report.EventId.ToString("N"), timestamp = report.Timestamp.ToUnixTimeMilliseconds() / 1000.0,
            platform = "csharp", level = report.Origin == CrashOrigin.Command ? "error" : "fatal",
            release = report.Context.Release,
            tags = new { operating_system = report.Context.OperatingSystem, architecture = report.Context.Architecture, origin = report.Origin.ToString() },
            exception = new
            {
                values = report.Exceptions.Reverse().Select(exception => new
                {
                    type = exception.Type, value = "Managed application error",
                    mechanism = new { type = "generic", handled = report.Origin == CrashOrigin.Command },
                    stacktrace = new { frames = exception.Frames.Select(frame => new { module = frame.Module, function = frame.Type + "." + frame.Function, in_app = true }) },
                }),
            },
        });

    private static TimeSpan? RetryDelay(HttpResponseMessage response, string category)
    {
        var delay = response.Headers.RetryAfter?.Delta ??
            (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow);
        if (response.Headers.TryGetValues("X-Sentry-Rate-Limits", out var values))
        {
            foreach (var limit in values.SelectMany(value => value.Split(',')))
            {
                var parts = limit.Split(':');
                if (parts.Length < 2 || (parts[1].Length != 0 && !parts[1].Split(';').Contains(category))) continue;
                if (double.TryParse(parts[0], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds) &&
                    double.IsFinite(seconds) && seconds > 0)
                    delay = TimeSpan.FromSeconds(Math.Max(delay?.TotalSeconds ?? 0, Math.Min(seconds, 24 * 60 * 60)));
            }
        }
        return delay is null ? null : TimeSpan.FromSeconds(Math.Clamp(delay.Value.TotalSeconds, 1, 24 * 60 * 60));
    }

    private static void WriteJsonLine(Stream stream, object value)
    {
        stream.Write(JsonSerializer.SerializeToUtf8Bytes(value));
        stream.WriteByte((byte)'\n');
    }

    public void Dispose() => _client.Dispose();
}
