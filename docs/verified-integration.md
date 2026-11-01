# Verified integration: Jellyfin 12.4

Everything here was read from the `v12.4` tag of [jellyfin/jellyfin](https://github.com/jellyfin/jellyfin) (commit `83d0d0e7f76e`) and [jellyfin-web](https://github.com/jellyfin/jellyfin-web), then exercised against the `jellyfin/jellyfin:12.4.20261026-211555` image. The official plugin template still targets 10.11, so it was not treated as authoritative.

| Surface | What was verified | Source |
|---|---|---|
| Runtime | `net10.0`; NuGet `Jellyfin.Controller` and `Jellyfin.Model` 12.4.0 | `MediaBrowser.Controller.csproj` |
| Plugin loading | One `AssemblyLoadContext` per plugin; unresolved assemblies fall through to the host. A plugin compatible if server version ≥ `targetAbi` | `PluginManager.cs`, `PluginLoadContext.cs` |
| Version display | The reported version comes from the assembly, not `meta.json` | Observed on the server |
| SQLite | Host ships `Microsoft.Data.Sqlite` 10.0.14 and a native library; bundling a second `SQLitePCLRaw` would give an uninitialized copy | `Directory.Packages.props`; server reports `3.53.3` |
| Controllers | Plugin assemblies become MVC application parts | `ApiServiceCollectionExtensions.cs:160` |
| Authorization | `Policies.RequiresElevation` means role Administrator. **API keys also get that role** | `CustomAuthenticationHandler.cs` |
| CSRF | Authentication comes from an explicit header token, not ambient cookies | `CustomAuthenticationHandler.cs` |
| Web UI | `IHasWebPages`; a `data-role="page"` element with `data-controller="__plugin/x.js"` is imported as an ES module; `window.ApiClient` is available; `EnableInMainMenu` adds a dashboard drawer link | `viewContainer.js`, `PluginDrawerSection.tsx`, `routes.tsx` |
| Plugin page assets | `GET /web/ConfigurationPage` needs no authentication, so assets must hold no data | `DashboardController.cs` |
| User data | `GetUserDataBatch` batches by user. Fields: `Played`, `PlaybackPositionTicks`, `PlayCount`, `LastPlayedDate`, `IsFavorite` | `IUserDataManager.cs`, `UserItemData.cs` |
| Played semantics | A flag. Nothing records whether playback completed or a user set it manually | `UserItemData.cs` |
| Rewatching | `Played=true` and a resume position can coexist | Observed on the server |
| Versions | `GetLocalAlternateVersionIds` and `GetLinkedAlternateVersions`; alternates carry `PrimaryVersionId` | `ILibraryManager.cs`, `Video.cs` |
| Sessions | `ISessionManager.Sessions` with `NowPlayingItem` and `FullNowPlayingItem` | `SessionInfo.cs` |
| Tasks | `IScheduledTask` is discovered automatically | `ScheduledTasks`; shows in the dashboard |
| Manifest | `PackageInfo` and `VersionInfo` shape; **MD5** `checksum` enforced on install | `InstallationManager.cs:586` |

## Deletion scope of Jellyfin's APIs

`ILibraryManager.DeleteItem(item, options)` with `DeleteFileLocation = true` deletes `item.GetDeletePaths()`:

- **Movie not in a mixed folder:** `Video.GetDeletePaths` returns `ContainingFolderPath`, and `DeleteItemPath` runs `Directory.Delete(path, true)`. **That removes everything in the folder**, including extras and anything unrelated.
- **Episode or mixed-folder video:** the path, plus any file with a supported extension whose name *starts with* the video's stem. `Foo.mkv` also claims `Foo 2.srt`.
- Failures on all but the first path are silently ignored.

A plan cannot guarantee that scope, so jellytrim never uses it. See [ADR-0002](adr/0002-exact-file-deletion.md).

`DeleteItem` with `DeleteFileLocation = false` removes only records and metadata. On a primary video whose alternates' files are gone it also removes those alternates' records, which is what jellytrim relies on after deleting all versions.

## Discrepancies found by running it

- `.NET DriveInfo` reported **254 TB** for a 994 GB volume on virtiofs: it multiplies by `f_bsize`, while POSIX defines the counts in units of `f_frsize`. jellytrim calls `statvfs` on 64-bit Linux.
- Jellyfin's OpenAPI generator uses one flat schema namespace across controllers. A plugin DTO named `UserDto` made `/api-docs/openapi.json` return 500 for the whole server, so jellytrim's controller opts out of API exploration.
- A singleton that is both stopped by `StopAsync` and disposed by the DI container is disposed twice at shutdown. An exception there is unhandled and ends the server process, so disposal must be idempotent.
- Jellyfin's startup page answers 503 with HTML, so 503 alone does not mean jellytrim refused to start.
