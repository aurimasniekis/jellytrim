using System.Text;
using System.Text.Json.Serialization;
using Jellytrim.Core.Domain;
using Microsoft.Data.Sqlite;

namespace Jellytrim.Core.Persistence;

[JsonConverter(typeof(JsonStringEnumConverter<UserMatch>))]
public enum UserMatch { All = 0, Any = 1 }

[JsonConverter(typeof(JsonStringEnumConverter<ExplorerSort>))]
public enum ExplorerSort { Size = 0, Name = 1, DateAdded = 2, LastPlayed = 3, Reclaimable = 4 }

/// <summary>Filter set for the media explorer. Selection-by-query reuses this exact shape, so filters mean the same thing there.</summary>
public sealed class ExplorerQuery
{
    public List<Guid> LibraryIds { get; set; } = [];

    public List<MediaKind> Kinds { get; set; } = [];

    public Guid? ParentId { get; set; }

    public string? Search { get; set; }

    public long? MinBytes { get; set; }

    public long? MaxBytes { get; set; }

    public List<Guid> UserIds { get; set; } = [];

    public UserMatch UserMatch { get; set; } = UserMatch.All;

    public WatchState? WatchState { get; set; }

    public DateTime? LastPlayedBeforeUtc { get; set; }

    public DateTime? LastPlayedAfterUtc { get; set; }

    public DateTime? AddedBeforeUtc { get; set; }

    public DateTime? AddedAfterUtc { get; set; }

    public bool? Protected { get; set; }

    public Guid? PolicyId { get; set; }

    public bool? Eligible { get; set; }

    public ExplorerSort Sort { get; set; } = ExplorerSort.Size;

    public bool Descending { get; set; } = true;

    public int Offset { get; set; }

    public int Limit { get; set; } = 50;

    /// <summary>Internal: restricts the query to one row for the detail panel.</summary>
    [JsonIgnore]
    public Guid? OnlyIdForDetail { get; set; }

    public const int MaxLimit = 200;

    public const int MaxUsers = 25;

    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (Limit is < 1 or > MaxLimit)
        {
            problems.Add($"Limit must be between 1 and {MaxLimit}.");
        }

        if (Offset < 0)
        {
            problems.Add("Offset cannot be negative.");
        }

        if (UserIds.Count > MaxUsers)
        {
            problems.Add($"Select at most {MaxUsers} users.");
        }

        if (WatchState is not null && UserIds.Count == 0)
        {
            problems.Add("A watch-state filter needs at least one selected user.");
        }

        if (Eligible is not null && PolicyId is null)
        {
            problems.Add("An eligibility filter needs a policy.");
        }

        if (Search?.Length > 200)
        {
            problems.Add("Search is too long.");
        }

        return problems;
    }
}

public sealed record ExplorerUserState(Guid UserId, WatchState State, long PositionTicks, int PlayCount, DateTime? LastPlayedUtc, bool IsFavorite);

public sealed class ExplorerRow
{
    public required Guid Id { get; init; }

    public required MediaKind Kind { get; init; }

    public required string Name { get; init; }

    public string? SeriesName { get; init; }

    public int? SeasonNumber { get; init; }

    public int? EpisodeNumber { get; init; }

    public required Guid LibraryId { get; init; }

    public string? LibraryName { get; init; }

    public Guid? SeriesId { get; init; }

    public Guid? SeasonId { get; init; }

    public long LogicalBytes { get; init; }

    public long UniqueBytes { get; init; }

    public long ReclaimableBytes { get; init; }

    public int FileCount { get; init; }

    public int VersionCount { get; init; }

    public DateTime? DateAddedUtc { get; init; }

    public DateTime? LastPlayedUtc { get; init; }

    public long? RuntimeTicks { get; init; }

    public ItemHealth Health { get; init; }

    public bool Protected { get; init; }

    /// <summary>Leaves: whether eligible. Containers: whether any descendant is eligible. Null without a policy.</summary>
    public bool? Eligible { get; init; }

    public int? EligibleDescendants { get; init; }

    public int? TotalDescendants { get; init; }

    public int? Children { get; init; }

    public string? Path { get; init; }

    public IReadOnlyList<ExplorerUserState> UserStates { get; set; } = [];
}

public sealed record ExplorerPage(IReadOnlyList<ExplorerRow> Rows, long Total, int Offset, int Limit);

/// <summary>
/// Server-side filtering, sorting and paging over the inventory. Only constant fragments and integer keys reach the SQL
/// text; all client values are bound as parameters and the sort column is an allowlisted enum.
/// </summary>
public sealed class ExplorerStore(JellytrimDb db)
{
    private const string ProtectedSql = "(EXISTS (SELECT 1 FROM protections p WHERE p.target_id IN (i.item_id, i.season_id, i.series_id, i.library_id)))";

    public ExplorerPage Query(ExplorerQuery q) => db.Read(c =>
    {
        var build = Build(c, q);
        var total = Bind(c.Cmd($"{build.With} SELECT COUNT(*) FROM items i {build.Where}"), build).Scalar();
        var order = q.Sort switch
        {
            ExplorerSort.Name => "i.sort_name COLLATE NOCASE",
            ExplorerSort.DateAdded => "i.date_added",
            ExplorerSort.LastPlayed => "last_played",
            ExplorerSort.Reclaimable => "i.reclaimable_bytes",
            _ => "i.logical_bytes",
        };
        var direction = q.Descending ? "DESC" : "ASC";
        var nulls = q.Sort is ExplorerSort.LastPlayed or ExplorerSort.DateAdded ? " NULLS LAST" : string.Empty;
        var policy = q.PolicyId is null ? "NULL,NULL,NULL" :
            """
            (SELECT COUNT(*) FROM policy_eligible e WHERE e.policy_id=$policy AND e.item_key=i.item_key),
            CASE i.kind WHEN 2 THEN (SELECT COUNT(*) FROM items d JOIN policy_eligible e ON e.item_key=d.item_key AND e.policy_id=$policy WHERE d.series_id=i.item_id AND d.kind=4)
                        WHEN 3 THEN (SELECT COUNT(*) FROM items d JOIN policy_eligible e ON e.item_key=d.item_key AND e.policy_id=$policy WHERE d.season_id=i.item_id AND d.kind=4) END,
            CASE i.kind WHEN 2 THEN (SELECT COUNT(*) FROM items d WHERE d.series_id=i.item_id AND d.kind=4)
                        WHEN 3 THEN (SELECT COUNT(*) FROM items d WHERE d.season_id=i.item_id AND d.kind=4) END
            """;
        var lastPlayed = build.UserKeys.Length > 0
            ? $"""
              CASE WHEN i.kind IN (1,4) THEN (SELECT MAX(s.last_played) FROM item_user_state s WHERE s.item_key=i.item_key AND s.user_key IN ({string.Join(',', build.UserKeys)}))
                   ELSE (SELECT MAX(s.last_played) FROM items d JOIN item_user_state s ON s.item_key=d.item_key WHERE s.user_key IN ({string.Join(',', build.UserKeys)}) AND d.kind=4 AND (d.series_id=i.item_id OR d.season_id=i.item_id)) END
              """
            : "NULL";
        var sql = $"""
            {build.With}
            SELECT i.item_key,i.item_id,i.kind,i.name,i.series_name,i.season_no,i.episode_no,i.library_id,l.name,i.series_id,i.season_id,
                   i.logical_bytes,i.unique_bytes,i.reclaimable_bytes,i.file_count,i.version_count,i.date_added,({lastPlayed}) AS last_played,i.runtime_ticks,i.health,
                   {ProtectedSql},{policy},
                   CASE i.kind WHEN 2 THEN (SELECT COUNT(*) FROM items d WHERE d.series_id=i.item_id AND d.kind=3) WHEN 3 THEN (SELECT COUNT(*) FROM items d WHERE d.season_id=i.item_id AND d.kind=4) END,
                   i.path
            FROM items i LEFT JOIN libraries l ON l.library_id=i.library_id
            {build.Where}
            ORDER BY {order} {direction}{nulls}, i.item_id
            LIMIT $limit OFFSET $offset
            """;
        var keyed = new List<(long Key, ExplorerRow Row)>();
        using (var cmd = Bind(c.Cmd(sql), build).With("$limit", q.Limit).With("$offset", q.Offset))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                var kind = (MediaKind)r.GetInt32(2);
                var hasPolicy = q.PolicyId is not null;
                var eligibleDesc = r.IsDBNull(22) ? (int?)null : r.GetInt32(22);
                keyed.Add((r.GetInt64(0), new ExplorerRow
                {
                    Id = Guid.Parse(r.GetString(1)),
                    Kind = kind,
                    Name = r.GetString(3),
                    SeriesName = Db.Str(r, 4),
                    SeasonNumber = Db.Int(r, 5),
                    EpisodeNumber = Db.Int(r, 6),
                    LibraryId = Guid.Parse(r.GetString(7)),
                    LibraryName = Db.Str(r, 8),
                    SeriesId = Db.GuidOrNull(r, 9),
                    SeasonId = Db.GuidOrNull(r, 10),
                    LogicalBytes = r.GetInt64(11),
                    UniqueBytes = r.GetInt64(12),
                    ReclaimableBytes = r.GetInt64(13),
                    FileCount = r.GetInt32(14),
                    VersionCount = r.GetInt32(15),
                    DateAddedUtc = Db.Date(r, 16),
                    LastPlayedUtc = Db.Date(r, 17),
                    RuntimeTicks = r.IsDBNull(18) ? null : r.GetInt64(18),
                    Health = (ItemHealth)r.GetInt32(19),
                    Protected = r.GetBoolean(20),
                    Eligible = !hasPolicy ? null : kind is MediaKind.Movie or MediaKind.Episode ? r.GetInt32(21) > 0 : eligibleDesc > 0,
                    EligibleDescendants = eligibleDesc,
                    TotalDescendants = r.IsDBNull(23) ? null : r.GetInt32(23),
                    Children = r.IsDBNull(24) ? null : r.GetInt32(24),
                    Path = Db.Str(r, 25),
                }));
            }
        }

        AttachStates(c, q, build, keyed);
        return new ExplorerPage(keyed.Select(k => k.Row).ToList(), total, q.Offset, q.Limit);
    });

    /// <summary>Resolves every leaf id matching a query, for "select all matching". Bounded by the caller's expected count.</summary>
    public IReadOnlyList<Guid> MatchingIds(ExplorerQuery q, int max) => db.Read(c =>
    {
        var build = Build(c, q);
        return Bind(c.Cmd($"{build.With} SELECT i.item_id FROM items i {build.Where} ORDER BY i.item_id LIMIT $max"), build).With("$max", max)
            .Rows(r => Guid.Parse(r.GetString(0)));
    });

    public ExplorerRow? GetRow(Guid id, Guid? policyId)
    {
        var page = Query(new ExplorerQuery { ParentId = null, PolicyId = policyId, Limit = 1, OnlyIdForDetail = id });
        return page.Rows.FirstOrDefault();
    }

    public IReadOnlyList<Guid> ExpandToLeaves(IReadOnlyCollection<Guid> ids) => db.Read(c =>
    {
        var leaves = new List<Guid>();
        foreach (var id in ids)
        {
            leaves.AddRange(c.Cmd("""
                SELECT item_id FROM items WHERE kind IN (1,4) AND item_id=$i
                UNION SELECT item_id FROM items WHERE kind=4 AND (season_id=$i OR series_id=$i)
                """).With("$i", Db.Id(id)).Rows(r => Guid.Parse(r.GetString(0))));
        }

        return leaves.Distinct().ToList();
    });

    private sealed record Built(string With, string Where, long[] UserKeys, List<(string Name, object? Value)> Parameters);

    private static SqliteCommand Bind(SqliteCommand cmd, Built build)
    {
        foreach (var (name, value) in build.Parameters)
        {
            cmd.With(name, value);
        }

        return cmd;
    }

    private static Built Build(SqliteConnection c, ExplorerQuery q)
    {
        var where = new List<string>();
        var p = new List<(string, object?)>();
        void Add(string clause, string name, object? value)
        {
            where.Add(clause);
            p.Add((name, value));
        }

        if (q.PolicyId is { } policy)
        {
            p.Add(("$policy", Db.Id(policy)));
        }

        if (q.OnlyIdForDetail is { } only)
        {
            Add("i.item_id=$only", "$only", Db.Id(only));
        }

        if (q.LibraryIds.Count > 0)
        {
            var names = q.LibraryIds.Select((_, n) => "$lib" + n).ToArray();
            where.Add($"i.library_id IN ({string.Join(',', names)})");
            p.AddRange(q.LibraryIds.Select((id, n) => ("$lib" + n, (object?)Db.Id(id))));
        }

        if (q.ParentId is { } parent)
        {
            // Hierarchical navigation: a series shows its seasons, a season its episodes.
            Add("((i.kind=3 AND i.series_id=$parent) OR (i.kind=4 AND i.season_id=$parent))", "$parent", Db.Id(parent));
        }
        else if (q.Kinds.Count > 0)
        {
            where.Add($"i.kind IN ({string.Join(',', q.Kinds.Select(k => (int)k))})");
        }

        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var escaped = InventoryStore.Normalize(q.Search.Trim()).Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
            Add("i.search_name LIKE $search ESCAPE '\\'", "$search", "%" + escaped + "%");
        }

        if (q.MinBytes is { } min)
        {
            Add("i.logical_bytes >= $min", "$min", min);
        }

        if (q.MaxBytes is { } max)
        {
            Add("i.logical_bytes <= $max", "$max", max);
        }

        if (q.AddedBeforeUtc is { } before)
        {
            Add("i.date_added <= $addedBefore", "$addedBefore", Db.Ms(before));
        }

        if (q.AddedAfterUtc is { } after)
        {
            Add("i.date_added >= $addedAfter", "$addedAfter", Db.Ms(after));
        }

        if (q.Protected is { } isProtected)
        {
            where.Add(isProtected ? ProtectedSql : $"NOT {ProtectedSql}");
        }

        if (q.PolicyId is not null && q.Eligible is { } eligible)
        {
            var match = """
                (EXISTS (SELECT 1 FROM policy_eligible e WHERE e.policy_id=$policy AND e.item_key=i.item_key)
                 OR (i.kind=2 AND EXISTS (SELECT 1 FROM items d JOIN policy_eligible e ON e.item_key=d.item_key AND e.policy_id=$policy WHERE d.series_id=i.item_id AND d.kind=4))
                 OR (i.kind=3 AND EXISTS (SELECT 1 FROM items d JOIN policy_eligible e ON e.item_key=d.item_key AND e.policy_id=$policy WHERE d.season_id=i.item_id AND d.kind=4)))
                """;
            where.Add(eligible ? match : $"NOT {match}");
        }

        long[] userKeys = [];
        var with = string.Empty;
        if (q.UserIds.Count > 0)
        {
            var known = c.Cmd("SELECT user_id,user_key,readable_generation,deleted FROM users").Rows(r => (Id: Guid.Parse(r.GetString(0)), Key: r.GetInt64(1), Readable: r.GetInt64(2) > 0 && !r.GetBoolean(3)));
            var selected = known.Where(k => q.UserIds.Contains(k.Id)).ToList();
            userKeys = selected.Select(s => s.Key).ToArray();

            if (q.LastPlayedBeforeUtc is not null || q.LastPlayedAfterUtc is not null)
            {
                var keys = string.Join(',', userKeys.DefaultIfEmpty(-1));
                var latest = $"(SELECT MAX(s.last_played) FROM item_user_state s WHERE s.user_key IN ({keys}) AND s.item_key IN (SELECT d.item_key FROM items d WHERE d.item_id=i.item_id OR d.series_id=i.item_id OR d.season_id=i.item_id))";
                if (q.LastPlayedBeforeUtc is { } lpBefore)
                {
                    Add($"{latest} <= $lpBefore", "$lpBefore", Db.Ms(lpBefore));
                }

                if (q.LastPlayedAfterUtc is { } lpAfter)
                {
                    Add($"{latest} >= $lpAfter", "$lpAfter", Db.Ms(lpAfter));
                }
            }

            if (q.WatchState is { } state)
            {
                // Selected users who cannot be read count as Unknown on every item, so they can never satisfy "played by all".
                var unreadable = q.UserIds.Count - selected.Count(s => s.Readable);
                var u = q.UserIds.Count;
                var keys = string.Join(',', selected.Where(s => s.Readable).Select(s => s.Key).DefaultIfEmpty(-1));
                with = $"""
                    WITH leaf AS (
                        SELECT i.item_key AS k, i.item_id AS id, i.series_id AS series, i.season_id AS season,
                               COALESCE(SUM(s.state=1),0) AS played, COALESCE(SUM(s.state=2),0) AS partial, COALESCE(SUM(s.state=3),0)+{unreadable} AS unknown
                        FROM items i LEFT JOIN item_user_state s ON s.item_key=i.item_key AND s.user_key IN ({keys})
                        WHERE i.kind IN (1,4) GROUP BY i.item_key),
                    hit AS (SELECT k, id, series, season, ({Predicate(state, q.UserMatch, u)}) AS ok FROM leaf)
                    """;

                // Containers: "played" requires every episode to match; other states need at least one matching episode.
                var containerAll = state == Domain.WatchState.Played;
                var container = containerAll
                    ? "(EXISTS (SELECT 1 FROM hit h WHERE h.{0}=i.item_id) AND NOT EXISTS (SELECT 1 FROM hit h WHERE h.{0}=i.item_id AND h.ok=0))"
                    : "EXISTS (SELECT 1 FROM hit h WHERE h.{0}=i.item_id AND h.ok=1)";
                where.Add($"""
                    CASE i.kind
                        WHEN 2 THEN {string.Format(container, "series")}
                        WHEN 3 THEN {string.Format(container, "season")}
                        ELSE EXISTS (SELECT 1 FROM hit h WHERE h.k=i.item_key AND h.ok=1) END
                    """);
            }
        }

        var whereText = where.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", where);
        return new Built(with, whereText, userKeys, p);
    }

    private static string Predicate(WatchState state, UserMatch match, int u) => (state, match) switch
    {
        (WatchState.Played, UserMatch.All) => $"played={u}",
        (WatchState.Played, UserMatch.Any) => "played>=1",
        (WatchState.Partial, UserMatch.All) => $"partial={u}",
        (WatchState.Partial, UserMatch.Any) => "partial>=1",
        (WatchState.Unknown, UserMatch.All) => $"unknown={u}",
        (WatchState.Unknown, UserMatch.Any) => "unknown>=1",
        (WatchState.Unplayed, UserMatch.All) => "played=0 AND partial=0 AND unknown=0",
        (WatchState.Unplayed, UserMatch.Any) => $"({u}-played-partial-unknown)>=1",
        _ => "0",
    };

    private static void AttachStates(SqliteConnection c, ExplorerQuery q, Built build, List<(long Key, ExplorerRow Row)> keyed)
    {
        if (q.UserIds.Count == 0 || keyed.Count == 0)
        {
            return;
        }

        var users = c.Cmd("SELECT user_id,user_key,readable_generation,deleted FROM users").Rows(r => (Id: Guid.Parse(r.GetString(0)), Key: r.GetInt64(1), Readable: r.GetInt64(2) > 0 && !r.GetBoolean(3)))
            .Where(u => q.UserIds.Contains(u.Id)).ToList();
        var byKey = users.ToDictionary(u => u.Key, u => u.Id);
        var rows = new Dictionary<(long, long), ExplorerUserState>();
        var itemKeys = string.Join(',', keyed.Select(k => k.Key));
        var userKeys = string.Join(',', users.Select(u => u.Key).DefaultIfEmpty(-1));
        foreach (var (item, user, state) in c.Cmd($"SELECT item_key,user_key,state,position_ticks,play_count,last_played,favorite FROM item_user_state WHERE item_key IN ({itemKeys}) AND user_key IN ({userKeys})")
            .Rows(r => (r.GetInt64(0), r.GetInt64(1), new ExplorerUserState(byKey[r.GetInt64(1)], (WatchState)r.GetInt32(2), r.GetInt64(3), r.GetInt32(4), Db.Date(r, 5), r.GetBoolean(6)))))
        {
            rows[(item, user)] = state;
        }

        foreach (var (key, row) in keyed)
        {
            row.UserStates = q.UserIds.Select(id =>
            {
                var known = users.FirstOrDefault(u => u.Id == id);
                if (known == default || !known.Readable)
                {
                    return new ExplorerUserState(id, WatchState.Unknown, 0, 0, null, false);
                }

                return rows.TryGetValue((key, known.Key), out var s) ? s : new ExplorerUserState(id, WatchState.Unplayed, 0, 0, null, false);
            }).ToList();
        }
    }
}
