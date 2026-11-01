# ADR-0006: Preact in a plugin page

**Decision:** Preact and TypeScript, bundled by esbuild into one ES module, served through `IHasWebPages` with `data-controller`.

**Why:** this is Jellyfin Web's supported extension point and needs no patched client. React cannot be shared with the host, and a 100 KB self-contained bundle is fine. Preact escapes by default, which matters with hostile metadata. A separate frontend would mean separate deployment and auth.

**Cost:** styles are scoped by hand, the host page's lifecycle has to be respected, and in-app navigation is not linkable by URL.
