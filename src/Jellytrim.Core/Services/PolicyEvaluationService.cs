using Jellytrim.Core.Abstractions;
using Jellytrim.Core.Domain;
using Jellytrim.Core.FileSystem;
using Jellytrim.Core.Persistence;
using Jellytrim.Core.Policies;
using Jellytrim.Core.Storage;

namespace Jellytrim.Core.Services;

/// <summary>Runs policies over the inventory and stores the eligible set so that the explorer can filter by it.</summary>
public sealed class PolicyEvaluationService(InventoryStore inventory, PolicyStore policies, TimeProvider clock)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public EvaluationInput LoadInput(ActivePlayback? playback = null) =>
        inventory.LoadEvaluationInput(Now, playback ?? ActivePlayback.None, policies.ProtectedIds(), PathGuard.PlatformComparison);

    public void EvaluateAll()
    {
        var all = policies.List();
        if (all.Count == 0)
        {
            return;
        }

        var input = LoadInput();
        foreach (var policy in all)
        {
            Evaluate(policy, input);
        }
    }

    public PolicyRun Evaluate(CleanupPolicy policy, EvaluationInput? input = null)
    {
        input ??= LoadInput();
        var result = PolicyEvaluator.Evaluate(policy, input);

        var eligible = new List<Guid>();
        var blocked = new Dictionary<string, int>(StringComparer.Ordinal);
        var refs = new List<LeafItem>();
        var byId = input.Leaves.ToDictionary(l => l.Id);
        foreach (var (id, evaluation) in result.Items)
        {
            if (evaluation.Eligible)
            {
                eligible.Add(id);
                refs.Add(byId[id]);
            }
            else
            {
                foreach (var rule in evaluation.Blocking)
                {
                    blocked[rule.RuleId] = blocked.GetValueOrDefault(rule.RuleId) + 1;
                }
            }
        }

        var run = new PolicyRun(policy.Id, policy.Revision, inventory.CurrentGeneration(), policies.ProtectionRevision(), Now, eligible.Count, input.Leaves.Count, refs.Sum(r => r.LogicalBytes), refs.Sum(r => r.ReclaimableBytes), blocked);
        policies.SaveRun(run, eligible);
        return run;
    }

    public RecommendationSet Recommend(CleanupPolicy policy, long targetBytes, ActivePlayback playback, int maxItems = 1000)
    {
        var input = LoadInput(playback);
        var result = PolicyEvaluator.Evaluate(policy, input);
        var users = policy.EffectiveUsers;
        var candidates = input.Leaves.Where(l => result.Items[l.Id].Eligible).Select(l => new RecommendationCandidate
        {
            Item = l,
            Evaluation = result.Items[l.Id],
            LastPlayedUtc = users.Select(u => input.States.TryGetValue((l.Id, u), out var s) ? s.LastPlayedUtc : null).Max(),
        });
        return RecommendationRanker.Rank(candidates, targetBytes, Now, maxItems);
    }

    /// <summary>Full explanation for one item, including rules that passed.</summary>
    public ItemEvaluation? Explain(CleanupPolicy policy, Guid itemId, ActivePlayback playback)
    {
        var input = LoadInput(playback);
        return PolicyEvaluator.Evaluate(policy, input).Items.GetValueOrDefault(itemId);
    }
}
