# Handoff — `Siemens2`, TIA Portal PLC-software loop

**Date:** 2026-08-19 · **Branch:** `Siemens2` (off `main` at `1180418`).
The living queue is still [`PHASE2.md`](PHASE2.md); this page is what a later
session needs in front of it before touching the second vendor or packaging.

PLC software (SCL, UDTs, tag tables) now has the same product loop the first
vendor has: export, textual round-trip, held-project wait, adopt in the app.
Hardware, HMI, safety and packaging stay out of scope, as the owner set when
item 7 opened.

---

## Before this ships further

Nothing below is a defect in the loop. They are the three things this session
did not own.

| | What | Status |
|---|---|---|
| **1** | **Run `Versio.Plc.Codesys.Tests`** — the ~30 min real-CODESYS gate | **Not run on this branch.** Core grew `ExportHeldViaCopy`, the held-wait, and the occupancy-then-hash path. Those are vendor-blind and covered in Core / App, but the first vendor's gate has not seen them. |
| **2** | **Confirm-import and restore on the TIA tree** | **Export was eyeballed** on `%TEMP%\versio-tia-p7` (owner: looks good). The confirm-import dialog and a restore of the ~500 MB tree were not clicked. Both go through the existing directory-payload seam. |
| **3** | **Packaging (PHASE2 item 8)** | **Next in the queue**, unless the owner opens the coverage skips. |

Work only on a **copy** of `fixtures/projects/CleaningStation_V20_V21/` (Git LFS).
Never the OneDrive original. Close Versio **and** Portal before a host rebuild;
click **Yes to all** on Openness access (path + hash — a rebuild drops the host
off the AllowList).

---

## What landed

The ordered remaining list in PHASE2 item 7.1–7.5 is struck, with what actually
happened:

1. **SCL generate overwrites.** Openness V21 `GenerateBlockOption` is
   `None | KeepOnError` — there is no Overwrite. Group + `None` is the
   overwrite; `KeepOnError` cloned `BoolToDigVal_1`. The gate mutation
   `IF FALSE THEN (* ident *) END_IF` does not compile, so Save left the
   block unexportable. The mutation that survives is an assignment on an
   existing TEMP plus a `//` marker. Generate verifies via `GenerateSource`
   before Save and **throws** rather than listing a no-op in `ChangedPaths`.
   Gate Fact green; P7 SETTLES.
2. **Host import finds UDTs and tag tables**, not only `BlockGroup`. Native
   apply routes by identity. `TryDelete` keeps the object and warns when
   `Delete()` is missing.
3. **Two unchanged textual exports match byte-for-byte**, re-exported into
   the **same** tree so Openness refusing an existing XML is visible. Native
   XML is expected to differ (G12).
4. **App loop** on `%TEMP%\versio-tia-p7` (git init, `.ap21` committed,
   `System/` gitignored). Adopt, export ~28 s warm / 159 objects / 270
   manifest records. Owner saw the panel after the "not exported" fix.
5. **Held export is a wait**, not a 500 MB copy. `ExportHeldViaCopy` is
   false on the directory format; Sync waits both directions. Occupancy is
   `<stem>.info` beside the `.apXX`, measured under Openness. Attach-to-running
   and a "launch Portal" button stay unused.

Sync, Git, Locking, Watching, Projects and Diff still do not spell a vendor.
`ExportHeldViaCopy` is the seam.

---

## Defects this session found, all closed

Written up in [`FINDINGS.md`](FINDINGS.md), newest first. None was findable by
running the gate against two empty directories.

| | What | Why it mattered |
|---|---|---|
| Coverage skips as `NOT_IMPLEMENTED` | Hardware / HMI / Safety / Technology painted `HasGaps` on every TIA tree | The project row said `not exported · 4 objects not exported` after a successful PLC-software export |
| Hash before occupancy cleared | Openness rewrites `System/` while it holds the project | Commit gate: "1 project has changes that have not been exported" |
| Occupancy vanish = IDE closed | Host `Projects.Open` writes and deletes `.info` | Four export cycles of 159 objects / ~32 s with nobody saving |
| `Export(None)` on an existing file | Openness refuses overwrite | Second export into the same tree: 159 `XML_EXPORT_FAILED`, **zero objects**. The determinism gate used two empty dirs, so it never saw this |
| Host `"warnings": [ "name: …" ]` | Core `ExportWarning` is `{code, path, detail}` | Panel: `manifest not fully readable` |
| `File.ReadAllBytesAsync` exclusive | Pointer still held after `WaitForExit` | Session logged `failed to open … at startup` |
| Group+`None` vs KeepOnError / empty IF | V21 has no Overwrite; empty IF does not compile | Import reported success; re-export had no `Level.scl` |

---

## What the owner saw

Versio launched against `%TEMP%\versio-tia-p7` on 2026-08-19 after the
"not exported" rebuild. The panel looked right. Filter the launch window in
`%LOCALAPPDATA%\Versio\logs\versio-2026-08-19.log` — older `ERR` lines from
16:42 are the exclusive-hash failure, not this launch.

---

## What is still open

One claim in one place. This list is the handoff; PHASE2 / UX-PLAN keep the
reasoning.

### Next to build (PHASE2)

- **Item 8 — packaging.** `third_party/` vendoring with SHA-256 pinning;
  Velopack; code signing (procurement lead time). `STATUS.md` §7.

### TIA, deliberately not this loop

- Hardware, HMI, safety (`F_*`), technology objects, know-how-protected
  import, compile-after-import, VCI, Project Server. `_coverage.md` names
  them. They are `SERVICE_OBJECT`, not holes.
- GUI occupancy with a **Portal window** (no Openness) was not separately
  measured. The marker path is the exclusive-open lock file; treat a
  different sidecar as a new measurement, not an assumption.
- Identity stays `device/plc/type/name#number` until a GUID-shaped SimaticML
  value appears. Folders are not recorded objects.
- AllowList after every host rebuild. Ten tree backups × ~500 MB. Several
  Portal versions on one machine refuse rather than pick one.

### Still owed from earlier queues (not TIA)

| Where | What |
|---|---|
| [`PHASE2.md`](PHASE2.md) item 5 | C5 (`DELETE_UNREADABLE`) and `native_partial` — close when reality produces a specimen |
| [`PHASE2.md`](PHASE2.md) item 6 | Merge dialog, both themes on the rail badge, headers dragged to 260 px |
| [`UX-PLAN.md`](UX-PLAN.md) | UX-6, UX-7's second half (`Ctrl+F` / Escape), UX-8, UX-12 (amend-while-unpushed — a decision, not a gap) |
| [`HANDOFF-UxPlan.md`](HANDOFF-UxPlan.md) | P-M2 (commit gate skip after merge — needs a real CODESYS export); counted CODESYS gate for `LOCK-1` |
| [`STATUS.md`](STATUS.md) loose ends | `RefrigeratorControl.project` is the gate fixture and the daily project |

### Friction to remember, not to fix next

- Close Versio before rebuilding — it locks its own binaries.
- `dotnet` on this machine that builds: `C:\Program Files\dotnet\dotnet.exe`
  (the user-local path in `CLAUDE.md` is not always the SDK).
- Host is **net48**: no range indexer; use `.Substring(0, 8)`.
- Never round-trip source through PowerShell.

---

## Tests, re-derived this session

| Suite | Count | This branch |
|---|---|---|
| `Versio.Core.Tests` | 1,434 | held-wait, skip-locked hash, wait-until-free |
| `Versio.Integration.Tests` | 152 | `WindowsFileSystemTests` — share + retry on a held pointer |
| `Versio.App.Tests` | 613 | occupancy retry only if `BlockedByOpenProject` was recorded |
| `Versio.Arch.Tests` | 14 | unchanged |
| `Versio.Plc.Codesys.Pure.Tests` | 260 | unchanged |
| `Versio.Plc.Codesys.Parity.Tests` | 92 | not re-run |
| `Versio.Plc.TiaPortal.Pure.Tests` | 16 | `ExportHeldViaCopy` |
| `Versio.Plc.TiaPortal.Tests` | 5 | **green** on this machine — probe, export, same-tree determinism, import/re-export |
| `Versio.Plc.Codesys.Tests` | 54 | **not run** |

**2,640** across nine projects. Parity 92 is the last counted run, not this
session.
