using System.Text.Json.Serialization;

namespace DesktopNMS.Diagnostics;

public enum CrashOrigin { Application, UiThread, Command }

public sealed record CrashReportContext(string Release, string OperatingSystem, string Architecture);

public sealed record CrashFrame(string Module, string Type, string Function);

public sealed record CrashException(string Type, IReadOnlyList<CrashFrame> Frames);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(CrashReport), "error")]
[JsonDerivedType(typeof(UsageReport), "usage")]
public abstract record DiagnosticReport(int Schema, Guid EventId, DateTimeOffset Timestamp, CrashReportContext Context);

public sealed record CrashReport(int Schema, Guid EventId, DateTimeOffset Timestamp,
    CrashReportContext Context, CrashOrigin Origin, IReadOnlyList<CrashException> Exceptions)
    : DiagnosticReport(Schema, EventId, Timestamp, Context);

public sealed record UsageReport(int Schema, Guid EventId, DateTimeOffset Timestamp, CrashReportContext Context)
    : DiagnosticReport(Schema, EventId, Timestamp, Context);

public enum CrashSendDisposition { Accepted, Retry, Rejected }

public sealed record CrashSendResult(CrashSendDisposition Disposition, TimeSpan? RetryAfter = null);

public interface ICrashTransport
{
    bool IsAvailable { get; }
    Task<CrashSendResult> SendAsync(DiagnosticReport report, CancellationToken cancellationToken);
}

public interface ICrashReporter
{
    bool IsAvailable { get; }
    void Configure(string dataDirectory, bool sendErrors, bool sendUsage = false);
    void Capture(Exception exception, CrashOrigin origin);
    void RecordUsage();
    Task SendPendingAsync(CancellationToken cancellationToken = default);
}
