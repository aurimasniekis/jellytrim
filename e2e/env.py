#!/usr/bin/env python3
"""Disposable Jellyfin test environment: generates fixtures, starts the pinned server and seeds users and state.

    e2e/env.py up      fresh server plus fixtures, plugin installed from dist/
    e2e/env.py down    stop and delete everything under .e2e/run
    e2e/env.py restart stop and start the container, keeping its data

Every path it touches is under .e2e/run. It refuses to run if that directory is a symlink.
"""
from __future__ import annotations
import json, os, shutil, subprocess, sys, time, urllib.error, urllib.parse, urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
RUN = ROOT / ".e2e" / "run"
COMPOSE = ["docker", "compose", "-f", str(ROOT / "deploy" / "compose.e2e.yml"), "-p", "jellytrim-e2e"]
os.environ.setdefault("JELLYTRIM_E2E_USER", f"{os.getuid()}:{os.getgid()}")
PORT = os.environ.get("JELLYTRIM_E2E_PORT", "18096")
BASE = f"http://localhost:{PORT}"
CLIENT = 'MediaBrowser Client="jellytrim-e2e", Device="e2e", DeviceId="jellytrim-e2e", Version="1"'
ADMIN = ("e2e-admin", "e2e-admin-password")
XSS_NAME = 'Xss <img src=x onerror=window.__xss=1> (2006)'
# No online metadata: fixtures must keep their generated names, and test runs must not contact third parties.
OFFLINE_TYPES = [{"Type": t, "MetadataFetchers": [], "MetadataFetcherOrder": [], "ImageFetchers": [], "ImageFetcherOrder": []} for t in ("Movie", "Series", "Season", "Episode")]


def call(method, path, token=None, body=None, ok=(200, 204)):
    data = json.dumps(body).encode() if body is not None else None
    auth = f'MediaBrowser Token="{token}"' if token else CLIENT
    request = urllib.request.Request(BASE + path, data=data, method=method, headers={"Authorization": auth, "Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(request, timeout=60) as response:
            raw = response.read()
            return json.loads(raw) if raw else None
    except urllib.error.HTTPError as e:
        if e.code in ok:
            return None
        raise RuntimeError(f"{method} {path} -> {e.code}: {e.read()[:300]!r}") from e


def wait_up(timeout=180):
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            with urllib.request.urlopen(BASE + "/System/Info/Public", timeout=3) as r:
                if r.status == 200:
                    return
        except Exception:
            pass
        time.sleep(1.5)
    raise SystemExit("server did not start")


def video(path: Path, seconds: int):
    path.parent.mkdir(parents=True, exist_ok=True)
    subprocess.run(["ffmpeg", "-loglevel", "error", "-y", "-f", "lavfi", "-i", f"testsrc=size=160x90:rate=5:duration={seconds}",
                    "-f", "lavfi", "-i", f"sine=frequency=440:duration={seconds}", "-c:v", "libx264", "-preset", "ultrafast",
                    "-c:a", "aac", "-shortest", str(path)], check=True)


def fixtures():
    media = RUN / "media"
    m, t = media / "movies", media / "tv"
    base = RUN / "base.mkv"
    video(base, 2)
    def copy(rel: Path, pad: int = 0):
        rel.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(base, rel)
        if pad:
            with open(rel, "ab") as f:
                f.write(b"\0" * pad)
        return rel

    for name, pad in (("Alpha (2001)", 400_000), ("Bravo (2002)", 800_000), ("Charlie (2003)", 200_000), ("Foxtrot (2007)", 600_000), ("Golf (2008)", 100_000), ("Hotel (2009)", 150_000), ("India (2010)", 120_000), ("Juliet (2011)", 110_000)):
        copy(m / name / f"{name}.mkv", pad)
    d = m / "Alpha (2001)"
    (d / "Alpha (2001).en.srt").write_text("1\n00:00:00,000 --> 00:00:01,000\nHi\n")
    (d / "poster.jpg").write_bytes(b"\xff\xd8\xff\xe0jellytrim-fixture")
    (d / "notes.txt").write_text("keep me: not attributable to the movie\n")
    (d / "extras").mkdir()
    shutil.copyfile(base, d / "extras" / "Behind the scenes.mkv")
    copy(m / XSS_NAME / f"{XSS_NAME}.mkv", 50_000)
    # Hard link kept outside the library, as a torrent client's seed copy would be.
    copy(m / "Linked (2012)" / "Linked (2012).mkv", 300_000)
    (media / "seed").mkdir()
    os.link(m / "Linked (2012)" / "Linked (2012).mkv", media / "seed" / "Linked.mkv")
    # Two versions of one movie.
    copy(m / "Echo (2005)" / "Echo (2005) - 1080p.mkv", 500_000)
    copy(m / "Echo (2005)" / "Echo (2005) - 720p.mkv", 250_000)
    # Symlink inside the library pointing outside it.
    copy(media / "outside" / "Target.mkv", 10_000)
    (m / "Symlinked (2013)").mkdir()
    os.symlink("/media/outside/Target.mkv", m / "Symlinked (2013)" / "Symlinked (2013).mkv")

    for show, season, episodes in (("Show One", 1, 4), ("Show One", 0, 1), ("Show Two", 1, 2)):
        for e in range(1, episodes + 1):
            copy(t / show / f"Season {season:02}" / f"{show} S{season:02}E{e:02}.mkv", 100_000 + e * 1000)
    (t / "Show Two" / "tvshow.nfo").write_text("<tvshow><title>Show Two</title></tvshow>\n")
    (RUN / "base.mkv").unlink()


def wait_tasks(token, timeout=240):
    deadline = time.time() + timeout
    while time.time() < deadline:
        tasks = call("GET", "/ScheduledTasks", token) or []
        if not any(t.get("State") == "Running" and t.get("Key") == "RefreshLibrary" for t in tasks):
            return
        time.sleep(1)
    raise SystemExit("library scan did not finish")


def up():
    if RUN.is_symlink():
        raise SystemExit("refusing: .e2e/run is a symlink")
    down()
    dist = next(iter(sorted((ROOT / "dist").glob("jellytrim_*/"))), None)
    if dist is None:
        raise SystemExit("run `make package` first")
    plugin = RUN / "config" / "plugins" / dist.name
    plugin.mkdir(parents=True)
    (RUN / "cache").mkdir()
    for file in dist.iterdir():
        shutil.copy(file, plugin / file.name)
    fixtures()
    subprocess.run(COMPOSE + ["up", "-d", "--quiet-pull"], check=True)
    wait_up()
    deadline = time.time() + 120
    while True:
        try:
            call("GET", "/Startup/User", ok=(200,))
            break
        except Exception:
            if time.time() > deadline:
                raise
            time.sleep(1.5)
    call("POST", "/Startup/User", body={"Name": ADMIN[0], "Password": ADMIN[1]})
    call("POST", "/Startup/Complete")
    auth = call("POST", "/Users/AuthenticateByName", body={"Username": ADMIN[0], "Pw": ADMIN[1]})
    token, admin_id = auth["AccessToken"], auth["User"]["Id"]

    users = {"admin": admin_id}
    for name in ("ann", "bob"):
        users[name] = call("POST", "/Users/New", token, {"Name": name, "Password": name + "-pw"})["Id"]
    bob_token = call("POST", "/Users/AuthenticateByName", body={"Username": "bob", "Pw": "bob-pw"})["AccessToken"]
    api_key_name = urllib.parse.quote("jellytrim-e2e-key")
    call("POST", f"/Auth/Keys?app={api_key_name}", token)
    api_key = next(k["AccessToken"] for k in call("GET", "/Auth/Keys", token)["Items"] if k["AppName"] == "jellytrim-e2e-key")

    for name, kind, path in (("Movies", "movies", "/media/movies"), ("Shows", "tvshows", "/media/tv")):
        q = urllib.parse.urlencode({"name": name, "collectionType": kind, "refreshLibrary": "false"})
        call("POST", f"/Library/VirtualFolders?{q}", token, {"LibraryOptions": {"PathInfos": [{"Path": path}], "EnableRealtimeMonitor": False, "EnablePhotos": False, "EnableInternetProviders": False, "MetadataSavers": [], "EnableChapterImageExtraction": False, "ExtractTrickplayImagesDuringLibraryScan": False, "TypeOptions": OFFLINE_TYPES}})
    call("POST", "/Library/Refresh", token)
    time.sleep(2)
    wait_tasks(token)

    def everything():
        q = urllib.parse.urlencode({"recursive": "true", "includeItemTypes": "Movie,Episode,Series,Season", "userId": admin_id, "fields": "Path"})
        return call("GET", f"/Items?{q}", token)["Items"]

    deadline = time.time() + 120
    while len([i for i in everything() if i["Type"] in ("Movie", "Episode")]) < 19:
        if time.time() > deadline:
            raise SystemExit("fixtures were not indexed in time")
        time.sleep(2)
    wait_tasks(token)

    # Keyed by path, the only thing about a fixture that metadata cannot rename.
    ids = {}
    for item in everything():
        path = Path(item["Path"])
        ids[{"Movie": path.parent.name, "Episode": path.stem, "Series": path.name, "Season": f"{path.parent.name}/{path.name}"}[item["Type"]]] = item["Id"]

    def played(item, user):
        call("POST", f"/UserPlayedItems/{item}?userId={users[user]}", token)

    # Alpha, Bravo: everyone selected. Charlie: ann only. Foxtrot: both, but bob favorited it. Golf: ann played, bob in progress.
    for movie, who in (("Alpha", ("ann", "bob")), ("Bravo", ("ann", "bob")), ("Charlie", ("ann",)), ("Foxtrot", ("ann", "bob")), ("Golf", ("ann",)), ("Linked", ("ann", "bob")), ("Echo", ("ann", "bob")), ("Hotel", ("ann", "bob")), ("India", ("ann", "bob")), ("Juliet", ("ann", "bob")), ("Xss", ("ann", "bob")), ("Symlinked", ("ann", "bob"))):
        item = next((v for k, v in ids.items() if k.startswith(movie)), None)
        if item:
            for user in who:
                played(item, user)
    fox = next(v for k, v in ids.items() if k.startswith("Foxtrot"))
    call("POST", f"/UserFavoriteItems/{fox}?userId={users['bob']}", token)
    golf = next(v for k, v in ids.items() if k.startswith("Golf"))
    call("POST", f"/UserItems/{golf}/UserData?userId={users['bob']}", token, {"PlaybackPositionTicks": 5_000_000})
    for key, value in ids.items():
        if key.startswith("Show Two S") or key == "Show One S01E01":
            for user in ("ann", "bob"):
                played(value, user)

    state = {"base": BASE, "token": token, "nonAdminToken": bob_token, "apiKey": api_key, "users": users, "items": ids, "admin": {"username": ADMIN[0], "password": ADMIN[1]}, "xssName": XSS_NAME}
    (ROOT / ".e2e" / "state.json").write_text(json.dumps(state, indent=2))
    print(json.dumps({"base": BASE, "users": users, "items": len(ids)}))


def down():
    subprocess.run(COMPOSE + ["down", "-v", "--remove-orphans"], check=False, capture_output=True)
    if RUN.exists() and not RUN.is_symlink():
        # The container may have written as root. Remove through it so that we never need host privileges.
        subprocess.run(["docker", "run", "--rm", "-v", f"{RUN}:/run-dir", "alpine:3.22", "sh", "-c", "rm -rf /run-dir/* /run-dir/.[!.]*"], check=False, capture_output=True)
        shutil.rmtree(RUN, ignore_errors=True)
    (ROOT / ".e2e" / "state.json").unlink(missing_ok=True)


def restart():
    subprocess.run(COMPOSE + ["restart"], check=True, capture_output=True)
    wait_up()


if __name__ == "__main__":
    {"up": up, "down": down, "restart": restart}[sys.argv[1]]()
