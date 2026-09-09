using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Api;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.Services;

/// <summary>
/// Caches alert rule definitions so the fault view can tell which columns the
/// rule actually tests.
/// </summary>
/// <remarks>
/// Rules change rarely, and the answer is needed every time the user selects an
/// alert, so this is cached for the life of the session. A failed lookup is
/// cached as "unknown" rather than retried on every click.
/// </remarks>
public interface IAlertRuleCache
{
    /// <summary>
    /// The columns rule <paramref name="ruleId"/> tests, or an empty set when
    /// the rule could not be fetched or parsed.
    /// </summary>
    Task<IReadOnlySet<string>> GetConditionFieldsAsync(int ruleId, CancellationToken cancellationToken = default);

    /// <summary>Drops everything, e.g. after signing in to a different server.</summary>
    void Clear();
}

public sealed class AlertRuleCache : IAlertRuleCache
{
    private static readonly IReadOnlySet<string> None =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private readonly ILibreNmsClient _client;
    private readonly ILogger<AlertRuleCache> _logger;
    private readonly ConcurrentDictionary<int, IReadOnlySet<string>> _fields = new();

    public AlertRuleCache(ILibreNmsClient client, ILogger<AlertRuleCache> logger)
    {
        _client = client;
        _logger = logger;
    }

    public void Clear() => _fields.Clear();

    public async Task<IReadOnlySet<string>> GetConditionFieldsAsync(
        int ruleId,
        CancellationToken cancellationToken = default)
    {
        if (_fields.TryGetValue(ruleId, out var cached))
        {
            return cached;
        }

        try
        {
            var rule = await _client.Rules.GetAsync(ruleId, cancellationToken).ConfigureAwait(false);
            var fields = AlertRuleConditions.ExtractFields(rule);

            _fields[ruleId] = fields;

            _logger.LogDebug(
                "Rule {RuleId} tests {Count} column(s): {Columns}",
                ruleId,
                fields.Count,
                string.Join(", ", fields));

            return fields;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Without the rule the faults are still shown, just undifferentiated.
            _logger.LogWarning(ex, "Could not read alert rule {RuleId}", ruleId);
            _fields[ruleId] = None;
            return None;
        }
    }
}
