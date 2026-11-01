# ADR-0001: Keep domain logic free of Jellyfin

**Decision:** `Jellytrim.Core` has no Jellyfin references. Only `Jellyfin.Plugin.Jellytrim` does, through `IMediaHost`.

**Why:** safety logic has to be testable with real files and a real database, without a server. A test enforces the boundary.

**Cost:** one more assembly to ship, and a mapping layer.
