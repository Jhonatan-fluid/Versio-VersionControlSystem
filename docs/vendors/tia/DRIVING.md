# Driving TIA Portal

**Date:** 2026-08-19 · Beside `Versio.Plc.TiaPortal*` — one vendor's docs, beside its
projects. How TIA Portal is driven, and what was measured rather than assumed.

| | |
|---|---|
| Document | **The TIA Portal integration, end to end.** Which Openness surface Versio uses, how the net48 host is launched, what crosses the boundary, what every job does, and where the seams and the gaps are |
| Audience | An engineer who has to change the export, the import, the host or the runner — or add a fourth vendor beside the first three |
| Related | [`STATUS.md`](../../STATUS.md) · [`archive/HANDOFF-Siemens2.md`](../../archive/HANDOFF-Siemens2.md) · [`SAFEGUARDS.md`](../../SAFEGUARDS.md) · the first vendor's [`vendors/codesys/DRIVING.md`](../codesys/DRIVING.md) · TwinCAT's [`vendors/twincat/DRIVING.md`](../twincat/DRIVING.md) |

Versio is not a TIA Portal tool. Neutral code must not grow any of this. TwinCAT is the same
kind of directory project; that is `PayloadPathFor` / `CopyTreeAsync` growing, not a brand
leaking.

---

## 0. In one paragraph

Versio never parses an `.apXX` file. A TIA project is a **directory** whose pointer is ~150 KB
and whose program lives in `System/` (~445 MB on the gate fixture). Openness — Siemens's
automation API — is **.NET Framework 4.8**; Versio is **net9.0-windows**. They cannot share a
process. A net48 helper (`Versio.Plc.TiaPortal.Host`) loads `Siemens.Engineering*.dll` from the
install at runtime, opens the project **without a UI**, and talks back over stdout with the same
`@@VERSIO@@` JSON-lines the first vendor's scripts use. The host is the analogue of
`CODESYS.exe --noUI --runscript`. Siemens DLLs are never shipped.

PLC software (SCL blocks, UDTs, tag tables) is the round-trip subject. Hardware, HMI, safety
and technology objects are coverage skips.

---

## 1. The three projects

| Project | Role |
|---|---|
| `Versio.Plc.TiaPortal` | Pure. Format (`.ap17`–`.ap21`, `.alXX` as template), directory payload, occupancy, transients, import planner, SCL header, SimaticML scrubber, `TiaPortalJob` / `TiaPortalProtocol` |
| `Versio.Plc.TiaPortal.Infrastructure` | Driven. Locator, toolchain (refuses if several Portal versions are installed), `LocalTiaPortalRunner`, `TiaPortalPack.Register()` |
| `Versio.Plc.TiaPortal.Host` | net48 x64 exe. Reflection / `dynamic` against Openness. `[STAThread]`. `OpennessLoader` installs `AssemblyResolve` **before** any Openness type is touched. Split: `HostApp` (session), `HostExport`, `HostImport`, `HostReflection` |

`Versio.App` has a second `ProjectReference` and calls `TiaPortalPack.Register()` **after**
`CodesysPack.Register()`. A missing `vendor` field on an old binding still deserialises as
the first vendor; the session probes every registered format and no longer pins
`PlcFormats.All[0]`. The log filter includes `TiaPortalLog.Category` (`TiaPortal`),
declared on the pack rather than in the neutral `LogCategory` table.

The host does **not** reference `Versio.Core` (different TFM). The JSON job file is the
contract. `CopyHostToReferrer.targets` copies the exe to `tia\` beside Infrastructure, the app,
and the real-TIA tests, because a `ProjectReference` cannot carry a net48 output into net9.

---

## 2. Setup (measured 2026-08-19)

### 2.1 Install

| Fact | Value |
|---|---|
| Portal | `C:\Program Files\Siemens\Automation\Portal V21` |
| Openness API | `…\PublicAPI\V21\net48\` |
| Shape | **Modular DLLs**, not the historical single `Siemens.Engineering.dll` |
| Required | `Siemens.Engineering.Base.dll`, `Siemens.Engineering.Step7.dll` |
| Host TFM | net48 x64 |

`TiaPortalLocator` walks `Program Files\Siemens\Automation\Portal V*` and takes the `PublicAPI\<version>\net48` folder that contains `Siemens.Engineering.Base.dll`. Version is the folder name (`V21`), not a file version — guessing a newer PublicAPI is how `OpenWithUpgrade` happens by accident.

**Do not call `OpenWithUpgrade`.** That silently upgrades a project, the same class of mistake
`ProfileResolver` refuses when several CODESYS profiles exist. The host looks up
`Projects.Open(FileInfo)` and refuses if that overload is missing.

Several Portal versions on one machine make the toolchain **unavailable** until one is picked.
A project opened against the wrong PublicAPI is an upgrade.

### 2.2 Windows group `Siemens TIA Openness`

Openness will not open a project unless the **process token** is in this local group. Adding
the account is not enough: the user must **sign out and in**. `whoami /groups` must show
`…\Siemens TIA Openness`.

Until membership is present, `Projects.Open` fails in ~3 s with the group error. The gate
skips with that reason rather than going red. That skip is the record on a machine without
Portal.

On this machine: group existed empty; `net localgroup … /add` needed elevation; after
`elli_` was added and a new logon, the token carried the group.

### 2.3 Firewall AllowList

The first successful connect also needs **Yes to all** on the Openness access dialog
(path + hash + timestamp of the host exe). Window title: `Openness access (0033:000666)`.

A debug rebuild of the host changes the hash and drops off the list. An unanswered dialog is:

```
Security error. The operation has timed out.
```

Measured at **8 min 53 s**. That string names neither `Access` nor `Openness`, so the gate
used to go red instead of skip until those words were added to the skip matcher.

### 2.4 Closing before a rebuild

A running `Versio.exe` locks its binaries. A running Portal / host holds the project
exclusively. Close **both** before rebuilding, same rule as the first vendor.

---

## 3. How the host is launched

```
Versio.Plc.TiaPortal.Host.exe <job.json>
```

`LocalTiaPortalRunner` writes the job beside `%TEMP%`, takes
`Global\Versio.TiaPortal.SingleInstance` (15 min wait), and reads stdout for `@@VERSIO@@`
lines. Timeout floor is **20 minutes** — a 488 MB project is not the first vendor's 20-second
session. `TypicalStartupCost` is **45 s** (measured warm probe 42 s, warm export 37 s).
Since 2026-08-20 the runner drains the async readers with a parameterless `WaitForExit()`
before reading the buffer (the timed overload is not a flush barrier — a successful run
could report "host exited 0"), logs malformed protocol lines instead of dropping them, and
refuses a plan-less import instead of fabricating an empty plan. Findings, 2026-08-20.

The job schema (`TiaPortalJob` / `JobReader`):

| Field | Meaning |
|---|---|
| `op` | `probe` / `export` / `import` |
| `schema` | 1 |
| `project` | absolute path to the `.apXX` |
| `out` | export destination (probe, export) |
| `src` | import source directory |
| `apiFolder` | `PublicAPI\V21\net48` |
| `install` | Portal folder |
| `save` | import only; export and probe do not save |
| `timeoutSeconds` | at least 1200 |
| `plan` | confirmed `ImportPlan` JSON; an import without one refuses to serialise |

---

## 4. Openness members Versio actually uses

Loaded by name so the host compiles without Siemens DLLs on the build machine.
`OpennessLoader` `LoadFrom`s every `Siemens.Engineering*.dll` in `apiFolder` and resolves
the rest; optional products (Safety, WinCC) may be absent.

Every call below is **reflection**. A missing overload is a refusal, not a guess.

### 4.1 Session

| Member | Used by | Notes |
|---|---|---|
| `new TiaPortal(TiaPortalMode.WithoutUserInterface)` | every job | STA thread required; MTA fails with a threading exception that looks like a missing install |
| `portal.Projects` | every job | |
| `Projects.Open(FileInfo)` | every job | **The only open used.** `OpenWithUpgrade` is not looked up |
| `project.Name` / `Path` | probe | |
| `project.Devices` | walk | |
| `device.DeviceItems` (recursive) | walk | PLC software is nested under the station |
| `deviceItem.Software` as `PlcSoftware` | walk | |
| `project.Save()` | import, when `save` | |
| `IDisposable` on project and portal | finally | |

Attach-to-running is a different design and is **not used**. Exclusive open: if Portal already
holds the project, `Open` fails and the runner reports it.

### 4.2 Walk (PLC software only)

| Member | Used by |
|---|---|
| `PlcSoftware.BlockGroup` / `.Blocks` / `.Groups` | export, import, probe counts |
| `PlcSoftware.TypeGroup` / `.Types` / `.Groups` | export; import find (`…/UDT/{name}`) |
| `PlcSoftware.TagTableGroup` / `.TagTables` / `.Groups` | export; import find (`…/TAG_TABLE/{name}`) |
| `block.Name`, `Number`, `ProgrammingLanguage`, `IsKnowHowProtected` | export |
| `block.Export(FileInfo, ExportOptions.None)` | SimaticML beside every object |
| `IGenerateSource.GenerateSource(FileInfo)` on the block | SCL / `.udt` |
| `ExternalSourceGroup.GenerateSource(collection, FileInfo)` | fallback when the block has no `IGenerateSource` |

Export walks emit `progress` (`phase` + `detail`). The runner already maps `t == "progress"`
onto `PlcProgress`.

Languages starting `F_` are safety — coverage skip. Know-how-protected blocks export for
review and are not imported.

### 4.3 Import (confirmed plan only)

| Member | Used by | Notes |
|---|---|---|
| `FindObject` over `BlockGroup`, `TypeGroup`, `TagTableGroup` | rename, delete | Same identity strings export writes. A UDT or tag table used to be a silent no-op |
| `obj.Name = newName` | rename | Identity is name+number, so this is the rename |
| `obj.Delete()` via `TryDelete` | delete | Refused when `cannotVerify` is set. If `Delete()` is missing or throws, emit a warning and **keep** the object — same class of refusal as an unverifiable delete. No second API is invented |
| `ExternalSourceGroup.CreateFromFile` / `CreateFrom` | SCL apply / create | Existing source of the same file name is deleted first |
| `externalSource.GenerateBlocksFromSource()` | SCL apply / create | V21: zero-arg only. `GenerateBlockOptions` is `None \| KeepOnError` — there is no `Overwrite`. KeepOnError created `*_1` clones; zero-arg then dropped `Level.scl` from the next export. See Findings. Do not invent a second API |
| `BlockGroup.Blocks.Import` / `TypeGroup.Types.Import` / `TagTableGroup.TagTables.Import` | native XML | Chosen from the identity (`…/UDT/…`, `…/TAG_TABLE/…`, else blocks). Handing a tag table to `Blocks.Import` fails "out of context" — measured 2026-08-19. SimaticML marked `XML_REVIEW` is **never** this path |

G12: one object, one importable file. SCL (or `.udt`) imports; the SimaticML beside it is
`XML_REVIEW` and is never applied back. Importing both puts the old body back.

### 4.4 Hardware surface (measured 2026-08-20)

Product Export writes Comment catalogs as `*.devices.yaml` (form `DEVICE_COMMENT`).
The host op `hwprobe` (`tools/tia/Probe-Hardware.ps1`) is the measurement that led to
that slice. Work copy under `%TEMP%`; the probe never saved.
Two untouched dumps of CleaningStation were SHA-256 identical
(`4a69e75cfc2230e5c3feea47011c175f1679e676eea11a25811aa0a39aadc148`). Raw dump:
[`probes/hwprobe-CleaningStation-2026-08-20.json`](probes/hwprobe-CleaningStation-2026-08-20.json)
(api type list stripped; 1,149 `Siemens.Engineering.HW.*` names were present).

**Render — yes.** One device in `project.Devices`: `ET 200SP station_2`
(`System:Device.ET200SP`). Thirteen `DeviceItem`s, including:

| Name | What it is | TypeIdentifier / order |
|---|---|---|
| Rack_0 | rack | `System:Rack.ET200SP` |
| CleaningStation | CPU 1514SP-2 PN | `OrderNumber:6ES7 514-2DN03-0AB0/V4.0` |
| BA 2xRJ45 | bus adapter | `OrderNumber:6ES7 193-6AR00-0AA0/V0.0` |
| Server module_1 | server module | `OrderNumber:6ES7 193-6PA00-0AA0/V1.1` |
| PROFINET interface_1 / _2 | PN interfaces | position 32768 / 33024 |
| Port_1 / Port_2 | PN ports | |

`GetAttributeInfos` (not `GetAttributes`) lists names and write access. The CPU has 83
attributes, 56 writable (cycle time, comment, plant designation, …). `ShortDesignation`
and `OrderNumber` are readable. `IpAddress` / `StartAddress` appear in the **name** list
and did not return via `GetAttribute` on this run. HMI is not in `Devices` or
`DeviceGroups` (empty). `PositionNumber` is overloaded — rack slots, PN-encoded 32768+,
OPC UA at 254 — so subtracting gaps is not “empty ET 200SP slots”.

**Topology — API yes, this fixture no partners.** `NetworkInterface` and `NetworkPort`
exist as `GetService` types. Interface_1 is `IoController`, nodes `X1`/`X2`, ports
`Port_1; Port_2`. `X1.ConnectedSubnet` is `Plantbus`. `project.Subnets` has that one
subnet with node `X1` on `PROFINET interface_1`. `ConnectedPorts` is empty on every
port. `IoConnector` / `IoController` / `IoSystem` types live under
`Siemens.Engineering.HW.*`, not `Features.*` (the first probe looked in the wrong
namespace). This project has **one** station, so device-to-device “who is on which port”
has no partner to name. `NetworkPort.ConnectToPort` / `DisconnectFromPort` exist.

**Import — partial, not a rack rebuild.** Methods present: `PlugNew` / `PlugCopy` /
`PlugMove` / `CanPlugNew` / `SetAttribute` / `SetAttributes` / `Delete`. Missing:
`CreateWithItem`, `Unplug`, device `Import` / `Export`. Comment `SetAttribute` applied
and restored on the station (`versio-hwprobe-temp`); project **not** saved. That is
grade 2: a scalar will take; plugging a module still needs the HSP/GSD for that
`TypeIdentifier` on this PC, and this dump is not a measured round-trip of the rack.
Planner `DEVICES` stays export-only (CODESYS I/O). This vendor writes form
`DEVICE_COMMENT` so Comment can ApplyText without a missing file unplugging the station.

**Product (2026-08-20, owner picked narrow import).** Export writes
`ET 200SP station_2/ET 200SP station_2.devices.yaml`. Identity is parent chain +
name + `@PositionNumber` (two `Port_1` on this fixture would otherwise collide).
Import applies **Comment** via `SetAttribute`, verifies the read-back, never PlugNew. Gate Fact: yaml present,
Hardware skip gone, two textual exports include the yaml byte-identical, Comment
mutation round-trips with SCL/UDT. HMI / safety / technology stay skips. Topology
is not in the catalog.

---

## 5. Payload, hash, occupancy

The project is a **directory**. `IPlcProjectFormat.PayloadPathFor` returns the folder
containing the `.apXX`. Sync copies, hashes, backs up and restores that folder.

| Counts toward the project hash | Does not |
|---|---|
| `System/` (the program) | `Logs/`, `tmp/`, `Vci/` |
| `.apXX`, `IM/`, `UserFiles/`, … | root-level `<stem>.info` (occupancy) |
| | any file whose name starts with `~` (`System/~PEData.1`) |

Skipping `System/` would hash a 150 KB pointer and call a half-gigabyte project unchanged.
`Vci/` is Siemens's competing git exporter sitting inside the tree — hashing it would make
somebody else's workspace look like a program change. Versio must not write into it.

Ten folder backups are bounded and heavy (~5 GB at this fixture size). Restore copies the
tree back. Backup names that collide in the same second get `-2`, `-3` rather than
overwriting the copy they are about to restore.

### Occupancy (measured 2026-08-19, Openness holding CleaningStation)

No CODESYS-style `.~u` sidecar. While `Projects.Open` holds the project:

| Path | When |
|---|---|
| `<stem>.info` beside the `.apXX` | Appeared 16 s after open, **gone** when the host exited |
| `System/~PEData.1` | Appeared two seconds later, **gone** on close |
| `Logs/` size | Churns (already excluded from the hash) |

`OccupancyMarkerFor` is `ChangeExtension(projectPath, ".info")`. Existence is Held. The
body is not parsed — a leftover after a crash stays Held rather than guessed Free. Stale
recovery needs a pid, which this marker has not been shown to carry.

`System/~PEData.*` is a transient inside the program folder, not the marker the probe watches.

Export-while-open **in the GUI** is a wait (`ExportHeldViaCopy` is false): exclusive
`Projects.Open`, and a 488 MB copy is not the first vendor's sidecar copy.

**GUI occupancy — measured 2026-08-19 evening, Portal V21 with a window, no Openness.**
`Siemens.Automation.Portal.exe <project.ap21>` against a scratch copy, a filesystem poll
every 3 s beside it. The GUI writes **the same `<stem>.info` marker** — first seen ~20 s
after launch, while the project was still loading (Openness measured 16 s) — and deletes
it when the project closes (graceful `taskkill`, no `/F`). So a project an engineer holds
in the Portal window reads Held, and closing Portal is the retry, the same as the first
vendor. One nuance: a Portal *process* outlived the project close by minutes; the marker
tracks the project, not the process, which is the fact Versio actually needs. And one
honesty note: Portal treated that `taskkill` as an abnormal end (its "uncritical problem"
reporter appeared afterwards) — yet it still deleted the marker on the way out. A hard
kill (`/F`) is the case that would leave the marker behind, and a leftover reads Held
until cleared, which is the safe side.

Closed, top-level names: `.ap21`, `System/`, `IM/`, `Logs/`, `tmp/`, `Vci/`, `XRef/`,
`AdditionalFiles/`, `UserFiles/`, `src/`, `ProjectInfo.txt`. `System/` then is `PEData.idx`
and `PEData.plf` only.

---

## 6. Identity

Manifest `guid` is `{device}/{plc}/{type}/{name}#{number}` (UDT and tag tables omit the
number). Example: `ET 200SP station_2/CleaningStation/FB/Level#11`.

SimaticML `UId` is **not** used. It is per-document numbering:

| Attempt | Result |
|---|---|
| First `UId` in the XML | every guid `"0"` (wrapper) |
| Skip `"0"` | every guid `"1"` |
| Count of distinct `UId` values in 159 XML files | **26,661**, most common `"35"` |

A GUID-shaped value would be accepted if a future export carried one. Rename pairing is
content hash, then text similarity / line sketch against `departed` — the same shape as the
first vendor when a GUID is missing. TIA folders are not recorded objects; `NewParent` is
the arrival path prefix.

---

## 7. Export shape

Warm export of CleaningStation (measured 2026-08-19): **~37 s**, 270 form records, 159 unique
identities, 58 coverage skips.

| Files | Forms | Types |
|---|---|---|
| 18 `.scl`, 93 `.udt`, 159 `.xml` | 111 SCL + 111 XML_REVIEW + 48 NATIVE | 24 DB, 29 FB, 26 FC, 4 OB, 186 UDT, 1 tag table |

Namespaced as `ET 200SP station_2/CleaningStation/…`. Manifest schema 9. `_coverage.md`
names Hardware, HMI, safety, technology.

Textual hashes are UTF-8 **without BOM** so they match `ContentHash.Of`. Siemens
`GenerateSource` writes a BOM; hashing the raw file would make every rename pairing miss.
`_manifest.json` is also without BOM — `System.Text.Json` rejects one.

Out / coverage skip: hardware, HMI, safety (`F_*`), technology objects, know-how-protected
import, system blocks, VCI, Project Server, compile-after-import.

---

## 8. Protocol

JSON-lines, marker `@@VERSIO@@`, field `t` — the same shape as the first vendor's scripts so
the heartbeat and the log filter are not a second language. The emitter is C#, in the net48
host.

Events: `info`, `object`, `warning`, `fatal`, `done`. Heartbeat is the runner's, not the
host's: a silent host looks like a hang, so the 20-minute floor is the backstop.

---

## 9. The fixture

**Committed, Git LFS**, at:

```
fixtures/projects/CleaningStation_V20_V21/CleaningStation_V20_V21.ap21
```

The directory beside that pointer **is** the project (`System/` ~445 MB, plus `IM/`,
`UserFiles/`, …). The Versio mirror is the conventional export path — the stem next to the
pointer:

```
fixtures/projects/CleaningStation_V20_V21/CleaningStation_V20_V21/
```

`Logs/`, `tmp/`, `Vci/` are not in the tree. `.gitattributes` sends the payload through LFS
and keeps the mirror as text.

`ProjectInfo.txt`: TIA V21.0, STEP 7 Professional V21. Upgraded V20→V21 on 2026-08-19.
HMI (`HMI_RT`) and MTP Creator exports are coverage skips.

**Never write the OneDrive original**
(`C:\Users\elli_\OneDrive\Documentos\Automation\CleaningStation_V20_V21`). That copy is not
the fixture.

This repository's `.versio/repo.json` **ignores** the fixture, so opening Versio on Versio
does not adopt a 488 MB TIA tree and start exporting it. The first vendor's
`RefrigeratorControl.project` is the opposite — it is both the gate fixture and a working
project, which is a known loose end. Do not repeat that here.

A clone without `git lfs pull` gets a 130-byte pointer. `TestPaths.TiaPortalProject()`
returns null and the gate skips. Mutating round-trips (`Test-RoundTrip.ps1 -MutateFirst`)
write **this** tree; close Versio and Portal first.

A private scratch copy still exists at `.versio/local/tia/` (gitignored) if a test needs to
destroy a tree that is not the committed one.

---

## 10. First host runs (measured 2026-08-19)

| Run | Result |
|---|---|
| Probe (group missing) | ~3 s, skip with the group error |
| Probe (group present, AllowList unanswered) | 8 m 53 s, `Security error. The operation has timed out.` |
| Probe (AllowList accepted) | **42 s**, `report.json` written |
| Export | **~37 s**, 270 forms, 159 identities, 58 skips |

Cold start of a first AllowList session is the 8-minute timeout, not the 42 s success.

---

## 11. Harnesses

Close Versio **and** TIA Portal first. Default project is the committed fixture.

```
tools/tia/Test-RoundTrip.ps1 -MutateFirst
tools/tia/Measure-ImportStability.ps1
tools/tia/Probe-Hardware.ps1
```

`Probe-Hardware.ps1` copies the fixture to `%TEMP%`, runs `hwprobe` twice, and optionally
`-WriteTest` (Comment set-and-restore, no Save). Close Versio and Portal first. A host
rebuild drops the Openness AllowList.

Layered verdict: textual must match; native XML is expected to differ; never a single
`Lossless: False`. `-MutateFirst` is mandatory — without it a round trip that writes nothing
passes vacuously.

The C# gate is `tests/Versio.Plc.TiaPortal.Tests` (5 tests, `[Collection("tia-portal")]`).
Skip is the success path when Portal, Openness membership, AllowList, or LFS content is
missing. Probe, export, same-tree textual determinism, and the mutating import/re-export
Fact are **green** on this machine. The import Fact copies the committed fixture aside,
mutates one `.scl` with a compiling body assignment **and one `.udt` with a real member**
(a comment might not survive generate), imports the planner's plan, re-exports, and
asserts both mutations in **textual** files — never native XML. It then runs the
**inverse edit**: the added statement is deleted from the mirror, imported, and must be
gone from the next re-export — a deletion adds no line the verify can look for, so this
is the only proof a no-op generate cannot fake (see Findings, 2026-08-19). Session
notes: [`archive/HANDOFF-Siemens2.md`](../../archive/HANDOFF-Siemens2.md).

---

## 12. What is left

The PLC-software loop is done. Remaining TIA is coverage skips, not a build
queue — see [`STATUS.md`](../../STATUS.md). This section is the evidence that
queue was derived from, so a later session does not have to re-derive it.

### Done (PLC-program parity with the first vendor)

1. ~~**`GenerateBlocksFromSource` does not overwrite.**~~ — **done 2026-08-19.** Compiling
   body mutation, group+`None`, verify-before-Save. Fact green; P7 SETTLES.
2. ~~**The host's import only finds blocks.**~~ — **done 2026-08-19.**
3. ~~**Two unchanged textual exports have not been shown byte-identical.**~~ —
   **done 2026-08-19.** Native XML still churns (G12). The Fact re-exports into the same
   tree so an existing XML file is overwritten.
4. ~~**The app has not been eyeballed against a TIA project it is allowed to adopt.**~~ —
   **done 2026-08-19** on a temp copy. Zero `ERR` after hashing with share/retry, warning
   records, and XML overwrite. Coverage skips no longer paint `HasGaps` on every tree.
5. ~~**GUI occupancy and export-while-the-IDE-holds-it.**~~ — **wait, not copy.**
   `.info` measured under Openness. `ExportHeldViaCopy = false`. ~~A Portal window (no
   host) was not separately measured this session~~ — **measured 2026-08-19 evening: the
   GUI writes and deletes the same `.info` marker** (§5). Nothing left open here.

### Not this queue (coverage skips)

HMI, safety (`F_*`), technology objects, know-how-protected import,
compile-after-import, VCI, Project Server. Archives (`.alXX`) are seen and refused.
**Hardware** Comment catalogs ship 2026-08-20 (§4.4): export-only was the probe;
the owner picked Comment `SetAttribute`. PlugNew, topology, HMI, safety and
technology stay off this queue.

### Friction that remains after the loop works

AllowList after every host rebuild (path + hash). Ten tree backups × ~500 MB.
Several Portal versions on one machine refuse rather than pick one (same reason as
`OpenWithUpgrade`: a silent upgrade). Identity is name+number until a GUID-shaped
SimaticML value appears; folders are not recorded objects.
