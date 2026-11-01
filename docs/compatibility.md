# Compatibility

| jellytrim | Jellyfin | .NET | Status |
|---|---|---|---|
| 0.1.0.0 | 12.4.x (`targetAbi` 12.4.0.0) | 10.0 | Verified on `12.4.20261026-211555` |
| 0.1.0.0 | 12.5 or newer | | **Unverified.** Jellyfin only checks that the server is at least `targetAbi`, so it may load. Do not enable deletion without re-verifying |
| 0.1.0.0 | Older than 12.4 | | Not loaded |

| Platform | Status |
|---|---|
| Official container, Linux arm64 | **Verified** on a real server |
| Official container, Linux x64 | Same code path and `statx` layout; **not run** |
| Linux glibc 2.28 or later, musl 1.2.5 or later | Needs `statx`. Otherwise identity is unavailable, and nothing is eligible for deletion |
| Windows | Identity and junction handling written, **never run**. Allocation is reported as logical size |
| macOS | Identity probe covered by unit tests on arm64. Running a server on it is **unverified** |

| Storage | Status |
|---|---|
| Local filesystems, Docker bind mounts | Verified on virtiofs |
| NFS, SMB, mergerfs, ZFS, Btrfs, others | **Unverified.** Identity may be weak on some network filesystems, in which case a plan may refuse or be rejected. mergerfs and Btrfs subvolumes may report several volumes. Deduplication and snapshots are not modeled |

| Media | Status |
|---|---|
| Movies, episodes as plain video files | Supported |
| Multiple versions, stacked parts | Deleted together |
| Series, seasons | Through their episodes |
| DVD and Blu-ray folders, `.strm`, remote streams, virtual items | Listed, never deleted |
| Music, books, photos, home video, Live TV recordings | Not indexed |
| A location shared by two libraries | An item is attributed to whichever library was scanned last |
