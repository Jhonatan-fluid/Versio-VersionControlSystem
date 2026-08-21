# Versio — status and what is next

**Date:** 2026-08-21 · **This is the live page.** Where the project is, and what is next, live
here and only here. Search [`FINDINGS.md`](FINDINGS.md) for defects. Completed plans and session
handoffs are records in [`phase1/`](phase1/) and [`archive/`](archive/) — read them for why,
never for state.

**Continue from here — item 2 of *What is next*: the owner eyeballs.** Item 1 is done:
`Versio.Plc.Codesys.Tests` ran on `Siemens2` on 2026-08-19 evening — **54/54 green in
39 m 33 s, zero skips**, on an idle machine with the IDE closed, over the Core seam
changes (`ExportHeldViaCopy`, the held-wait, occupancy-then-hash, the locked-pointer
skip). That run is also the counted gate `LOCK-1` owed. After the eyeballs: UX-6 /
UX-7 `Ctrl+F` / UX-8 / UX-12 (item 3), unless the owner picks packaging (item 5)
instead. Do not start from `archive/` or a handoff.

**Tests:** 2,708 across eleven projects, last counted 2026-08-21 after first-vendor leftover
fixes (tooltip, log chips from the registry, template path from the format). TwinCAT XAE gate: 5 tests, **green in 3 m 4 s on
2026-08-21** over I/O export-only — probe, export (`Devices/Devices.devices.yaml`, form
`DEVICES`), two unchanged textual exports byte-identical including the yaml, import/re-export
of an ST mutation and its deletion. TIA Portal gate: 5 tests,
**green in 9 m on 2026-08-20** (re-run after slot identity) including Comment `SetAttribute` on
`*.devices.yaml`. The CODESYS gate last full run is **54/54, 2026-08-19 evening**. I/O export adds
one live Fact (`Empty_IO_Link_slots_are_listed_in_the_parent_yaml_rather_than_colliding`);
that gate has not been re-run this session.

A 2026-08-20 code review of the TwinCAT pack found and fixed four defects the same day —
an import that could delete or overwrite a wrong same-named object, a conversion check that
failed open when a version could not be read, a dead lazy-TIPC wait whose empty export
reported success, and a runner output race shared with TIA. A review of the same day's
CODESYS I/O export found a failed Device YAML write that would drop Plc Logic from the
swapped mirror, plus quoted-colon split, name-vs-encoded-stem merge, added-connector
marking, duplicate-param keys, and the detail-pane lookup. All of them are in
[`FINDINGS.md`](FINDINGS.md), newest first.

| Project | Tests | Time | Needs |
|---|---|---|---|
| `Versio.Core.Tests` | 1,457 | under a second | nothing |
| `Versio.Integration.Tests` | 153 | ~10 s | real git |
| `Versio.App.Tests` | 621 | ~50 s | nothing |
| `Versio.Arch.Tests` | 15 | instant | nothing |
| `Versio.Plc.Codesys.Pure.Tests` | 263 | under a second | nothing |
| `Versio.Plc.Codesys.Parity.Tests` | 97 | ~15 s | `python` on PATH — skips cleanly without it, so check the count |
| `Versio.Plc.TiaPortal.Pure.Tests` | 19 | instant | nothing |
| `Versio.Plc.TiaPortal.Tests` | 5 | seconds when skipped; minutes when Portal runs the import gate | **real TIA Portal** — skips without Openness membership or LFS content |
| `Versio.Plc.TwinCat.Pure.Tests` | 21 | instant | nothing |
| `Versio.Plc.TwinCat.Tests` | 5 | seconds when skipped; ~3 min when XAE runs the import gate | **real TwinCAT XAE** — skips without `TcXaeShell.DTE.17.0` or the fixture |
| `Versio.Plc.Codesys.Tests` | 55 | **~30 min** | **real CODESYS** |

The first ten are the loop to work in — about thirty seconds plus the UI suite. The last is
the CODESYS gate before a commit. `Versio.Plc.TiaPortal.Tests` and `Versio.Plc.TwinCat.Tests`
are the same kind of gate for the second and third vendors and skip when the install is
missing. Until 2026-08-17 the fast/slow split was a `--filter` expression over one
integration project; it is project boundaries now, split by need — nothing / python / the
real toolchain.

---

## The map

| File | Role |
|---|---|
| **This page** | Where the project is, and what is next |
| [`dev.md`](dev.md) | How the running app works — layers, sync, git, scripts, roundtrip |
| [`FINDINGS.md`](FINDINGS.md) | Every defect with its evidence, newest first — **search it** |
| [`SAFEGUARDS.md`](SAFEGUARDS.md) | What Versio refuses, asks about, or waits for |
| [`vendors/codesys/DRIVING.md`](vendors/codesys/DRIVING.md) | How CODESYS is driven |
| [`vendors/tia/DRIVING.md`](vendors/tia/DRIVING.md) | How TIA Portal is driven. §4.4 is the hardware probe; remaining TIA is coverage skips until the owner picks a slice |
| [`vendors/twincat/DRIVING.md`](vendors/twincat/DRIVING.md) | How TwinCAT 3 is driven. PLC-software loop is evidence; I/O is export-only; remaining TwinCAT is coverage skips |
| [`phase1/`](phase1/) | Phase 1 archive — founding architecture, import plan, git-panel plan, Phase 0 measurements |
| [`archive/`](archive/) | Phase 2 queue, UX plan, session handoffs, the long STATUS diary they replaced |

---

## Where the project is

Three vendors' **PLC-software loops work**. A project in the repository is tracked without
ceremony, exported to a reviewable per-object mirror when it changes, and edits to that mirror
flow back — driven by the direction lock, confirmed before an import writes, refused rather than
guessed wherever the toolchain is silent.

- **CODESYS 3.5** — export, plan-driven import (create / update / delete / rename / move),
  occupancy wait while the IDE holds the project, retry when it closes. Proven against
  `RefrigeratorControl`. That file is both the gate fixture and the daily project; it is not a
  test-only artefact to "clean up". **I/O export 2026-08-20:** each device writes
  `*.devices.yaml` inside its directory (export-only); eight empty IO-Link slots are distinct
  YAML keys rather than one collided `%3CEmpty%3E.xml`. Parameter import and IEC mapping are
  not this pass.
- **TIA Portal** — the same product loop for SCL, UDTs and tag tables, through a net48 Openness
  host. PLC-software loop closed 2026-08-19 on `Siemens2`. Hardware **Comment** catalogs
  (`*.devices.yaml`, form `DEVICE_COMMENT`) ship 2026-08-20: export plus `SetAttribute`
  with verify-before-Save; never PlugNew. This fixture has no port-to-port partners.
  HMI, safety and technology stay coverage skips. Gate fixture:
  `fixtures/projects/CleaningStation_V20_V21/` (Git LFS). Never the
  OneDrive original. Session notes: [`archive/HANDOFF-Siemens2.md`](archive/HANDOFF-Siemens2.md).
- **TwinCAT 3** — the same product loop for ST, through a net9 COM host into
  `TcXaeShell.DTE.17.0`. **Gated 2026-08-19, re-gated 2026-08-20** after the hardening
  pass: probe, export, two unchanged `.st` files byte-identical, ST overwrite and the
  inverse deletion. Destructive import actions are identity-only and hash-verified
  against the last export since 2026-08-20; the conversion check fails closed. Create /
  object-delete / rename are in the planner and host, not an XAE Fact yet. 4024.4
  opened on 4026.25 only as a disposable `%TEMP%` copy. **I/O catalogs 2026-08-21:**
  `Devices/Devices.devices.yaml` (form `DEVICES`, export-only). Comment set in-session
  and showed in `ProduceXml`; it does not persist through Save. Never CreateChild.
  This fixture has an empty TIID node. NC, safety, C++ and visualisation stay coverage
  skips. Gate fixture:
  `fixtures/projects/TC3_PlcSample_reStrText/`. Session notes:
  [`vendors/twincat/DRIVING.md`](vendors/twincat/DRIVING.md).

Versio **deletes, renames and moves objects**. Delete a `.st` from the mirror, confirm the
import, and the POU leaves the project. That is built as refusals — five planner preconditions,
a dialog that names what will really go and what will really stay, and an in-process check that
an object edited in the IDE since the last export is kept. The full reasoning is in
[`SAFEGUARDS.md`](SAFEGUARDS.md) and the findings.

The shell has a full git surface, a project tree, a repo explorer (visualize only), side-by-side
and word-level diff, and a UX pass over the running window. The owner saw the merged `Siemens` +
`UxPlan` window on 2026-08-19 and said it looks right. What that look did not include is in the
queue below.

**Packaging is the remaining product *build*** (item 5). The CODESYS gate ran on
`Siemens2` (item 1, done); what stands between here and packaging is the owner's eyeballs
and the UX remainder. Code signing is procurement lead time: start it before it is
needed.

```
  CODESYS pipeline    ████████████████████  proven on a real project
  TIA PLC-software    ████████████████████  SCL / UDT / tag tables; coverage skips elsewhere
  TwinCAT PLC-software████████████████████  ST; I/O export-only; coverage skips for NC / safety / C++
  Domain core         ████████████████████  vendor-blind, tested without a toolchain
  Vendor seam         ████████████████████  enforced by the project graph + metadata
  Desktop shell       ███████████████████░  git surface, tree, explorer; UX remainder below
  Packaging           ██░░░░░░░░░░░░░░░░░░  icon and publish profile only
```

---

## What is next

One list. Reorder it; do not copy it into another file.

1. ~~**Run `Versio.Plc.Codesys.Tests`.**~~ — **done 2026-08-19 evening: 54/54 green in
   39 m 33 s, zero skips**, IDE closed, machine otherwise idle. The first vendor's gate has
   now seen `ExportHeldViaCopy`, the held-wait, the occupancy-then-hash path and the
   locked-pointer skip, and the run is the counted gate `LOCK-1` owed.
2. **Owner eyeball leftovers.** Merge dialog; both themes on the rail badge; staged / not-staged
   headers dragged to 260 px. TIA confirm-import and restore of the ~500 MB tree (the existing
   directory-payload seam; export was seen on `%TEMP%\versio-tia-p7`). TwinCAT: adopt the
   committed fixture in this repository and confirm export / confirm-import look right.
3. **UX remainder** — evidence and ranking in [`archive/UX-PLAN.md`](archive/UX-PLAN.md):
   - **UX-6** — search in the git panel and the history
   - **UX-7** second half — `Ctrl+F` / Escape (`Ctrl+1/2/3/4` shipped; `Ctrl+F` waits on UX-6)
   - **UX-8** — act on / copy a history commit
   - **UX-12** — amend-while-unpushed (owner-approved, never built)
4. **P-M2.** Does the commit gate skip the export after a merge? Inferred from reading the gate;
   needs a real CODESYS export. P-M1 already measured that a merge can commit a project its
   mirror disagrees with (41 mirror files, two carrying content the kept project did not have);
   `LOCK-1` pointed the lock at import so the export gate cannot win that race.
5. **Packaging.** `third_party/` vendoring with SHA-256 pinning; Velopack (`Setup.exe`, MSI,
   delta updates); **code signing**. Sequenced last on 2026-08-17, on purpose. Open decisions
   1–4 below are the packaging questions.
6. **Wait for a specimen, not a build.** C5 (`DELETE_UNREADABLE`) and `native_partial` close when
   reality produces an object nobody has observed. They cannot be manufactured. See
   [`phase1/IMPORT-PLAN.md`](phase1/IMPORT-PLAN.md).
7. **Not this queue.** TIA HMI / safety (`F_*`) / technology / know-how-protected
   import / compile-after-import / VCI / Project Server. TIA **hardware** Comment
   apply shipped 2026-08-20 ([`vendors/tia/DRIVING.md`](vendors/tia/DRIVING.md) §4.4);
   PlugNew and topology stay off this queue. TwinCAT I/O **export** catalogs shipped
   2026-08-21 ([`vendors/twincat/DRIVING.md`](vendors/twincat/DRIVING.md) §4.5);
Comment apply, CreateChild, mappings, NC / safety / C++ / visualisation stay off this queue,
   plus create / object-delete / rename Facts and folder move —
   [`vendors/twincat/DRIVING.md`](vendors/twincat/DRIVING.md) §9.
   `_coverage.md` names the skips. They are `SERVICE_OBJECT`. ~~GUI occupancy with a
   Portal window (no Openness) was not separately measured~~ — **measured 2026-08-19 evening:
   the Portal GUI writes and deletes the same `.info` marker Openness does**
   (`vendors/tia/DRIVING.md` §5), so a project held in the window reads Held and closing Portal
   is the retry. ~~TwinCAT live XAE occupancy was not separately watched~~ — **measured
   2026-08-21: TcXaeShell writes the same sibling `.~u` the format already watches**,
   rewritten every ~5 s via `.~u~` (`vendors/twincat/DRIVING.md` §5). Export copies
   the directory aside (`ExportHeldViaCopy`); import still waits for close.

Close Versio **and** Portal before a host rebuild. Click **Yes to all** on Openness access (path
+ hash — a rebuild drops the host off the AllowList).

---

## Known limitations, carried deliberately

- **Library references are export-only.** Editing `Library Manager.libraries.yaml` changes
  nothing in the project. The changed-files row carries a lock glyph saying so.
- **Device YAML is export-only.** Editing `Device.devices.yaml` changes nothing in the
  project. Parameter `setattr` and IEC `%I/%Q` mapping are not built. Identification is
  missing on this ScriptEngine, so the file does not record which `.device` description a
  node is.
- **How a library reference resolves is not recorded.** The file carries what the project asks
  for, not what this machine resolved it to — two engineers with different libraries installed
  would otherwise disagree in bytes.
- **Managed (fixed-version) library references are untested.** The fixture's 34 references are
  all placeholders.
- **Task XML is export-only** (`TASK_NATIVE`). The `.tasks.yaml` is the importable half. A task
  named in the YAML but absent from the project is warned about, never created; a task deleted
  from the YAML is left alone.
- **Graphical POUs export as native XML.** Their diffs will never read as well as ST.
- **Visualisation bindings after a rename: unmeasured, by decision (2026-08-17).** The owner
  does not use CODESYS visualisations. A project that does should measure whether bindings hold
  by name before trusting a rename.
- **TwinCAT identity is the Automation Interface `PathName`** (`TIPC^…`). Conversion of a
  4024 project on a 4026 install is refused on the path the user bound; only a throwaway
  copy (`%TEMP%` or `.versio/local/open/`) is allowed to convert. Occupancy is sibling
  `.~u` (GUI measured 2026-08-21: same marker, rewrite via `.~u~` every ~5 s);
  `ExportHeldViaCopy` is true.
- **TwinCAT I/O YAML is export-only.** `Devices/Devices.devices.yaml` is rewritten on every
  export. Comment set in-session and showed in `ProduceXml`; `File.SaveAll` does not persist
  it on an empty TIID, and `SaveProject` is `DISP_E_UNKNOWNNAME`. Change devices in XAE.
- **Interfaces, text lists, image pools** have export rules written and unit-tested but never
  exercised — the CODESYS fixture contains none.

---

## Open decisions

| # | Question | Blocks |
|---|---|---|
| 1 | Who else runs this, and on what kind of machine? | Whether signing and an MSI are needed at all |
| 2 | Budget and legal entity for a code-signing certificate? | External distribution |
| 3 | Bundle PortableGit, or require Git for Windows? | Installer size and GPL compliance work |
| 4 | Where does the update feed live? | Packaging |
| 5 | Is a **resident CODESYS session** worth building? | 17–31 s vs sub-second on every save |
| 6 | Can CODESYS run on a shared server? *(a licensing call, not engineering)* | PLC CI |

~~**7. Which vendor comes second?**~~ — **answered 2026-08-19: TIA Portal.** The seam held.
~~TwinCAT as the third pack~~ — **closed 2026-08-19**: PLC-software loop green on a `%TEMP%`
copy of `TC3_PlcSample_reStrText`. Studio 5000 remains additive, not this queue.

---

## How to start

1. Read `CLAUDE.md` end to end — the rules are load-bearing and each one names what it cost.
2. **This page, item 2** — the owner eyeballs (item 1, the CODESYS gate, ran green on
   2026-08-19). Then item 3 (UX), unless the owner says packaging. Do not re-derive this
   list from the archive.
3. The loop: `Versio.Core.Tests` + `Versio.Integration.Tests` + `Versio.Arch.Tests` +
   `Versio.Plc.Codesys.Pure.Tests` + `Versio.Plc.Codesys.Parity.Tests` +
   `Versio.Plc.TiaPortal.Pure.Tests` + `Versio.Plc.TwinCat.Pure.Tests`, about thirty seconds;
   `Versio.App.Tests` when the UI is in play. `Versio.Plc.TiaPortal.Tests` is the Portal gate
   (copy of the LFS fixture, **Yes to all** after a host rebuild). `Versio.Plc.TwinCat.Tests`
   is the XAE gate (copy of `TC3_PlcSample_reStrText` under `%TEMP%`; never the committed
   4024.4 tree). `Versio.Plc.Codesys.Tests` is the CODESYS gate before a commit — half an hour,
   and it is item 1.
4. When something surprises you, search [`FINDINGS.md`](FINDINGS.md) before deriving it from
   scratch — an audit once re-derived, over an hour, what was already written there by name.
