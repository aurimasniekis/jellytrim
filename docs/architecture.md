# Architecture

```
Browser  ── Authorization header ──▶  Jellyfin server
  jellytrim.js (Preact)                 │
                                        ▼
                     Jellyfin.Plugin.Jellytrim  (depends on Jellyfin)
                       Api/      admin controller, stable errors
                       Adapter/  JellyfinMediaHost : IMediaHost
                       Runtime/  DI, startup and shutdown, scheduled scan
                                        │
                                        ▼
                     Jellytrim.Core  (no Jellyfin references; a test enforces it)
                       Policies/    pure, deterministic evaluation
                       Storage/     accounting, budgets, recommendations
                       FileSystem/  identity probe, PathGuard, sidecars
                       Inventory/   scanner, root checks, volume probe
                       Cleanup/     planner, coordinator
                       Persistence/ SQLite stores, migrations
```

## Data separation

| Concern | Where | Authority |
|---|---|---|
| Jellyfin item identity | `items` | Cache of the server's view |
| Physical file identity | `files`: device:inode, size, blocks, links | Cache of the filesystem |
| Per-user state | `item_user_state`, sparse | Cache of the server's view |
| Plugin observations | `library_snapshots`, `volume_snapshots`, `scans` | jellytrim itself |
| Derived eligibility | `policy_eligible`, `policy_runs` | Recomputable at any time |
| Plans and outcomes | `plans`, `jobs`, `job_items`, `job_files`, `audit` | Durable record |

**The inventory is display data, never authority.** Execution re-reads the server and the filesystem.

## Scanning

A scan takes a snapshot of ids per library, because paging by id stays stable while the library changes. It reads batches of 500, stats files with concurrency 8, and commits each batch under a new generation. Only libraries read to completion are swept, so cancelling, crashing or an offline root leaves existing rows in place, and the next scan starts over safely. A page's stat calls are bounded by a 3-minute timeout, because `stat` on a hung mount cannot be cancelled.

Scans and cleanup jobs exclude each other through `MaintenanceGate`. The database also enforces a single active job with a unique partial index.

## Job execution

1. Validate synchronously: enabled, hash, not preview, not expired, not used, confirmation.
2. **Preflight:** revalidate every item. Any material change rejects the job with nothing deleted.
3. For each item: stop if cancelled, then revalidate again.
4. Commit `InProgress`; for each file, final probe, commit `Deleting`, unlink, commit the outcome.
5. Remove empty planned folders non-recursively, then remove the Jellyfin record.
6. After all items: complete seasons and shows, only if every episode in them succeeded.

State is written before each action, so a crash leaves evidence. On startup, jobs still active are finalized as `Interrupted`, what happened is established by probing, and nothing resumes.

## Persistence

SQLite at `<jellyfin data>/jellytrim/jellytrim.db`, WAL mode, `synchronous=FULL`, `BEGIN IMMEDIATE` for writes, `busy_timeout` 30 s. Migrations are forward-only and tracked in `PRAGMA user_version`. A database newer than the build disables the plugin with a clear error (verified on the real server).

Default retention: snapshots 730 days; audit and job history 1095 days; unexecuted plans 7 days after expiry. Diagnostic logs belong to Jellyfin's logging configuration and are separate.

## Logging

Through Jellyfin's `ILogger` (Serilog) with named properties such as `{JobId}`, `{CorrelationId}` and `{PlanId}`, so that a JSON sink can feed standard collectors, including OpenSearch, with no runtime dependency on one. Viewing data and tokens are not logged. Paths appear only as Jellyfin itself logs them, and in the audit trail.
