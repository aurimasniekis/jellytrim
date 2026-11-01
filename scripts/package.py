#!/usr/bin/env python3
"""Builds the release zip and repository manifest from a Release build.

Only the two plugin assemblies and meta.json ship. SQLite and every Jellyfin assembly come from the host: bundling
SQLitePCLRaw would load a second, uninitialized copy into the plugin's load context.
"""
from __future__ import annotations
import argparse, hashlib, json, re, shutil, subprocess, zipfile
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
GUID = "80b15e17-71cd-42e6-8c50-b077e7c8f0b7"
TARGET_ABI = "12.4.0.0"
ARTIFACTS = ["Jellyfin.Plugin.Jellytrim.dll", "Jellytrim.Core.dll"]
OVERVIEW = "Keep what matters. Reclaim the rest."
DESCRIPTION = ("Understand where library storage goes, see who has watched what, and safely reclaim space "
               "through a preview-first, auditable cleanup workflow. Read-only until an administrator enables deletion.")


def version() -> str:
    props = (ROOT / "Directory.Build.props").read_text()
    return re.search(r"<Version>([\d.]+)</Version>", props).group(1)


def source_epoch() -> str:
    """The last commit time, so that rebuilding one commit produces identical metadata."""
    try:
        return subprocess.check_output(["git", "-C", str(ROOT), "log", "-1", "--format=%cI"], text=True).strip()
    except subprocess.CalledProcessError:
        return "2026-01-01T00:00:00+00:00"


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source-url", default=None, help="URL where the zip will be published, for the manifest")
    parser.add_argument("--changelog", default="See CHANGELOG.md")
    args = parser.parse_args()

    ver = version()
    timestamp = datetime.fromisoformat(source_epoch()).astimezone(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    build = ROOT / "src" / "Jellyfin.Plugin.Jellytrim" / "bin" / "Release" / "net10.0"
    dist = ROOT / "dist"
    shutil.rmtree(dist, ignore_errors=True)
    folder = dist / f"jellytrim_{ver}"
    folder.mkdir(parents=True)
    for name in ARTIFACTS:
        shutil.copy(build / name, folder / name)

    # meta.json mirrors what Emby.Server.Implementations/Plugins/PluginManager reads from a plugin folder.
    meta = {
        "category": "General", "changelog": args.changelog, "description": DESCRIPTION, "guid": GUID, "name": "jellytrim",
        "overview": OVERVIEW, "owner": "aurimasniekis", "targetAbi": TARGET_ABI, "timestamp": timestamp, "version": ver,
        "status": "Active", "autoUpdate": False, "imagePath": "", "assemblies": ARTIFACTS,
    }
    (folder / "meta.json").write_text(json.dumps(meta, indent=2) + "\n")

    archive = dist / f"jellytrim_{ver}.zip"
    fixed_time = datetime.fromisoformat(timestamp.replace("Z", "+00:00")).timetuple()[:6]
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as z:
        for path in sorted(folder.iterdir()):
            info = zipfile.ZipInfo(path.name, date_time=fixed_time)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o644 << 16
            z.writestr(info, path.read_bytes())

    # Jellyfin's InstallationManager verifies an MD5 checksum, so that is what the manifest format requires.
    checksum = hashlib.md5(archive.read_bytes(), usedforsecurity=False).hexdigest()
    manifest = [{
        "guid": GUID, "name": "jellytrim", "description": DESCRIPTION, "overview": OVERVIEW, "owner": "aurimasniekis", "category": "General",
        "versions": [{
            "version": ver, "changelog": args.changelog, "targetAbi": TARGET_ABI,
            "sourceUrl": args.source_url or f"https://github.com/aurimasniekis/jellytrim/releases/download/v{ver}/{archive.name}",
            "checksum": checksum, "timestamp": timestamp,
        }],
    }]
    (dist / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
    (dist / "SHA256SUMS").write_text(f"{hashlib.sha256(archive.read_bytes()).hexdigest()}  {archive.name}\n")
    print(f"{archive} md5={checksum}")


if __name__ == "__main__":
    main()
