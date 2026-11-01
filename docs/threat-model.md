# Threat model

**Assets:** media files; per-user viewing data; the audit trail. **Trusted:** the Jellyfin server and its administrators, the OS. **Out of scope:** an attacker with shell or filesystem write access, who can already destroy the library.

| Threat | Mitigation | Residual risk |
|---|---|---|
| **Unauthorized deletion** by anonymous or ordinary users | `RequiresElevation` at class level; a test fails if an action opts out; e2e sweeps all 26 routes as anonymous and non-admin | A compromised admin account can delete, with an audit record |
| **API key misuse** | Jellyfin gives API keys the admin role. jellytrim refuses them for settings changes, plan execution, removing protections, and editing or deleting policies | Keys can read viewing data, create plans and policies, add protections and cancel jobs |
| **CSRF** | Authentication is an explicit header token rather than ambient cookies | Relies on Jellyfin's design |
| **Viewing data exposure** | All endpoints are admin-only; only non-default state is stored; UI assets hold no data; state is not logged | Admins can see who watched what, as they already can in Jellyfin. The database is readable by the server's OS user |
| **Malicious metadata** (XSS) | Preact escapes by default and `dangerouslySetInnerHTML` is unused. A hostile-named fixture runs through e2e | A future bug in a dependency |
| **SQL injection** | Values are bound parameters; sort is an enum; only integer keys and fixed fragments are interpolated | None known |
| **Path traversal, arbitrary path deletion** | No path input; `PathGuard` refuses traversal, non-normalized paths, roots and escapes | Paths in Jellyfin's own database are trusted as candidates, then checked |
| **Symlink or junction escape** | Every component below the root is checked; links are never followed or claimed | The check-then-unlink window. Roots may themselves be links, by design |
| **Stale plans** | 30-minute default expiry; hash; single use; preflight and per-item revalidation | The final window, see [deletion safety](deletion-safety.md#residual-risks) |
| **Filesystem races** | Identity, size and mtime re-checked immediately before unlink | As above |
| **Overbroad deletion APIs** | Jellyfin's file deletion is never used, enforced by a source-scanning test | Jellyfin's record removal could change in a later version, so the ABI is pinned to 12.4 |
| **Plan tampering in the database** | Stored hash compared with the document | An attacker who can write the database can rewrite both; that requires server-level access |
| **Compromised or misconfigured automation** | No scheduled deletion exists; API keys cannot execute; budgets cannot trigger deletion | Scans still run on schedule, read-only |
| **Double execution** | Idempotency key, one job per plan, one active job enforced by a unique index | None known |
| **Overwhelming the server** | Bounded pages, plan size limits, concurrency caps | Large plans take time, and scans are full reconciliations |
| **Secrets in logs or UI** | Tokens are read per request and not stored; internal errors return a correlation id only | Filesystem paths are visible to admins by design |
