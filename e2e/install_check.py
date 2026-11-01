#!/usr/bin/env python3
"""Proves the distribution path against a real server: repository manifest, install, upgrade keeping data, uninstall leaving data.

Serves dist/ over HTTP to a fresh disposable container. Uses its own server name and port, and only .e2e/install.
"""
import functools, hashlib, http.server, json, re, shutil, socketserver, subprocess, sys, threading, time, urllib.error, urllib.request, zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
RUN = ROOT / ".e2e" / "install"
SERVE = RUN / "serve"
NAME, PORT, BASE = "jellytrim-install", 18097, "http://localhost:18097"
IMAGE = re.search(r"image: (\S+)", (ROOT / "deploy/compose.e2e.yml").read_text()).group(1)
CLIENT = 'MediaBrowser Client="jt", Device="i", DeviceId="i", Version="1"'
GUID = "80b15e1771cd42e68c50b077e7c8f0b7"


def call(method, path, token=None, body=None, tolerate=()):
    request = urllib.request.Request(BASE + path, method=method, data=json.dumps(body).encode() if body is not None else None,
                                     headers={"Authorization": f'MediaBrowser Token="{token}"' if token else CLIENT, "Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(request, timeout=60) as r:
            raw = r.read()
            return json.loads(raw) if raw else None
    except urllib.error.HTTPError as e:
        if e.code in tolerate:
            return e.code
        raise RuntimeError(f"{method} {path}: {e.code} {e.read()[:200]!r}") from e


def wait_up():
    for _ in range(120):
        try:
            with urllib.request.urlopen(BASE + "/System/Info/Public", timeout=3) as r:
                if r.status == 200 and b"Id" in r.read():
                    return
        except Exception:
            pass
        time.sleep(1.5)
    raise SystemExit("server did not start")


def wipe():
    subprocess.run(["docker", "rm", "-f", NAME], capture_output=True)
    if RUN.exists():
        subprocess.run(["docker", "run", "--rm", "-v", f"{RUN}:/d", "alpine:3.22", "sh", "-c", "rm -rf /d/* /d/.[!.]*"], capture_output=True)
        shutil.rmtree(RUN, ignore_errors=True)


def build_version(version):
    """Compiles a real build at this version. Jellyfin reports the assembly's version, so re-stamping metadata would not be an upgrade."""
    out = RUN / f"build-{version}"
    subprocess.run(["dotnet", "build", str(ROOT / "src/Jellyfin.Plugin.Jellytrim"), "-c", "Release", f"-p:Version={version}", f"-p:AssemblyVersion={version}", f"-p:FileVersion={version}", "--no-incremental", "-o", str(out), "-v", "q", "-nologo"], check=True, capture_output=True)
    return out


def publish(version):
    """Publishes a zip and manifest entry for a version, as a release would."""
    build = build_version(version)
    target = SERVE / f"jellytrim_{version}.zip"
    meta = json.loads((next((ROOT / "dist").glob("jellytrim_*/")) / "meta.json").read_text())
    meta["version"] = version
    with zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED) as z:
        for name in meta["assemblies"]:
            z.write(build / name, name)
        z.writestr("meta.json", json.dumps(meta))
    manifest = json.loads((ROOT / "dist/manifest.json").read_text())
    entry = dict(manifest[0]["versions"][0], version=version, sourceUrl=f"http://host.docker.internal:18098/{target.name}", checksum=hashlib.md5(target.read_bytes(), usedforsecurity=False).hexdigest())
    versions = json.loads((SERVE / "manifest.json").read_text())[0]["versions"] if (SERVE / "manifest.json").exists() else []
    manifest[0]["versions"] = [entry] + versions
    (SERVE / "manifest.json").write_text(json.dumps(manifest))


def plugin(token):
    return next((p for p in call("GET", "/Plugins", token) if p["Id"] == GUID), None)


def restart(token):
    subprocess.run(["docker", "restart", NAME], check=True, capture_output=True)
    wait_up()
    for _ in range(60):
        status = call("GET", "/Plugins", token, tolerate=(401, 503))
        if isinstance(status, list):
            return
        time.sleep(1)


def install(token, version):
    call("POST", f"/Packages/Installed/jellytrim?assemblyGuid={GUID}&version={version}&repositoryUrl=http://host.docker.internal:18098/manifest.json", token)
    for _ in range(60):
        time.sleep(1)
        if list((RUN / "config/plugins").glob(f"jellytrim_{version}*/meta.json")):
            break
    time.sleep(2)
    restart(token)


def main():
    wipe()
    (RUN / "config").mkdir(parents=True)
    SERVE.mkdir()
    class Quiet(http.server.SimpleHTTPRequestHandler):
        def log_message(self, *args):
            pass

    handler = functools.partial(Quiet, directory=str(SERVE))
    socketserver.ThreadingTCPServer.allow_reuse_address = True
    server = socketserver.ThreadingTCPServer(("0.0.0.0", 18098), handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    subprocess.run(["docker", "run", "-d", "--name", NAME, "-p", f"{PORT}:8096", "--add-host", "host.docker.internal:host-gateway", "-v", f"{RUN}/config:/config", IMAGE], check=True, capture_output=True)
    try:
        wait_up()
        for _ in range(60):
            try:
                call("GET", "/Startup/User")
                break
            except Exception:
                time.sleep(1.5)
        call("POST", "/Startup/User", body={"Name": "admin", "Password": "pw"})
        call("POST", "/Startup/Complete")
        token = call("POST", "/Users/AuthenticateByName", body={"Username": "admin", "Pw": "pw"})["AccessToken"]

        current = re.search(r"<Version>([\d.]+)</Version>", (ROOT / "Directory.Build.props").read_text()).group(1)
        first = current
        second = ".".join(current.split(".")[:3] + [str(int(current.split(".")[3]) + 1)])
        publish(first)
        call("POST", "/Repositories", token, [{"Name": "jellytrim-test", "Url": "http://host.docker.internal:18098/manifest.json", "Enabled": True}])
        packages = call("GET", "/Packages", token)
        listed = next((p for p in packages if p["name"] == "jellytrim"), None)
        assert listed, "manifest was not accepted: plugin missing from catalog"
        print("PASS catalog lists jellytrim from the manifest, versions:", [v["version"] for v in listed["versions"]])

        install(token, first)
        p = plugin(token)
        assert p and p["Version"] == first and p["Status"] == "Active", f"install: {p}"
        print("PASS installed through the catalog with its checksum verified, Active", first)

        policy = {"name": "survives upgrades", "participatingUserIds": [call("GET", "/Users", token)[0]["Id"]], "watched": "AllSelected"}
        created = call("POST", "/Jellytrim/Policies", token, policy)
        settings = call("GET", "/Jellytrim/Settings", token)
        settings["settings"]["planTtlMinutes"] = 17
        call("PUT", "/Jellytrim/Settings", token, {"settings": settings["settings"], "expectedRevision": settings["revision"]})

        publish(second)
        call("POST", "/Repositories", token, [{"Name": "jellytrim-test", "Url": "http://host.docker.internal:18098/manifest.json", "Enabled": True}])
        install(token, second)
        p = plugin(token)
        assert p and p["Version"] == second and p["Status"] == "Active", f"upgrade: {p}"
        assert any(x["policy"]["id"] == created["policy"]["id"] for x in call("GET", "/Jellytrim/Policies", token)), "policy lost in upgrade"
        assert call("GET", "/Jellytrim/Settings", token)["settings"]["planTtlMinutes"] == 17, "settings lost in upgrade"
        print("PASS upgrade to", second, "kept policies and settings")

        call("DELETE", f"/Plugins/{GUID}", token)
        restart(token)
        assert plugin(token) is None, "still listed after uninstall"
        assert call("GET", "/Jellytrim/Status", token, tolerate=(404,)) == 404, "API still answers after uninstall"
        db = RUN / "config/data/jellytrim/jellytrim.db"
        assert db.exists(), "database should remain after uninstall"
        print("PASS uninstall removed the plugin and its API, and left the database at data/jellytrim/")

        call("POST", f"/Packages/Installed/jellytrim?assemblyGuid={GUID}&version={second}&repositoryUrl=http://host.docker.internal:18098/manifest.json", token)
        time.sleep(4)
        restart(token)
        assert any(x["policy"]["id"] == created["policy"]["id"] for x in call("GET", "/Jellytrim/Policies", token)), "data lost across reinstall"
        print("PASS reinstall picked up the retained data")
    finally:
        server.shutdown()
        server.server_close()
        wipe()


if __name__ == "__main__":
    main()
