# Driving TIA Portal

**Date opened:** 2026-08-19 · Beside `Versio.Plc.TiaPortal*` — one vendor's docs, beside its
projects. How TIA Portal is driven, and what was measured rather than assumed.

Versio is not a TIA Portal tool. This file is what the CODESYS `DRIVING.md` is for CODESYS:
the traps, the protocol, the payload, the refusals. Neutral code must not grow any of it.

---

## Install (measured 2026-08-19)

| Fact | Value |
|---|---|
| Portal | `C:\Program Files\Siemens\Automation\Portal V21` |
| Openness API | `C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\net48\` |
| Shape | **Modular DLLs**, not the historical single `Siemens.Engineering.dll` |
| Required | `Siemens.Engineering.Base.dll`, `Siemens.Engineering.Step7.dll` |
| Host TFM | net48 x64. Versio is net9.0-windows. They cannot share a process. |

The host resolves those DLLs from the install at runtime (`Private=false`, `AssemblyResolve`
before any Openness type is used). They are never shipped.

Do not call `OpenWithUpgrade`. That silently upgrades a project, the same class of mistake
`ProfileResolver` refuses when several CODESYS profiles exist. The host looks up
`Projects.Open(FileInfo)` and refuses if that overload is missing.

## Openness access (measured 2026-08-19)

Local group **`Siemens TIA Openness` exists and has zero members.** Adding
`rog-jvm\elli_` failed with Access denied (error 5). The first `Projects.Open` is therefore
expected to fail until that membership is granted and the user logs off and on.

The first successful connect also needs **Yes to all** on the Openness firewall AllowList
(path + hash + timestamp). A debug rebuild of the host changes the hash and drops off the
list.

Until those two are true, `TiaPortalGateTests.Probe_opens_the_scratch_fixture_or_explains_why_not`
skips with the host's reason rather than going red. That skip is the record, not a silent pass.

## Fixture

Scratch copy only, gitignored:

`C:\GIT\Versio\.versio\local\tia\CleaningStation_V20_V21`

Never write the OneDrive original. Do not commit this tree. `.ap21` is ~151 KB; the program
lives in `System/` (~488 MB together). TIA cannot open the pointer file alone.

`ProjectInfo.txt`: TIA V21.0, STEP 7 Professional V21. Upgraded V20→V21 on 2026-08-19.
HMI (`HMI_RT`) and MTP Creator exports are coverage skips, not roundtrip subjects.

## Payload

The project is a **directory**. `IPlcProjectFormat.PayloadPathFor` returns the folder
containing the `.apXX`. Sync copies, hashes, backs up and restores that folder. `Logs/` and
`tmp/` do not count toward the project hash. **`System/` does** — skipping it would hash a
150 KB pointer and call a half-gigabyte project unchanged.

Ten folder backups are bounded and heavy (~5 GB). Restore copies the tree back. The backup
catalogue lists **immediate children** of the backup folder; walking files underneath a tree
copy would prune the newest backup from the inside.

## Occupancy

**Measured 2026-08-19, TIA closed.** The scratch tree has no CODESYS-style `.~u` sidecar.
Top-level names: `CleaningStation_V20_V21.ap21`, `System/`, `IM/`, `Logs/`, `tmp/`,
`Vci/`, `XRef/`, `AdditionalFiles/`, `UserFiles/`, `src/`, `ProjectInfo.txt`.

`OccupancyMarkerFor` returns null, so the probe reports free. The backstop is
`Projects.Open` failing when another instance holds the project, or when this user is
not in the Openness group. Export-while-open is a **wait**: exclusive open, and a 488 MB
copy is not the first vendor's sidecar copy.

Attach-to-running is a different design and is not used.

`Vci/` is Siemens's competing git exporter. It is ignored for the project hash and listed
as a transient; Versio must not write into it.

## First host run (measured 2026-08-19)

`TiaPortalGateTests.Probe_opens_the_scratch_fixture_or_explains_why_not` started the
net48 host against the scratch `.ap21`. It returned in **~3 s** with:

```
Owner 'ROG-JVM\elli_' of this process is not member of the windows group
'Siemens TIA Openness'
```

That is the skip reason, not a hang and not a product bug. Cold start of a successful
`WithoutUserInterface` session is still untimed. Determinism, round-trip, create/rename/
delete via Openness, and a one-line SCL edit's payload churn wait on group membership.

## Identity

Manifest `guid` prefers SimaticML `UId` when the XML export carries one. Fallback is
`{device}/{plc}/{type}/{name}#{number}` — recorded so a rename can still be a rename once a
stable UId is measured on a real export, not used as the thing a rename is keyed on.

## Round trip (PLC software)

In: SCL blocks, UDTs, DBs, tag tables.

Out / coverage skip: hardware, HMI, safety (`F_*` languages), technology objects,
know-how-protected import, system blocks, VCI, Project Server, compile-after-import.

G12: one object, one importable file. SCL (or `.udt`) imports; the SimaticML beside it is
`XML_REVIEW` and is never applied back. Importing both puts the old body back.

The host timeout floor is 20 minutes. `TypicalStartupCost` is 2 minutes until a cold start is
timed on this machine.

## Protocol

JSON-lines, marker `@@VERSIO@@`, field `t` — the same shape as the first vendor's scripts so
the heartbeat and the log filter are not a second language. The emitter is C#, in the net48
host.

## VCI

A competing Siemens git exporter. Do not write into a VCI workspace.

## Harnesses

Close Versio **and** TIA Portal first.

```
tools/tia/Test-RoundTrip.ps1 -MutateFirst
tools/tia/Measure-ImportStability.ps1
```

Layered verdict: textual must match; native XML is expected to differ; never a single
`Lossless: False`. `-MutateFirst` is mandatory — without it a round trip that writes nothing
passes vacuously.
