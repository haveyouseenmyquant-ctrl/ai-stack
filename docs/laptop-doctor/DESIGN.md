# Laptop Doctor — design

Status: draft 1 (2026-11-01). Spec: `BRIEF.md` (kept in the project folder, not in the repo).
This file records the decisions the brief leaves open, and the reasons for them.

## 1. Shape of the feature

```
                 shared/doctor-catalogue.json   (categories, OS paths, tiers, thresholds, never-list)
                          │                 │
       macOS: Sources/AIStack/Doctor/      Windows: windows/helper/Doctor/
       ─────────────────────────────       ───────────────────────────────
       Catalogue   (loads JSON)            Catalogue
       Scanner     (Tier 0 only)           Scanner
       Sampler     (14-day rolling)        Sampler
       Planner     → Report + Plan         Planner
       Executor    (only writer)           Executor
       Journal     (JSONL, fsync)          Journal
       Guard       (never-list, tiers)     Guard
       Scheduler   (NSBackgroundActivity)  Scheduler (per-user Task Scheduler task)
       UI          (SwiftUI window)        UI (extension page doctor.html via native messaging)
```

Both sides expose the same headless CLI, which is what the fixtures, the safety suite and CI drive:

```
AIStack --doctor scan    --root <dir> [--dry-run] --out report.json
AIStack --doctor tier1   --root <dir> --journal <file>
AIStack --doctor plan    --root <dir> --out plan.json
AIStack --doctor execute --plan plan.json --approvals approvals.json --journal <file>
AIStack --doctor undo    --journal <file> [--entry <id> | --checkup <id>]
AIStack --doctor verify-journal <file>          # counts unapproved Tier 2 executions
AIStackHelper.exe --doctor …                    # identical verbs on Windows
```

`--root` confines every write to one directory tree; it is how tests point the tool at a fixture.
Without `--root` the tool works on the real home folder, and in development I only ever run that with `--dry-run`.
In dry-run, the Executor is never built: the CLI calls the Scanner and Planner and exits, so dry-run cannot write anything. That's enforced by the code structure, not by a flag checked further down.

**Decision: the Executor is the only code that touches user files, and it accepts only typed actions.**
Each action type is an enum case with a static `tier`. Swift: `enum DoctorAction { case evictCloud, compress, archiveFolder, quarantine, moveToTrash, emptyTrash, disableStartupItem, quitProcess, uninstallApp, … }` with `var tier: Tier { switch … }`. There is no `default:` branch, so a new case won't compile until someone gives it a tier. C# mirrors it with a switch expression and `<WarningsAsErrors>CS8509</WarningsAsErrors>`. Every action passes three gates, in this order:
1. `Guard.check(path)`: the never-list (section 5). It runs on every path for every tier and rejects before anything else happens.
2. Tier 2 only: an approval record matching the item's `planItemId` and fingerprint must be in the journal. If it isn't, the action is refused and journalled as `refused`.
3. Journal `intent` entry written and fsynced. Only then does the action start.

## 2. Tiers of every action type

| Action type | Tier | Why |
|---|---|---|
| scan, measure, sample, codesign/spctl/Authenticode checks, Defender status, `du`-style sizing | 0 | read-only |
| write report/plan/journal/sampler data in AI Stack support folder | 0 | own folder |
| `evictCloud`: evict an uploaded, in-sync, not-pinned cloud file unopened ≥ N days | 1 | bytes stay in the user's cloud; undo = download |
| `compress`: APFS decmpfs / NTFS LZNT1 in place | 1 | same path, same bytes; undo = decompress |
| `archiveFolder`: zip an **opted-in** folder, SHA-256 verify, then remove the original | 1 | bytes stay local in the zip; undo = extract |
| `quarantine` (macOS ClamAV hit → AI Stack quarantine folder, chmod −x) | 1 | bytes kept; undo = restore path + mode |
| `reportDefenderQuarantine` (Windows) | 0 | Defender acted; we only link to it |
| `moveToTrash` (caches, installers, DerivedData, simulators, iOS backups, duplicates, node_modules, old recordings) | 2, can become a rule | reversible while in Trash |
| `disableStartupItem` (LaunchAgent via `launchctl disable`/`bootout` + `Disabled` override, login item, Run key value moved to a disabled store, scheduled task disable) | 2, can become a rule | reversible, nothing deleted |
| `disableBrowserExtension` | 2, can become a rule | reversible |
| `emptyTrash` | 2, **never a rule** | lossy |
| `quitProcess` / `killProcess` | 2, **never a rule** | possible unsaved work |
| `uninstallApp` | 2, **never a rule** | lossy |
| `moveFile` (incl. into a cloud folder) | 2, never a rule in v1 | changes location; the brief lists it as Tier 2 |
| `deleteDockerImages` (`docker image prune`) | 2, never a rule | goes through Docker, not the Trash |
| anything unclassified | 2 | "when in doubt, it's Tier 2" |

Compression and eviction run only after the Guard and the Tier 1 eligibility checks: cold for N days (default 60), not open in any process (no `lsof` shell-out: `proc_listpidspath` on macOS, Restart Manager `RmGetList` on Windows), not signed code / bundle / system path, not hard-linked (`nlink > 1` → skip, because rename would split the link), and not already a compressed format (extension list plus a sampled 64 KiB LZFSE/LZNT1 trial that must shrink by ≥ 25%).

## 3. How each Tier 1 action stays crash-safe

Every Tier 1 action is a **copy → verify → atomic rename** sequence, so at any instant either the original is intact or both are.

**Compression (macOS), verified on macOS 26.5.2 / APFS (2026-11-01, scratch fixture):**
1. `ditto --hfsCompression <f> <dir>/.<f>.ld-<id>` makes a compressed sibling. Measured: 13.5 MB text file went from 26 376 to 9 240 blocks, `ls -lO` shows `compressed`, and the SHA-256 is identical. **ditto keeps mtime and xattrs but sets birthtime := mtime**, so step 2 is required.
2. Restore birthtime (`setattrlist ATTR_CMN_CRTIME`; `URLResourceValues.creationDate` in Swift), plus mode, flags and ACL from the original, then re-hash the sibling.
3. `rename(2)` the sibling over the original (atomic on the same volume). If we crash before this, the original is untouched and a leftover `.ld-<id>` sibling gets cleaned up on the next run using the journal.
4. Undo: **`ditto --nohfsCompression` does NOT decompress** (measured: the result was still `compressed`), and `cp` clones on APFS, which keeps the compression. Undo therefore streams the bytes into a fresh sibling (`read`/`write`, no clonefile), restores mtime and birthtime, checks the hash, then renames. Verified: the result has `flags=-`, the same SHA-256, the original mtime and the original birthtime.
5. Check: `stat` flags show `UF_COMPRESSED` and the block count drops. The freed bytes are what the journal records: `(blocks_before − blocks_after) × 512`.

**Compression (Windows):** NTFS compresses in place (`FSCTL_SET_COMPRESSION` = `compact /c`). The data never moves at the API level, and NTFS is journalled. The file stays intact if the process is killed halfway (the per-file attribute is set and chunks are compressed by the FS). Timestamps: `compact` doesn't change LastWrite or Creation time; the safety suite checks this on `windows-latest`. Undo is `compact /u` (`COMPRESSION_FORMAT_NONE`). Unverified until CI runs.

**Cloud eviction (macOS), what I verified:**
- `brctl evict` / `brctl download` **no longer exist** on macOS 26 (`brctl` now only has diagnose/log/dump/status/accounts/quota/monitor). The brief's CLI option is gone.
- The supported route is `FileManager.evictUbiquitousItem(at:)`. Eligibility comes from `URLResourceValues`: `ubiquitousItemIsUploaded`, `ubiquitousItemDownloadingStatus == .current`, `!ubiquitousItemHasUnresolvedConflicts`, and not uploading or downloading. Undo is `startDownloadingUbiquitousItem(at:)`, polled until the status is current.
- Third-party File Provider apps (OneDrive, Google Drive, Dropbox under `~/Library/CloudStorage`) honour the same `evictUbiquitousItem` call that Finder's "Remove Download" uses. **Not verified yet**: this Mac's agent account has no iCloud or CloudStorage folders, and neither does `macos-latest`. Pinned ("Always keep") detection isn't verified either. Until it is, a file whose pin state can't be read is skipped. That makes macOS cloud eviction **off in tests**, and the fixture's macOS Tier 1 eligible bytes don't include cloud files.
- Evicting doesn't lose data by definition: we only evict files the provider says are uploaded. Undo needs the network. The UI says so.

**Cloud eviction (Windows), plan:** for OneDrive and other Cloud Files providers, read `FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS`, the pin state (`CF_PIN_STATE` via `CfGetPlaceholderInfo`) and the in-sync state. Evict with `CfDehydratePlaceholder` (= `attrib +U -P`), only when the state is in-sync and not pinned. On CI the fixture registers its **own test sync root** (`CfRegisterSyncRoot` + a tiny fetch-data callback that serves bytes from a backing folder). That gives a real synced-cloud folder whose eviction and re-hydration the safety suite can check byte-for-byte.

**Folder archive (implemented, session 3):** journal `intent` with a manifest of every entry (relative path, size, SHA-256, mtime, birthtime, mode) → `ditto -c -k --sequesterRsrc` into the hidden sibling `.<name>.ld-<id>.zip` → extract it again into `.<name>.ld-<id>-v` and compare the entry list, kinds, sizes and every SHA-256 with the manifest → re-check the folder's sizes and mtimes (unchanged while zipping) → journal `verified` with the zip's own hash → rename to `<name>.zip` (skip the folder if that name is taken) → rename the folder aside to `.<name>.ld-<id>-d` → remove it → journal `done`. Folders holding symlinks, hard links, special files or files open in another process are skipped. Undo extracts into `.<name>.ld-<id>-u`, verifies against the manifest, restores modes and both timestamps deepest-first, renames into place and only then deletes the zip. `recover` handles every intermediate state: folder present → our zip (identified by the `verified` hash) is dropped; folder aside + verified zip in place → finish the removal and record `done`. The safety suite kills the process at each of these seven points (`LD_CRASH_AT`, honoured only with `--root`).
**Changed from the first draft:** no 7-day holding area. The brief says to remove the original once the zip verifies, and renaming the folder aside before deleting it already means an interrupted removal never leaves a half-deleted folder in place — the verified zip holds every byte. A holding area would also make the freed space appear a week late, which the user (and the KPI) would read as "nothing happened". Undo is still one click while the zip exists.

**Quarantine (macOS ClamAV, implemented session 4):** the `clamav` matcher runs the user's own `clamscan` (`/opt/homebrew`, `/usr/local` or `/opt/local`; never bundled) with `-r --infected` over `~/Downloads` and `~/Desktop`. No ClamAV → the scan says so in `skipped` and nothing else happens; a clamscan error (exit ≥ 2, e.g. no signature database) is reported the same way. A hit the Guard refuses to touch (e.g. modified in the last 7 days) is still listed, as a Tier 0 `report` finding. The executor checks size + hash against the scan, then journals `intent` (original path, mode, hash) → one `rename` into `~/Library/Application Support/AIStack/Doctor/Quarantine/<id>/<name>` (same volume as the home, so the file is always at exactly one of the two paths; a cross-volume rename fails and the item is recorded as failed, no copy fallback in v1) → `chmod a-x` → `done`. Undo checks the quarantined hash, renames back and restores the mode. `recover` rolls an interrupted quarantine back and finishes an interrupted restore. Crash points: `quarantine-intent`, `quarantine-moved`, `unquarantine-moved`. The safety test uses a stand-in `clamscan` (`LD_CLAMSCAN`, honoured only with `--root`) so it doesn't depend on ClamAV's signature download; the planted-issue scan in CI uses the real ClamAV on the EICAR file.

## 4. Journal format

`~/Library/Application Support/AIStack/Doctor/journal.jsonl` (Windows: `%LOCALAPPDATA%\AIStack\doctor\journal.jsonl`). The file is append-only, there is one JSON object per line, and every record is fsynced before the step it describes.

```json
{"v":1,"id":"01J…","checkup":"2026-11-08T10:00Z","seq":7,"ts":"2026-11-08T10:03:12.120Z",
 "phase":"intent|done|failed|skipped|refused|approval|undo-intent|undo-done",
 "action":"compress","tier":1,"planItem":null,"approval":null,
 "from":"/Users/x/Documents/old.log","to":"/Users/x/Documents/old.log",
 "before":{"size":13501661,"blocks":26376,"mtime":"…","birth":"…","sha256":"e4b2…"},
 "after":{"blocks":9240,"sha256":"e4b2…"},
 "undo":{"op":"decompress","path":"/Users/x/Documents/old.log"},
 "reason":"cold 412 days, sample shrank 65%"}
```

- Approval records: `{"phase":"approval","planItem":"p-…","fingerprint":{"size":…,"mtime":…,"sha256":…},"by":"user|rule:<ruleId>","ts":…}`.
- `verify-journal` replays the file and counts every `tier:2, phase:"done"` record that has no earlier `approval` record for the same `planItem`. Those are `unapproved_destructive_actions`, and CI fails if the count isn't 0.
- Recovery on start: an `intent` without a matching `done`/`failed` gets reconciled. Clean up the temp sibling if the original is intact, otherwise finish the rename and record `done`.

## 5. Never-list (`Guard`)

Hard-coded in both implementations **and** mirrored in the catalogue (the code rejects even if the JSON is edited). A path is refused if it:
- resolves (after `realpath`, which also follows symlinks) outside the user's home, or outside `--root` in test mode;
- belongs to another user (owner uid ≠ ours);
- is inside `~/Library/Keychains`, `~/Library/Mail`, `~/Library/Messages`, `*.photoslibrary`, `~/Library/Application Support/{Google/Chrome,Firefox,…}` profiles, `~/Library/Safari`, 1Password or Bitwarden stores, Windows `AppData\…\Microsoft\Credentials`, `Protect`, Outlook `.pst/.ost`, or browser `User Data`;
- is inside a git work tree (any ancestor has `.git`). Sizes get reported, nothing gets touched;
- was modified (or, for folders, has any descendant modified) in the last 7 days;
- is an action that turns off a security feature. No such action type exists, and the catalogue loader rejects categories that name one.

## 6. Shared catalogue (`shared/doctor-catalogue.json`)

The file sits at the repo root under `shared/`. It's copied into the macOS bundle's Resources by `scripts/make_bundle.sh` and into the Windows package by `windows/package.sh`, two lines each. Both loaders validate it against the same JSON Schema in CI.

```json
{ "version": 1,
  "defaults": { "coldDays": 60, "minCompressionGain": 0.25, "neverModifiedWithinDays": 7,
                "undecidedExpiryDays": 30, "installerAgeDays": 30 },
  "categories": [
    { "id": "downloads-installers", "action": "moveToTrash", "tier": 2, "ruleable": true,
      "label": "Installers in Downloads older than {installerAgeDays} days",
      "paths": { "macos": ["~/Downloads"], "windows": ["%USERPROFILE%\\Downloads"] },
      "match": { "extensions": [".dmg",".pkg",".msi",".exe",".iso"], "olderThanDays": "installerAgeDays" } },
    { "id": "xcode-derived-data", "action": "moveToTrash", "tier": 2, "ruleable": true,
      "paths": { "macos": ["~/Library/Developer/Xcode/DerivedData"] } },
    { "id": "cold-compressible", "action": "compress", "tier": 1, … } ],
  "neverPaths": { "macos": [ … ], "windows": [ … ] },
  "incompressibleExtensions": [ ".jpg",".png",".heic",".mp4",".mov",".zip",".gz",".7z",".dmg",".pdf", … ] }
```

The loader rejects a category whose `tier` is lower than its action type's code tier. The JSON can raise a tier but never lower it.

## 7. Treatment plan and rules

- A **plan** is one JSON file per checkup: `groups[]` → `items[]`. Each item has `planItemId = hash(category, path)`, a fingerprint (size, mtime, sha256 for files ≤ 1 GB, otherwise a sampled hash plus inode) and bytes. A group has a total and a one-line reason.
- Carry-over: undecided items are merged by `planItemId` into the next plan, keeping the first-seen date, and dropped after 30 days.
- One notification per plan, and at most one approval notification per calendar day (`lastPromptDate` in the support folder).
- **Rules** (`rules.json`): `{id, categoryId, createdAt}`. Rules are only allowed when the action type is ruleable (code-level property: `moveToTrash`, `disableStartupItem`, `disableBrowserExtension`). `emptyTrash`, `quitProcess` and `uninstallApp` refuse rule creation in the code, not only in the UI. A rule-approved item still gets an `approval` record with `by: "rule:<id>"`.
- Execution re-checks the fingerprint and that the file isn't open. If anything changed, the item is `skipped` with the reason shown in History.

## 8. Scheduling, sampler, permissions

- **macOS:** `NSBackgroundActivityScheduler` (interval = weekly, tolerance) plus a check of the user's chosen day and time, mains power (`IOPSCopyPowerSourcesInfo`) and idle time (`CGEventSource.secondsSinceLastEventType` > 5 min). The sampler is a 5-minute timer reading `proc_pid_rusage` (CPU, energy `ri_billed_energy`, phys_footprint) plus `NSWorkspace.didActivateApplicationNotification`. **Decision:** it stores hourly aggregates as one JSONL file per day plus a rolling 14-day summary JSON, not SQLite, so Windows doesn't need a SQLite dependency. Cost: one `proc_listallpids` pass every 5 minutes, under 10 ms.
- **Windows:** the helper is a Chrome native-messaging host, so it only runs while Chrome does. That's not enough for a schedule. **Decision:** when the user turns Laptop Doctor on, the helper registers one per-user scheduled task (`schtasks /Create /SC WEEKLY … /RL LIMITED`, no admin) that runs `AIStackHelper.exe --doctor checkup` with the "only on AC power" and "run when idle" conditions. Turning the feature off deletes the task. This is the only new install-time effect, and it only happens on opt-in. The sampler runs inside the helper while Chrome is open, polling `GetForegroundWindow` every 30 s, plus a lightweight sampler inside the scheduled checkup. Its coverage is partial, and the report says so ("sampled 31 h over 14 days").
- **Full Disk Access (macOS):** probe by trying to read `~/Library/Safari/Bookmarks.plist`'s *metadata only*, or the TCC-protected folder listing. If that's denied, the report lists the skipped areas and offers one "Open Privacy settings" button (`x-apple.systempreferences:com.apple.preference.security?Privacy_AllFiles`). No repeated prompts.
- **Windows admin:** scans run without admin. BitLocker status (`manage-bde`) and some Defender details need elevation. Those rows show "Needs admin, check now", which launches an elevated `AIStackHelper --doctor posture-admin` once.

## 9. UI sketch

**macOS:** a new menu bar item "Laptop Doctor…" opens a window (same `WindowManager` pattern as Meeting Notes):

```
┌ Laptop Doctor ────────────────────────────────────────────────┐
│ Last checkup Sun 8 Nov 10:02 · next Sun 15 Nov  [Run checkup]  │
│ ┌ Summary (Claude, optional) ───────────────────────────────┐ │
│ │ Your Mac is in good shape. 2 startup items look unused…   │ │
│ └───────────────────────────────────────────────────────────┘ │
│ Security   ✓ Gatekeeper ✓ SIP ✓ FileVault ✗ Firewall off       │
│            ⚠ 1 unsigned LaunchAgent: com.foo.updater (orphan)  │
│ Speed      Dropbox helper 9% CPU avg / 14 d, not opened 41 d   │
│ Disk       412 GB used · Tier 1 freed 3.2 GB automatically     │
│ ── Treatment plan (needs your OK) ──────────── 11.4 GB ─────── │
│ ▸ Delete 14 installers in Downloads (>30 d)  6.1 GB [Approve]  │
│ ▸ Delete Xcode DerivedData                   4.8 GB [Approve]  │
│ ▸ Disable Dropbox at login                          [Approve]  │
│ ▸ Empty Trash                                0.5 GB [Approve]  │
│ [Approve all] [Decline]          ☐ Always do this for checked  │
│ History ▸   (each entry: Undo · per checkup: Undo all)         │
└───────────────────────────────────────────────────────────────┘
```

Settings gets a "Laptop Doctor" section: on/off (off by default; turning it on first shows the will / will-never list sheet), schedule (day, time, "only on power & idle"), the three Tier 1 switches with a folder picker for archiving, the N-day steppers, the rules list with remove buttons, and the Claude switch with "Only process names, signers and executable paths are sent. Never your file names or contents."

**Windows:** Meeting Notes lives in extension pages (`notes.html`, opened from the popup's "All meetings"), so Laptop Doctor gets `doctor.html` in the extension. The same layout, built with `ui/common.js` and `ui/style.css`. It talks to the helper over the existing native-messaging channel with new `doctor.*` message types. The popup gets a "Laptop Doctor" row with a badge when a plan is waiting, and the toast is a `chrome.notifications` notification. Without Chrome running, the scheduled checkup writes the plan and shows a Windows toast that opens `doctor.html`.

## 10. Claude call

One call per checkup through the existing `ClaudeCLI` / `ClaudeCli` (haiku). The input is built from a whitelist struct: `{processName, executablePath, signer, teamId, cpuAvg, lastActivatedDays}` plus posture booleans. The struct has no field that could carry user file or folder names. Executable paths under the home folder are reduced to `~/…/<binary name>`. Fixture names are chosen so that a leak would be detectable, and a unit test asserts the serialised prompt contains none of the fixture's file names. If Claude is off or the call fails, a template summary is used.

## 11. Testing and KPIs

- `fixtures/make_messy_laptop.py`: deterministic (seeded) and cross-platform (Python 3 is on both runners). Writes `<out>/home/…` plus `expected.json` (`planted[]` with id, kind, path and expected tier; `tier1EligibleBytes`, per platform). EICAR only when `CI=true`.
- `tests/safety/` contains the five suites from the brief, run against the CLI. The kill test runs each Tier 1 action under a harness that `SIGKILL`s / `TerminateProcess`es it after a random delay (seeded, 50 trials per action), then checks every original path still exists with its original hash (or the result's hash after a completed step).
- CI (`.github/workflows/doctor.yml` on the fork): a matrix of `macos-latest` and `windows-latest` that builds, makes the fixture, runs the suite, and uploads `doctor-results.json` (`safety: {passed,total}`, `planted: {found,total}`, `tier1: {freed,eligible}`, `unapprovedDestructive`) as an artifact.
- `measure.sh` (project folder) reads the latest successful-or-failed run's artifacts from the fork via `gh` and reports the worst platform.

## 12. Decisions made while implementing (session 2, 2026-11-01)

- **"Cold" = mtime older than N days AND Spotlight's `kMDItemLastUsedDate` (if any) older than N days.** atime is not used: APFS updates it on reads, including our own hashing and sampling, so it would make every scanned file look "opened".
- **Compression floor: 256 KiB** (`minCompressBytes`). Below that the block savings don't pay for a copy + verify + rename. Executable files (`mode & 0o111`), hard-linked files, already-compressed files, hidden folders, `~/Library`, bundles and git work trees are not walked.
- **The 7-day rule for folders looks at the newest *file* inside, not the folder's own mtime.** A folder's mtime changes whenever an entry is added or removed (e.g. the Trash whenever anything is trashed). Empty folders fall back to their own mtime.
- **Writing verbs (`tier1`, `approve`, `execute`, `undo`, `recover`) refuse to run without `--root`** until the UI ships. Without `--root` the CLI can only scan and plan, so real-machine runs are dry-run by construction.
- **Execute order per item:** Guard → approval record for the same `planItem`, path and action → fingerprint (size, mtime, sha256) re-check → journal `intent` → act → journal `done`/`failed`. A file changed between plan and execution is either refused by the 7-day rule (fresh mtime) or skipped by the fingerprint check (same mtime, different bytes). The safety suite covers both.
- **Rule-approved lossy actions are dropped at approval time** (`approve --by rule:<id>` writes no record for `emptyTrash`, `quitProcess`, `uninstallApp`), so `execute` refuses them.
- **Not implemented yet** (journalled as `skipped`, never silently ignored): `evictCloud`, `emptyTrash`, `disableStartupItem`, `quitProcess`.

## 13. Windows port (session 5, 2026-11-02)

- **Same CLI, same JSON.** `AIStackHelper.exe --doctor <verb>` (`windows/helper/Doctor/`) takes the same verbs, flags, report/plan/journal shapes as the macOS binary, so one Python safety suite drives both. `--doctor` is handled before console logging starts, so stdout stays pure JSON.
- **Compression = NTFS (LZNT1) set on a fresh temp sibling *before* any bytes are written** (`FSCTL_SET_COMPRESSION`), then copy → restore creation/modified times and attributes → SHA-256 verify → check the allocated size actually shrank (`GetCompressedFileSizeW`) → atomic `MoveFileEx(REPLACE_EXISTING)`. In-place `compact /c` would also keep the bytes, but the copy-and-swap keeps the crash story identical to macOS: the original is untouched until the rename. Undo copies into an uncompressed sibling the same way. The scan samples with Deflate (a bit stronger than LZNT1); the "did not shrink" check at execution is what decides.
- **Guard additions (Windows):** a path that goes through a junction/symlink is refused, and so is a folder that *contains* a never-list location (`%LOCALAPPDATA%\Google` holds Chrome's profile). Matching is case-insensitive. The catalogue's `%USERPROFILE%`/`%APPDATA%`/`%LOCALAPPDATA%` expand relative to the home in use, so a fixture root behaves like a real profile.
- **Persistence:** catalogue match kind renamed `launchAgents` → `startupItems` (each platform lists its own: LaunchAgents on macOS, HKCU `Run`/`RunOnce` on Windows). A Run value is flagged when its program is missing or has no embedded Authenticode signature; full WinVerifyTrust chain checks come with the UI work. In test mode only values pointing into the fixture are considered.
- **Malware on Windows:** no Tier 1 quarantine action. Defender quarantines on its own (brief §1); the checkup will read its history (`Get-MpThreat`). So the quarantine safety test is macOS-only and Windows runs a compress crash-point test in its slot (copied / verified / undo-copied).
- **Recycle Bin:** in test mode `<root>\RecycleBinFixture`. On a real machine `moveToTrash` is journalled as skipped until the IFileOperation Recycle Bin move with undo exists.
- `archiveFolder` on Windows: the same steps, crash points and recover states as macOS. The zip is written with `System.IO.Compression` (one entry per manifest entry, `/` separators) instead of `ditto`. The manifest lists folders directly (so `desktop.ini`/`Thumbs.db` are not left out) and stores Windows attributes in `mode`. Undo sets times, then attributes (so read-only comes last), deepest first.
- Malware on Windows: Defender quarantines files itself, so the checkup only *reports* (Tier 0) what it found under `~/Downloads` and `~/Desktop`. It reads `Get-MpThreatDetection` history, then runs `MpCmdRun -Scan -ScanType 3 -File <root> -DisableRemediation` (reports without remediating) and reads the history again. Nothing is moved by us.
- **Not on Windows yet** (journalled as skipped): `evictCloud` (needs a real Cloud Files sync root in the fixture), busy-process command lines beyond CIM.
