using Jellytrim.Core.Domain;
using Microsoft.Data.Sqlite;

namespace Jellytrim.Core.Persistence;

/// <summary>A user's progress through a series or season, totalled from its episodes.</summary>
public sealed record EpisodeProgress(int Played, int InProgress, int Unknown, int Total, DateTime? LastPlayedUtc, int PlayCount)
{
    /// <summary>
    /// Played only when every episode is played and none is part-way through, since a rewatch in progress is still being
    /// watched. In progress when any episode is played or started. Unknown when nothing is known to be watched but some
    /// episode's state could not be read. Matches how the explorer's watch filter treats shows.
    /// </summary>
    public WatchState State => Total > 0 && Played == Total && InProgress == 0 ? WatchState.Played
        : Played + InProgress > 0 ? WatchState.Partial
        : Unknown > 0 ? WatchState.Unknown
        : WatchState.Unplayed;
}

/// <summary>
/// Jellyfin's Played flag on a series or season is derived, so jellytrim records state only on episodes and movies
/// (see <c>UserStateMerge</c>). This totals episode states back up so a show can say who finished it.
/// </summary>
internal static class EpisodeRollup
{
    /// <summary>Progress for each container and user. A user with no episode state gets a zero row, so every container with episodes has one per user.</summary>
    public static Dictionary<(long Container, long User), EpisodeProgress> Load(SqliteConnection c, IReadOnlyCollection<long> containerKeys, IReadOnlyCollection<long> userKeys)
    {
        var result = new Dictionary<(long, long), EpisodeProgress>();
        if (containerKeys.Count == 0 || userKeys.Count == 0)
        {
            return result;
        }

        var containers = string.Join(',', containerKeys);
        var users = string.Join(',', userKeys);
        // Two joins rather than one OR, so each uses its index.
        var episodes = $"""
            WITH ep AS (
                SELECT c.item_key AS ck, e.item_key AS ek FROM items c JOIN items e ON e.series_id=c.item_id AND e.kind=4 WHERE c.kind=2 AND c.item_key IN ({containers})
                UNION ALL
                SELECT c.item_key, e.item_key FROM items c JOIN items e ON e.season_id=c.item_id AND e.kind=4 WHERE c.kind=3 AND c.item_key IN ({containers}))
            """;

        var totals = c.Cmd($"{episodes} SELECT ck, COUNT(*) FROM ep GROUP BY ck").Rows(r => (Key: r.GetInt64(0), Total: r.GetInt32(1)))
            .ToDictionary(t => t.Key, t => t.Total);
        var progress = c.Cmd($"""
            {episodes}
            SELECT ep.ck, s.user_key, SUM(s.state=1), SUM(s.state=2), SUM(s.state=3), MAX(s.last_played), SUM(s.play_count)
            FROM ep JOIN item_user_state s ON s.item_key=ep.ek AND s.user_key IN ({users})
            GROUP BY ep.ck, s.user_key
            """).Rows(r => (Container: r.GetInt64(0), User: r.GetInt64(1), Played: r.GetInt32(2), InProgress: r.GetInt32(3), Unknown: r.GetInt32(4), Last: Db.Date(r, 5), Plays: r.GetInt32(6)))
            .ToDictionary(p => (p.Container, p.User));

        foreach (var (container, total) in totals)
        {
            foreach (var user in userKeys)
            {
                result[(container, user)] = progress.TryGetValue((container, user), out var p)
                    ? new EpisodeProgress(p.Played, p.InProgress, p.Unknown, total, p.Last, p.Plays)
                    : new EpisodeProgress(0, 0, 0, total, null, 0);
            }
        }

        return result;
    }
}
