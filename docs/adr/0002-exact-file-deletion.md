# ADR-0002: Unlink exact files instead of using Jellyfin's deletion

**Context:** `ILibraryManager.DeleteItem` with `DeleteFileLocation=true` recursively deletes a movie's whole folder, claims sidecars by bare prefix, and ignores some failures.

**Decision:** jellytrim unlinks exactly the planned files, removes folders only when empty, and calls `DeleteItem` with `DeleteFileLocation=false` for records.

**Why:** previewed scope and executed scope must agree. Jellyfin's scope cannot be previewed reliably and can destroy files the plan never mentioned.

**Cost:** jellytrim owns attribution and leaves things like extras on disk. If record removal fails, a file can be gone while the record stays, which is reported and resolved by a library scan.
