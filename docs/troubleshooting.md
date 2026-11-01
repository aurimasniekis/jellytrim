# Troubleshooting

Include the **Reference** shown with an error when searching logs; it is also the correlation id in Jellyfin's log.

| Symptom | Likely cause | What to do |
|---|---|---|
| Plugin shows *NotSupported* | The server is older than 12.4 | Upgrade the server |
| Plugin shows *Malfunctioned*, or errors mention SQLite | Extra DLLs in the plugin folder | Keep only the two DLLs and `meta.json` |
| 503 `DATABASE_UNAVAILABLE` | A database newer than this build, or unreadable | See [rollback](install.md#rollback-or-downgrade); check permissions on `data/jellytrim/` |
| A library shows *Offline* | Root unreachable, or **empty though it had files**, which usually means a missing mount | Fix the mount and scan. Items are kept meanwhile and nothing can be planned from it |
| Everything is ineligible with reason *Unknown* | A selected user was deleted, has no access to the library, or their state could not be read | Fix the policy's users. Check the server log for read failures |
| Item says *Files missing* | Removed outside jellytrim while the root was online | Run a Jellyfin library scan |
| Items excluded: `PATH_OutsideApprovedRoots` | The root is not approved | Settings → approve it |
| Items excluded: `PATH_SymlinkInPath` or `PATH_TargetIsLink` | Links in the library | By design. Remove those files by other means |
| Items excluded: `IDENTITY_UNKNOWN` | `statx` missing or failing on this platform or filesystem | See [compatibility](compatibility.md) |
| Items excluded: `SHARED_FILE` | Another record uses the file | Include it too, or fix the duplicate |
| Plan shows 0 reclaimable | Hard links elsewhere, such as a torrent client's copy | Remove the other link too; jellytrim will not touch it |
| Job **Rejected** | Something changed since planning: files, a user's state, a protection | Normal safety behavior. Generate a new plan |
| `SELECTION_CHANGED` | "All matching" results changed | Review and select again |
| `SCAN_RUNNING` or `CLEANUP_RUNNING` | They exclude each other | Wait for it to finish |
| Item deleted but still in Jellyfin (`PendingNextLibraryScan`) | The record could not be removed | Run a library scan |
| Freed less than estimated | Snapshots, deduplication, compression, or other activity on the volume | Check your filesystem's snapshots |
| Free space looks wrong elsewhere in Jellyfin | Jellyfin's own views may use `DriveInfo`, which can misreport | jellytrim uses `statvfs` on Linux |
| Protection disappeared | The item was re-created with a new id, for example after moving it | Protect it again |
