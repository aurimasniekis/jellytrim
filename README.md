# jellytrim

**Keep what matters. Reclaim the rest.**

A Jellyfin plugin for keeping library storage under control. It shows where disk space goes, who has watched what, and what could safely go, then reclaims that space through a preview-first, audited workflow.

jellytrim is an independent project, not affiliated with or endorsed by the Jellyfin project.

> [!WARNING]
> Deletion is permanent, and this is a 0.1 release that has only been run against disposable test data, never a real library. See [Status](#status). Keep backups and start in read-only mode, which is the default.

## What it answers

1. **What is taking up space?** Size by library, largest movies, shows, seasons, episodes and files, trends from its own snapshots, budgets and free-space goals.
2. **What have we already watched?** Per-user played, in-progress, unplayed and unknown state, separating "watched by someone" from "watched by everyone selected".
3. **What can safely go?** Composable policies that explain every decision. Protections always win, and anything unknown keeps the item.
4. **How much space will that recover?** Physical accounting that understands hard links, shared files and sparse files, and labels estimates as estimates.

## How deletion works

Read-only until you approve library roots and type a confirmation. After that, every deletion is: select → immutable dry-run plan listing every file → review → confirm that exact plan → re-check every item → per-item report and audit record.

jellytrim does not use Jellyfin's file deletion, because for a movie in its own folder that recursively deletes the whole folder. It removes exactly the planned files, then updates Jellyfin's records. There is no scheduled deletion, no quarantine and no undo. See [deletion safety](docs/deletion-safety.md).

## Install

Requires **Jellyfin 12.4**. In the dashboard, go to **Plugins → Repositories**, add the manifest URL from the [release page](https://github.com/aurimasniekis/jellytrim/releases), install **jellytrim**, and restart. See [install, upgrade, rollback and uninstall](docs/install.md).

## Status

jellytrim has **not** met its own release criteria. Nothing is published, and the plugin has only run on disposable fixtures.

| Capability | Implemented | Verified by |
|---|---|---|
| Plugin load, API authorization, UI in dashboard | Yes | Real 12.4 server, Linux arm64 container |
| Manifest install, upgrade, uninstall, reinstall | Yes | Real server, `e2e/install_check.py` |
| Inventory, accounting, snapshots, budgets, goals | Yes | Unit, integration and real server |
| Per-user state, policies, recommendations | Yes | Unit, integration and real server |
| Plans, protections, execution, audit, reconciliation | Yes | Unit, integration and real deletion of generated fixtures |
| Offline roots, restart, newer-database refusal | Yes | Real server |
| Crash in the middle of an item | Yes | Reconstructed state only; a real kill is not guaranteed to land mid-item |
| 100,000-item performance | Yes | Real SQLite and scanner with **stubbed stat and fake server** ([results](docs/testing.md)) |
| Linux x64 | Yes | **Unverified** beyond CI being configured |
| Windows, including junctions | Written | **Unverified**: never run |
| Native macOS server | Written | Probe tests only; server unverified |
| NFS, SMB, mergerfs, ZFS, Btrfs | Handled generically | **Unverified** |
| A real library, real playback during cleanup | n/a | **Unverified**: playback blocking tested by item state and fakes, not a live stream |

Not implemented, deliberately: quarantine ([ADR-0004](docs/adr/0004-permanent-deletion-only.md)), scheduled deletion ([ADR-0005](docs/adr/0005-no-scheduled-deletion.md)), incremental indexing (every scan is a full reconciliation), and music, books or other media types.

## Documentation

- [Administrator guide](docs/admin-guide.md) · [Policy semantics](docs/policy-semantics.md) · [Troubleshooting](docs/troubleshooting.md)
- [Install, upgrade, rollback, uninstall](docs/install.md) · [Compatibility](docs/compatibility.md)
- [Deletion safety](docs/deletion-safety.md) · [Threat model](docs/threat-model.md)
- [Architecture](docs/architecture.md) · [Verified integration](docs/verified-integration.md) · [API](docs/api.md) · [Testing](docs/testing.md) · [Decision records](docs/adr)

## Build

Needs .NET SDK 10.0.401 or later in the 10.0 band, Node 22 or later, and Python 3. The end-to-end suite also needs Docker, ffmpeg and Chrome.

```sh
make test      # builds everything; runs .NET and UI tests
make package   # dist/jellytrim_<version>.zip, manifest.json, SHA256SUMS
make e2e       # disposable real Jellyfin plus generated media; deletes only those fixtures
python3 e2e/install_check.py  # manifest install, upgrade, uninstall lifecycle
make e2e-down
make audit
```

## License

[GPL-3.0-only](LICENSE), as required by linking against Jellyfin's GPL libraries. See [third-party notices](THIRD_PARTY_NOTICES.md).
