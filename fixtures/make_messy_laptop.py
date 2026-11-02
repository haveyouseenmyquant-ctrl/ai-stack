#!/usr/bin/env python3
"""Builds a reproducible "messy laptop" home folder for Laptop Doctor tests.

    python3 fixtures/make_messy_laptop.py <out-dir> [--platform macos|windows] [--seed N]

Writes <out-dir>/home/... and <out-dir>/expected.json. Same seed => same bytes; ages are relative to generation time.
Nothing outside <out-dir> is touched, except on Windows CI (CI=true), where a Run-key value
pointing into the fixture is registered under HKCU and listed in expected.json for cleanup.
"""
import argparse, hashlib, json, os, random, shutil, subprocess, sys, time, zlib

DAY = 86400
NOW = time.time()
EICAR = b"X5O!P%@AP[4\\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*"
WORDS = "alpha beta gamma delta epsilon request response error warning info debug value".split()


class Fixture:
    def __init__(self, out, platform, seed):
        self.out, self.platform = os.path.abspath(out), platform
        self.home = os.path.join(self.out, "home")
        self.rng = random.Random(seed)
        self.planted, self.traps = [], []

    def path(self, rel):
        return os.path.join(self.home, *rel.split("/"))

    def write(self, rel, data, age_days):
        p = self.path(rel)
        os.makedirs(os.path.dirname(p), exist_ok=True)
        with open(p, "wb") as f:
            f.write(data)
        t = NOW - age_days * DAY
        os.utime(p, (t, t))
        return p

    def text(self, lines):
        return ("\n".join(" ".join(self.rng.choice(WORDS) for _ in range(10)) for _ in range(lines)) + "\n").encode()

    def noise(self, size):
        return self.rng.randbytes(size)

    def age_dir(self, rel, age_days):
        """Sets mtimes bottom-up so a folder looks untouched for age_days."""
        top = self.path(rel)
        t = NOW - age_days * DAY
        for root, dirs, files in os.walk(top, topdown=False):
            for name in files + dirs:
                os.utime(os.path.join(root, name), (t, t))
        os.utime(top, (t, t))

    def plant(self, id, kind, rel, tier, action, **extra):
        p = self.path(rel)
        self.planted.append({"id": id, "kind": kind, "path": os.path.relpath(p, self.home).replace(os.sep, "/"),
                             "tier": tier, "action": action, "bytes": tree_bytes(p), **extra})

    def trap(self, id, rel, why):
        self.traps.append({"id": id, "path": rel, "why": why})


def tree_bytes(p):
    if os.path.isfile(p):
        return os.path.getsize(p)
    return sum(os.path.getsize(os.path.join(r, f)) for r, _, fs in os.walk(p) for f in fs)


def sha256(p):
    h = hashlib.sha256()
    with open(p, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def build(fx):
    mac = fx.platform == "macos"
    cache_root = "Library/Caches" if mac else "AppData/Local"

    # --- Tier 1: cold, compressible files (text logs, CSV, JSON) -------------------------------
    for i in range(6):
        rel = f"Documents/Old Logs/server-{i}.log"
        fx.write(rel, fx.text(40000 + 5000 * i), age_days=200 + i)
        fx.plant(f"compress-log-{i}", "cold-compressible", rel, 1, "compress")
    fx.write("Documents/Exports/ledger-2023.csv", fx.text(60000).replace(b" ", b","), age_days=400)
    fx.plant("compress-csv", "cold-compressible", "Documents/Exports/ledger-2023.csv", 1, "compress")

    # Not eligible: cold but incompressible, cold but recently-used text, warm text.
    fx.write("Documents/Old Logs/backup.zip", zlib.compress(fx.text(20000), 9), age_days=300)
    fx.trap("incompressible-zip", "Documents/Old Logs/backup.zip", "already compressed format")
    fx.write("Documents/Old Logs/random.bin", fx.noise(2_000_000), age_days=300)
    fx.trap("incompressible-noise", "Documents/Old Logs/random.bin", "sample shrinks < 25%")
    fx.write("Documents/notes-this-week.txt", fx.text(20000), age_days=2)
    fx.trap("recent-text", "Documents/notes-this-week.txt", "modified in the last 7 days")
    fx.write("Documents/draft-last-month.txt", fx.text(20000), age_days=20)
    fx.trap("warm-text", "Documents/draft-last-month.txt", "not cold for 60 days")

    # --- Tier 1: opted-in archive folder --------------------------------------------------------
    for i in range(12):
        fx.write(f"Projects/Thesis 2019/chapter-{i:02}.md", fx.text(3000), age_days=900)
    fx.write("Projects/Thesis 2019/figures/plot.dat", fx.text(8000), age_days=900)
    fx.age_dir("Projects/Thesis 2019", 900)
    fx.plant("archive-thesis", "opted-in-archive", "Projects/Thesis 2019", 1, "archiveFolder", optedIn=True)
    for i in range(3):
        fx.write(f"Projects/Not Opted/file-{i}.md", fx.text(3000), age_days=900)
    fx.trap("archive-not-opted", "Projects/Not Opted", "folder not opted in to archiving")

    # --- Tier 2: caches, installers, duplicates, dev junk ---------------------------------------
    for i in range(20):
        fx.write(f"{cache_root}/com.example.Browserish/Cache/blob-{i:03}", fx.noise(150_000), age_days=30)
    fx.age_dir(f"{cache_root}/com.example.Browserish", 30)
    fx.plant("cache-browserish", "cache", f"{cache_root}/com.example.Browserish", 2, "moveToTrash")

    installers = [".dmg", ".pkg", ".zip"] if mac else [".msi", ".exe", ".zip"]
    for i, ext in enumerate(installers):
        rel = f"Downloads/SomeApp-Installer-{i}{ext}"
        fx.write(rel, fx.noise(1_500_000), age_days=45 + i)
        fx.plant(f"installer-{i}", "downloads-installer", rel, 2, "moveToTrash")
    fx.write(f"Downloads/Fresh-Installer{installers[0]}", fx.noise(1_000_000), age_days=1)
    fx.trap("fresh-installer", f"Downloads/Fresh-Installer{installers[0]}", "modified in the last 7 days")

    dup = fx.noise(800_000)
    fx.write("Documents/Contract final.pdf", dup, age_days=120)
    fx.write("Desktop/Contract final (1).pdf", dup, age_days=110)
    fx.plant("duplicate-contract", "duplicate", "Desktop/Contract final (1).pdf", 2, "moveToTrash",
             duplicateOf="Documents/Contract final.pdf")

    for i in range(30):
        fx.write(f"Code/old-site/node_modules/pkg{i}/index.js", fx.text(400), age_days=200)
    fx.write("Code/old-site/package.json", b'{"name":"old-site"}\n', age_days=200)
    fx.age_dir("Code/old-site", 200)
    fx.plant("node-modules-inactive", "node-modules", "Code/old-site/node_modules", 2, "moveToTrash")

    if mac:
        for i in range(10):
            fx.write(f"Library/Developer/Xcode/DerivedData/OldApp-abc/Build/obj-{i}.o", fx.noise(200_000), 90)
        fx.age_dir("Library/Developer/Xcode/DerivedData", 90)
        fx.plant("xcode-derived", "xcode-derived-data", "Library/Developer/Xcode/DerivedData/OldApp-abc", 2, "moveToTrash")

    trash = ".Trash" if mac else "RecycleBinFixture"
    fx.write(f"{trash}/old-video.mov", fx.noise(1_200_000), age_days=40)
    fx.plant("trash", "trash", trash, 2, "emptyTrash")

    # --- Never: git repo contents, keychains, mail, browser profile ------------------------------
    fx.write("Code/myrepo/.git/HEAD", b"ref: refs/heads/main\n", age_days=300)
    fx.write("Code/myrepo/big-data.log", fx.text(30000), age_days=300)
    fx.trap("git-contents", "Code/myrepo/big-data.log", "inside a git repository: report only")
    never = (["Library/Keychains/login.keychain-db", "Library/Mail/V10/old.emlx",
              "Library/Application Support/Google/Chrome/Default/History"] if mac else
             ["AppData/Roaming/Microsoft/Credentials/blob", "AppData/Local/Google/Chrome/User Data/Default/History"])
    for i, rel in enumerate(never):
        fx.write(rel, fx.text(20000), age_days=400)
        fx.trap(f"never-{i}", rel, "never-list location")

    # --- Security: unsigned / orphaned persistence ----------------------------------------------
    bin_rel = "Library/Application Support/FishyUpdater/updater" if mac else "AppData/Roaming/FishyUpdater/updater.cmd"
    fx.write(bin_rel, b"#!/bin/sh\nsleep 1\n" if mac else b"@echo off\r\ntimeout 1\r\n", age_days=100)
    os.chmod(fx.path(bin_rel), 0o755)
    if mac:
        for label, target in [("com.fixture.fishyupdater", fx.path(bin_rel)),
                              ("com.fixture.orphan", fx.path("Library/Application Support/Gone/gone"))]:
            plist = (f'<?xml version="1.0" encoding="UTF-8"?>\n<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" '
                     f'"http://www.apple.com/DTDs/PropertyList-1.0.dtd">\n<plist version="1.0"><dict>'
                     f'<key>Label</key><string>{label}</string><key>ProgramArguments</key><array>'
                     f'<string>{target}</string></array><key>RunAtLoad</key><true/></dict></plist>\n')
            rel = f"Library/LaunchAgents/{label}.plist"
            fx.write(rel, plist.encode(), age_days=100)
            fx.plant(f"persist-{label.split('.')[-1]}", "persistence", rel, 2, "disableStartupItem",
                     flag="orphaned" if "orphan" in label else "unsigned")
    else:
        fx.plant("persist-fishyupdater", "persistence", bin_rel, 2, "disableStartupItem", flag="unsigned",
                 runKey="HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run\\FixtureFishyUpdater")
        if os.environ.get("CI") == "true" and sys.platform == "win32":
            import winreg
            with winreg.CreateKeyEx(winreg.HKEY_CURRENT_USER, r"Software\Microsoft\Windows\CurrentVersion\Run", 0,
                                    winreg.KEY_SET_VALUE) as k:   # a fresh runner profile may not have the key yet
                winreg.SetValueEx(k, "FixtureFishyUpdater", 0, winreg.REG_SZ, f'"{fx.path(bin_rel)}"')

    # --- Malware test string, CI only -----------------------------------------------------------
    if os.environ.get("CI") == "true":
        try:
            fx.write("Downloads/eicar-test.com", EICAR, age_days=10)
        except OSError:
            pass   # Windows Defender can block the write itself; its detection history still records the file
        # Windows: Defender quarantines it itself, so the checkup only reports it (Tier 0) and links to its history.
        fx.plant("eicar", "malware", "Downloads/eicar-test.com", *((0, "report") if sys.platform == "win32" else (1, "quarantine")))

    # --- CPU-burning dummy process (started by the test harness, not here) -----------------------
    burner = fx.write("Library/Application Support/BusyHelper/busyhelper.py" if mac else
                      "AppData/Roaming/BusyHelper/busyhelper.py",
                      b"import time\nend = time.time() + 600\nwhile time.time() < end:\n    pass\n", age_days=100)
    fx.planted.append({"id": "cpu-burner", "kind": "busy-process", "path": os.path.relpath(burner, fx.home).replace(os.sep, "/"),
                       "tier": 2, "action": "quitProcess", "bytes": 0, "processName": "busyhelper.py"})

    # --- Fake synced-cloud folder (only Windows can host a real sync root, see DESIGN.md) ---------
    if not mac:
        for i in range(5):
            rel = f"FixtureCloud/Photos 2020/img-{i}.raw"
            fx.write(rel, fx.noise(600_000), age_days=300)
            fx.plant(f"cloud-{i}", "cloud-evictable", rel, 1, "evictCloud", needsSyncRoot=True)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("out")
    ap.add_argument("--platform", choices=["macos", "windows"],
                    default="windows" if sys.platform == "win32" else "macos")
    ap.add_argument("--seed", type=int, default=20261101)
    a = ap.parse_args()
    if os.path.exists(a.out):
        if not os.path.exists(os.path.join(a.out, "expected.json")):
            sys.exit(f"refusing to replace {a.out}: not a fixture folder")
        shutil.rmtree(a.out)
    fx = Fixture(a.out, a.platform, a.seed)
    os.makedirs(fx.home)
    build(fx)
    tier1 = [p for p in fx.planted if p["tier"] == 1 and not p.get("needsSyncRoot")]
    expected = {
        "version": 1, "platform": a.platform, "seed": a.seed, "generatedAt": int(NOW), "home": "home",
        "optedInArchiveFolders": ["Projects/Thesis 2019"],
        "planted": fx.planted,
        "mustNotTouch": fx.traps,
        # Logical bytes of every Tier 1-eligible item; freed bytes are measured as allocated-size reduction.
        "tier1EligibleBytes": sum(p["bytes"] for p in tier1),
        "tier1EligibleBytesWithSyncRoot": sum(p["bytes"] for p in fx.planted if p["tier"] == 1),
        "hashes": {os.path.relpath(os.path.join(r, f), fx.home).replace(os.sep, "/"): sha256(os.path.join(r, f))
                   for r, _, fs in os.walk(fx.home) for f in fs},
    }
    with open(os.path.join(a.out, "expected.json"), "w") as f:
        json.dump(expected, f, indent=1, sort_keys=True)
    print(f"fixture: {len(fx.planted)} planted issues, {len(fx.traps)} traps, "
          f"{expected['tier1EligibleBytes']:,} Tier 1 bytes -> {a.out}")


if __name__ == "__main__":
    main()
