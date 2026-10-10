# CODESYS File-Based Storage in Versio

**Date:** 2026-10-10. **Branch:** `FileBasedStorage`.
**This is a design record, not the live queue.** What Versio is today lives in
[`../STATUS.md`](../STATUS.md). This document says how a File-Based Storage project is shown
and drawn, and why each piece sits where it does.

| | |
|---|---|
| Document | Showing a CODESYS File-Based Storage (FBS) project as a project: a tree, ST laid out like the IDE, member tables, ladder drawn and diffed. Never synced |
| Fixture | [`fixtures/projects/RefrigeratorControl_FBS/RefrigeratorControl.fbsproj/`](../../fixtures/projects/RefrigeratorControl_FBS/), the same machine as the classic `RefrigeratorControl` fixture, saved by CODESYS 3.5.22.40 with FBS 1.0.0.0 (6044ed1) |
| Out of scope | Writing to an FBS project, any CODESYS process for one, SFC, drawing tasks and devices from their XML |

---

## 1. Why this exists, and why it is not a synced format

CODESYS shipped File-Based Storage 1.0.0.0 on 2026-08-06, for CODESYS 3.5.22 and later. It
needs a Professional Developer Edition licence. A project stops being one compressed
`.project` file and becomes a folder `Name.fbsproj\` of readable files. That is the job
Versio's export does for a classic project, done by CODESYS itself. Export, import, the
direction lock and the occupancy wait buy nothing on top of it: the files a person edits *are*
the project.

What CODESYS does not provide is a way to **read** the project without CODESYS. The 0.9
whitepaper says no licence-free read-only mode is implemented; sharing with someone unlicensed
means converting back to a `.project`. A reviewer, a maintenance engineer, or the Outpost web
page ([`../outpost/PLAN.md`](../outpost/PLAN.md)) still has to see a ladder rung, a struct's
members, or a method's code in context. Versio already draws all of that for its own mirror.
This document points that drawing at an FBS folder.

**Agreed with the owner, 2026-10-10:**

- An `.fbsproj` folder is **detected and shown as a project**, the way a `.project` is. It
  **never** gets a binding, a lock, a watcher job, an export, or an import.
- **Neutral code stays neutral.** Detection, the tree, and the rendering hooks are members of
  the seam in `Versio.Core`. Every CODESYS fact (extensions, `^` folders, file headers, type
  GUIDs, the parsers) lives in `Versio.Plc.Codesys`.

### Why not "a format with no toolchain"

That was the obvious shortcut, and it would block the owner's commits. A format whose runner
is missing is not passive today:

1. `SyncViewModel.AutoAdoptAsync` adopts every non-template project it finds.
2. `RunnerFor` hands it an `UnavailableToolchainRunner`, so catch-up fails its export.
3. The lock goes `Failed`.
4. `CommitGate` then answers `Blocked` or `ExcludeAndAsk`.

Switching `PlcCapabilities` off does not help either. Four of its five flags (`TextualExport`,
`TextualImport`, `ObjectCreation`, `Headless`) are set by every pack and read by no source code;
only `ExportHeldViaCopy` is consulted. So a source project takes its own route, from the scanner
to its row, and never reaches `Adoption`, `OrphanScanner`, `SyncOrchestrator`, the watcher, or
`CommitGate`.

---

## 2. What the fixture measured

All of this was read off the committed fixture, not the whitepaper. The 0.9 whitepaper
described `__DECLARATION__` / `__METADATA__` dividers that 1.0 no longer writes.

### Layout

- **Child objects** live in a folder named after the parent's *file*, plus `^`:
  `Device.device.xml.v3^/Plc Logic.plclogic.xml.v3^/Application.application.xml.v3^/…`.
  An FB's methods and properties sit in `FB_Alarm.fb.st^/`.
- **A user folder** is a plain directory that holds `.folder_meta.json`
  (`{"objectGuid": "…"}`). Empty folders are real: `Library/Alarm/ITFs/` holds only its meta
  file. A folder inside an FB (`FB_Alarm.fb.st^/Private/`) is the same.
- **Names are hex-escaped** where the file system needs it: `__3cEmpty__3e 0.slotdevice.xml.v3`
  is the slot `<Empty> 0`.
- **Hidden CODESYS state:**
  - `.auxiliaries/` holds caches;
  - `.sidecars/` holds `*.opt` user options, a 320 KB precompile cache, and `.~u`;
  - `.FBS_Project_Related/`;
  - `Workspace.fbsws`;
  - `Preferences/*.prefs.json`.

  None of these is an object in the IDE's tree. `.sidecars/` is kept out of git
  (b15d365).
- **`.sidecars/.~u` is the occupancy marker.** It holds user, machine, pid and ticks, the same
  four lines `CodesysProjectFormat.ParseOccupancy` already reads for a classic `.project.~u`.
- **Line endings:** every object file is LF. Only the hidden state files are CRLF.

### Structured Text objects

The ST objects are `.prg.st`, `.fb.st`, `.meth.st`, `.prop.st`, `.gvl`, `.enum` and `.struct`.
The 0.9 whitepaper also names `.action.st`, `.transition.st`, `.union`, `.alias` and `.itf`.
Those are not in the fixture, and the whitepaper's `.meth` became `.meth.st` in 1.0, so they are
treated as unconfirmed. A function's extension is not known at all. Every ST object
opens with a header:

```
(* METADATA
{
	"v3Meta": {
		"objectGuid": "01bf9639-…",
		"subObjects": { "get": { "objectGuid": "27d95739-…" } }
	}
}
*)
```

After the header comes the object exactly as the IDE shows it: attributes, the declaration,
the body, and the closing keyword. **There is no divider between declaration and body.** The
split has to be found from IEC syntax, and four cases in the fixture show what that has to
handle:

| Case | Example | What it means for the split |
|---|---|---|
| Body after `END_VAR` | `PLC_PRG.prg.st`, `Simulation.prg.st` | The declaration runs to the last `END_VAR` |
| No `VAR` block | `M_Reset.meth.st`: `METHOD PUBLIC M_Reset : BOOL`, then code | The declaration is the header line alone |
| Closing keyword on a code line | `P_Active.prop.st`: `P_Active := State = E_AlarmState.Active;END_PROPERTY` | Cutting the keyword out would hide code, so the line stays whole |
| Declaration only | `Glob_Var.gvl` is wrapped in `NAMESPACE Glob_Var … END_NAMESPACE`; `.enum` and `.struct` are `TYPE … END_TYPE` | There is no body |

### Everything else: `*.xml.v3`

```
__V3_META__
{
	"objectGuid": "b18a62c7-…",
	"objectTypeGuid": "6f9dac99-8de1-4efc-8465-68ac443b7d08",
	"embeddedObjectTypeGuids": [ "a9ed5b7e-…", "25e509de-…" ]
}
__V3_CONTENT__
<?xml version="1.0" encoding="utf-8"?>
<Single xml:space="preserve" Type="{6f9dac99-…}" Method="IArchivable">…
```

The content is the object's own archive XML. It does **not** have the
`ExportFile/StructuredView/EntryList` envelope that a classic `export_native` writes. Inside,
the shape is identical: `Signals.pou.xml.v3` is the same LD network list as the classic mirror's
`Signals.xml`, with Implementation `{25e509de}`, `DefaultViewMode=Ld`, three networks and the
same element type GUIDs. It differs only in tab indentation and the missing envelope. **One
parser serves both.**

### What the fixture does not have

The fixture has no FBD, CFC or SFC POU, and no interface. Its LD has no OR branch and no
negated, edge or set/reset element. §5 lists the specimens wanted.

---

## 3. The pieces

### 3.1 The seam (Core, no vendor names)

New default members on `IPlcProjectFormat`. Each defaults to "none", so TIA, TwinCAT and every
stand-in format in the tests are untouched:

| Member | Answers |
|---|---|
| `SourceProjectExtensions` | Directory extensions whose folder **is** a project stored as its own readable source |
| `DescribeSourceProject(projectPath, relativeFiles)` | The objects of such a project as a `ProjectManifest`, from its file list alone. It does no IO, so it stays testable without a disk |
| `SyntaxFor(relativePath)` | How to highlight a file whose extension the neutral catalog does not know |
| `SourceSections(relativePath, content)` | Where a source file's hidden header, declaration and body are, when the file has no Versio delimiter |

The rendering members already exist: `TryParseLadder`, `TryParseMemberTable` and `CodeBody`.
The pane asks every registered format, so they need no change to reach an FBS file.

### 3.2 Detection and the row (Infrastructure, App)

- **`RepoScanner`** records a directory whose name ends in a claimed source extension in
  `RepoSnapshot.SourceProjects`, and does not walk into it.
- **The Project panel and the Explorer** each get one row per source project, with no binding.
  The tree comes from `DescribeSourceProject` over git's working-tree listing, through the
  existing `WorkspaceTreeBuilder`, with `ExportPath` set to the folder itself.

### 3.3 CODESYS (Versio.Plc.Codesys)

| Piece | Job |
|---|---|
| `Ladder/CodesysLadderParser` | LD/FBD network list, classic `.xml` and FBS `.xml.v3`, into the neutral `LadderDocument` |
| `FileBased/CodesysFileBasedLayout` | Extensions, `^` nesting, folder metadata, name escapes, and the hidden folders, turned into a manifest |
| `FileBased/` ST sections | The header and the declaration/body split described in §2 |
| `TryParseMemberTable` | `.struct`, `.enum`, `.gvl`, and POU interfaces |

**The network list is an expression tree, not a graph.** TIA writes parts and wires. CODESYS
writes, per network, one assignment: a right-hand expression of nested boxes (`AND`, `OR`,
`TON`, a user FB) whose leaves are operands, plus a list of outputs (coils, jumps). The neutral
model is a graph on purpose, and `LadderModel` already says a tree "turns into one trivially":
- a Boolean operand under `AND`/`OR` is a contact;
- `AND` is series and `OR` is parallel;
- any other box is a box, with its nested inputs wired to its pins;
- the outputs hang off the end of the expression.

---

## 4. Order

Each step is one commit, and each builds and passes on its own.

1. **The LD/FBD parser.** It needs nothing from FBS, and it also draws the classic mirror's
   `Signals.xml`, which shows as raw XML today. The FBS copy and the classic copy must parse to
   equal bodies.
2. **The source-project seam and detection.** No binding, no lock, no gate. This is tested by
   breaking it on purpose.
3. **The tree.** First check whether `ProjectTreeBuilder` absorbs the `^` segment of a child's
   path when `Parent` nests it. If it does not, add one neutral tree-path field to
   `ProjectObject`, never `^` logic in Core.
4. **CODESYS describe**, over the fixture's real file list.
5. **The syntax and section hooks**, and CODESYS's answers.
6. **Member tables.** Check `StDeclarationParser` first against the header, `{attribute}`
   lines, `NAMESPACE`, `VAR PERSISTENT` and tab-aligned declarations.
7. **"Open in CODESYS" on the row,** read from `.sidecars/.~u` through the existing
   `ParseOccupancy`. It is shown only and never gates anything.
8. **CFC.** This needs a new neutral language value, positions on nodes, and a layout that
   places rather than recovers structure. It starts from the classic
   `MachineWithMembers/…/PLC_PRG.xml`, which is CFC.

---

## 5. Specimens still wanted

Saved by CODESYS into this fixture, or into a sibling:

- LD with an OR branch, negated and edge contacts, set/reset coils, and a nested box;
- one FBD POU, one CFC POU, and one interface;
- a second commit that changes one rung and one ST line, so the HEAD diffs have something to
  compare.

The jump in network 1 of `Signals` (`Flags=4` on an output item whose `Boolean` is `False`,
pointing at network 3's label `DoorIsOpen`) is read that way from one example. The parser
treats it as an inference until a second specimen agrees.
