# Driving TwinCAT 3

**Date:** 2026-08-20 · Beside `Versio.Plc.TwinCat*` — one vendor's docs, beside its
projects. How TwinCAT XAE is driven, what the Automation Interface actually does in
this host, and what is still open.

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
I/O, NC, safety, C++ and visualisations are coverage skips.

---

## 1. The three projects

| Project | Role |
|---|---|
| `Versio.Plc.TwinCat` | Pure. Format (`.tsproj` working; `.tszip` / `.tspproj` as templates so adoption refuses), directory payload, occupancy, transients, import planner, ST header, `TwinCatJob` / `TwinCatProtocol` |
| `Versio.Plc.TwinCat.Infrastructure` | Driven. Locator (`TcXaeShell.DTE.17.0` preferred over 15.0), `LocalTwinCatRunner`, mutex `Global\Versio.TwinCat.SingleInstance`, `TwinCatPack.Register()` |
| `Versio.Plc.TwinCat.Host` | net9-windows x64 exe. `[STAThread]`, message filter, late-bound COM. Split: `Program` / `MessageFilter`, `HostApp` (session), `HostExport`, `HostImport`, `HostCom`, `HostText` |

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

A **`%TEMP%` copy** is the conscious conversion the product allows: the runner sets
`allowConvert` only when the `.tsproj` sits under `Path.GetTempPath()`. Gate Facts copy the
fixture there first. The committed tree is never opened.

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

The job schema (`TwinCatJob` / `JobReader`):

| Field | Meaning |
|---|---|
| `op` | `probe` / `export` / `import` |
| `schema` | 1 |
| `project` | absolute path to the `.tsproj` |
| `out` | export destination (probe, export) |
| `src` | import source directory |
| `progId` | `TcXaeShell.DTE.17.0` (or the locator's choice) |
| `install` | `…\TwinCAT\3.1` folder |
| `version` | TwinCAT build string from `Build_*`, e.g. `3.1.4026.25` |
| `save` | import only; export and probe do not save |
| `timeoutSeconds` | at least 1200 |
| `allowConvert` | **only** when the `.tsproj` is under `%TEMP%` |
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
  → probe | export | import
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
| `DTE.ExecuteCommand("File.SaveAll")` | import, when `save` | **The save that works.** `ITcSysManager.Save` is `DISP_E_UNKNOWNNAME` (0x80020006). Fallback: `SaveProject`, then `project.Save("")` |
| `Solution.Close(false)` / `DTE.Quit` | finally | Best effort; a leftover shell is a finding, not a second fatal |

Attach-to-running is **not used**. Two DTE instances on one project are not safe — that is
why the runner takes a machine-wide mutex.

### 4.2 The tree (TIPC is lazy)

TwinCAT's System Manager is a forest. Only the PLC branch is the round-trip subject:

| Path prefix | What it is | Versio |
|---|---|---|
| `TIPC` | PLC projects | **walked** — export / import ST |
| `TIID` | I/O devices and mappings | coverage skip (`SERVICE_OBJECT`) |
| `TINC` | NC / motion | coverage skip |
| Safety / C++ / visualisation | other project kinds | coverage skip (`ItemSubType` 619 is visualisation) |

**Lazy load.** `LookupTreeItem("TIPC^Name^Name Project")` fails until the nested PLC
project is touched. Measured: `Child` is a **1-based property indexer**
(`item.Child[i]`); `Child(i)` as a method did not. The host waits up to ~45 s (90 ×
500 ms): it looks up the gate fixture's known path first, then enumerates `TIPC`'s
children, reads `Name` / `ChildCount`, and calls `ProduceXml` to force the nested
`… Project` node into existence.

Identity in the manifest is that Automation Interface **`PathName`**
(`TIPC^LibraryDocumentationSample^LibraryDocumentationSample Project^…`), not a GUID
inside `.TcPOU`.

### 4.3 Export (TIPC only)

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
| Index by `PathName` (manifest `guid`) and `Name` | apply / delete / rename | Rebuild after each create |
| `DeclarationText` / `ImplementationText` set | `ApplyText` / create | No `ConsumeXml` fallback. If IDispatch rejects the set, the import fails rather than writing `.TcPOU` on disk |
| `CreateChild(name, 1, "", subType)` | create | `TREEITEMINSERT` last = 1. Subtype from the ST header |
| `item.Name = newName` | rename | **Name only.** A folder move is planned as `IsMove`; relocating under a new parent is not gated |
| `item.Delete()` | delete | Skipped when `cannotVerify` is set. Failure emits a warning and **keeps** the object |
| `File.SaveAll` | when `save` | See §4.1 |

The planner is the same `ImportPlan` the other vendors fill (`ApplyText` / `Create` /
`Skip` / `Deletions` / `Renames`). Five deletion preconditions stay in the planner, not
in Sync.

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

### Occupancy

| Fact | Value |
|---|---|
| Marker | `Path.ChangeExtension(tsproj, ".~u")` — same stem as the sibling `.sln` |
| Verdict | Existence is Held. Body is parsed as user / machine / pid when those three lines are present; stale PID uses the existing `ProjectOccupancyProbe.Resolve` |
| `ExportHeldViaCopy` | **false.** Exclusive DTE open. Copy-aside of the XML tree is not a second DTE on one project |
| Live XAE window | **Not separately watched this session.** Third-party notes and the format treat sibling `.~u` as the marker; a leftover after a crash stays Held rather than guessed Free |

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

`tests/Versio.Plc.TwinCat.Tests` — **5/5 green in 3 m 14 s**, zero skips, on a `%TEMP%`
copy. Close XAE and Versio first.

| Job | Cost on this install |
|---|---|
| Probe (TEMP copy) | **~19 s** |
| Export | **~22 s** |
| Two unchanged textual exports | **~44 s** together; `.st` bytes matched |
| Import mutation + line-deletion round-trip | **~1 m 49 s** |

The import Fact mutates one `.st` with a comment, imports the planner's `ApplyText` plan,
re-exports, asserts the marker, then **deletes that line**, imports again, and asserts it
is gone. That is ST overwrite + the inverse edit — not object create / delete / rename.

---

## 9. Open items

The PLC-software **ST overwrite** loop is done. This section is what a later session
should not have to re-derive.

### Done (gated against live XAE)

1. ~~Detect `.tsproj`, probe `TcXaeShell.DTE.17.0`.~~
2. ~~Export TIPC as `.st`; coverage-skip TIID / TINC / safety / C++ / visualisation.~~
3. ~~Two unchanged textual exports byte-identical.~~
4. ~~ST overwrite and the inverse line-deletion, re-exported.~~
5. ~~Refuse conversion on the bound path; allow it only under `%TEMP%`.~~
6. ~~`IOleMessageFilter`; version from `Build_*`, not the VS shell ProductVersion.~~

### Wired, not an XAE Fact yet

These exist in `TwinCatImportPlanner` and `HostImport`. Sync will offer them through the
existing confirm-import dialog. They have **not** been walked against live XAE.

| Item | What the host does | Gap |
|---|---|---|
| **Create** | `CreateChild` + set texts | No Fact that a new `.st` becomes a live POU |
| **Object delete** | `Delete()` when `cannotVerify` is empty | The gate deleted a *line*, not a POU |
| **Rename** | `Name = newName` | No Fact |
| **Folder move** | Planned as `IsMove`; host sets `Name` only | Relocating under a new parent is unmeasured, same first-loop bargain as TIA |

### Not measured

- **Live XAE window occupancy.** Sibling `.~u` is the marker the format watches; a
  watcher while the IDE holds the fixture was not run this session.
- **App eyeball.** Adopt a `%TEMP%` copy in the running window and read the log. Never
  this repository's own bindings.
- **`ConsumeXml` / import when IDispatch rejects `DeclarationText` set.** Export already
  falls back to `ProduceXml`. Import does not invent a second write path.
- **Idle-open LineId churn inside `.TcPOU`.** Exported `.st` is clean; the on-disk XML
  was not hashed across an unchanged open.

### Not this queue (coverage skips)

IO mapping, NC axes, Safety (`tspproj`), C++ projects, visualisation bindings,
compile-after-import, library YAML, a 54-test real-XAE suite. Archives (`.tszip`) and
safety projects (`.tspproj`) are seen and refused. Same bargain as TIA
hardware/HMI/`F_*`.
