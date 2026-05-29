using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Lorekeeper.Research;

public interface IWebRobotsPolicy
{
    Task<WebRobotsDecision> IsAllowedAsync(Uri uri, CancellationToken cancellationToken = default);
}

public sealed record WebRobotsDecision(bool Allowed, string Diagnostics);

public sealed class WebRobotsPolicy(
    IWebHttpFetchClient fetchClient,
    IOptionsMonitor<WebResearchOptions> options,
    ILogger<WebRobotsPolicy> logger) : IWebRobotsPolicy
{
    private readonly ConcurrentDictionary<string, RobotsHostState> _hosts = new(StringComparer.OrdinalIgnoreCase);

    public async Task<WebRobotsDecision> IsAllowedAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        if (!options.CurrentValue.HonorRobotsTxt)
            return new WebRobotsDecision(true, string.Empty);

        var hostKey = WebLinkPolicy.HostKey(uri);
        var state = _hosts.GetOrAdd(hostKey, _ => new RobotsHostState());
        CachedRobotsTxt cached;

        await state.Semaphore.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (state.Cached is null || state.Cached.ExpiresAt <= now)
                state.Cached = await FetchRobotsAsync(uri, cancellationToken);

            cached = state.Cached;
        }
        finally
        {
            state.Semaphore.Release();
        }

        if (string.IsNullOrWhiteSpace(cached.Text))
            return new WebRobotsDecision(true, string.Empty);

        var allowed = RobotsTxtAllows(cached.Text, uri.PathAndQuery, options.CurrentValue.UserAgent);
        return allowed
            ? new WebRobotsDecision(true, string.Empty)
            : new WebRobotsDecision(false, $"robots.txt disallows reading {uri.PathAndQuery} on {hostKey}.");
    }

    private async Task<CachedRobotsTxt> FetchRobotsAsync(Uri uri, CancellationToken cancellationToken)
    {
        var cacheMinutes = Math.Clamp(options.CurrentValue.RobotsTxtCacheMinutes, 1, 24 * 60);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(cacheMinutes);
        var robotsUri = new UriBuilder(uri.Scheme, uri.Host, uri.IsDefaultPort ? -1 : uri.Port, "robots.txt").Uri;

        try
        {
            var result = await fetchClient.GetAsync(robotsUri, "text/plain,*/*;q=0.2", cancellationToken);
            if (!result.Success)
                return new CachedRobotsTxt(string.Empty, expiresAt);

            var text = WebPageTextExtractor.Decode(result.Content, result.Charset);
            return new CachedRobotsTxt(text, expiresAt);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to read robots.txt for {Host}", uri.Host);
            return new CachedRobotsTxt(string.Empty, expiresAt);
        }
    }

    private static bool RobotsTxtAllows(string robotsTxt, string pathAndQuery, string userAgent)
    {
        var product = userAgent.Split(['/', ' ', ';'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? userAgent;
        var groups = ParseGroups(robotsTxt);
        var rules = groups
            .Where(group => group.AppliesTo(product, userAgent))
            .SelectMany(group => group.Rules)
            .Where(rule => !string.IsNullOrWhiteSpace(rule.Pattern) && RuleMatches(rule.Pattern, pathAndQuery))
            .OrderByDescending(rule => RuleSpecificity(rule.Pattern))
            .ThenByDescending(rule => rule.Allow)
            .ToList();

        return rules.Count == 0 || rules[0].Allow;
    }

    private static IReadOnlyList<RobotsGroup> ParseGroups(string robotsTxt)
    {
        var groups = new List<RobotsGroup>();
        var currentAgents = new List<string>();
        var currentRules = new List<RobotsRule>();
        var hasRules = false;

        foreach (var rawLine in robotsTxt.Replace("\r", "\n").Split('\n'))
        {
            var line = StripComment(rawLine).Trim();
            if (line.Length == 0) continue;

            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0) continue;

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();

            if (key.Equals("User-agent", StringComparison.OrdinalIgnoreCase))
            {
                if (hasRules)
                {
                    AddCurrentGroup();
                    currentAgents = [];
                    currentRules = [];
                    hasRules = false;
                }

                currentAgents.Add(value);
                continue;
            }

            if (currentAgents.Count == 0) continue;
            if (key.Equals("Allow", StringComparison.OrdinalIgnoreCase))
            {
                currentRules.Add(new RobotsRule(value, Allow: true));
                hasRules = true;
            }
            else if (key.Equals("Disallow", StringComparison.OrdinalIgnoreCase))
            {
                currentRules.Add(new RobotsRule(value, Allow: false));
                hasRules = true;
            }
        }

        if (currentAgents.Count > 0)
            AddCurrentGroup();

        return groups;

        void AddCurrentGroup() =>
            groups.Add(new RobotsGroup(currentAgents.ToArray(), currentRules.ToArray()));
    }

    private static string StripComment(string line)
    {
        var index = line.IndexOf('#', StringComparison.Ordinal);
        return index < 0 ? line : line[..index];
    }

    private static bool RuleMatches(string pattern, string pathAndQuery)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;

        var anchored = pattern.EndsWith('$');
        var trimmed = anchored ? pattern[..^1] : pattern;
        var regex = "^" + Regex.Escape(trimmed).Replace("\\*", ".*");
        if (anchored) regex += "$";
        return Regex.IsMatch(pathAndQuery, regex, RegexOptions.IgnoreCase);
    }

    private static int RuleSpecificity(string pattern) =>
        pattern.Count(character => character is not '*' and not '$');

    private sealed record CachedRobotsTxt(string Text, DateTimeOffset ExpiresAt);

    private sealed class RobotsHostState
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public CachedRobotsTxt? Cached { get; set; }
    }

    private sealed record RobotsRule(string Pattern, bool Allow);

    private sealed record RobotsGroup(IReadOnlyList<string> Agents, IReadOnlyList<RobotsRule> Rules)
    {
        public bool AppliesTo(string product, string userAgent) =>
            Agents.Any(agent =>
                agent == "*"
                || product.Contains(agent, StringComparison.OrdinalIgnoreCase)
                || userAgent.Contains(agent, StringComparison.OrdinalIgnoreCase));
    }
}
