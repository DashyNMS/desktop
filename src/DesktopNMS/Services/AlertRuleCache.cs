using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.Services;

/// <summary>
/// Caches every alert rule so callers can look one up by id - both for its
/// own details (e.g. its name) and for which columns its condition tests
/// (see <see cref="AlertRuleConditions.ExtractFields"/>), needed every time
/// the user selects an alert or a device's alert history loads a fault.
/// </summary>
/// <remarks>
/// Backed by one GET /rules call (see <see cref="IAlertRulesApi.ListAsync"/>)
/// instead of a separate GET /rules/{id} per rule id encountered - the whole
/// point of this cache, since rules change rarely and the ad hoc per-id
/// calls this replaced meant two different call sites (the Alerts tab and a
/// Device View's alert history) could each fetch the very same rule
/// separately. Loaded once, lazily, and kept for the life of the session;
/// <see cref="Clear"/> drops it (e.g. after signing in to a different
/// server) so the next lookup reloads from the new server.
/// </remarks>
public interface IAlertRuleCache
{
    /// <summary>The rule itself, or null if it could not be found or fetched.</summary>
    Task<AlertRule?> GetRuleAsync(int ruleId, CancellationToken cancellationToken = default);

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
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly ConcurrentDictionary<int, IReadOnlySet<string>> _fieldsByRule = new();

    private IReadOnlyDictionary<int, AlertRule>? _rulesById;

    public AlertRuleCache(ILibreNmsClient client, ILogger<AlertRuleCache> logger)
    {
        _client = client;
        _logger = logger;
    }

    public void Clear()
    {
        _rulesById = null;
        _fieldsByRule.Clear();
    }

    public async Task<AlertRule?> GetRuleAsync(int ruleId, CancellationToken cancellationToken = default)
    {
        var rules = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        return rules.TryGetValue(ruleId, out var rule) ? rule : null;
    }

    public async Task<IReadOnlySet<string>> GetConditionFieldsAsync(
        int ruleId,
        CancellationToken cancellationToken = default)
    {
        if (_fieldsByRule.TryGetValue(ruleId, out var cached))
        {
            return cached;
        }

        var rule = await GetRuleAsync(ruleId, cancellationToken).ConfigureAwait(false);
        var fields = AlertRuleConditions.ExtractFields(rule);
        _fieldsByRule[ruleId] = fields;

        if (rule is not null)
        {
            _logger.LogDebug(
                "Rule {RuleId} tests {Count} column(s): {Columns}",
                ruleId,
                fields.Count,
                string.Join(", ", fields));
        }

        return fields;
    }

    /// <summary>
    /// Loads every rule on first use and caches the result for the life of
    /// the session. Double-checked under <see cref="_loadGate"/> so several
    /// lookups arriving at once (e.g. a device's alert history resolving a
    /// handful of distinct rule ids concurrently) trigger exactly one
    /// GET /rules rather than one each.
    /// </summary>
    private async Task<IReadOnlyDictionary<int, AlertRule>> EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_rulesById is { } loaded)
        {
            return loaded;
        }

        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_rulesById is { } loadedWhileWaiting)
            {
                return loadedWhileWaiting;
            }

            try
            {
                var rules = await _client.Rules.ListAsync(cancellationToken).ConfigureAwait(false);
                var byId = rules.ToDictionary(r => r.Id);
                _rulesById = byId;
                _logger.LogDebug("Loaded {Count} alert rule(s)", byId.Count);
                return byId;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Not cached as empty: a transient failure here should not
                // permanently blank every rule lookup for the rest of the
                // session - the next lookup tries again.
                _logger.LogWarning(ex, "Could not load alert rules");
                return new Dictionary<int, AlertRule>();
            }
        }
        finally
        {
            _loadGate.Release();
        }
    }
}
