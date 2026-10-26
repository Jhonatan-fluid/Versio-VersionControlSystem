# Driving TwinCAT 3

**Date:** 2026-08-21 (I/O export-only; GUI occupancy same day) · Beside `Versio.Plc.TwinCat*` — one
vendor's docs, beside its projects. How TwinCAT XAE is driven, what the Automation
Interface actually does in this host, and what is still open.

| | |
|---|---|
| Document | **The TwinCAT integration, end to end.** Which Automation Interface surface Versio uses, how the helper host is launched, what crosses the boundary, what every job does, and where the seams and the gaps are |
| Audience | An engineer who has to change the export, the import, the host or the runner — or add a fourth vendor beside the first three |
| Related | [`STATUS.md`](../../STATUS.md) · [`FINDINGS.md`](../../FINDINGS.md) · [`SAFEGUARDS.md`](../../SAFEGUARDS.md) · TIA's [`vendors/tia/DRIVING.md`](../tia/DRIVING.md) · CODESYS's [`vendors/codesys/DRIVING.md`](../codesys/DRIVING.md) |

Versio is not a TwinCAT tool. Neutral code must not grow any of this. The directory payload
TIA already needed (`PayloadPathFor` / `CopyTreeAsync`) is the seam TwinCAT reuses, not a
brand leaking.

---

## 0. In one paragraph

Versio never parses a `.tsproj` as the program. The pointer is XML; the PLC lives in folders
beside it. TwinCAT has no `--runscript`. A helper exe (`Versio.Plc.TwinCat.Host`, net9 x64)
binds **`TcXaeShell.DTE.17.0`** from the machine via COM, registers an **`IOleMessageFilter`**,
opens the **sibling `.sln`**, waits until the nested PLC tree under **`TIPC`** is loaded, and
talks back over stdout with the same `@@VERSIO@@` JSON-lines the other vendors use. Beckhoff
DLLs are never shipped — every Automation Interface call is late-bound `IDispatch`
(`Type.InvokeMember`). PLC software (ST via `DeclarationText` / `ImplementationText`, with
`ProduceXml` when IDispatch does not surface those properties) is the round-trip subject.
I/O is exported as `*.devices.yaml` (form `DEVICES`, export-only). Comment set in-session
and showed up in `ProduceXml`; it does not persist through Save. NC, safety, C++ and
visualisations are coverage skips.

---

## 1. The three projects

| Project | Role |
|---|---|
| `Versio.Plc.TwinCat` | Pure. Format (`.tsproj` working; `.tszip` / `.tspproj` as templates so adoption refuses), directory payload, occupancy, transients, import planner, ST header, `TwinCatJob` / `TwinCatProtocol` |
| `Versio.Plc.TwinCat.Infrastructure` | Driven. Locator (`TcXaeShell.DTE.17.0` preferred over 15.0), `LocalTwinCatRunner`, mutex `Global\Versio.TwinCat.SingleInstance`, `TwinCatPack.Register()` |
| `Versio.Plc.TwinCat.Host` | net9-windows x64 exe. `[STAThread]`, message filter, late-bound COM. Split: `Program` / `MessageFilter`, `HostApp` (session), `HostExport`, `HostImport`, `HostDeviceCatalog`, `HostIoProbe`, `HostCom`, `HostText` |

`Versio.App` has a third `ProjectReference` and calls `TwinCatPack.Register()` **after**
`TiaPortalPack.Register()`. A missing `vendor` field on an old binding still deserialises as
the first vendor. The log filter includes `TwinCatLog.Category` (`TwinCat`), declared on the
pack rather than in the neutral `LogCategory` table.

The host does **not** reference `Versio.Core`. The JSON job file is the contract. The ST
delimiter `// --- BEGIN IMPLEMENTATION ---` is duplicated in `HostText` because the host
cannot import `StTextCodec` — a drift there would split a file in the wrong place on import.
`CopyHostToReferrer.targets` copies the exe to `twincat\` beside Infrastructure, the app, and
the real-XAE tests. Isolation from the WPF UI thread is the reason this is a helper exe, not
a TFM constraint — COM works from net9.

---

## 2. Setup (measured 2026-08-19)

### 2.1 Install

| Fact | Value |
|---|---|
| TwinCAT | **3.1.4026.25** at `C:\Program Files (x86)\Beckhoff\TwinCAT\3.1\` |
| How version is read | `Components\Plc\Build_4026.25` — **not** `TcXaeShell.exe` ProductVersion, which is `15.0.0.0` (the Visual Studio shell). Comparing that to a project's `TcVersion="3.1.4024.4"` refused every open as a conversion until the locator (and the host's convert check) were taught the `Build_*` folder. See Findings, 2026-08-19 |
| Preferred ProgID | `TcXaeShell.DTE.17.0` (64-bit). `15.0` exists on the same TwinCAT build; 17.0 is chosen and logged, never a silent conversion between shells |
| Type libs | `Components\Base\TypeLib\` (`TCatSysManagerLib` in the GAC). Bound at runtime; never shipped |
| Host TFM | net9.0-windows x64 (`PlatformTarget=x64` — AnyCPU can load as 32-bit and then fail to bind 17.0) |

Several TwinCAT shells on one machine are **not** TIA's "several Portal versions → refuse"
case: both are 4026.25 here. Prefer 17.0.

### 2.2 Message filter (mandatory)

Without `IOleMessageFilter`, DTE create succeeded in **2.6 s** (2026-08-19, first probe) and
the next member access was `RPC_E_CALL_REJECTED` (0x80010001). Beckhoff documents the filter;
it is not optional. `Program.Main` registers it **before** `HostApp.Run`, and `HostCom`
retries the same HRESULT (and `RPC_E_SERVERCALL_RETRYLATER`) up to 80 times at 100 ms.

### 2.3 Conversion (4024.4 fixture on 4026.25)

The committed gate fixture is Beckhoff's `TC3_PlcSample_reStrText` (`TcVersion="3.1.4024.4"`).
Opening it on 4026 is a conversion. The host **refuses** that on the path the user bound —
the OpenWithUpgrade class of mistake.

**The check fails closed since 2026-08-20.** It used to return "no conversion" when either
version could not be read — and with `SuppressUI` set, that is exactly when a mismatched
project converts headless with nobody asked. Unknown refuses now, with a sentence naming
which side could not be read; the false-refusal cost is paid in discovery instead:
`TWINCAT3DIR` (set machine-wide by the Beckhoff installer) is a fallback in both the locator
and the host's own walk, `Build_*` is picked numerically, and an unreadable `Version.txt`
degrades to unknown rather than faulting the probe. See Findings, 2026-08-20.

A **throwaway copy** is the conscious conversion the product allows: the runner sets
`allowConvert` when the `.tsproj` sits under `Path.GetTempPath()` or under
`.versio/local/open/` (the held-export scratch). Gate Facts copy the fixture to
`%TEMP%` first. The committed tree is never opened. Carried deliberately: any path
under `%TEMP%` qualifies, so a project somebody *binds* from their temp directory loses the
refusal — scoping the grant to Versio-created copies is the tighter rule if that ever bites.

### 2.4 Sibling `.sln`

Open the sibling `.sln`, not the `.tsproj` alone. Binding identity stays `.tsproj`. If no
`.sln` sits beside it, the host refuses with a sentence rather than inventing a solution in
the user's tree.

### 2.5 Closing before a rebuild

A running `Versio.exe` locks its binaries. Exclusive DTE open holds the solution. Close
**Versio and XAE** before rebuilding, same rule as the other vendors.

---

## 3. How the host is launched

```
Versio.Plc.TwinCat.Host.exe <job.json>
```

`LocalTwinCatRunner` writes the job beside `%TEMP%`, takes
`Global\Versio.TwinCat.SingleInstance` (15 min wait), starts the exe with
`CREATE_NO_WINDOW`, and reads stdout/stderr for `@@VERSIO@@` lines. Timeout floor is
**20 minutes**. `TypicalStartupCost` is **22 s** (measured export of a TEMP copy).

Since 2026-08-21 the wait honours cancel (`ChildProcess.WaitForExitOrKill`): Stop closes
stdin so the host can `TryQuit` DTE, then kills leftovers. Host.exe is **not** in the
kill-on-close Job Object. `VERSIO_PARENT_PID` plus a stdin-EOF watch: if Versio dies, the
host Quits COM and exits. The host measures `TcXaeShell` PIDs across its DTE creation
call alone and reports the one that appeared as `ide_pid`; a cancel reaps that PID only,
after checking its image name and start time (`ChildProcess.KillOwned`). ~~Snapshotted
before Host starts~~ reaped every XAE that appeared during the whole run, the engineer's
included (FINDINGS 2026-10-24, item 28). The 15-minute mutex wait observes the token too.
The log says `cancelled while waiting for the machine-wide TwinCAT lock`, or `closed
stdin so the host can Quit COM`, and if XAE outlived Host, `reaped the TcXaeShell process
this run started (pid N) — nothing else`; with no reported PID, `the host reported no
TcXaeShell pid; nothing was reaped by name`.

The job schema (`TwinCatJob` / `JobReader`):

| Field | Meaning |
|---|---|
| `op` | `probe` / `export` / `import` / `ioprobe` (tools-only) |
| `schema` | 1 |
| `project` | absolute path to the `.tsproj` |
| `out` | export destination (probe, export) |
| `src` | import source directory |
| `progId` | `TcXaeShell.DTE.17.0` (or the locator's choice) |
| `install` | `…\TwinCAT\3.1` folder |
| `version` | TwinCAT build string from `Build_*`, e.g. `3.1.4026.25` |
| `save` | import only; export and probe do not save |
| `timeoutSeconds` | at least 1200 |
| `allowConvert` | throwaway copies: `%TEMP%` or `.versio/local/open/` |
| `plan` | confirmed `ImportPlan` JSON; an import without one refuses to serialise |

---

## 4. How the Automation Interface is used

TwinCAT's engineering API is COM into **TcXaeShell** (a Visual Studio isolated shell) plus
the **System Manager** tree (`ITcSysManager` / `ITcSmTreeItem`). There is no C# SDK Versio
ships. The host never `#using`s a Beckhoff type. `HostCom` is `InvokeMember` with retries.

Beckhoff names the PLC-facing faces `ITcPlcDeclaration` / `ITcPlcImplementation`. On this
install those properties often **do not appear on IDispatch**, even though the object is a
POU. `ProduceXml` still returns the declaration / implementation CDATA. That is still the
Automation Interface — not a disk parse of `.TcPOU`. Do not add a file-system fallback;
import still needs the IDE to write the project.

### 4.1 Session (every job)

```
STA thread
  → CoRegisterMessageFilter (IOleMessageFilter)
  → Type.GetTypeFromProgID("TcXaeShell.DTE.17.0")
  → Activator.CreateInstance          // EnvDTE.DTE
  → DTE.SuppressUI = true
  → DTE.MainWindow.Visible = false
  → DTE.Solution.Open(sibling.sln)
  → solution.Projects.Item(1).Object  // ITcSysManager
  → wait until TIPC^…^… Project exists
  → probe | export | import | ioprobe
  → Solution.Close(false) ; DTE.Quit
```

| Member | Used by | Notes |
|---|---|---|
| `TcXaeShell.DTE.17.0` ProgID | every job | 64-bit host. 15.0 is the same TwinCAT build, not a second version to refuse |
| `IOleMessageFilter` | every job | Without it, `RPC_E_CALL_REJECTED` after a successful create |
| `DTE.SuppressUI` / `MainWindow.Visible` | every job | Headless. A window flash was not observed from this process |
| `Solution.Open(sln)` | every job | Sibling `.sln` only. No `.tsproj`-alone open |
| `Projects.Item(1).Object` | every job | The System Manager. EnvDTE `Item` is **1-based** |
| `ITcSysManager.LookupTreeItem(path)` | session, export | Caret paths: `TIPC`, `TIPC^PlcName^PlcName Project`, `TIID`, `TINC` |
| `DTE.ExecuteCommand("File.SaveAll")` | import, when `save` | **The save that works for PLC.** `ITcSysManager.Save` and `SaveProject` are `DISP_E_UNKNOWNNAME` (0x80020006). Fallback: `project.Save("")`. I/O Comment does not persist through any of these on an empty TIID — see §4.5 |
| `Solution.Close(false)` / `DTE.Quit` | finally | Best effort; a leftover shell is a finding, not a second fatal |

Attach-to-running is **not used**. Two DTE instances on one project are not safe — that is
why the runner takes a machine-wide mutex.

### 4.2 The tree (TIPC is lazy)

TwinCAT's System Manager is a forest. PLC software is the round-trip subject; the
I/O catalog is exported for review:

| Path prefix | What it is | Versio |
|---|---|---|
| `TIPC` | PLC projects | **walked** — export / import ST |
| `TIID` | I/O devices | **walked** — `*.devices.yaml`, export-only |
| `TINC` | NC / motion | coverage skip |
| Safety / C++ / visualisation | other project kinds | coverage skip (`ItemSubType` 619 is visualisation) |

**Lazy load.** `LookupTreeItem("TIPC^Name^Name Project")` fails until the nested PLC
project is touched. Measured: `Child` is a **1-based property indexer**
(`item.Child[i]`); `Child(i)` as a method did not. The host waits up to ~45 s (90 ×
500 ms): it enumerates `TIPC`'s children, reads `Name` / `ChildCount`, and calls
`ProduceXml` to force the nested `… Project` node into existence. The first cut ended
its coalesce in `?? child`, which made the retry loop dead code and could hand export a
node whose children never loaded — and fast-pathed the fixture by name so the gate never
saw the generic branch (Findings, 2026-08-20). Now: the nested lookup gets the first
half of the wait exclusively; only after that may the outer child stand in, and only
with demonstrably loaded children. **Export refuses a zero-object mirror outright** —
an empty mirror reads as every object deleted, so a genuinely empty PLC project cannot
export, on purpose.

Identity in the manifest is that Automation Interface **`PathName`**
(`TIPC^LibraryDocumentationSample^LibraryDocumentationSample Project^…`), not a GUID
inside `.TcPOU`.

### 4.3 Export (TIPC + TIID catalog)

| Member | Used by | Notes |
|---|---|---|
| `item.Name`, `PathName`, `ItemSubType`, `ChildCount` | walk | Subtype 601 = folder, 619 = visualisation |
| `item.Child[i]` (1-based property) | walk | See §4.2 |
| `DeclarationText` / `ImplementationText` | ST | `ITcPlcDeclaration` / `ITcPlcImplementation`. Often missing on IDispatch |
| `ProduceXml` | ST fallback | Parse `<Declaration>` and `<Implementation><ST>` CDATA from the returned XML |
| `TREEITEMSUBTYP_*` | create (import) and type guess | 602 PROGRAM, 603 FUNCTION, 604 FB, 606 STRUCT, 608 ACTION, 609 METHOD, 611 PROPERTY, 615 GVL, 618 INTERFACE |

Packed with the same `// --- BEGIN IMPLEMENTATION ---` split CODESYS uses. LineIds live
in the `.TcPOU` XML on disk; they are **not** in the exported `.st`, so two unchanged
textual exports match.

### 4.4 Import (confirmed plan only)

| Member | Used by | Notes |
|---|---|---|
| Index by `PathName` (manifest `guid`), ambiguity-aware `Name` | apply / delete / rename | Rebuild after renames and each create. A name carried by more than one node resolves **nothing** |
| `DeclarationText` / `ImplementationText` set | `ApplyText` / create | No `ConsumeXml` fallback. If IDispatch rejects the set, the import fails rather than writing `.TcPOU` on disk |
| `CreateChild(name, nSubType, "", IECLANGUAGE_ST)` | create | Documented arity (2026-08-22). Subtype from the ST header (604 FB). `vInfo` is `IECLANGUAGETYPES.IECLANGUAGE_ST` = 1 — an empty string fails to parse. Parent walked segment-by-segment down the recorded `resolve` chain, never a tree-wide name lookup. The I/O-probe order `(name, 1, "", subType)` treats 1 as `nSubType` and XAE refuses a PLC FB |
| `item.Name = newName` | rename | **Name only.** A folder move is planned as `IsMove`; relocating under a new parent is not gated. A missing target aborts the whole import — renaming a guess is how a different POU gets somebody else's name |
| `parent.DeleteChild(Name)` | delete | **Identity-only, hash-verified** (2026-08-20), **XAE-gated 2026-08-22**: `PathName` lookup, then the live packed text is hashed and compared to what the plan recorded — miss or mismatch keeps the object with a sentence, honouring `DeletedFile.Hash`'s contract. Skipped when `cannotVerify` is set. `ITcSmTreeItem` has no `Delete()` on the item; calling it was swallowed as a warning and the POU survived. Failure emits a warning and **keeps** the object |
| `File.SaveAll` | when `save` | See §4.1 |

`ApplyText` resolves its target as: `PathName` → this session's rename map (a rename
changes the live `PathName` mid-import — the one legitimate reason a fallback exists) →
display name only when unique in the tree. Ambiguity throws, and every throw lands before
the save: `Solution.Close(false)` discards the half-applied session, so disk stays
untouched. Planned work that cannot be carried out — a missing source file, an unmatched
rename, an action this host does not apply (`ApplyNative` included) — refuses the same
way rather than importing less than was confirmed. `ApplyText` on `*.devices.yaml` is
refused: Comment does not persist through Save (§4.5). See Findings, 2026-08-20.

The planner is the same `ImportPlan` the other vendors fill (`ApplyText` / `Create` /
`Skip` / `Deletions` / `Renames`). Five deletion preconditions stay in the planner, not
in Sync — and the planner's refusal paths are pinned by mutation-verified pure tests
since 2026-08-20. The runner refuses a plan-less import outright instead of fabricating
an empty plan that no-ops, saves and reports green.

### 4.5 I/O surface (measured 2026-08-20)

Product Export writes `Devices/Devices.devices.yaml` (form `DEVICES`, export-only).
The host op `ioprobe` (`tools/twincat/Probe-Io.ps1`) is the measurement. Work copy
under `%TEMP%`; the probe never saved. Two untouched dumps of
`TC3_PlcSample_reStrText` were SHA-256 identical
(`f076680fd611b99eac630625a471e680795861cd1f75ad2bb139d25bec85b0d4`). Raw dump:
[`probes/ioprobe-TC3-2026-08-20.json`](probes/ioprobe-TC3-2026-08-20.json).

**Render — yes, and this fixture has no rack.** `LookupTreeItem("TIID")` returns a
node named `Devices` (`PathName` `TIID`, `ItemType` 17, `ItemSubType` -1,
`ChildCount` 0). The committed sample has no `<Io>` tree in the `.tsproj`; the
node still exists. `Comment` is an empty string; `Disabled` is `0`. `ProduceXml`
returns a 291-byte `TreeItem`. Process-image `Var` / mappings were not walked —
ChildCount on this node is zero, and mapping is not this slice.

**Comment in-session, not through Save.** `ITcSmTreeItem.Comment` set to
`versio-ioprobe-temp` on `TIID` applied, `ProduceXml` contained the marker, and
the property restored; project **not** saved on the probe. The gate then set
Comment, verified the read-back, `File.SaveAll` persisted a companion ST edit,
and the next export still read an empty Comment. `ITcSysManager.SaveProject` is
`DISP_E_UNKNOWNNAME` (same as `.Save`). This fixture has no Io XML to hang a
Comment on. Applying Comment would confirm an import that silently reverts —
form `DEVICES`, export-only, same as CODESYS I/O.

**CreateChild — I/O measured, refused; PLC gated 2026-08-22.** A probe-only
`CreateChild("EtherCAT Master", 111, "", "")` (Beckhoff's documented EtherCAT
master subtype) was refused (`Invalid item sub type`). The guessed argument
order (`name, 1, "", 111`) created a child named EtherCAT Master at
`TIID^EtherCAT Master` whose `ItemSubType` was **1**, not 111 — not a trustworthy
EtherCAT plug. **I/O import never calls CreateChild.** PLC import does, with the
documented `(name, nSubType, before, vInfo)` order — the I/O-probe order was
copied into `HostImport` and live XAE refused it (Findings, 2026-08-22).

**Product (2026-08-21).** Export writes `Devices/Devices.devices.yaml` (the node's
Name; a folder literally named `I/O` is not a Windows path). Identity is
Automation Interface `PathName`. Import does not apply the file. Gate: yaml
present, I/O skip gone, two textual exports include the yaml byte-identical, ST
overwrite still round-trips. NC / safety / C++ / visualisation stay skips.
Mappings stay out of the catalog.

---

## 5. Payload, hash, occupancy

The project is a **directory**. `PayloadPathFor` returns the folder containing the
`.tsproj`. Sync copies, hashes, backs up and restores that folder.

| Counts toward the project hash | Does not |
|---|---|
| `.TcPOU`, `.plcproj`, the `.tsproj` itself | `_Boot/`, `_CompileInfo/`, `.vs/` |
| LibPics and the rest of the tree | `*.suo`, `*.user`, `LineIDs.dbg`, `*.~u`, names starting `~` |

Skipping `.TcPOU` would hash a pointer and call the program unchanged. LineIds
*inside* `.TcPOU` are a finding if they churn on idle open, not a reason to skip the
program files. They do not appear in the exported `.st`.

### Occupancy (measured 2026-08-21, live TcXaeShell window)

`OccupancyMarkerFor` is `ChangeExtension(tsproj, ".~u")`. On this fixture that is the
same path as `ChangeExtension(sln, ".~u")` — one stem. Existence is Held. The body is
parsed as user / machine / pid when those three lines are present; stale PID uses
`ProjectOccupancyProbe.Resolve`. A leftover after a crash stays Held rather than
guessed Free.

**GUI occupancy — measured 2026-08-21, TcXaeShell 41772 holding the committed
`TC3_PlcSample_reStrText` (do not save; 4024.4 on 4026.25 converts).** Shell started
10:00:33. `_Boot/` appeared at 10:01:01. `<stem>.~u` first seen at 10:01:33 (~60 s
after launch), 43 bytes, CRLF:

```
elli_
ROG-JVM
41772
<DateTime ticks>
```

Pid **41772 matches the live `TcXaeShell.exe`**. The fourth line is a tick timestamp
and is ignored by `ParseOccupancy` (first three lines only). Idle rewrite is every
**~5 s**, not in place: XAE writes `<stem>.~u~` and renames it over `<stem>.~u`. A
45 s poll saw nine SHA changes of the marker (ticks only) and watcher events
Created/Changed/Deleted on `.~u~` then Changed on `.~u` — the same two-file hop
CODESYS uses, which is why `TransientPatternsFor` is `.~u*` rather than `.~u`.
`ExportHeldViaCopy` is **true**: the lock is that sidecar, so a copy of the
directory at another path is unencumbered the same way a held `.project` is.
Sync copies under `.versio/local/open/` and the host opens the copy; import
still waits (the IDE holds the original in memory).

**Held-copy with the GUI still holding the original — measured 2026-08-21,
10:32, TcXaeShell 41772 + Versio 25012.** Copy landed, host opened the copy
(`TcXaeShell.DTE.17.0`), exported **36 objects in 27.4 s** including
`Devices/Devices.devices.yaml`. Two DTE on different paths works. A follow-on
job then failed: the conventional export folder is nested *inside* the
payload directory, so swapping the mirror looked like a Project change,
a second copy started on the same `.versio/local/open/{id}/` path, and the
first job deleted that folder on the way out. The lock went Failed even
though the tree is on disk. Finding at the top of `docs/FINDINGS.md`.
Marker-gone-on-close was not measured this session (the window was still
open).

Headless probe/export/import of a TEMP copy completed with no interactive dialog.

License: the engineering license folder was empty on inspect; the gate did **not** hit a
license refusal on 2026-08-19. A license failure is rewritten as
`TwinCAT XAE refused the session (license): …` so the skip is honest.

---

## 6. Protocol

JSON-lines, marker `@@VERSIO@@`, field `t` — the same shape as the other vendors so the
heartbeat and the log filter are not a third language. The emitter is C#, in the net9 host.

Events: `info`, `progress`, `object`, `warning`, `fatal`, `done`. Heartbeat is the
runner's, not the host's: a silent host looks like a hang, so the 20-minute floor is the
backstop.

---

## 7. Gate fixture

```
fixtures/projects/TC3_PlcSample_reStrText/TC3_PlcSample_reStrText.tsproj
```

Owner-supplied Beckhoff sample (library-documentation example): sibling `.sln`, nested PLC
`LibraryDocumentationSample\` (35 `.TcPOU`). **No LFS.** Do not copy `.vs\` or `_Boot\`.
The packed `.tszip` is left out; the format still *sees* `.tszip` as a template so adoption
refuses. Work only on a **copy** under `%TEMP%`. Do not adopt it into this repository's own
`.versio` bindings — it is in `ignore` beside CleaningStation.

`TestPaths.TwinCatProject()` returns the `.tsproj` or null. The gate is
`[Collection("twincat")]` so two Facts cannot share one DTE. Skips: not Windows, no
`TcXaeShell.DTE.17.0`, fixture missing, host missing, conversion/license/held/timeout.

---

## 8. What the gate proved (2026-08-19)

`tests/Versio.Plc.TwinCat.Tests` — **6/6 green in 5 m 14 s**, zero skips, on a `%TEMP%`
copy, 2026-08-22. Close XAE and Versio first. Earlier: **5/5 in 3 m 4 s** on
2026-08-21 over I/O export-only (`DEVICES`) — yaml present, I/O skip gone, two
textual exports include the yaml byte-identical, ST overwrite still round-trips.
Comment apply is not this gate. Folder move is not this gate.

| Job | Cost on this install |
|---|---|
| Probe (TEMP copy) | **~19 s** |
| Export | **~22 s** |
| Two unchanged textual exports | **~44 s** together; `.st` bytes matched |
| Import mutation + line-deletion round-trip | **~1 m 49 s** |
| Create + rename + object-delete round-trip | **~2 m 18 s** |

The overwrite Fact mutates one `.st` with a comment, imports the planner's `ApplyText` plan,
re-exports, asserts the marker, then **deletes that line**, imports again, and asserts it
is gone. That is ST overwrite + the inverse edit.

The structural Fact writes a unique `FUNCTION_BLOCK VersioGateFb_<stamp>`, imports
`Create`, re-exports (header present, no folder asserted — create resolve is the
declared name only), renames file and declaration with `departed` pairing, imports
`Rename`, then deletes the `.st` and imports a hash-bearing `Deletion`. CreateChild
arity / ST `vInfo` / `DeleteChild` were host fixes the Fact named — see Findings.

---

## 9. Open items

The PLC-software **ST overwrite** loop is done. This section is what a later session
should not have to re-derive.

### Done (gated against live XAE)

1. ~~Detect `.tsproj`, probe `TcXaeShell.DTE.17.0`.~~
2. ~~Export TIPC as `.st`; I/O as `*.devices.yaml`; coverage-skip TINC / safety / C++ / visualisation.~~
3. ~~Two unchanged textual exports byte-identical.~~
4. ~~ST overwrite and the inverse line-deletion, re-exported.~~
5. ~~Refuse conversion on the bound path; allow it on throwaway copies (`%TEMP%` and `.versio/local/open/`).~~
6. ~~`IOleMessageFilter`; version from `Build_*`, not the VS shell ProductVersion.~~
7. ~~I/O catalogs (`Devices/Devices.devices.yaml`, form `DEVICES`, export-only). Comment does not persist through Save; never CreateChild.~~
8. ~~Live XAE window occupancy — sibling `.~u`, rewrite via `.~u~` every ~5 s (2026-08-21).~~
9. ~~Create / object-delete / rename against live XAE (2026-08-22). CreateChild documented arity + ST vInfo; `Parent.DeleteChild` after PathName + live-hash.~~

### Wired, not an XAE Fact yet

Folder move is planned as `IsMove`. Sync will offer it through the existing
confirm-import dialog. It has **not** been walked against live XAE.

| Item | What the host does | Gap |
|---|---|---|
| **Folder move** | Planned as `IsMove`; host sets `Name` only | Relocating under a new parent is unmeasured, same first-loop bargain as TIA. A Fact that moved `Motor.st` between folders would import as `Name = "Motor"` and re-export in the old folder |

### Not measured

- ~~**Held-copy export with the GUI still holding the original.**~~ Measured
  2026-08-21: 36 objects in 27.4 s. The follow-on Failed lock is a separate
  defect (nested export path + shared scratch folder) — see FINDINGS.
- **Marker gone on close.** GUI occupancy is the same `.~u` as the format watches
  (§5); the window was still open when that was recorded, so delete-on-close was
  not seen this session.
- **App eyeball.** Adopt a `%TEMP%` copy in the running window and read the log. Never
  this repository's own bindings.
- **`ConsumeXml` / import when IDispatch rejects `DeclarationText` set.** Export already
  falls back to `ProduceXml`. Import does not invent a second write path.
- **Idle-open LineId churn inside `.TcPOU`.** Exported `.st` is clean; the on-disk XML
  was not hashed across an unchanged open.

### Not this queue (coverage skips)

IO **mapping** (process image / `Var`), NC axes, Safety (`tspproj`), C++ projects,
visualisation bindings, compile-after-import, library YAML, a 54-test real-XAE suite.
CreateChild of EtherCAT terminals. Archives (`.tszip`) and safety projects (`.tspproj`)
are seen and refused. Same bargain as TIA PlugNew / topology / HMI / `F_*`.
I/O catalogs ship **export-only** 2026-08-21 (§4.5). Comment apply does not persist.
