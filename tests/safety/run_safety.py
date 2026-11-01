#!/usr/bin/env python3
"""Laptop Doctor safety suite. Drives the headless CLI against fresh messy-laptop fixtures.

    python3 tests/safety/run_safety.py --bin <AIStack binary> --work <scratch dir> --out doctor-results.json

Every write goes to fixtures under --work. Prints one line per test and writes doctor-results.json:
  safety {passed,total,tests}, planted {found,total,missing}, tier1 {freed,eligible}, unapprovedDestructive.
"""
import argparse, glob, hashlib, json, os, random, shutil, signal, stat, subprocess, sys, time, traceback

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
MAKE = os.path.join(REPO, "fixtures", "make_messy_laptop.py")
CATALOGUE = os.path.join(REPO, "shared", "doctor-catalogue.json")
ARCHIVE = "Projects/Thesis 2019"


class Ctx:
    def __init__(self, binary, work):
        self.bin, self.work, self.n, self.journals = os.path.abspath(binary), os.path.abspath(work), 0, []

    def fixture(self):
        self.n += 1
        out = os.path.join(self.work, f"fx{self.n}")
        subprocess.run([sys.executable, MAKE, out], check=True, stdout=subprocess.DEVNULL)
        exp = json.load(open(os.path.join(out, "expected.json")))
        journal = os.path.join(out, "journal.jsonl")
        self.journals.append(journal)
        return os.path.join(out, "home"), exp, journal, out

    def doctor(self, verb, *args, check=True):
        cmd = [self.bin, "--doctor", verb, "--catalogue", CATALOGUE, *args]
        r = subprocess.run(cmd, capture_output=True, text=True, timeout=300)
        if check and r.returncode != 0:
            raise AssertionError(f"{verb} failed rc={r.returncode}: {r.stderr.strip()[:400]}")
        return r


def sha(p):
    h = hashlib.sha256()
    with open(p, "rb") as f:
        for c in iter(lambda: f.read(1 << 20), b""):
            h.update(c)
    return h.hexdigest()


def snapshot(home, skip_trash=False):
    snap = {}
    for root, dirs, files in os.walk(home):
        for f in files:
            p = os.path.join(root, f)
            rel = os.path.relpath(p, home).replace(os.sep, "/")
            if skip_trash and rel.startswith(".Trash/"):
                continue
            st = os.lstat(p)
            snap[rel] = (sha(p), st.st_mtime_ns // 1000, int(getattr(st, "st_birthtime", 0) * 1e6))
    return snap


def blocks(home):
    return sum(os.lstat(os.path.join(r, f)).st_blocks * 512 for r, _, fs in os.walk(home) for f in fs)


def compressed_files(home):
    flag = getattr(stat, "UF_COMPRESSED", 0x20)
    return [os.path.join(r, f) for r, _, fs in os.walk(home) for f in fs
            if getattr(os.lstat(os.path.join(r, f)), "st_flags", 0) & flag]


def leftovers(home):
    return [os.path.join(r, f) for r, _, fs in os.walk(home) for f in fs if ".ld-" in f]


def diff(a, b, what="hash"):
    """Names of files missing or different between snapshots (what: hash = bytes only, all = bytes + times)."""
    idx = 1 if what == "hash" else 3
    bad = [k for k in a if k not in b or a[k][:idx] != b[k][:idx]]
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
    c.doctor("undo", "--root", home, "--journal", j)
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
    inst = os.path.join(home, "Downloads", "SomeApp-Installer-0.dmg")
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


def t_writes_need_root(c, res):
    r = c.doctor("tier1", "--journal", os.path.join(c.work, "x.jsonl"), check=False)
    assert r.returncode != 0, "tier1 ran without --root"
    r = c.doctor("execute", "--plan", "/nonexistent", "--journal", os.path.join(c.work, "x.jsonl"), check=False)
    assert r.returncode != 0, "execute ran without --root"


def planted(c, res):
    home, exp, j, out = c.fixture()
    burner = [x for x in exp["planted"] if x["kind"] == "busy-process"]
    procs = [subprocess.Popen([sys.executable, os.path.join(home, *b["path"].split("/"))]) for b in burner]
    try:
        time.sleep(3)
        report = os.path.join(out, "report.json")
        c.doctor("scan", "--root", home, "--archive", ARCHIVE, "--out", report)
    finally:
        for pr in procs:
            pr.kill()
    found = load(report)["findings"]
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


SAFETY = [t_tier1_roundtrip, t_kill_during_tier1, t_changed_between_plan_and_execute, t_no_approval_no_action,
          t_approval_for_other_item_does_not_count, t_rules_cannot_approve_lossy, t_never_list_refused, t_traps_untouched,
          t_writes_need_root]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--bin", required=True)
    ap.add_argument("--work", required=True)
    ap.add_argument("--out", required=True)
    a = ap.parse_args()
    if os.path.exists(a.work):
        shutil.rmtree(a.work)
    os.makedirs(a.work)
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
    r = c.doctor("verify-journal", *[j for j in c.journals if os.path.exists(j)], check=False)
    res["unapprovedDestructive"] = json.loads(r.stdout)["unapprovedDestructive"]
    res["safety"] = {"passed": sum(v == "pass" for v in tests.values()), "total": len(tests), "tests": tests}
    res["platform"] = sys.platform
    json.dump(res, open(a.out, "w"), indent=1)
    print(json.dumps({k: v for k, v in res.items() if k != "safety"} | {"safety": f"{res['safety']['passed']}/{res['safety']['total']}"}))
    return 0 if res["safety"]["passed"] == res["safety"]["total"] and res["unapprovedDestructive"] == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
