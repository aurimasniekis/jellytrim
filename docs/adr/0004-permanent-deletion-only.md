# ADR-0004: Permanent deletion only

**Decision:** no quarantine in this version.

**Why:** a same-volume move frees nothing, which conflicts with the point of the product. A cross-volume move needs copy verification and space. Either way, Jellyfin rescans treat moved files as removed, so metadata and user state are lost, and restoring cannot bring those back. There is no tested restore path, and offering one untested would be false comfort.

**Consequence:** the UI and docs say permanent everywhere and offer no undo. Administrators need their own backups.

**Revisit if:** there is demand and a tested restore that reconciles Jellyfin state.
