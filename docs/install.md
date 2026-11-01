# Install, upgrade, rollback, uninstall

Requires Jellyfin 12.4. The lifecycle below was run on a real 12.4 server by `e2e/install_check.py`, against a local manifest. No public release exists yet.

## Install from a repository

1. *Dashboard → Plugins → Repositories → +*, and enter the manifest URL.
2. *Plugins → Catalog → jellytrim → Install*. Jellyfin verifies the zip's MD5 from the manifest.
3. Restart the server.

## Install manually

Unzip `jellytrim_<version>.zip` into `<config>/plugins/jellytrim_<version>/` and restart. It holds `Jellyfin.Plugin.Jellytrim.dll`, `Jellytrim.Core.dll` and `meta.json`. Check it against `SHA256SUMS`. Do not add other DLLs: a second SQLite would fail to initialize.

## Where data lives

`<jellyfin data>/jellytrim/jellytrim.db`, plus `-wal` and `-shm`, for example `/config/data/jellytrim/` in the official container. Hold onto it through upgrades: it is outside the versioned plugin folder.

**Backup:** stop the server and copy the folder, or use `sqlite3 jellytrim.db ".backup out.db"` while it runs. Media is not in it, so a backup cannot restore deleted files.

## Upgrade

Install the new version and restart. Migrations run once at startup and move forward only. Back up first if the release notes mention a schema change. A plan from the old version remains subject to all its checks, and expires on its normal schedule.

A new Jellyfin version needs a jellytrim build for it: the ABI is pinned on purpose because deletion relies on verified server behavior.

## Rollback or downgrade

| Database schema | What happens |
|---|---|
| Same | The older version works normally |
| Newer than the build | jellytrim **disables itself**: the API answers 503 `DATABASE_UNAVAILABLE`, nothing can delete, and the UI says why. Nothing is changed |

To roll back across a schema change, restore the backup taken before upgrading, or delete the database to start over. Deleting it loses settings, policies, protections, snapshots and history, but re-enables read-only mode by default.

## Uninstall

*Dashboard → Plugins → jellytrim → Uninstall*, then restart.

| Thing | After uninstalling |
|---|---|
| Plugin files and API | Removed |
| Settings, policies, protections | **Kept** in the database |
| Inventory, snapshots | **Kept** |
| Audit and job history | **Kept** |
| Quarantined files | None exist. jellytrim never quarantines |
| Media and Jellyfin's records | Untouched |

To remove everything, delete `<jellyfin data>/jellytrim/` after uninstalling. Reinstalling without doing so picks up the old data, including whether deletion was enabled.
