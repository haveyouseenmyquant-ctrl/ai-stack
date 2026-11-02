#!/usr/bin/env python3
"""Laptop Doctor safety suite. Drives the headless CLI against fresh messy-laptop fixtures.

    python3 tests/safety/run_safety.py --bin <AIStack binary> --work <scratch dir> --out doctor-results.json

Every write goes to fixtures under --work. Prints one line per test and writes doctor-results.json:
  safety {passed,total,tests}, planted {found,total,missing}, tier1 {freed,eligible}, unapprovedDestructive.
"""
import argparse, glob, hashlib, json, os, random, shutil, signal, stat, subprocess, sys, time, traceback, zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
MAKE = os.path.join(REPO, "fixtures", "make_messy_laptop.py")
CATALOGUE = os.path.join(REPO, "shared", "doctor-catalogue.json")
ARCHIVE = "Projects/Thesis 2019"
WINDOWS = sys.platform == "win32"
TRASH = "RecycleBinFixture" if WINDOWS else ".Trash"   # test-mode Trash / Recycle Bin inside the fixture home


# One helper call should take seconds; a hang (e.g. a blocked Cloud Files request) must fail one test, not the job.
CMD_TIMEOUT = 240


def run_bin(cmd, env=None, text=False):
    """subprocess.run with a watchdog. On timeout it prints the hung process's managed stacks (dotnet-stack, if
    installed) and kills its whole process tree, so inherited pipes can't block the suite."""
    p = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=text, env=env)
    try:
        out, err = p.communicate(timeout=CMD_TIMEOUT)
    except subprocess.TimeoutExpired:
        if WINDOWS and shutil.which("dotnet-stack"):
            st = subprocess.run(["dotnet-stack", "report", "-p", str(p.pid)], capture_output=True, text=True, timeout=60)
            print(f"hung after {CMD_TIMEOUT}s: {cmd[1:4]}\n{st.stdout[-6000:]}{st.stderr[-1000:]}", flush=True)
        if WINDOWS:
            subprocess.run(["taskkill", "/T", "/F", "/PID", str(p.pid)], capture_output=True)
        else:
            p.kill()
        try:
            p.communicate(timeout=30)
        except subprocess.TimeoutExpired:
            pass
        raise AssertionError(f"timed out after {CMD_TIMEOUT}s: {' '.join(map(str, cmd[1:4]))}")
    return subprocess.CompletedProcess(cmd, p.returncode, out, err)


class Ctx:
    def __init__(self, binary, work):
        self.bin, self.work, self.n, self.journals, self.providers = os.path.abspath(binary), os.path.abspath(work), 0, [], []

    def fixture(self):
        self.n += 1
        out = os.path.join(self.work, f"fx{self.n}")
        subprocess.run([sys.executable, MAKE, out], check=True, stdout=subprocess.DEVNULL)
        exp = json.load(open(os.path.join(out, "expected.json")))
        exp["syncRoot"] = self.cloud_provider(out)
        if exp["syncRoot"]:
            exp["tier1EligibleBytes"] = exp["tier1EligibleBytesWithSyncRoot"]
        journal = os.path.join(out, "journal.jsonl")
        self.journals.append(journal)
        return os.path.join(out, "home"), exp, journal, out

    def cloud_provider(self, out):
        """Windows: makes home/FixtureCloud a real Cloud Files sync root served by the test provider
        (LD_CLOUD_PROVIDER). False if there is none or the machine can't host one; the cloud items then count as missed."""
        exe, cloud = os.environ.get("LD_CLOUD_PROVIDER"), os.path.join(out, "home", "FixtureCloud")
        if not (WINDOWS and exe and os.path.isdir(cloud)):
            return False
        pr = subprocess.Popen([exe, cloud, os.path.join(out, "cloud-server")], stdout=subprocess.PIPE, text=True)
        line = pr.stdout.readline().strip()
        if line != "ready":
            print("cloud provider:", line or f"exited {pr.wait()}")
            return False
        self.providers.append(pr)
        if self.n == 1:   # what the file system says about one placeholder (CI log)
            f = glob.glob(os.path.join(cloud, "*", "*"))[0]
            print("cloud provider:", f, hex(os.lstat(f).st_file_attributes), hex(getattr(os.lstat(f), "st_reparse_tag", 0)),
                  subprocess.run(["fsutil", "reparsepoint", "query", f], capture_output=True, text=True).stdout[:300])
        return True

    def doctor(self, verb, *args, check=True, env=None):
        cmd = [self.bin, "--doctor", verb, "--catalogue", CATALOGUE, *args]
        r = run_bin(cmd, env=merged(env), text=True)
        if check and r.returncode != 0:
            raise AssertionError(f"{verb} failed rc={r.returncode}: {r.stderr.strip()[:400]}")
        return r


def merged(env):
    """os.environ plus `env`; a None value removes the variable."""
    e = dict(os.environ)
    for k, v in (env or {}).items():
        if v is None:
            e.pop(k, None)
        else:
            e[k] = v
    return e


def sha(p):
    h = hashlib.sha256()
    with open(p, "rb") as f:
        for c in iter(lambda: f.read(1 << 20), b""):
            h.update(c)
    return h.hexdigest()


def zip_members(path, prefix):
    """The doctor's archive of an opted-in folder, as virtual entries: bytes only (times live in the journal)."""
    snap = {}
    with zipfile.ZipFile(path) as z:
        for m in z.infolist():
            if not m.is_dir() and not m.filename.startswith("__MACOSX/"):
                snap[prefix + "/" + m.filename] = (hashlib.sha256(z.read(m)).hexdigest(), None, None)
    return snap


def snapshot(home, skip_trash=False):
    """rel path -> (sha256, mtime us, birthtime us). Our temp siblings (.ld-) are skipped (see leftovers);
    the archive of ARCHIVE counts as the files it holds, so a folder in a verified zip is not 'missing'."""
    snap = {}
    for root, dirs, files in os.walk(home):
        for f in files:
            p = os.path.join(root, f)
            rel = os.path.relpath(p, home).replace(os.sep, "/")
            if (skip_trash and rel.startswith(TRASH + "/")) or ".ld-" in rel:
                continue
            if rel == ARCHIVE + ".zip" and not os.path.isdir(os.path.join(home, ARCHIVE)):
                snap.update(zip_members(p, ARCHIVE))
                continue
            st = os.lstat(p)
            snap[rel] = (sha(p), st.st_mtime_ns // 1000, int(getattr(st, "st_birthtime", 0) * 1e6))
    return snap


def allocated(p):
    """Bytes on disk: st_blocks on macOS, GetCompressedFileSizeW (the NTFS compressed size) on Windows."""
    if not WINDOWS:
        return os.lstat(p).st_blocks * 512
    import ctypes
    hi = ctypes.c_ulong(0)
    lo = ctypes.windll.kernel32.GetCompressedFileSizeW(ctypes.c_wchar_p(p), ctypes.byref(hi))
    return (hi.value << 32) | lo


def blocks(home):
    return sum(allocated(os.path.join(r, f)) for r, _, fs in os.walk(home) for f in fs)


def is_compressed(p):
    st = os.lstat(p)
    if WINDOWS:
        return bool(st.st_file_attributes & stat.FILE_ATTRIBUTE_COMPRESSED)
    return bool(getattr(st, "st_flags", 0) & getattr(stat, "UF_COMPRESSED", 0x20))


def compressed_files(home):
    return [os.path.join(r, f) for r, _, fs in os.walk(home) for f in fs if is_compressed(os.path.join(r, f))]


def leftovers(home):
    return [os.path.join(r, f) for r, ds, fs in os.walk(home) for f in fs + ds if ".ld-" in os.path.join(r, f)]


def diff(a, b, what="hash"):
    """Names of files missing or different between snapshots (what: hash = bytes only, all = bytes + times)."""
    idx = 1 if what == "hash" else 3
    bad = [k for k in a if k not in b or a[k][:1 if b[k][1] is None else idx] != b[k][:1 if b[k][1] is None else idx]]
    return bad + [k for k in b if k not in a]


def load(p):
    return json.load(open(p))


# ------------------------------------------------------------------------------------------------ tests

def t_tier1_roundtrip(c, res):
    home, exp, j, out = c.fixture()
    before, b0 = snapshot(home), blocks(home)
    c.doctor("tier1", "--root", home, "--journal", j, "--archive", ARCHIVE)
    b1 = blocks(home)
    res["tier1"] = {"freed": max(0, b0 - b1), "eligible": exp["tier1EligibleBytes"]}
    mid = snapshot(home)
    assert not diff(before, mid, "all"), f"tier1 changed bytes or times: {diff(before, mid, 'all')[:5]}"
    assert compressed_files(home), "nothing was compressed"
    assert os.path.isfile(os.path.join(home, ARCHIVE + ".zip")) and not os.path.exists(os.path.join(home, ARCHIVE)), \
        "opted-in folder was not archived"
    c.doctor("undo", "--root", home, "--journal", j)
    assert not os.path.exists(os.path.join(home, ARCHIVE + ".zip")), "archive left after undo"
    after = snapshot(home)
    assert not diff(before, after, "all"), f"undo did not restore: {diff(before, after, 'all')[:5]}"
    assert not compressed_files(home), f"still compressed after undo: {compressed_files(home)[:3]}"
    assert not leftovers(home), f"temp files left: {leftovers(home)}"


def t_kill_during_tier1(c, res, trials=12):
    home, exp, j, out = c.fixture()
    before = snapshot(home)
    rng = random.Random(7)
    for t in range(trials):
        p = subprocess.Popen([c.bin, "--doctor", "tier1", "--catalogue", CATALOGUE, "--root", home, "--journal", j,
                              "--archive", ARCHIVE], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        time.sleep(rng.uniform(0.02, 1.2))
        p.send_signal(signal.SIGKILL) if hasattr(signal, "SIGKILL") else p.kill()
        p.wait()
        now = snapshot(home)
        missing = [k for k in before if k not in now]
        assert not missing, f"trial {t}: files missing after kill: {missing[:5]}"
        assert not diff(before, now, "hash"), f"trial {t}: bytes changed: {diff(before, now)[:5]}"
        c.doctor("undo", "--root", home, "--journal", j)
    c.doctor("recover", "--root", home, "--journal", j)
    assert not leftovers(home), f"temp files left after recover: {leftovers(home)}"
    c.doctor("undo", "--root", home, "--journal", j)
    assert not diff(before, snapshot(home), "all"), "kill + undo did not restore the tree"


def t_crash_points_archive(c, res):
    """Power cut at every step of archiving and un-archiving: no file goes missing, recover + undo restore the tree."""
    home, exp, j, out = c.fixture()
    before = snapshot(home)
    folder, zipf = os.path.join(home, ARCHIVE), os.path.join(home, ARCHIVE + ".zip")
    def run(verb, point, *extra):
        r = run_bin([c.bin, "--doctor", verb, "--catalogue", CATALOGUE, "--root", home, "--journal", j, *extra],
                           env=dict(os.environ, LD_CRASH_AT=point))
        assert r.returncode != 0, f"{point}: crash point was not reached"
    def intact(point):
        now = snapshot(home)
        missing = [k for k in before if k not in now]
        assert not missing, f"{point}: files missing: {missing[:5]}"
        changed = [k for k in before if now[k][0] != before[k][0]]   # an extra zip beside the intact folder is fine
        assert not changed, f"{point}: bytes changed: {changed[:5]}"
    for point in ["archive-zipped", "archive-verified", "archive-zip-in-place", "archive-aside", "archive-removed"]:
        run("tier1", point, "--archive", ARCHIVE)
        intact(point)
        c.doctor("recover", "--root", home, "--journal", j)
        assert not leftovers(home), f"{point}: temp files left after recover: {leftovers(home)}"
        assert os.path.isdir(folder) != os.path.isfile(zipf), f"{point}: after recover want folder xor zip"
        intact(point + "+recover")
        c.doctor("undo", "--root", home, "--journal", j)
        assert not diff(before, snapshot(home), "all"), f"{point}: recover + undo did not restore the tree"
    for point in ["unarchive-extracted", "unarchive-in-place"]:
        c.doctor("tier1", "--root", home, "--journal", j, "--archive", ARCHIVE)
        assert os.path.isfile(zipf)
        run("undo", point)
        intact(point)
        c.doctor("recover", "--root", home, "--journal", j)
        assert not leftovers(home), f"{point}: temp files left after recover: {leftovers(home)}"
        intact(point + "+recover")
        c.doctor("undo", "--root", home, "--journal", j)
        assert not diff(before, snapshot(home), "all"), f"{point}: recover + undo did not restore the tree"
        assert not os.path.exists(zipf), f"{point}: zip left behind"


FAKE_CLAMSCAN = """#!/usr/bin/env python3
import os, sys
hits = [os.path.join(r, f) for a in sys.argv[1:] if os.path.isdir(a) for r, _, fs in os.walk(a) for f in fs if "flagged" in f]
for h in hits:
    print(h + ": Fixture.Test.Signature FOUND")
sys.exit(1 if hits else 0)
"""
QUARANTINE = "Library/Application Support/AIStack/Doctor/Quarantine"


def t_quarantine_roundtrip_and_crash_points(c, res):
    """A scanner hit is moved to quarantine with execute bits off; undo and every power cut put it back intact.
    A stand-in clamscan (flags files named *flagged*) keeps this test independent of ClamAV's signature download."""
    home, exp, j, out = c.fixture()
    fake = os.path.join(out, "fake-clamscan")
    with open(fake, "w") as f:
        f.write(FAKE_CLAMSCAN)
    os.chmod(fake, 0o755)
    env = {"LD_CLAMSCAN": fake}
    target = os.path.join(home, "Downloads", "invoice-flagged.bin")
    with open(target, "wb") as f:
        f.write(b"not really malware\n" * 100)
    os.chmod(target, 0o755)
    old = time.time() - 40 * 86400
    os.utime(target, (old, old))
    before = snapshot(home)
    def where():
        q = glob.glob(os.path.join(home, QUARANTINE, "*", "invoice-flagged.bin"))
        return os.path.exists(target), q
    def intact(point):
        at_orig, q = where()
        assert at_orig != bool(q), f"{point}: want the file at exactly one place, got orig={at_orig} quarantine={q}"
        p = target if at_orig else q[0]
        assert sha(p) == before["Downloads/invoice-flagged.bin"][0], f"{point}: bytes changed"
    c.doctor("tier1", "--root", home, "--journal", j, env=env)
    at_orig, q = where()
    assert not at_orig and len(q) == 1, f"not quarantined: orig={at_orig} q={q}"
    assert os.stat(q[0]).st_mode & 0o111 == 0, "execute bits left on"
    intact("quarantined")
    c.doctor("undo", "--root", home, "--journal", j)
    assert not diff(before, snapshot(home), "all"), "undo did not restore the file"
    assert os.stat(target).st_mode & 0o777 == 0o755, "mode not restored"
    for verb, point in [("tier1", "quarantine-intent"), ("tier1", "quarantine-moved"), ("undo", "unquarantine-moved")]:
        if verb == "undo":
            c.doctor("tier1", "--root", home, "--journal", j, env=env)
        r = run_bin([c.bin, "--doctor", verb, "--catalogue", CATALOGUE, "--root", home, "--journal", j],
                           env=dict(os.environ, LD_CRASH_AT=point, **env))
        assert r.returncode != 0, f"{point}: crash point was not reached"
        intact(point)
        c.doctor("recover", "--root", home, "--journal", j)
        intact(point + "+recover")
        c.doctor("undo", "--root", home, "--journal", j)
        assert not diff(before, snapshot(home), "all"), f"{point}: recover + undo did not restore the tree"
        assert os.stat(target).st_mode & 0o777 == 0o755, f"{point}: mode not restored"
        assert not glob.glob(os.path.join(home, QUARANTINE, "*", "*")), f"{point}: quarantine not empty"


def t_crash_points_compress(c, res):
    """Power cut after the compressed copy, after it verified, and during the undo copy: the original stays intact."""
    home, exp, j, out = c.fixture()
    before = snapshot(home)
    for verb, point in [("tier1", "compress-copied"), ("tier1", "compress-verified"), ("undo", "decompress-copied")]:
        if verb == "undo":
            c.doctor("tier1", "--root", home, "--journal", j)
        r = run_bin([c.bin, "--doctor", verb, "--catalogue", CATALOGUE, "--root", home, "--journal", j],
                           env=dict(os.environ, LD_CRASH_AT=point))
        assert r.returncode != 0, f"{point}: crash point was not reached"
        now = snapshot(home)
        assert not diff(before, now, "hash"), f"{point}: files missing or changed: {diff(before, now)[:5]}"
        c.doctor("recover", "--root", home, "--journal", j)
        assert not leftovers(home), f"{point}: temp files left after recover: {leftovers(home)}"
        c.doctor("undo", "--root", home, "--journal", j)
        assert not diff(before, snapshot(home), "all"), f"{point}: recover + undo did not restore the tree"
        assert not compressed_files(home), f"{point}: still compressed after undo"


def t_evict_cloud_roundtrip(c, res):
    """Windows: in-sync cloud files lose their local bytes, a pinned one is kept, a crash right after the journal
    intent is recovered, and undo downloads the same bytes and times. (Reading a file downloads it, so the
    local-bytes checks come before any snapshot.)"""
    home, exp, j, out = c.fixture()
    assert exp["syncRoot"], "no sync root: the test cloud provider did not start"
    cloud = [os.path.join(home, *x["path"].split("/")) for x in exp["planted"] if x["action"] == "evictCloud"]
    pinned = cloud.pop()
    subprocess.run(["attrib", "+P", pinned], check=True, capture_output=True)
    before = snapshot(home)
    r = run_bin([c.bin, "--doctor", "tier1", "--catalogue", CATALOGUE, "--root", home, "--journal", j],
                       env=dict(os.environ, LD_CRASH_AT="evict-intent"))
    assert r.returncode != 0, "evict-intent: crash point was not reached"
    c.doctor("recover", "--root", home, "--journal", j)
    c.doctor("tier1", "--root", home, "--journal", j)
    local = [p for p in cloud if allocated(p) > 0]
    if local:
        recs = [r for r in map(json.loads, open(j, encoding="utf-8")) if r.get("action") == "evictCloud"]
        why = [(os.path.basename(r.get("from") or ""), r.get("phase"), r.get("reason"), r.get("freedBytes")) for r in recs]
        raise AssertionError(f"still local after tier1: {[(os.path.basename(p), allocated(p)) for p in local]}; journal: {why}")
    assert allocated(pinned) > 0, "evicted a pinned file"
    c.doctor("undo", "--root", home, "--journal", j)
    local = [p for p in cloud if allocated(p) > 0]
    assert len(local) == len(cloud), f"undo did not download {set(cloud) - set(local)}"
    assert not diff(before, snapshot(home), "all"), f"undo did not restore the tree: {diff(before, snapshot(home), 'all')[:5]}"


def t_archive_refuses_changed_or_odd_folders(c, res):
    """A symlink inside the folder, or an existing <folder>.zip, means no archiving at all."""
    home, exp, j, out = c.fixture()
    folder = os.path.join(home, ARCHIVE)
    before = snapshot(home)
    os.symlink("/etc/hosts", os.path.join(folder, "figures", "link"))
    c.doctor("tier1", "--root", home, "--journal", j, "--archive", ARCHIVE)
    assert os.path.isdir(folder) and not os.path.exists(folder + ".zip"), "archived a folder holding a symlink"
    os.unlink(os.path.join(folder, "figures", "link"))
    open(folder + ".zip", "wb").write(b"user's own zip")
    c.doctor("tier1", "--root", home, "--journal", j, "--archive", ARCHIVE)
    assert os.path.isdir(folder) and open(folder + ".zip", "rb").read() == b"user's own zip", "overwrote an existing zip"
    now = snapshot(home)
    assert all(now[k][0] == v[0] for k, v in before.items() if k.startswith(ARCHIVE + "/")), "archive folder bytes changed"


def plan_for(c, home, out):
    plan = os.path.join(out, "plan.json")
    c.doctor("plan", "--root", home, "--out", plan)
    return plan, load(plan)


def items(plan):
    return [i for g in plan["groups"] for i in g["items"]]


def t_changed_between_plan_and_execute(c, res):
    home, exp, j, out = c.fixture()
    plan, p = plan_for(c, home, out)
    c.doctor("approve", "--plan", plan, "--journal", j, "--all", "--root", home)
    dup = os.path.join(home, "Desktop", "Contract final (1).pdf")
    st = os.stat(dup)
    with open(dup, "r+b") as f:          # same size, same mtime, different bytes
        f.write(b"CHANGED!")
    os.utime(dup, ns=(st.st_atime_ns, st.st_mtime_ns))
    inst = os.path.join(home, *[x for x in exp["planted"] if x["id"] == "installer-0"][0]["path"].split("/"))
    with open(inst, "ab") as f:          # grows and gets a fresh mtime
        f.write(b"more")
    before = snapshot(home, skip_trash=True)
    c.doctor("execute", "--root", home, "--plan", plan, "--journal", j)
    assert os.path.exists(dup) and os.path.exists(inst), "changed items were executed anyway"
    moved = [i for i in items(p) if i["action"] == "moveToTrash" and not os.path.exists(i["path"])]
    assert moved, "unchanged approved items were not executed"
    # Undo every Trash move: originals come back byte-identical.
    c.doctor("undo", "--root", home, "--journal", j)
    assert not diff(before, snapshot(home, skip_trash=True), "hash"), "undo of Trash moves did not restore"


def t_no_approval_no_action(c, res):
    home, exp, j, out = c.fixture()
    plan, p = plan_for(c, home, out)
    before = snapshot(home)
    c.doctor("execute", "--root", home, "--plan", plan, "--journal", j)
    assert not diff(before, snapshot(home), "all"), "something changed without approval"
    recs = [json.loads(l) for l in open(j)]
    assert len([r for r in recs if r["phase"] == "refused"]) == len(items(p)), "not every item was refused"


def t_approval_for_other_item_does_not_count(c, res):
    home, exp, j, out = c.fixture()
    plan, p = plan_for(c, home, out)
    first = items(p)[0]["id"]
    c.doctor("approve", "--plan", plan, "--journal", j, "--items", first, "--root", home)
    before = snapshot(home, skip_trash=True)
    c.doctor("execute", "--root", home, "--plan", plan, "--journal", j)
    changed = diff(before, snapshot(home, skip_trash=True), "hash")
    allowed = os.path.realpath(items(p)[0]["path"])
    assert changed and all(os.path.realpath(os.path.join(home, k)).startswith(allowed) for k in changed), f"unapproved items changed: {changed[:5]}"


def t_rules_cannot_approve_lossy(c, res):
    home, exp, j, out = c.fixture()
    plan, p = plan_for(c, home, out)
    c.doctor("approve", "--plan", plan, "--journal", j, "--all", "--by", "rule:test", "--root", home)
    recs = [json.loads(l) for l in open(j)]
    lossy = [r for r in recs if r["phase"] == "approval" and r["action"] in ("emptyTrash", "quitProcess", "uninstallApp")]
    assert not lossy, f"a rule approved a never-ruleable action: {lossy[:2]}"


def t_never_list_refused(c, res):
    home, exp, j, out = c.fixture()
    plan, p = plan_for(c, home, out)
    forged = []
    for t in exp["mustNotTouch"]:
        if t["why"] in ("never-list location", "inside a git repository: report only", "modified in the last 7 days"):
            path = os.path.join(home, *t["path"].split("/"))
            st = os.stat(path)
            forged.append({"id": "forged-" + t["id"], "category": "cache", "action": "moveToTrash", "tier": 2, "path": path,
                           "bytes": st.st_size, "reason": "forged", "fingerprint": {"size": st.st_size, "mtime": st.st_mtime,
                                                                                      "sha256": sha(path)}})
    # The folder that holds a never-list location is refused too (moving it would take the location with it).
    trap = next(t["path"].split("/") for t in exp["mustNotTouch"] if "/Chrome/" in t["path"])
    holder = os.path.join(home, *trap[:trap.index("Chrome")])   # .../Google, which holds Chrome's profile
    forged.append({"id": "forged-holder", "category": "cache", "action": "moveToTrash", "tier": 2, "path": holder,
                   "bytes": 1, "reason": "forged parent of a never-list location", "fingerprint": {"size": 1, "mtime": 0, "sha256": None}})
    forged.append({"id": "forged-outside", "category": "cache", "action": "moveToTrash", "tier": 2, "path": MAKE,
                   "bytes": 1, "reason": "forged outside root", "fingerprint": {"size": 1, "mtime": 0, "sha256": None}})
    p["groups"] = [{"category": "cache", "action": "moveToTrash", "label": "forged", "totalBytes": 0, "items": forged}]
    json.dump(p, open(plan, "w"))
    c.doctor("approve", "--plan", plan, "--journal", j, "--all", "--root", home)
    before = snapshot(home)
    make_sha = sha(MAKE)
    c.doctor("execute", "--root", home, "--plan", plan, "--journal", j)
    assert not diff(before, snapshot(home), "all"), "a never-list item was touched"
    assert sha(MAKE) == make_sha and os.path.exists(MAKE)
    recs = [json.loads(l) for l in open(j)]
    assert sum(r["phase"] == "refused" for r in recs) == len(forged), "not every forged item was refused"


def t_traps_untouched(c, res):
    home, exp, j, out = c.fixture()
    before = snapshot(home)
    c.doctor("tier1", "--root", home, "--journal", j, "--archive", ARCHIVE)
    plan, p = plan_for(c, home, out)
    c.doctor("approve", "--plan", plan, "--journal", j, "--all", "--root", home)
    c.doctor("execute", "--root", home, "--plan", plan, "--journal", j)
    now = snapshot(home)
    comp = set(os.path.relpath(f, home).replace(os.sep, "/") for f in compressed_files(home))
    for t in exp["mustNotTouch"]:
        for k in [k for k in before if k == t["path"] or k.startswith(t["path"] + "/")]:
            assert k in now and now[k][:3] == before[k][:3], f"trap {t['id']} changed: {k}"
            assert k not in comp, f"trap {t['id']} was compressed: {k}"


def t_summary_sends_no_user_names(c, res):
    """The Claude summary's prompt names startup items and processes, never the user's files or folders."""
    home, exp, j, out = c.fixture()
    burner = [x for x in exp["planted"] if x["kind"] == "busy-process"]
    procs = [subprocess.Popen([sys.executable, os.path.join(home, *b["path"].split("/"))]) for b in burner]
    try:
        time.sleep(3)
        report = os.path.join(out, "report-summary.json")
        c.doctor("scan", "--root", home, "--archive", ARCHIVE, "--out", report)
    finally:
        for pr in procs:
            pr.kill()
    prompt = c.doctor("summary-prompt", "--report", report).stdout
    facts = json.loads(prompt[prompt.index("{"):])
    assert facts["startupItems"] and facts["processes"] and facts["findings"], f"summary facts are empty: {facts}"
    allowed = {"Library", "Caches", "LaunchAgents", "Documents", "Downloads", "Desktop", "Projects", "Code",
               "AppData", "Local", "Roaming", ".Trash"}
    named = set()
    for f in load(report)["findings"]:   # what the summary may name: process/job names and their programs
        named |= {f.get("processName") or "", os.path.basename(f.get("executable") or "")}
    allowed |= named
    names = set()
    for r, ds, fs in os.walk(home):
        names |= {n for n in ds + fs if len(n) >= 4}
    # A startup item's own name may contain a folder's (Run value "FixtureFishyUpdater", folder "FishyUpdater"):
    # take the permitted names out first, then any user name still present is a leak.
    rest = prompt
    for a in sorted(filter(None, named), key=len, reverse=True):
        rest = rest.replace(a, " ")
    leaked = sorted(n for n in names - allowed if n in rest)
    assert not leaked, f"summary prompt contains user file/folder names: {leaked[:5]}"
    assert os.path.basename(home) not in prompt and home not in prompt and json.dumps(home)[1:-1] not in prompt, \
        "summary prompt contains the home path"


def t_checkup_respects_switches(c, res):
    """The scheduled checkup runs only the Tier 1 kinds the user switched on, with the user's N-day thresholds."""
    home, exp, j, out = c.fixture()
    before, b0 = snapshot(home), blocks(home)
    r = json.loads(c.doctor("checkup", "--root", home, "--journal", j, "--dir", os.path.join(out, "c1"), "--tier1",
                            "--archive", ARCHIVE).stdout)
    assert r["tier1"]["done"] == 0 and r["tier1Disabled"] > 0, f"switches off but tier1 ran: {r}"
    assert not diff(before, snapshot(home), "all") and blocks(home) == b0, "checkup with every switch off changed files"
    for f in ("report.json", "plan.json", "outcome.json"):
        assert os.path.isfile(os.path.join(r["folder"], f)), f"checkup wrote no {f}"
    assert r["planItems"] == sum(len(g["items"]) for g in load(os.path.join(r["folder"], "plan.json"))["groups"]) > 0
    # Compression only: the opted-in folder stays a folder.
    r = json.loads(c.doctor("checkup", "--root", home, "--journal", j, "--dir", os.path.join(out, "c2"), "--tier1", "compress",
                            "--archive", ARCHIVE).stdout)
    acts = {x["action"] for x in map(json.loads, open(j)) if x["phase"] in ("intent", "done")}
    assert r["tier1"]["done"] > 0 and acts == {"compress"}, f"compress-only checkup did {acts}"
    assert os.path.isdir(os.path.join(home, ARCHIVE)), "archiving ran while switched off"
    # Archiving on, but the folder (900 days old) is younger than the user's threshold: left alone; then archived.
    c.doctor("checkup", "--root", home, "--journal", j, "--dir", os.path.join(out, "c3"), "--tier1", "archiveFolder",
             "--archive", ARCHIVE, "--threshold", "archiveDays=2000")
    assert os.path.isdir(os.path.join(home, ARCHIVE)), "archived a folder younger than archiveDays"
    c.doctor("checkup", "--root", home, "--journal", j, "--dir", os.path.join(out, "c4"), "--tier1", "archiveFolder",
             "--archive", ARCHIVE, "--threshold", "archiveDays=365")
    assert os.path.isfile(os.path.join(home, ARCHIVE + ".zip")), "opted-in folder older than archiveDays was not archived"
    c.doctor("undo", "--root", home, "--journal", j)
    assert not diff(before, snapshot(home), "all"), "undo after checkups did not restore the tree"


def t_plans_carry_over_and_rules(c, res):
    """Undecided items carry over (once each) and age out after 30 days; declined items stay off; rules only for
    reversible kinds (a hand-edited rules.json can't widen them); ruled items run through execute() and undo."""
    import datetime
    home, exp, j, out = c.fixture()
    d = os.path.join(out, "checkups")
    def checkup(*a, at=None):
        extra = ["--checkup", at] if at else []
        r = json.loads(c.doctor("checkup", "--root", home, "--journal", j, "--dir", d, "--tier1", *extra, *a).stdout)
        return r, load(os.path.join(r["folder"], "plan.json"))
    def ids(p, cat=None):
        return [i["id"] for i in items(p) if cat is None or i["category"] == cat]
    r1, p1 = checkup()
    pend = load(os.path.join(d, "pending.json"))["items"]
    assert r1["planItems"] == len(pend) == len({e["item"]["id"] for e in pend}) > 0, "pending is not one entry per plan item"
    # Decline one item: it stays off the next plan while unchanged, and the rest are not duplicated.
    gone = ids(p1, "duplicate")[0]
    c.doctor("decline", "--root", home, "--plan", os.path.join(r1["folder"], "plan.json"), "--journal", j, "--items", gone)
    r2, p2 = checkup()
    assert gone not in ids(p2) and r2["suppressed"] >= 1, "a declined item came back unchanged"
    assert sorted(ids(p2)) == sorted(set(ids(p1)) - {gone}), "second plan is not the first minus the declined item"
    # Installers the scan no longer flags (higher threshold) are carried over, then age out after 30 days.
    inst = set(ids(p1, "downloads-installer"))
    assert inst, "fixture has no installers"
    r3, p3 = checkup("--threshold", "installerAgeDays=5000")
    assert inst <= set(ids(p3)) and r3["carried"] >= len(inst), "undecided installers did not carry over"
    later = (datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(days=31)).strftime("%Y-%m-%dT%H:%M:%S.000Z")
    r4, p4 = checkup("--threshold", "installerAgeDays=5000", at=later)
    assert not inst & set(ids(p4)) and r4["expired"] >= len(inst), "carried items older than 30 days did not age out"
    # Rules: never for emptying the Trash or quitting processes, and a hand-edited rules.json can't add them.
    for cat in ("trash", "busy-process", "cold-compressible"):
        assert c.doctor("rule-add", "--root", home, "--dir", d, "--category", cat, check=False).returncode != 0, f"rule for {cat} allowed"
    c.doctor("rule-add", "--root", home, "--dir", d, "--category", "cache")
    rb = load(os.path.join(d, "rules.json"))
    rb["rules"] += [{"id": "r-forged", "category": "trash", "created": "x"}, {"id": "r-forged2", "category": "busy-process", "created": "x"}]
    json.dump(rb, open(os.path.join(d, "rules.json"), "w"))
    caches = [i for i in items(p2) if i["category"] == "cache"]
    assert caches, "fixture has no cache items"
    before = snapshot(home, skip_trash=True)
    r5, p5 = checkup()
    assert (r5.get("ruled") or {}).get("done") == len(caches), f"cache rule did not run: {r5}"
    assert not ids(p5, "cache"), "ruled items left on the plan"
    for cat in ("trash", "busy-process"):
        assert ids(p5, cat) or not ids(p2, cat), f"a forged rule approved {cat}"
    recs = [json.loads(l) for l in open(j)]
    by_rule = [x for x in recs if x["phase"] == "approval" and (x.get("by") or "").startswith("rule:")]
    assert by_rule and all(x["action"] == "moveToTrash" for x in by_rule), "a rule approved something other than a Trash move"
    assert all(not x["by"].startswith("rule:r-forged") for x in by_rule), "a forged rule was used"
    after = snapshot(home, skip_trash=True)
    moved = set(diff(before, after, "hash"))
    touched = [t["path"] for t in exp["mustNotTouch"] if any(m == t["path"] or m.startswith(t["path"] + "/") for m in moved)]
    assert not touched, f"rule run touched {touched}"
    c.doctor("undo", "--root", home, "--journal", j)
    assert not diff(before, snapshot(home, skip_trash=True), "hash"), "undo did not restore the ruled Trash moves"


def t_report_window_decisions(c, res):
    """The report window's buttons (decide/history/undo-checkup): "always" is refused as a whole for lossy kinds;
    decline touches nothing; approve runs only the chosen items, once; decided items leave pending; History undoes."""
    home, exp, j, out = c.fixture()
    d = os.path.join(out, "checkups")
    r = json.loads(c.doctor("checkup", "--root", home, "--journal", j, "--dir", d, "--tier1").stdout)
    plan = os.path.join(r["folder"], "plan.json")
    p = load(plan)
    def cat(name):
        return [i["id"] for i in items(p) if i["category"] == name]
    def decide(decision, ids, check=True):
        return c.doctor("decide", "--root", home, "--journal", j, "--dir", d, "--plan", plan, "--decision", decision,
                        "--items", ",".join(ids), check=check)
    def journal():   # the Windows journal file only appears with its first record
        return [json.loads(l) for l in open(j)] if os.path.exists(j) else []
    before = snapshot(home, skip_trash=True)
    n = len(journal())
    lossy = cat("trash") or cat("busy-process")
    mixed = lossy + cat("cache")
    assert lossy and cat("cache"), "fixture lacks Trash/busy-process or cache items"
    assert decide("always", mixed, check=False).returncode != 0, "always accepted a group with a lossy kind"
    assert len(journal()) == n and not os.path.exists(os.path.join(d, "rules.json")), "a refused 'always' still recorded something"
    dup = cat("duplicate")[0]
    decide("decline", [dup])
    assert not diff(before, snapshot(home, skip_trash=True), "hash"), "decline touched files"
    cache = cat("cache")
    o = json.loads(decide("approve", cache).stdout)
    assert o["outcome"]["done"] == len(cache) and o["decided"] == len(cache), f"approve did not run exactly the chosen items: {o}"
    moved = set(diff(before, snapshot(home, skip_trash=True), "hash"))
    paths = {os.path.relpath(i["path"], home).replace(os.sep, "/") for i in items(p) if i["id"] in cache}
    assert moved and all(any(m == q or m.startswith(q + "/") for q in paths) for m in moved), f"approve moved other files: {moved - paths}"
    assert json.loads(decide("approve", cache).stdout)["decided"] == 0, "approving twice ran again"
    pend = {e["item"]["id"] for e in load(os.path.join(d, "pending.json"))["items"]}
    assert not pend & (set(cache) | {dup}), "decided items still pending"
    inst = cat("downloads-installer")
    o = json.loads(decide("always", inst).stdout)
    assert o["rules"] and o["outcome"]["done"] == len(inst), f"always did not add a rule and run: {o}"
    assert [x["category"] for x in load(os.path.join(d, "rules.json"))["rules"]] == ["downloads-installer"], "wrong rules"
    h = json.loads(c.doctor("history", "--journal", j).stdout)
    entries = [e for hc in h for e in hc["entries"] if hc["checkup"] == p["checkup"]]
    assert len(entries) == len(cache) + len(inst) and all(e["canUndo"] for e in entries), "History lists the wrong entries"
    assert all(e["by"] == "user" for e in entries), "History entries lack the user's approval"
    c.doctor("undo-checkup", "--root", home, "--journal", j, "--checkup", p["checkup"])
    assert not diff(before, snapshot(home, skip_trash=True), "hash"), "undo of the checkup did not restore everything"
    h = json.loads(c.doctor("history", "--journal", j).stdout)
    assert all(e["undone"] and not e["canUndo"] for hc in h for e in hc["entries"] if hc["checkup"] == p["checkup"]), "undone entries still undoable"


class HostPipe:
    """Chrome's side of native messaging: 4-byte little-endian length + UTF-8 JSON. Events (no id) are kept apart."""
    def __init__(self, binary, env):
        self.p = subprocess.Popen([binary, "--host"], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, env=env)
        self.n, self.events = 0, []

    def call(self, type_, **payload):
        self.n += 1
        body = json.dumps({"id": self.n, "type": type_, **payload}).encode()
        self.p.stdin.write(len(body).to_bytes(4, "little") + body)
        self.p.stdin.flush()
        while True:
            head = self.p.stdout.read(4)
            assert len(head) == 4, f"host closed the pipe during {type_}"
            m = json.loads(self.p.stdout.read(int.from_bytes(head, "little")))
            if m.get("id") == self.n:
                return m
            self.events.append(m)

    def close(self):
        self.p.stdin.close()
        try:
            self.p.wait(timeout=30)
        except subprocess.TimeoutExpired:
            self.p.kill()


def t_extension_page_drives_host(c, res):
    """Windows: what doctor.html sends to the native host. Settings clamp thresholds and switching on waits for the
    next slot; "Check up now" with every switch off changes no file; approve runs only the ticked items, once;
    a bad checkup id is refused; History undoes the checkup."""
    home, exp, j, out = c.fixture()
    data = os.path.join(out, "doctor-data")
    h = HostPipe(c.bin, merged({"AISTACK_DOCTOR_HOME": home, "AISTACK_DOCTOR_DATA": data}))
    try:
        s = h.call("doctor.settings")
        assert s["ok"] and not s["settings"]["enabled"], f"Doctor not off by default: {s}"
        want = dict(s["settings"], enabled=True, compress=False, evictCloud=False, archive=False, thresholds={"coldDays": 1, "bogus": 3})
        s = h.call("doctor.settings.set", settings=want)["settings"]
        assert s["thresholds"] == {"coldDays": 7} and s["lastRun"], f"settings not sanitised: {s}"
        before = snapshot(home, skip_trash=True)
        r = h.call("doctor.checkNow", wait=True)
        assert r["ok"] and r["result"]["tier1"]["done"] == 0, f"checkup with switches off: {r}"
        assert not diff(before, snapshot(home, skip_trash=True), "hash"), "checkup with every switch off changed files"
        ups = h.call("doctor.checkups")["checkups"]
        assert len(ups) == 1, f"checkups: {ups}"
        cid = ups[0]["checkup"]
        g = h.call("doctor.get", checkup=cid)
        assert g["ok"] and not g["decided"] and g["plan"]["groups"], f"get: {g}"
        cache = [i["id"] for grp in g["plan"]["groups"] for i in grp["items"] if i["category"] == "cache"]
        assert cache, "fixture lacks cache items"
        o = h.call("doctor.decide", checkup=cid, decision="approve", items=cache)
        assert o["ok"] and o["result"]["outcome"]["done"] == len(cache), f"approve: {o}"
        assert h.call("doctor.decide", checkup=cid, decision="approve", items=cache)["result"]["decided"] == 0, "approved twice"
        assert set(h.call("doctor.get", checkup=cid)["decided"]) == set(cache), "decided items not reported"
        assert not h.call("doctor.get", checkup="../../x")["ok"], "a path-like checkup id was accepted"
        hist = [e for hc in h.call("doctor.history")["history"] for e in hc["entries"]]
        assert len(hist) == len(cache) and all(e["canUndo"] for e in hist), f"history: {hist}"
        assert h.call("doctor.undoCheckup", checkup=cid)["ok"], "undo failed"
        assert not diff(before, snapshot(home, skip_trash=True), "hash"), "undo of the checkup did not restore everything"
    finally:
        h.close()
    c.journals.append(os.path.join(data, "journal.jsonl"))


def t_schedule_once_per_slot(c, res):
    """Weekly slot Sunday 11:00 UTC (2026-11-01 is a Sunday); at most one approval request a day."""
    def due(now, last=None, notified=None):
        a = ["--now", now, "--weekday", "1", "--hour", "11"] + (["--last", last] if last else []) + \
            (["--notified", notified] if notified else [])
        return json.loads(c.doctor("due", *a).stdout)
    assert due("2026-11-01T11:30:00Z", "2026-10-25T11:05:00Z")["due"], "slot passed, last run a week ago: not due"
    assert not due("2026-11-01T10:59:00Z", "2026-10-25T11:05:00Z")["due"], "due before the slot"
    assert not due("2026-11-01T11:30:00Z", "2026-11-01T11:10:00Z")["due"], "due twice in one slot"
    assert due("2026-11-04T03:00:00Z", "2026-10-25T11:05:00Z")["due"], "missed slot (asleep) does not catch up"
    assert not due("2026-11-01T11:00:00Z")["due"], "just switched on: runs before its first slot"
    assert not due("2026-11-04T03:00:00Z", notified="2026-11-04T01:00:00Z")["mayNotify"], "two approval requests in a day"
    assert due("2026-11-04T03:00:00Z", notified="2026-11-03T23:00:00Z")["mayNotify"], "no request the next day"


def t_writes_need_root(c, res):
    r = c.doctor("tier1", "--journal", os.path.join(c.work, "x.jsonl"), check=False)
    assert r.returncode != 0, "tier1 ran without --root"
    r = c.doctor("checkup", "--journal", os.path.join(c.work, "x.jsonl"), "--dir", c.work, check=False)
    assert r.returncode != 0, "checkup ran without --root"
    r = c.doctor("execute", "--plan", "/nonexistent", "--journal", os.path.join(c.work, "x.jsonl"), check=False)
    assert r.returncode != 0, "execute ran without --root"


def planted(c, res):
    home, exp, j, out = c.fixture()
    burner = [x for x in exp["planted"] if x["kind"] == "busy-process"]
    procs = [subprocess.Popen([sys.executable, os.path.join(home, *b["path"].split("/"))]) for b in burner]
    try:
        time.sleep(3)
        report = os.path.join(out, "report.json")
        # The real ClamAV: the user-installed one, or CI's copy (LD_REAL_CLAMSCAN); the other tests switch it off for speed.
        c.doctor("scan", "--root", home, "--archive", ARCHIVE, "--out", report, env={"LD_CLAMSCAN": os.environ.get("LD_REAL_CLAMSCAN")})
    finally:
        for pr in procs:
            pr.kill()
    rep = load(report)
    found = rep["findings"]
    for note in rep.get("skipped", []):
        print("scan skipped:", note)   # e.g. why the malware scanner didn't run
    real = os.path.realpath(home)
    got = set()
    for f in found:
        rel = os.path.relpath(os.path.realpath(f["path"]), real).replace(os.sep, "/") if f["path"] else ""
        got.add((f["action"], rel))
        if f.get("processName"):
            got.add((f["action"], "proc:" + f["processName"]))
    missing = [x["id"] for x in exp["planted"]
               if (x["action"], x["path"]) not in got and (x["action"], "proc:" + x.get("processName", "")) not in got]
    res["planted"] = {"found": len(exp["planted"]) - len(missing), "total": len(exp["planted"]), "missing": missing}


SAFETY = [t_tier1_roundtrip, t_kill_during_tier1, t_crash_points_archive, t_archive_refuses_changed_or_odd_folders,
          t_crash_points_compress if WINDOWS else t_quarantine_roundtrip_and_crash_points,
          t_changed_between_plan_and_execute, t_no_approval_no_action,
          t_approval_for_other_item_does_not_count, t_rules_cannot_approve_lossy, t_never_list_refused, t_traps_untouched,
          t_writes_need_root, t_summary_sends_no_user_names, t_checkup_respects_switches, t_schedule_once_per_slot,
          t_plans_carry_over_and_rules] + ([t_evict_cloud_roundtrip] if WINDOWS else []) + [t_report_window_decisions] + ([t_extension_page_drives_host] if WINDOWS else [])


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--bin", required=True)
    ap.add_argument("--work", required=True)
    ap.add_argument("--out", required=True)
    a = ap.parse_args()
    if os.path.exists(a.work):
        shutil.rmtree(a.work)
    os.makedirs(a.work)
    # ClamAV loads its whole signature database on every scan (~30 s); only the planted-issue scan uses the real one.
    os.environ["LD_CLAMSCAN"] = "off"
    c, res, tests = Ctx(a.bin, a.work), {}, {}
    for t in SAFETY:
        try:
            t(c, res)
            tests[t.__name__] = "pass"
        except Exception as e:
            tests[t.__name__] = f"FAIL: {e}"
            traceback.print_exc()
        print(f"{t.__name__}: {tests[t.__name__]}", flush=True)
    try:
        planted(c, res)
    except Exception:
        traceback.print_exc()
    for pr in c.providers:
        pr.kill()
    r = c.doctor("verify-journal", *[j for j in c.journals if os.path.exists(j)], check=False)
    res["unapprovedDestructive"] = json.loads(r.stdout)["unapprovedDestructive"]
    res["safety"] = {"passed": sum(v == "pass" for v in tests.values()), "total": len(tests), "tests": tests}
    res["platform"] = sys.platform
    json.dump(res, open(a.out, "w"), indent=1)
    print(json.dumps({k: v for k, v in res.items() if k != "safety"} | {"safety": f"{res['safety']['passed']}/{res['safety']['total']}"}))
    return 0 if res["safety"]["passed"] == res["safety"]["total"] and res["unapprovedDestructive"] == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
