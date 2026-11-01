namespace Jellytrim.Core.Persistence;

/// <summary>Forward-only, append-only migrations. Never edit a released entry; add a new one.</summary>
public static class Migrations
{
    public static readonly (int Version, string Sql)[] All =
    [
        (1, """
        -- Key/value documents with an optimistic revision.
        CREATE TABLE settings (key TEXT PRIMARY KEY, revision INTEGER NOT NULL, value TEXT NOT NULL, updated_at INTEGER NOT NULL);

        -- Monotonic counters used to detect staleness of derived data.
        CREATE TABLE counters (key TEXT PRIMARY KEY, value INTEGER NOT NULL);
        INSERT INTO counters VALUES ('scan_generation', 0), ('protection_revision', 0);

        CREATE TABLE libraries (
            library_id TEXT PRIMARY KEY, name TEXT NOT NULL, collection_type TEXT, locations TEXT NOT NULL,
            online INTEGER NOT NULL, offline_detail TEXT, last_scan_at INTEGER, scan_generation INTEGER NOT NULL,
            item_count INTEGER NOT NULL DEFAULT 0, file_count INTEGER NOT NULL DEFAULT 0,
            logical_bytes INTEGER NOT NULL DEFAULT 0, unique_bytes INTEGER NOT NULL DEFAULT 0,
            allocated_bytes INTEGER NOT NULL DEFAULT 0, reclaimable_bytes INTEGER NOT NULL DEFAULT 0);

        -- Users: only id and display name. user_key keeps per-user rows small.
        -- readable_generation is the last scan that successfully read this user's state.
        CREATE TABLE users (
            user_key INTEGER PRIMARY KEY, user_id TEXT NOT NULL UNIQUE, name TEXT NOT NULL, is_admin INTEGER NOT NULL,
            is_disabled INTEGER NOT NULL, deleted INTEGER NOT NULL DEFAULT 0, all_libraries INTEGER NOT NULL,
            libraries TEXT NOT NULL, readable_generation INTEGER NOT NULL DEFAULT 0);

        CREATE TABLE items (
            item_key INTEGER PRIMARY KEY, item_id TEXT NOT NULL UNIQUE, kind INTEGER NOT NULL, library_id TEXT NOT NULL,
            series_id TEXT, season_id TEXT, series_name TEXT, name TEXT NOT NULL, sort_name TEXT NOT NULL, search_name TEXT NOT NULL,
            season_no INTEGER, episode_no INTEGER, date_added INTEGER, premiere INTEGER, runtime_ticks INTEGER, tags TEXT NOT NULL,
            path TEXT, health INTEGER NOT NULL, logical_bytes INTEGER NOT NULL DEFAULT 0, unique_bytes INTEGER NOT NULL DEFAULT 0,
            allocated_bytes INTEGER NOT NULL DEFAULT 0, reclaimable_bytes INTEGER NOT NULL DEFAULT 0, file_count INTEGER NOT NULL DEFAULT 0,
            version_count INTEGER NOT NULL DEFAULT 1, scan_generation INTEGER NOT NULL);
        CREATE INDEX ix_items_library_kind ON items(library_id, kind, logical_bytes DESC);
        CREATE INDEX ix_items_kind_size ON items(kind, logical_bytes DESC);
        CREATE INDEX ix_items_series ON items(series_id, season_no, episode_no);
        CREATE INDEX ix_items_season ON items(season_id, episode_no);
        CREATE INDEX ix_items_added ON items(date_added);
        CREATE INDEX ix_items_sort ON items(sort_name);
        CREATE INDEX ix_items_generation ON items(scan_generation);

        -- Physical file observations; identity is device:inode where available.
        CREATE TABLE files (
            file_key INTEGER PRIMARY KEY, path TEXT NOT NULL UNIQUE, identity TEXT, volume_id TEXT, size INTEGER NOT NULL,
            allocated INTEGER NOT NULL, link_count INTEGER NOT NULL, mtime_ns INTEGER NOT NULL, status INTEGER NOT NULL,
            observed_at INTEGER NOT NULL, scan_generation INTEGER NOT NULL);
        CREATE INDEX ix_files_identity ON files(identity);
        CREATE TABLE item_files (
            item_key INTEGER NOT NULL REFERENCES items(item_key) ON DELETE CASCADE,
            file_key INTEGER NOT NULL REFERENCES files(file_key) ON DELETE CASCADE,
            role INTEGER NOT NULL, version_id TEXT NOT NULL, PRIMARY KEY (item_key, file_key)) WITHOUT ROWID;
        CREATE INDEX ix_item_files_file ON item_files(file_key);

        -- Sparse: a row exists only for non-default state. Viewing data is sensitive, so nothing beyond this is stored.
        CREATE TABLE item_user_state (
            item_key INTEGER NOT NULL REFERENCES items(item_key) ON DELETE CASCADE,
            user_key INTEGER NOT NULL REFERENCES users(user_key) ON DELETE CASCADE,
            state INTEGER NOT NULL, position_ticks INTEGER NOT NULL, play_count INTEGER NOT NULL,
            last_played INTEGER, favorite INTEGER NOT NULL, PRIMARY KEY (item_key, user_key)) WITHOUT ROWID;
        CREATE INDEX ix_user_state_user ON item_user_state(user_key, state);

        CREATE TABLE protections (
            target_id TEXT PRIMARY KEY, target_type INTEGER NOT NULL, name TEXT NOT NULL, note TEXT,
            created_at INTEGER NOT NULL, created_by TEXT);

        CREATE TABLE policies (
            policy_id TEXT PRIMARY KEY, revision INTEGER NOT NULL, name TEXT NOT NULL, document TEXT NOT NULL,
            created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL);
        -- Sparse: only eligible items.
        CREATE TABLE policy_eligible (
            policy_id TEXT NOT NULL REFERENCES policies(policy_id) ON DELETE CASCADE,
            item_key INTEGER NOT NULL REFERENCES items(item_key) ON DELETE CASCADE,
            PRIMARY KEY (policy_id, item_key)) WITHOUT ROWID;
        CREATE TABLE policy_runs (
            policy_id TEXT PRIMARY KEY REFERENCES policies(policy_id) ON DELETE CASCADE,
            revision INTEGER NOT NULL, scan_generation INTEGER NOT NULL, protection_revision INTEGER NOT NULL,
            evaluated_at INTEGER NOT NULL, eligible_count INTEGER NOT NULL, considered_count INTEGER NOT NULL,
            eligible_logical INTEGER NOT NULL, eligible_reclaimable INTEGER NOT NULL, blocked TEXT NOT NULL);

        CREATE TABLE volumes (
            volume_id TEXT PRIMARY KEY, sample_path TEXT NOT NULL, total_bytes INTEGER, free_bytes INTEGER,
            observed_at INTEGER, available INTEGER NOT NULL, library_ids TEXT NOT NULL);

        -- Collected only by this plugin. There is deliberately no backfill before installation.
        CREATE TABLE library_snapshots (
            taken_at INTEGER NOT NULL, library_id TEXT NOT NULL, library_name TEXT NOT NULL, item_count INTEGER NOT NULL,
            logical_bytes INTEGER NOT NULL, unique_bytes INTEGER NOT NULL, allocated_bytes INTEGER NOT NULL,
            PRIMARY KEY (taken_at, library_id)) WITHOUT ROWID;
        CREATE TABLE volume_snapshots (
            taken_at INTEGER NOT NULL, volume_id TEXT NOT NULL, sample_path TEXT NOT NULL, total_bytes INTEGER NOT NULL, free_bytes INTEGER NOT NULL,
            PRIMARY KEY (taken_at, volume_id)) WITHOUT ROWID;

        CREATE TABLE scans (
            scan_id INTEGER PRIMARY KEY, generation INTEGER NOT NULL, trigger TEXT NOT NULL, status TEXT NOT NULL,
            started_at INTEGER NOT NULL, finished_at INTEGER, items_seen INTEGER NOT NULL DEFAULT 0, files_seen INTEGER NOT NULL DEFAULT 0,
            message TEXT, warnings TEXT NOT NULL DEFAULT '[]');

        -- document is the immutable plan JSON and hash its SHA-256; status moves but the document never does.
        CREATE TABLE plans (
            plan_id TEXT PRIMARY KEY, created_at INTEGER NOT NULL, expires_at INTEGER NOT NULL, created_by TEXT,
            status TEXT NOT NULL, hash TEXT NOT NULL, item_count INTEGER NOT NULL, file_count INTEGER NOT NULL,
            reclaim_bytes INTEGER NOT NULL, document TEXT NOT NULL);
        CREATE INDEX ix_plans_created ON plans(created_at);

        CREATE TABLE jobs (
            job_id TEXT PRIMARY KEY, plan_id TEXT NOT NULL UNIQUE, idempotency_key TEXT NOT NULL UNIQUE, correlation_id TEXT NOT NULL,
            status TEXT NOT NULL, created_by TEXT, created_at INTEGER NOT NULL, started_at INTEGER, finished_at INTEGER,
            cancel_requested INTEGER NOT NULL DEFAULT 0, total_items INTEGER NOT NULL, completed_items INTEGER NOT NULL DEFAULT 0,
            deleted_bytes INTEGER NOT NULL DEFAULT 0, reclaim_estimate INTEGER NOT NULL, message TEXT,
            free_before TEXT, free_after TEXT, problems TEXT);
        -- At most one job may be active, enforced by the database rather than by application locks alone.
        CREATE UNIQUE INDEX ux_jobs_single_active ON jobs((1)) WHERE status IN ('Queued', 'Preflight', 'Running');
        CREATE INDEX ix_jobs_created ON jobs(created_at);

        CREATE TABLE job_items (
            job_id TEXT NOT NULL REFERENCES jobs(job_id) ON DELETE CASCADE, seq INTEGER NOT NULL, item_id TEXT NOT NULL,
            name TEXT NOT NULL, status TEXT NOT NULL, code TEXT, message TEXT, started_at INTEGER, finished_at INTEGER,
            freed_bytes INTEGER NOT NULL DEFAULT 0, reconcile TEXT, PRIMARY KEY (job_id, seq)) WITHOUT ROWID;
        CREATE TABLE job_files (
            job_id TEXT NOT NULL REFERENCES jobs(job_id) ON DELETE CASCADE, seq INTEGER NOT NULL, file_seq INTEGER NOT NULL,
            path TEXT NOT NULL, role INTEGER NOT NULL, status TEXT NOT NULL, code TEXT, bytes INTEGER NOT NULL,
            at INTEGER, PRIMARY KEY (job_id, seq, file_seq)) WITHOUT ROWID;

        CREATE TABLE audit (
            audit_id INTEGER PRIMARY KEY, at INTEGER NOT NULL, actor TEXT, action TEXT NOT NULL, subject TEXT,
            correlation_id TEXT, outcome TEXT NOT NULL, details TEXT);
        CREATE INDEX ix_audit_at ON audit(at);
        """),
    ];

    public static int Latest => All[^1].Version;
}
