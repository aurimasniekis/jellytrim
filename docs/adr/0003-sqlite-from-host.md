# ADR-0003: SQLite, provided by the host

**Decision:** one SQLite database, compiled against `Microsoft.Data.Sqlite` but resolved at runtime from Jellyfin's copy. The package ships only the plugin's own assemblies.

**Why:** plans and audit need crash-safe transactions, and the explorer needs indexes over 100k items, which XML config cannot give. Jellyfin's load context falls through to the host, whose copy is already initialized. Bundling `SQLitePCLRaw` would load a second, uninitialized copy.

**Rejected:** tables in Jellyfin's own database, because it is not a supported extension point.

**Cost:** tied to the host's assembly version, which is acceptable since the ABI is pinned.
