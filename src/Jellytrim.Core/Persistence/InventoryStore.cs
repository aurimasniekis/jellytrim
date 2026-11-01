using System.Text;
using Jellytrim.Core.Abstractions;
using Jellytrim.Core.Domain;
using Jellytrim.Core.FileSystem;
using Jellytrim.Core.Policies;
using Jellytrim.Core.Storage;
using Microsoft.Data.Sqlite;

namespace Jellytrim.Core.Persistence;

public sealed record LibraryRow(Guid Id, string Name, string? CollectionType, IReadOnlyList<string> Locations, bool Online, string? OfflineDetail, DateTime? LastScanUtc, int ItemCount, int FileCount, long LogicalBytes, long UniqueBytes, long AllocatedBytes, long ReclaimableBytes);

public sealed record UserRow(Guid Id, string Name, bool IsAdministrator, bool IsDisabled, bool Deleted, bool AllLibraries, IReadOnlyList<Guid> Libraries, long ReadableGeneration);

public sealed record VolumeRow(string VolumeId, string SamplePath, long? TotalBytes, long? FreeBytes, DateTime? ObservedUtc, bool Available, IReadOnlyList<Guid> LibraryIds);

public sealed record ObservedFile(string Path, FileRole Role, Guid VersionId, FileObservation Observation);

public sealed record ScannedItem(HostItem Item, ItemHealth Health, IReadOnlyList<ObservedFile> Files);

public sealed record ScanRow(long Id, long Generation, string Trigger, string Status, DateTime StartedUtc, DateTime? FinishedUtc, int ItemsSeen, int FilesSeen, string? Message, IReadOnlyList<string> Warnings);

public sealed record ItemFileRow(string Path, FileRole Role, Guid VersionId, string? Identity, string? VolumeId, long Size, long Allocated, int LinkCount, long MtimeNs, ProbeStatus Status, DateTime ObservedUtc, int OtherItemReferences);

public sealed record ItemStateRow(Guid UserId, string UserName, WatchState State, long PositionTicks, int PlayCount, DateTime? LastPlayedUtc, bool IsFavorite);

public sealed record SnapshotPoint(DateTime TakenUtc, string ScopeId, string ScopeName, long LogicalBytes, long UniqueBytes, long AllocatedBytes, long? FreeBytes, long? TotalBytes);

public sealed record WatchSummary(Guid LibraryId, int Items, long PlayedByAllBytes, int PlayedByAllItems, long PlayedBySomeBytes, int PlayedBySomeItems, long UnplayedBytes, int UnplayedItems, long UnknownBytes, int UnknownItems);

/// <summary>Inventory reads and writes. All observation data is advisory: execution re-reads the filesystem and the server.</summary>
public sealed class InventoryStore(JellytrimDb db)
{
    public long CurrentGeneration() => db.Read(c => c.Cmd("SELECT value FROM counters WHERE key='scan_generation'").Scalar());

    public long BeginScan(string trigger, DateTime nowUtc) => db.Write((c, tx) =>
    {
        c.Cmd("UPDATE scans SET status='Interrupted', finished_at=$t, message='The server stopped before this scan finished.' WHERE status='Running'", tx).With("$t", Db.Ms(nowUtc)).Exec();
        c.Cmd("UPDATE counters SET value=value+1 WHERE key='scan_generation'", tx).Exec();
        var generation = c.Cmd("SELECT value FROM counters WHERE key='scan_generation'", tx).Scalar();
        c.Cmd("INSERT INTO scans(generation,trigger,status,started_at) VALUES($g,$t,'Running',$s)", tx).With("$g", generation).With("$t", trigger).With("$s", Db.Ms(nowUtc)).Exec();
        return generation;
    });

    public void FinishScan(long generation, string status, DateTime nowUtc, int items, int files, string? message, IReadOnlyList<string> warnings) => db.Write((c, tx) =>
        c.Cmd("UPDATE scans SET status=$s,finished_at=$f,items_seen=$i,files_seen=$n,message=$m,warnings=$w WHERE generation=$g", tx)
            .With("$s", status).With("$f", Db.Ms(nowUtc)).With("$i", items).With("$n", files).With("$m", message).With("$w", Json.Write(warnings)).With("$g", generation).Exec());

    public ScanRow? LatestScan(bool successfulOnly = false) => db.Read(c =>
        c.Cmd($"SELECT scan_id,generation,trigger,status,started_at,finished_at,items_seen,files_seen,message,warnings FROM scans {(successfulOnly ? "WHERE status IN ('Completed','CompletedWithWarnings')" : string.Empty)} ORDER BY scan_id DESC LIMIT 1")
            .Rows(r => new ScanRow(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), Db.Date(r, 4)!.Value, Db.Date(r, 5), r.GetInt32(6), r.GetInt32(7), Db.Str(r, 8), Json.Read<List<string>>(r.GetString(9))))
            .FirstOrDefault());

    public void UpsertUsers(IReadOnlyList<HostUser> users) => db.Write((c, tx) =>
    {
        c.Cmd("UPDATE users SET deleted=1", tx).Exec();
        foreach (var u in users)
        {
            c.Cmd("""
                INSERT INTO users(user_id,name,is_admin,is_disabled,deleted,all_libraries,libraries) VALUES($i,$n,$a,$d,0,$al,$l)
                ON CONFLICT(user_id) DO UPDATE SET name=$n,is_admin=$a,is_disabled=$d,deleted=0,all_libraries=$al,libraries=$l
                """, tx).With("$i", Db.Id(u.Id)).With("$n", u.Name).With("$a", u.IsAdministrator).With("$d", u.IsDisabled).With("$al", u.HasAllLibraries).With("$l", Json.Write(u.EnabledLibraries)).Exec();
        }
    });

    public void MarkUsersReadable(IEnumerable<Guid> users, long generation) => db.Write((c, tx) =>
    {
        foreach (var u in users)
        {
            c.Cmd("UPDATE users SET readable_generation=$g WHERE user_id=$i", tx).With("$g", generation).With("$i", Db.Id(u)).Exec();
        }
    });

    public IReadOnlyList<UserRow> ListUsers(bool includeDeleted = false) => db.Read(c =>
        c.Cmd($"SELECT user_id,name,is_admin,is_disabled,deleted,all_libraries,libraries,readable_generation FROM users {(includeDeleted ? string.Empty : "WHERE deleted=0")} ORDER BY name COLLATE NOCASE")
            .Rows(r => new UserRow(Guid.Parse(r.GetString(0)), r.GetString(1), r.GetBoolean(2), r.GetBoolean(3), r.GetBoolean(4), r.GetBoolean(5), Json.Read<List<Guid>>(r.GetString(6)), r.GetInt64(7))));

    public void UpsertLibrary(HostLibrary library, bool online, string? detail, long generation, DateTime nowUtc) => db.Write((c, tx) =>
        c.Cmd("""
            INSERT INTO libraries(library_id,name,collection_type,locations,online,offline_detail,last_scan_at,scan_generation) VALUES($i,$n,$t,$l,$o,$d,$s,$g)
            ON CONFLICT(library_id) DO UPDATE SET name=$n,collection_type=$t,locations=$l,online=$o,offline_detail=$d,last_scan_at=CASE WHEN $o THEN $s ELSE last_scan_at END,scan_generation=CASE WHEN $o THEN $g ELSE scan_generation END
            """, tx).With("$i", Db.Id(library.Id)).With("$n", library.Name).With("$t", library.CollectionType).With("$l", Json.Write(library.Locations))
            .With("$o", online).With("$d", detail).With("$s", Db.Ms(nowUtc)).With("$g", generation).Exec());

    public IReadOnlyList<LibraryRow> ListLibraries() => db.Read(c =>
        c.Cmd("SELECT library_id,name,collection_type,locations,online,offline_detail,last_scan_at,item_count,file_count,logical_bytes,unique_bytes,allocated_bytes,reclaimable_bytes FROM libraries ORDER BY name COLLATE NOCASE")
            .Rows(r => new LibraryRow(Guid.Parse(r.GetString(0)), r.GetString(1), Db.Str(r, 2), Json.Read<List<string>>(r.GetString(3)), r.GetBoolean(4), Db.Str(r, 5), Db.Date(r, 6), r.GetInt32(7), r.GetInt32(8), r.GetInt64(9), r.GetInt64(10), r.GetInt64(11), r.GetInt64(12))));

    /// <summary>Removes libraries the server no longer has, with everything indexed under them.</summary>
    public void RemoveLibrariesExcept(IReadOnlyCollection<Guid> keep) => db.Write((c, tx) =>
    {
        var existing = c.Cmd("SELECT library_id FROM libraries", tx).Rows(r => r.GetString(0));
        foreach (var id in existing.Where(id => !keep.Contains(Guid.Parse(id))))
        {
            c.Cmd("DELETE FROM items WHERE library_id=$i", tx).With("$i", id).Exec();
            c.Cmd("DELETE FROM libraries WHERE library_id=$i", tx).With("$i", id).Exec();
        }
    });

    /// <summary>Writes one page in a single transaction. Failed users get an explicit Unknown row on every item of the page.</summary>
    public void WritePage(IReadOnlyList<ScannedItem> items, HostItemPage page, IReadOnlyCollection<Guid> users, long generation, DateTime nowUtc) => db.Write((c, tx) =>
    {
        var userKeys = c.Cmd("SELECT user_id,user_key FROM users", tx).Rows(r => (Guid.Parse(r.GetString(0)), r.GetInt64(1))).ToDictionary(p => p.Item1, p => p.Item2);
        using var upsert = c.Cmd("""
            INSERT INTO items(item_id,kind,library_id,series_id,season_id,series_name,name,sort_name,search_name,season_no,episode_no,date_added,premiere,runtime_ticks,tags,path,health,logical_bytes,unique_bytes,allocated_bytes,reclaimable_bytes,file_count,version_count,scan_generation)
            VALUES($id,$kind,$lib,$series,$season,$sn,$name,$sort,$search,$sno,$eno,$added,$prem,$rt,$tags,$path,$health,$lb,$ub,$ab,$rb,$fc,$vc,$gen)
            ON CONFLICT(item_id) DO UPDATE SET kind=$kind,library_id=$lib,series_id=$series,season_id=$season,series_name=$sn,name=$name,sort_name=$sort,search_name=$search,season_no=$sno,episode_no=$eno,date_added=$added,premiere=$prem,runtime_ticks=$rt,tags=$tags,path=$path,health=$health,logical_bytes=$lb,unique_bytes=$ub,allocated_bytes=$ab,reclaimable_bytes=$rb,file_count=$fc,version_count=$vc,scan_generation=$gen
            RETURNING item_key
            """, tx);
        foreach (var scanned in items)
        {
            var item = scanned.Item;
            var refs = scanned.Files.Where(f => f.Observation.Status == ProbeStatus.Ok)
                .Select(f => new FileRef(f.Path, f.Observation.Identity, f.Observation.Size, f.Observation.AllocatedBytes, f.Observation.LinkCount)).ToList();
            var estimate = StorageAccounting.Estimate(refs);
            upsert.Parameters.Clear();
            upsert.With("$id", Db.Id(item.Id)).With("$kind", (int)item.Kind).With("$lib", Db.Id(item.LibraryId)).With("$series", Db.Id(item.SeriesId)).With("$season", Db.Id(item.SeasonId))
                .With("$sn", item.SeriesName).With("$name", item.Name).With("$sort", item.SortName ?? item.Name).With("$search", Normalize(item.SeriesName is null ? item.Name : item.SeriesName + " " + item.Name))
                .With("$sno", item.SeasonNumber).With("$eno", item.EpisodeNumber).With("$added", Db.Ms(item.DateAddedUtc)).With("$prem", Db.Ms(item.PremiereDateUtc)).With("$rt", item.RuntimeTicks)
                .With("$tags", EncodeTags(item.Tags)).With("$path", item.Path).With("$health", (int)scanned.Health).With("$lb", estimate.LogicalBytes).With("$ub", estimate.UniqueBytes)
                .With("$ab", refs.GroupBy(r => r.Identity ?? r.Path).Sum(g => g.First().AllocatedBytes)).With("$rb", estimate.EstimatedReclaimableBytes).With("$fc", scanned.Files.Count).With("$vc", Math.Max(1, item.VersionIds.Count)).With("$gen", generation);
            var key = Convert.ToInt64(upsert.ExecuteScalar());

            c.Cmd("DELETE FROM item_files WHERE item_key=$k", tx).With("$k", key).Exec();
            foreach (var file in scanned.Files)
            {
                var o = file.Observation;
                var fileKey = c.Cmd("""
                    INSERT INTO files(path,identity,volume_id,size,allocated,link_count,mtime_ns,status,observed_at,scan_generation) VALUES($p,$i,$v,$s,$a,$l,$m,$st,$t,$g)
                    ON CONFLICT(path) DO UPDATE SET identity=$i,volume_id=$v,size=$s,allocated=$a,link_count=$l,mtime_ns=$m,status=$st,observed_at=$t,scan_generation=$g RETURNING file_key
                    """, tx).With("$p", file.Path).With("$i", o.Identity).With("$v", o.VolumeId).With("$s", o.Size).With("$a", o.AllocatedBytes).With("$l", o.LinkCount).With("$m", o.ModifiedUnixNanos).With("$st", (int)o.Status).With("$t", Db.Ms(nowUtc)).With("$g", generation).Scalar();
                c.Cmd("INSERT OR REPLACE INTO item_files(item_key,file_key,role,version_id) VALUES($k,$f,$r,$v)", tx).With("$k", key).With("$f", fileKey).With("$r", (int)file.Role).With("$v", Db.Id(file.VersionId)).Exec();
            }

            c.Cmd("DELETE FROM item_user_state WHERE item_key=$k", tx).With("$k", key).Exec();
            foreach (var user in users)
            {
                if (!userKeys.TryGetValue(user, out var userKey))
                {
                    continue;
                }

                UserItemState? state = page.FailedUsers.Contains(user) ? UserItemState.Unknown
                    : page.States.TryGetValue((item.Id, user), out var found) ? found : null;
                if (state is null || state.IsDefault)
                {
                    continue;
                }

                c.Cmd("INSERT INTO item_user_state(item_key,user_key,state,position_ticks,play_count,last_played,favorite) VALUES($k,$u,$s,$p,$c,$l,$f)", tx)
                    .With("$k", key).With("$u", userKey).With("$s", (int)state.State).With("$p", state.PositionTicks).With("$c", state.PlayCount).With("$l", Db.Ms(state.LastPlayedUtc)).With("$f", state.IsFavorite).Exec();
            }
        }
    });

    /// <summary>
    /// Sweep and roll up after a scan. Items are removed only from libraries that were fully read this generation:
    /// an offline root is never treated as proof that its files are gone.
    /// </summary>
    public void CompleteGeneration(long generation, IReadOnlyCollection<Guid> fullyScannedLibraries) => db.Write((c, tx) =>
    {
        foreach (var library in fullyScannedLibraries)
        {
            c.Cmd("DELETE FROM items WHERE library_id=$l AND scan_generation<$g", tx).With("$l", Db.Id(library)).With("$g", generation).Exec();
        }

        c.Cmd("DELETE FROM files WHERE file_key NOT IN (SELECT file_key FROM item_files)", tx).Exec();

        // A path referenced by more than one leaf cannot be freed by removing just one of them.
        c.Cmd("""
            UPDATE items SET reclaimable_bytes=0 WHERE kind IN (1,4) AND item_key IN (
                SELECT x.item_key FROM item_files x WHERE x.file_key IN (
                    SELECT file_key FROM item_files GROUP BY file_key HAVING COUNT(DISTINCT item_key) > 1))
            """, tx).Exec();

        RollUpContainers(c, tx, "season_id", 3);
        RollUpContainers(c, tx, "series_id", 2);
        RollUpLibraries(c, tx);
    });

    /// <summary>After deletion: drops the rows immediately so the UI is not misleading until the next scan.</summary>
    public void RemoveItems(IReadOnlyCollection<Guid> itemIds) => db.Write((c, tx) =>
    {
        foreach (var id in itemIds)
        {
            c.Cmd("DELETE FROM items WHERE item_id=$i", tx).With("$i", Db.Id(id)).Exec();
        }

        c.Cmd("DELETE FROM files WHERE file_key NOT IN (SELECT file_key FROM item_files)", tx).Exec();
        RollUpContainers(c, tx, "season_id", 3);
        RollUpContainers(c, tx, "series_id", 2);
        RollUpLibraries(c, tx);
    });

    private static void RollUpContainers(SqliteConnection c, SqliteTransaction tx, string column, int kind)
    {
        c.Cmd($"""
            WITH totals AS (
                SELECT {column} AS id, SUM(logical_bytes) AS logical, SUM(reclaimable_bytes) AS reclaim, SUM(file_count) AS files FROM items WHERE kind=4 AND {column} IS NOT NULL GROUP BY {column}),
            uniq AS (
                SELECT id, SUM(size) AS bytes, SUM(allocated) AS alloc FROM (
                    SELECT DISTINCT i.{column} AS id, COALESCE(f.identity, f.path) AS k, f.size AS size, f.allocated AS allocated
                    FROM items i JOIN item_files x ON x.item_key=i.item_key JOIN files f ON f.file_key=x.file_key
                    WHERE i.kind=4 AND i.{column} IS NOT NULL AND f.status=0) GROUP BY id)
            UPDATE items SET
                logical_bytes=COALESCE((SELECT logical FROM totals WHERE totals.id=items.item_id),0),
                reclaimable_bytes=COALESCE((SELECT reclaim FROM totals WHERE totals.id=items.item_id),0),
                file_count=COALESCE((SELECT files FROM totals WHERE totals.id=items.item_id),0),
                unique_bytes=COALESCE((SELECT bytes FROM uniq WHERE uniq.id=items.item_id),0),
                allocated_bytes=COALESCE((SELECT alloc FROM uniq WHERE uniq.id=items.item_id),0)
            WHERE kind={kind}
            """, tx).Exec();
    }

    private static void RollUpLibraries(SqliteConnection c, SqliteTransaction tx) => c.Cmd("""
        WITH totals AS (
            SELECT library_id AS id, COUNT(*) AS n, SUM(logical_bytes) AS logical, SUM(reclaimable_bytes) AS reclaim, SUM(file_count) AS files FROM items WHERE kind IN (1,4) GROUP BY library_id),
        uniq AS (
            SELECT id, SUM(size) AS bytes, SUM(allocated) AS alloc FROM (
                SELECT DISTINCT i.library_id AS id, COALESCE(f.identity, f.path) AS k, f.size AS size, f.allocated AS allocated
                FROM items i JOIN item_files x ON x.item_key=i.item_key JOIN files f ON f.file_key=x.file_key WHERE i.kind IN (1,4) AND f.status=0) GROUP BY id)
        UPDATE libraries SET
            item_count=COALESCE((SELECT n FROM totals WHERE totals.id=libraries.library_id),0),
            file_count=COALESCE((SELECT files FROM totals WHERE totals.id=libraries.library_id),0),
            logical_bytes=COALESCE((SELECT logical FROM totals WHERE totals.id=libraries.library_id),0),
            reclaimable_bytes=COALESCE((SELECT reclaim FROM totals WHERE totals.id=libraries.library_id),0),
            unique_bytes=COALESCE((SELECT bytes FROM uniq WHERE uniq.id=libraries.library_id),0),
            allocated_bytes=COALESCE((SELECT alloc FROM uniq WHERE uniq.id=libraries.library_id),0)
        """, tx).Exec();

    public void SaveVolumes(IReadOnlyList<VolumeRow> volumes) => db.Write((c, tx) =>
    {
        c.Cmd("DELETE FROM volumes", tx).Exec();
        foreach (var v in volumes)
        {
            c.Cmd("INSERT INTO volumes(volume_id,sample_path,total_bytes,free_bytes,observed_at,available,library_ids) VALUES($i,$p,$t,$f,$o,$a,$l)", tx)
                .With("$i", v.VolumeId).With("$p", v.SamplePath).With("$t", v.TotalBytes).With("$f", v.FreeBytes).With("$o", Db.Ms(v.ObservedUtc)).With("$a", v.Available).With("$l", Json.Write(v.LibraryIds)).Exec();
        }
    });

    public IReadOnlyList<VolumeRow> ListVolumes() => db.Read(c =>
        c.Cmd("SELECT volume_id,sample_path,total_bytes,free_bytes,observed_at,available,library_ids FROM volumes ORDER BY sample_path")
            .Rows(r => new VolumeRow(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetInt64(2), r.IsDBNull(3) ? null : r.GetInt64(3), Db.Date(r, 4), r.GetBoolean(5), Json.Read<List<Guid>>(r.GetString(6)))));

    public void WriteSnapshot(DateTime nowUtc) => db.Write((c, tx) =>
    {
        var at = Db.Ms(nowUtc);
        c.Cmd("INSERT OR REPLACE INTO library_snapshots SELECT $t,library_id,name,item_count,logical_bytes,unique_bytes,allocated_bytes FROM libraries WHERE online=1", tx).With("$t", at).Exec();
        c.Cmd("INSERT OR REPLACE INTO volume_snapshots SELECT $t,volume_id,sample_path,total_bytes,free_bytes FROM volumes WHERE available=1 AND total_bytes IS NOT NULL", tx).With("$t", at).Exec();
    });

    public (IReadOnlyList<SnapshotPoint> Libraries, IReadOnlyList<SnapshotPoint> Volumes) ReadSnapshots(DateTime sinceUtc) => db.Read(c =>
    {
        var since = Db.Ms(sinceUtc);
        var libs = c.Cmd("SELECT taken_at,library_id,library_name,logical_bytes,unique_bytes,allocated_bytes FROM library_snapshots WHERE taken_at>=$s ORDER BY taken_at").With("$s", since)
            .Rows(r => new SnapshotPoint(Db.Date(r, 0)!.Value, r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5), null, null));
        var vols = c.Cmd("SELECT taken_at,volume_id,sample_path,total_bytes,free_bytes FROM volume_snapshots WHERE taken_at>=$s ORDER BY taken_at").With("$s", since)
            .Rows(r => new SnapshotPoint(Db.Date(r, 0)!.Value, r.GetString(1), r.GetString(2), 0, 0, 0, r.GetInt64(4), r.GetInt64(3)));
        return ((IReadOnlyList<SnapshotPoint>)libs, (IReadOnlyList<SnapshotPoint>)vols);
    });

    public void PruneSnapshots(DateTime olderThanUtc) => db.Write((c, tx) =>
    {
        c.Cmd("DELETE FROM library_snapshots WHERE taken_at<$t", tx).With("$t", Db.Ms(olderThanUtc)).Exec();
        c.Cmd("DELETE FROM volume_snapshots WHERE taken_at<$t", tx).With("$t", Db.Ms(olderThanUtc)).Exec();
        c.Cmd("DELETE FROM scans WHERE started_at<$t AND status<>'Running'", tx).With("$t", Db.Ms(olderThanUtc)).Exec();
    });

    public EvaluationInput LoadEvaluationInput(DateTime nowUtc, ActivePlayback playback, IReadOnlySet<Guid> protectedIds, StringComparison pathComparison) => db.Read(c =>
    {
        var paths = new Dictionary<long, List<string>>();
        using (var cmd = c.Cmd("SELECT x.item_key,f.path FROM item_files x JOIN files f ON f.file_key=x.file_key"))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                var key = r.GetInt64(0);
                if (!paths.TryGetValue(key, out var list))
                {
                    paths[key] = list = new List<string>(1);
                }

                list.Add(r.GetString(1));
            }
        }

        var keyToId = new Dictionary<long, Guid>();
        var leaves = new List<LeafItem>();
        using (var cmd = c.Cmd("SELECT item_key,item_id,kind,name,library_id,series_id,season_id,season_no,episode_no,date_added,logical_bytes,reclaimable_bytes,health,tags FROM items"))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                var key = r.GetInt64(0);
                var id = Guid.Parse(r.GetString(1));
                keyToId[key] = id;
                var kind = (MediaKind)r.GetInt32(2);
                if (kind is MediaKind.Movie or MediaKind.Episode)
                {
                    leaves.Add(new LeafItem
                    {
                        Id = id,
                        Kind = kind,
                        Name = r.GetString(3),
                        LibraryId = Guid.Parse(r.GetString(4)),
                        SeriesId = Db.GuidOrNull(r, 5),
                        SeasonId = Db.GuidOrNull(r, 6),
                        SeasonNumber = Db.Int(r, 7),
                        EpisodeNumber = Db.Int(r, 8),
                        DateAddedUtc = Db.Date(r, 9),
                        LogicalBytes = r.GetInt64(10),
                        ReclaimableBytes = r.GetInt64(11),
                        Health = (ItemHealth)r.GetInt32(12),
                        Tags = DecodeTags(r.GetString(13)),
                        Paths = paths.TryGetValue(key, out var p) ? p : [],
                    });
                }
            }
        }

        var users = c.Cmd("SELECT user_key,user_id,deleted,readable_generation,all_libraries,libraries FROM users").Rows(r => (Key: r.GetInt64(0), Id: Guid.Parse(r.GetString(1)), Deleted: r.GetBoolean(2), Readable: r.GetInt64(3), All: r.GetBoolean(4), Libraries: Json.Read<List<Guid>>(r.GetString(5))));
        var keyToUser = users.ToDictionary(u => u.Key, u => u.Id);
        var states = new Dictionary<(Guid, Guid), UserItemState>();
        using (var cmd = c.Cmd("SELECT item_key,user_key,state,position_ticks,play_count,last_played,favorite FROM item_user_state"))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                if (keyToId.TryGetValue(r.GetInt64(0), out var item) && keyToUser.TryGetValue(r.GetInt64(1), out var user))
                {
                    states[(item, user)] = new UserItemState((WatchState)r.GetInt32(2), r.GetInt64(3), r.GetInt32(4), Db.Date(r, 5), r.GetBoolean(6));
                }
            }
        }

        var access = users.Where(u => !u.Deleted).ToDictionary(u => u.Id, u => (u.All, Set: u.Libraries.ToHashSet()));
        return new EvaluationInput
        {
            Leaves = leaves,
            NowUtc = nowUtc,
            KnownUsers = users.Where(u => !u.Deleted).Select(u => u.Id).ToList(),
            // Deleted or never-read users are not readable, so their state is Unknown and fails closed.
            ReadableUsers = users.Where(u => !u.Deleted && u.Readable > 0).Select(u => u.Id).ToHashSet(),
            States = states,
            ProtectedIds = protectedIds,
            Playback = playback,
            PathComparison = pathComparison,
            UserCanAccessLibrary = (user, library) => access.TryGetValue(user, out var a) && (a.All || a.Set.Contains(library)),
        };
    });

    /// <summary>Episode ordering facts for one show, used to rank keep-newest at execution time.</summary>
    public IReadOnlyList<LeafItem> LeavesOfSeries(Guid seriesId) => db.Read(c =>
        c.Cmd("SELECT item_id,name,library_id,season_id,season_no,episode_no,date_added,logical_bytes,health FROM items WHERE series_id=$s AND kind=4").With("$s", Db.Id(seriesId))
            .Rows(r => new LeafItem
            {
                Id = Guid.Parse(r.GetString(0)),
                Kind = MediaKind.Episode,
                Name = r.GetString(1),
                LibraryId = Guid.Parse(r.GetString(2)),
                SeriesId = seriesId,
                SeasonId = Db.GuidOrNull(r, 3),
                SeasonNumber = Db.Int(r, 4),
                EpisodeNumber = Db.Int(r, 5),
                DateAddedUtc = Db.Date(r, 6),
                LogicalBytes = r.GetInt64(7),
                Health = (ItemHealth)r.GetInt32(8),
            }));

    public IReadOnlyList<ItemFileRow> GetItemFiles(Guid itemId) => db.Read(c =>
        c.Cmd("""
            SELECT f.path,x.role,x.version_id,f.identity,f.volume_id,f.size,f.allocated,f.link_count,f.mtime_ns,f.status,f.observed_at,
                   (SELECT COUNT(DISTINCT o.item_key) FROM item_files o WHERE o.file_key=f.file_key AND o.item_key<>x.item_key)
            FROM item_files x JOIN files f ON f.file_key=x.file_key JOIN items i ON i.item_key=x.item_key
            WHERE i.item_id=$i ORDER BY x.role,f.path
            """).With("$i", Db.Id(itemId))
            .Rows(r => new ItemFileRow(r.GetString(0), (FileRole)r.GetInt32(1), Guid.Parse(r.GetString(2)), Db.Str(r, 3), Db.Str(r, 4), r.GetInt64(5), r.GetInt64(6), r.GetInt32(7), r.GetInt64(8), (ProbeStatus)r.GetInt32(9), Db.Date(r, 10)!.Value, r.GetInt32(11))));

    public IReadOnlyList<ItemStateRow> GetItemStates(Guid itemId) => db.Read(c =>
        c.Cmd("""
            SELECT u.user_id,u.name,CASE WHEN u.readable_generation=0 THEN 3 ELSE COALESCE(s.state,0) END,COALESCE(s.position_ticks,0),COALESCE(s.play_count,0),s.last_played,COALESCE(s.favorite,0)
            FROM users u LEFT JOIN item_user_state s ON s.user_key=u.user_key AND s.item_key=(SELECT item_key FROM items WHERE item_id=$i)
            WHERE u.deleted=0 ORDER BY u.name COLLATE NOCASE
            """).With("$i", Db.Id(itemId))
            .Rows(r => new ItemStateRow(Guid.Parse(r.GetString(0)), r.GetString(1), (WatchState)r.GetInt32(2), r.GetInt64(3), r.GetInt32(4), Db.Date(r, 5), r.GetBoolean(6))));

    /// <summary>Other indexed media paths in a directory, so a neighbor's sidecars are never claimed.</summary>
    public IReadOnlyList<string> MediaPathsInDirectory(string directory) => db.Read(c =>
    {
        var prefix = directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return c.Cmd("SELECT path FROM files WHERE path >= $a AND path < $b").With("$a", prefix).With("$b", prefix + char.MaxValue)
            .Rows(r => r.GetString(0)).Where(p => string.Equals(Path.GetDirectoryName(p), directory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal)).ToList();
    });

    /// <summary>The other leaf items referencing a path. A non-empty result means the file is shared.</summary>
    public IReadOnlyList<Guid> ItemsReferencing(string path) => db.Read(c =>
        c.Cmd("SELECT i.item_id FROM files f JOIN item_files x ON x.file_key=f.file_key JOIN items i ON i.item_key=x.item_key WHERE f.path=$p").With("$p", path).Rows(r => Guid.Parse(r.GetString(0))));

    public IReadOnlyList<WatchSummary> WatchSummaries(IReadOnlyList<Guid> users) => db.Read(c =>
    {
        if (users.Count == 0)
        {
            return (IReadOnlyList<WatchSummary>)[];
        }

        var rows = c.Cmd("SELECT user_id,user_key,readable_generation,deleted FROM users").Rows(r => (Id: Guid.Parse(r.GetString(0)), Key: r.GetInt64(1), Readable: r.GetInt64(2) > 0 && !r.GetBoolean(3)));
        var selected = rows.Where(r => users.Contains(r.Id)).ToList();
        var missing = users.Count - selected.Count(s => s.Readable);
        var keys = string.Join(',', selected.Select(s => s.Key));
        var u = users.Count;
        return c.Cmd($"""
            WITH t AS (
                SELECT i.library_id AS lib, i.logical_bytes AS bytes,
                    COALESCE(SUM(s.state=1),0) AS played, COALESCE(SUM(s.state=3),0)+{missing} AS unknown
                FROM items i LEFT JOIN item_user_state s ON s.item_key=i.item_key AND s.user_key IN ({keys})
                WHERE i.kind IN (1,4) AND i.health=0 GROUP BY i.item_key)
            SELECT lib, COUNT(*),
                COALESCE(SUM(CASE WHEN unknown=0 AND played={u} THEN bytes END),0), COALESCE(SUM(unknown=0 AND played={u}),0),
                COALESCE(SUM(CASE WHEN unknown=0 AND played>0 AND played<{u} THEN bytes END),0), COALESCE(SUM(unknown=0 AND played>0 AND played<{u}),0),
                COALESCE(SUM(CASE WHEN unknown=0 AND played=0 THEN bytes END),0), COALESCE(SUM(unknown=0 AND played=0),0),
                COALESCE(SUM(CASE WHEN unknown>0 THEN bytes END),0), COALESCE(SUM(unknown>0),0)
            FROM t GROUP BY lib
            """).Rows(r => new WatchSummary(Guid.Parse(r.GetString(0)), r.GetInt32(1), r.GetInt64(2), r.GetInt32(3), r.GetInt64(4), r.GetInt32(5), r.GetInt64(6), r.GetInt32(7), r.GetInt64(8), r.GetInt32(9)));
    });

    public static string Normalize(string text) => text.ToLowerInvariant();

    public static string EncodeTags(IReadOnlyList<string> tags) => tags.Count == 0 ? string.Empty : "|" + string.Join('|', tags.Select(t => t.ToLowerInvariant())) + "|";

    public static IReadOnlyList<string> DecodeTags(string encoded) => encoded.Length == 0 ? [] : encoded.Trim('|').Split('|');
}
