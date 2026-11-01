# Deletion safety

## Guarantees and where they are enforced

| Guarantee | Enforcement | Tests |
|---|---|---|
| Read-only by default | `JellytrimSettings` defaults; enabling needs an exact phrase and an approved root | Core, e2e |
| No client-supplied paths | Requests carry ids and filters. Paths come from the server record | `ContractTests` |
| Inside approved roots only | `PathGuard`; approved roots must be current library locations, re-checked at execution | Core, e2e |
| No following links | Each component below the root is checked for reparse points; link targets are refused | Core, e2e |
| No recursive delete | `Directory.Delete(path, recursive: false)` only; a source scan fails the build's tests if that changes | `ContractTests` |
| Only planned files | Sidecars need positive attribution; everything else is reported as staying | Core, e2e |
| Plan equals execution | Files come from the stored plan; its SHA-256 is checked; `File.Delete` is called from one place | Core, e2e |
| Stale plans rejected | Expiry, hash, policy revision, file identity, size and mtime, server record | Core, e2e |
| Protections win | Live re-evaluation at preflight and again per item | Core, e2e |
| Crash never resumes | Startup finalizes as `Interrupted` | Core; real kill, see below |

## Why not Jellyfin's deletion

See [verified integration](verified-integration.md#deletion-scope-of-jellyfins-apis). jellytrim unlinks exact files, then calls `DeleteItem` with `DeleteFileLocation = false` to reconcile.

## Revalidation

Preflight covers the whole plan first, so a stale plan deletes nothing. Each item is then checked again immediately before deletion, and a change at that point skips that item and is reported. Each file gets a final probe right before unlink.

Approved roots, library locations, the policy revision and the enabled switch are re-read for every item during a job, so changes take effect immediately.

Checked: deletion still enabled; policy revision unchanged; the server still has the item with the same media files; root available and approved; no links in the path; file identity (device:inode), size and mtime unchanged; rules still pass using live user state, favorites, protections and playback.

## Physical accounting

| Situation | Behavior |
|---|---|
| Hard link outside the plan | This path is unlinked, 0 bytes claimed, warning shown |
| All links in the plan | Allocated blocks counted once |
| Same path in two records, same files | The lower id owns deletion, both records are re-checked like any item, then both are removed |
| Same path, different file sets | Refused: `SHARED_FILE` |
| Sparse file | Estimated from allocated blocks, not size |
| Compression, dedup, snapshots | Not modeled. Freed space may be less. On Windows allocation is reported as size. |
| Filesystem snapshots | Space stays held until the snapshot is removed |

Free space before and after is observed for the whole volume, so other activity affects the difference. The report says so.

## Sidecars

A file belongs to a video only if its extension is on an allowlist and its name is exactly `stem.ext`, `stem.something.ext` or `stem-something.ext`, and no other video in that folder, indexed or not, has an equal or longer matching stem. Folder artwork such as `poster.jpg` is claimed only for a movie alone in its folder, with no other videos in subfolders apart from named extras folders. Show and season artwork is claimed only when every episode below is in the plan and the server reports nothing left below at execution time, and is checked before anything is removed. Only `<stem>.trickplay/` subfolders are claimed. `extras/`, `trailers/` and everything else stay, and are listed in the plan as staying.

**Limits:** extras stay on disk even when their movie goes, and Jellyfin may keep their records until its next scan. A folder that still holds anything stays.

## Item types never deleted

Virtual and missing items, remote streams, `.strm` shortcuts, and DVD or Blu-ray folder rips. These have no plain file to reason about, so they report as having no local file.

## Residual risks

- **The last check and the unlink are not atomic.** POSIX offers no compare-and-unlink. A file replaced in that window of microseconds would be deleted. Directory swaps in the same window are possible in principle too. An attacker with write access to the library can already destroy it.
- **Playback cannot be locked.** Playback that starts just after the check is not stopped. On Linux, an open file keeps playing until it is closed; on Windows the delete usually fails and is reported.
- **Playback outside Jellyfin** is invisible. So are transcodes not attached to a session.
- **Hung mounts:** a `stat` or `unlink` on a hung network mount cannot be cancelled and may delay a job, or shutdown past its 30-second wait. Recovery on the next start finalizes the job.
- **Library monitor:** Jellyfin's realtime monitor may react to deletions by refreshing the folder. That is harmless but adds activity.
- **Case-insensitive volumes on Linux, or case-sensitive ones on macOS,** are compared by the platform default.
- **Unicode:** paths are compared exactly, without normalization. A mismatch refuses rather than matches.
- **A real kill landing mid-item** is tested only through a reconstructed state, because a short test job usually finishes first.

## Quarantine

Not implemented. See [ADR-0004](adr/0004-permanent-deletion-only.md). All deletion is permanent, and jellytrim offers no undo. **Uninstalling leaves no quarantined files because there are none.**
