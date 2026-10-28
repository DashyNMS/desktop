using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DesktopNMS.Diagnostics;

internal static partial class CrashPayload
{
    internal const int MaximumBytes = 32 * 1024;
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        MaxDepth = 12,
    };

    private static readonly HashSet<string> AppAssemblies = new(StringComparer.Ordinal)
    {
        "DashyNMS", "DesktopNMS.Avalonia", "DesktopNMS.Presentation", "DesktopNMS.Core",
    };

    internal static CrashReport Create(Exception exception, CrashOrigin origin, CrashReportContext context, DateTimeOffset now)
    {
        var exceptions = new List<CrashException>();
        var remainingFrames = 32;
        for (Exception? current = exception; current is not null && exceptions.Count < 3; current = current.InnerException)
        {
            var type = current.GetType();
            var trusted = type.Assembly == typeof(Exception).Assembly || IsAppAssembly(type.Assembly);
            var name = trusted ? MetadataName(type) : "ExternalException";
            var frames = new StackTrace(current, false).GetFrames()
                .Select(frame => frame.GetMethod())
                .Where(method => method?.DeclaringType is not null && IsAppAssembly(method.Module.Assembly))
                .Take(remainingFrames)
                .Select(method => new CrashFrame(method!.Module.Assembly.GetName().Name!,
                    MetadataName(method.DeclaringType!), SafeName(method.Name)))
                .Reverse().ToArray();
            exceptions.Add(new(name, frames));
            remainingFrames -= frames.Length;
        }
        return new(1, Guid.NewGuid(), now, SafeContext(context), origin, exceptions);
    }

    internal static CrashReportContext SafeContext(CrashReportContext context) => new(
            context.Release.Length <= 96 && ReleasePattern().IsMatch(context.Release) ? context.Release : "unknown",
            context.OperatingSystem.ToLowerInvariant() switch { "windows" => "windows", "macos" or "osx" => "macos", "linux" => "linux", _ => "unknown" },
            context.Architecture.ToLowerInvariant() switch { "x64" => "x64", "arm64" => "arm64", "x86" => "x86", "arm" => "arm", _ => "unknown" });

    internal static bool IsValid(DiagnosticReport report)
    {
        if (report.Schema != 1 || report.EventId == Guid.Empty || report.Context is null ||
            report.Context.Release is null || report.Context.Release.Length > 96 ||
            (report.Context.Release != "unknown" && !ReleasePattern().IsMatch(report.Context.Release)) ||
            report.Context.OperatingSystem is not ("windows" or "macos" or "linux" or "unknown") ||
            report.Context.Architecture is not ("x64" or "arm64" or "x86" or "arm" or "unknown")) return false;
        if (report is UsageReport) return true;
        if (report is SessionHealthReport session) return Enum.IsDefined(session.Outcome) &&
            (session.DurationMilliseconds is null || (session.Outcome != SessionOutcome.Abnormal &&
             double.IsFinite(session.DurationMilliseconds.Value) && session.DurationMilliseconds is >= 0 and <= 7_776_000_000));
        if (report is MetricsReport metrics) return metrics.Points is { Count: > 0 and <= AppMetrics.MaximumPoints } &&
            metrics.Points.All(point => point is not null && AppMetrics.IsValid(point.Observation) &&
                point.Timestamp >= metrics.Timestamp && (point.Timestamp - metrics.Timestamp) <= TimeSpan.FromDays(7));
        if (report is not CrashReport crash || !Enum.IsDefined(crash.Origin) ||
            crash.Exceptions is null || crash.Exceptions.Count is < 1 or > 3) return false;
        return crash.Exceptions.All(exception => exception is not null && IsName(exception.Type) && exception.Frames is not null &&
            exception.Frames.Count <= 32 && exception.Frames.All(frame => frame is not null &&
                AppAssemblies.Contains(frame.Module) && IsName(frame.Type) && IsName(frame.Function)));
    }

    private static bool IsAppAssembly(Assembly assembly) => AppAssemblies.Contains(assembly.GetName().Name ?? "");
    private static string MetadataName(Type type) => SafeName(string.IsNullOrEmpty(type.Namespace) ? type.Name : type.Namespace + "." + type.Name);
    private static string SafeName(string name) => IsName(name) ? name : "Unknown";
    private static bool IsName(string? name) => name is { Length: > 0 and <= 256 } && MetadataPattern().IsMatch(name);

    [GeneratedRegex("^[A-Za-z0-9_.+<>`]+$", RegexOptions.CultureInvariant)]
    private static partial Regex MetadataPattern();

    [GeneratedRegex("^[0-9]+\\.[0-9]+\\.[0-9]+(?:-[A-Za-z0-9.-]+)?(?:\\+[A-Za-z0-9.-]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex ReleasePattern();
}
