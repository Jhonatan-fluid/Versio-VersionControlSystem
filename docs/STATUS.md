# Versio — status and what is next

**Date:** 2026-09-14 · **This is the live page.** Where the project is, and what is next, live
here and only here. Search [`FINDINGS.md`](FINDINGS.md) for defects. Completed plans and session
handoffs are records in [`phase1/`](phase1/) and [`archive/`](archive/) — read them for why,
never for state.

**Branch `UiLook` (2026-09-21/22) is not merged.** Twenty commits from one look at the running
window: all twelve findings fixed, plus two defects found under them (a second Versio wrote no
log file; the diff parser read a removed `--` line as a file header). Everything is tested and
the working loop is green; the visual half is item 2's first bullet. FINDINGS 2026-09-21 has
the list.

**Continue from here — item 2 of *What is next*: the owner eyeballs.** Item 1 is done:
`Versio.Plc.Codesys.Tests` ran on `Siemens2` on 2026-08-19 evening — **54/54 green in
39 m 33 s, zero skips**, on an idle machine with the IDE closed, over the Core seam
changes (`ExportHeldViaCopy`, the held-wait, occupancy-then-hash, the locked-pointer
skip). That run is also the counted gate `LOCK-1` owed. Item 5 (packaging) has unsigned
`Setup.exe` / MSI from `Build_Package.bat`; signing and the feed remain. **Item 3 (the
UX remainder) is built and tested as of 2026-08-25** — UX-6 find boxes, UX-7 `Ctrl+F` /
Escape, UX-8 copy-a-commit, UX-12 amend-while-unpushed — and its visual half joins the
eyeball list in item 2. Do not start from `archive/` or a handoff.

**Tests:** 3,561 across twelve projects (plus 22 vitest in `web/`), last counted 2026-10-14
on `ReviewFixes` — Integration 172 and App 783 after the review fixes; the toolchain-gated
counts (TIA 5, TwinCAT 6, CODESYS 55) are from their last gate runs, not re-run for the count. **The Rendered view of an ST object reads like the IDE (2026-09-20):** no
`@@` line, one gutter, a Declaration / Implementation divider, and the implementation
numbered from 1 so a compile error's line number can be found — FINDINGS 2026-09-20. **With TIA Portal open, a commit no longer records a stale mirror**: the
commit is refused with what to close, a push no longer marks an unexported save as
exported, and a commit of only the program store no longer skips the export. A TIA
project's program store also gets LFS rules, not only its pointer — both FINDINGS
2026-09-14. Earlier:
a guarded merge or rebase now asks in its own verb — `GuardIntent.Merge` / `Rebase`
closed the known gap where all three bring-commits-in verbs said "pull"). **The
auto-stash suite flake had a second hole, closed 2026-08-27**: the 2026-08-26 gate
serialised refreshes, and the next switch's guard still did not wait, so an in-flight
refresh was read as work-in-flight or a dirty tree and the switch was abandoned.
`ClearForContextChangeAsync` now drains before `Ask()`. The git panel is one CHANGES list: checkboxes pick the include set,
Commit / Discard are the verbs, and a failed project no longer vetoes unrelated files.
**Stop / Retry / close (2026-08-21):** the status bar kills
in-flight work including an import; Try again lights after cancel; close asks even during
import; CODESYS.exe and git.exe die with Versio (Job Object); TIA/TwinCAT hosts watch the
parent and Quit COM, and Stop reaps only the Portal/XAE PID this run created. TwinCAT XAE
gate: 6 tests, **green in 5 m 14 s on
2026-08-22** over create / rename / object-delete — probe, export (`Devices/Devices.devices.yaml`, form
`DEVICES`), two unchanged textual exports byte-identical including the yaml, import/re-export
of an ST mutation and its deletion, plus a unique FB created, renamed, and removed. TIA Portal gate: 5 tests,
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
| `Versio.Core.Tests` | 1,770 | under a second | nothing |
| `Versio.Integration.Tests` | 174 | ~10 s | real git |
| `Versio.App.Tests` | 789 | 6 m 35 s on 2026-10-10; 8 m 38 s on 2026-10-05; ~3.6 min on 2026-09-22 | nothing |
| `Versio.Arch.Tests` | 35 | instant | nothing |
| `Versio.Plc.Codesys.Pure.Tests` | 369 | under a second | nothing |
| `Versio.Plc.Codesys.Parity.Tests` | 97 | ~15 s | `python` on PATH — skips cleanly without it, so check the count |
| `Versio.Plc.TiaPortal.Pure.Tests` | 96 | instant | nothing |
| `Versio.Plc.TiaPortal.Tests` | 5 | seconds when skipped; minutes when Portal runs the import gate | **real TIA Portal** — skips without Openness membership or LFS content |
| `Versio.Plc.TwinCat.Pure.Tests` | 23 | instant | nothing |
| `Versio.Plc.TwinCat.Tests` | 6 | seconds when skipped; ~5 min when XAE runs the import gate | **real TwinCAT XAE** — skips without `TcXaeShell.DTE.17.0` or the fixture |
| `Versio.Web.Tests` | 142 | a few seconds | nothing — GitHub is faked |
| `web/` (vitest) | 22 | ~20 s | the pinned Node in `web/.node` |
| `Versio.Plc.Codesys.Tests` | 55 | **~30 min**; 41 m 14 s, 55/55, on 2026-10-11 on `WebViewer` | **real CODESYS** |

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
| [`project/PLAN.md`](project/PLAN.md) | Design record for Versio Project — one PLC beside the IDE. Not the live queue |
| [`outpost/PLAN.md`](outpost/PLAN.md) | Design record for Versio Outpost — plant backup and confirmed restore. Not the live queue |
| [`web/ARCHITECTURE.md`](web/ARCHITECTURE.md) | Versio Web as built: the layers, where every file is, one request end to end, what it reuses from the desktop |
| [`web/PLAN.md`](web/PLAN.md) | Design record for Versio Web — a read-only browser view of a repository on GitHub, drawn by the same Core renderers. Its Step 1 is Outpost's Step 2b. Not the live queue |
| [`fbs/PLAN.md`](fbs/PLAN.md) | Design record for CODESYS File-Based Storage — an `.fbsproj` folder shown and drawn as a project, never synced. What the fixture measured. Not the live queue |

---

## Where the project is

Three vendors' **PLC-software loops work**. A project in the repository is tracked without
ceremony, exported to a reviewable per-object mirror when it changes, and edits to that mirror
flow back — driven by the direction lock, confirmed before an import writes, refused rather than
guessed wherever the toolchain is silent. **Stop / Retry / close (2026-08-21):** the status bar
kills in-flight work including an import; Try again lights after cancel; close asks rather
than refusing an import; CODESYS.exe and git.exe die with Versio; TIA/TwinCAT hosts Quit COM
when the parent dies, and Stop reaps only the Portal/XAE PID this run created.

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
  with verify-before-Save; never PlugNew. **Data blocks 2026-08-22:** `GenerateSource` to
  `*.scl` (`SCL_REVIEW`, export-only); SimaticML stays the import. Re-export to see them.
  **Rendered table 2026-08-26:** InstanceDB / GlobalDB members parse from the native XML
  into a vendor-neutral `MemberTable` (name, type, start value, external access,
  comment). Raw is still
  XML or SCL. The table does not write back. This fixture has no port-to-port partners.
  HMI, safety and technology stay coverage skips. Gate fixture:
  `fixtures/projects/CleaningStation_V20_V21/` (Git LFS). Never the
  OneDrive original. Session notes: [`archive/HANDOFF-Siemens2.md`](archive/HANDOFF-Siemens2.md).
- **TwinCAT 3** — the same product loop for ST, through a net9 COM host into
  `TcXaeShell.DTE.17.0`. **Gated 2026-08-19, re-gated 2026-08-22** after create /
  rename / object-delete: probe, export, two unchanged `.st` files byte-identical, ST
  overwrite and the inverse deletion, plus a unique FB created, renamed, and removed.
  Destructive import actions are identity-only and hash-verified
  against the last export since 2026-08-20; the conversion check fails closed. Folder
  move is still Name-only, not an XAE Fact. 4024.4
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

**Packaging ships Setup.exe and a per-machine MSI** (item 5, in progress).
`Build_Package.bat` uses a cached PortableGit 7z and a pinned vpk when they are
already on disk, publishes a self-contained win-x64 payload (git, both vendor hosts,
.NET 9), and packs. Bundled git pins Git Credential Manager so PortableGit's
helper-selector does not ask on every fill. Settings and logs live in
`%LOCALAPPDATA%\VersioUser\`, not in the Velopack install directory, so a reinstall
does not wipe the recent-repository list. `Build_Package.bat` asks how to bump
`VersioVersion` (last / middle / first number, keep, or type one) before packing;
System information shows that product version. Re-packing the same number still
overwrites `artifacts/releases` (`vpk --legacyConsole --yes`). Signing and
the update feed are still open. The CODESYS gate ran on `Siemens2` (item 1, done). Owner eyeballs
and the UX remainder are still the product remainder beside this.

```
  CODESYS pipeline    ████████████████████  proven on a real project
  TIA PLC-software    ████████████████████  SCL / UDT / tag tables; coverage skips elsewhere
  TwinCAT PLC-software████████████████████  ST; I/O export-only; coverage skips for NC / safety / C++
  Domain core         ████████████████████  vendor-blind, tested without a toolchain
  Vendor seam         ████████████████████  enforced by the project graph + metadata
  Desktop shell       ███████████████████░  git surface, tree, explorer; UX remainder below
  Packaging           ████████████░░░░░░░░  unsigned Setup.exe + MSI; signing and feed open
```

---

## What is next

One list. Reorder it; do not copy it into another file.

1. ~~**Run `Versio.Plc.Codesys.Tests`.**~~ — **done 2026-08-19 evening: 54/54 green in
   39 m 33 s, zero skips**, IDE closed, machine otherwise idle. The first vendor's gate has
   now seen `ExportHeldViaCopy`, the held-wait, the occupancy-then-hash path and the
   locked-pointer skip, and the run is the counted gate `LOCK-1` owed.
2. **Owner eyeball leftovers.** **`UiLook` first**, in the dark theme, on this repository:
   the Project panel with nothing changed names its in-sync projects in a muted note at the
   bottom — no green dots, nothing at the top (no "Open a repository"); with commits ahead,
   an accent Push sits beside Commit at the same height, and is gone when nothing waits;
   a `.md` file's Rendered tab is a page (headings, tables, lists) in both themes - PLAN.md's
   four-column table sized by content and its Mermaid flowchart drawn - and a
   `../STATUS.md` link in it opens that document; an `.html` file shows Rendered | Raw |
   Web page, the page draws with its scripts off, and a web link in it opens the browser;
   a UDT's Rendered tab is its member table (DEFAULT VALUE column), an FB's is its interface
   by section over its code, a quoted user type is a link that opens the type, and editing
   that UDT tints the FB row - the link then opens the UDT on its diff, and the tree selects
   and scrolls to it; an FC shows no external access, no Void return, and its code from BEGIN;
   every selection opens on Rendered, and a changed UDT's table marks its changed (amber, old
   value on hover), added (green) and removed (struck) members under a one-line summary,
   each row lettered A / M / D, and a changed cell showing old (struck) → new; an FB's
   code pane is the body alone - no BEGIN/END_, no shared indent, numbered from 1;
   an empty window has Open / Clone and Ctrl+O works; launching Versio a second time brings
   the first window forward; the log pill says "N new" only for warnings; no white square
   where the diff's scrollbars meet; the pane header reads FILE / DIFF / PROJECT / COMMIT with
   the name first; Rendered | Raw looks like one control; explorer rows have kind icons and
   "Persistent GVL"-style types, and hovering a deep row lights only that row; a disabled
   Commit is grey; the caption reads "Versio — Versio"; history rows carry branch/tag pills;
   a commit opens with its message body and one heading per file, highlighted. Also check
   the light theme — the look on 2026-09-21 was dark only.
   Merge dialog; both themes on the rail badge. **Git panel is
   one CHANGES list** (no STAGED / NOT STAGED splitter); two-line rows are 40 px so they no
   longer overlap. **Stop / Retry / close:** start an export, click Stop, confirm the
   log names the operation (not `the session is ending`), Try again lights, then close during a
   run and the window actually goes. Filter `%LOCALAPPDATA%\VersioUser\logs\versio-<date>.log` to
   that window. TIA confirm-import and restore of the ~500 MB tree (the existing
   directory-payload seam; export was seen on `%TEMP%\versio-tia-p7`). TwinCAT: adopt the
   committed fixture in this repository and confirm export / confirm-import look right.
   **From the 2026-08-25 UX remainder:** the find boxes over the changes list and the
   history look right in both themes and hide nothing they should not; `Ctrl+F` lands the
   caret in the visible panel's box (all four rails, including Explorer); **Escape closes the settings popup and the
   ahead/behind preview** (headless tests can only prove the dead half — a Popup with no
   presentation source refuses to open); right-click a history commit and copy all three
   things; and the Amend flow end to end — empty box loads the message, second press asks
   with both subjects, the button vanishes after a push. **TIA data-block Rendered table
   (2026-08-26):** open `FIC100_Type`, `FIT100Liter_Type` and `Global_Limits_Interlocks`
   on CleaningStation — Rendered is a bordered member table including external
   access; Raw is still XML.
   **Rendered ST source (2026-09-20):** open `FB_Alarm` on RefrigeratorControl in the dark
   theme — the DECLARATION / IMPLEMENTATION labels and their rules are visible but quiet,
   `IF NOT Settings.Latch` is line 16 as in CODESYS, and Raw still shows `@@` and file
   numbers.
3. ~~**UX remainder** — evidence and ranking in [`archive/UX-PLAN.md`](archive/UX-PLAN.md):~~
   **built and tested 2026-08-25**, three commits on `UxRemainder`; visual checks moved
   into item 2:
   - ~~**UX-6** — search in the git panel and the history~~ — find boxes over both lists,
     as veils on the CollectionView: ticks, counts and paging never change, and each
     empty-through-the-veil state says which empty it is.
   - ~~**UX-7** second half — `Ctrl+F` / Escape~~ — `Ctrl+F` on the window routes to the
     active panel's own `FocusFindBox` (all four rail panels; the explorer's box was
     already there and a first pass skipped it); Escape closes the two transient
     surfaces, CanExecute-gated so an idle Escape stays unhandled.
   - ~~**UX-8** — act on / copy a history commit~~ — three read-only menu entries (id,
     subject, whole message via a new `CommitMessageAsync`); revert/reset/cherry-pick
     deliberately stay out; `Branch from here…` remains an owner question. The stale
     "detached by clicking the history" comments were corrected with it.
   - ~~**UX-12** — amend-while-unpushed (owner-approved, never built)~~ — message-only
     (`--amend --only`), offered only while HEAD is unpushed and re-verified against
     `rev-list` at press time; two presses with a confirmation naming both subjects.
4. **P-M2.** Does the commit gate skip the export after a merge? Inferred from reading the gate;
   needs a real CODESYS export. P-M1 already measured that a merge can commit a project its
   mirror disagrees with (41 mirror files, two carrying content the kept project did not have);
   `LOCK-1` pointed the lock at import so the export gate cannot win that race.
5. **Packaging.** Unsigned `Setup.exe` and per-machine MSI from `Build_Package.bat`.
   The bat asks how to bump `VersioVersion` (last / middle / first number, keep, or type
   one). System information shows that product version. PortableGit is bundled (decision 3,
   closed). **Code signing** and the update feed remain. Sequenced last on 2026-08-17, on
   purpose; first artifacts 2026-08-23.
6. **Ladder (LAD) drawn in the Rendered tab — built 2026-10-05 on `LadderRendering`;
   what is left.** The plan, and why the code is shaped as it is, are in
   [`vendors/tia/DRIVING.md`](vendors/tia/DRIVING.md), "Graphical blocks". Core holds the
   neutral model, layout, diff and SVG (`Versio.Core.Iec.Ladder`); TIA holds one parser; the
   app holds one view. Left, in order:
   - **Merged by default (owner feedback, 2026-10-05).** A changed network is one rung with
     both versions on it:
     - deleted elements stay where they stood, red, dashed and struck;
     - added elements are green;
     - modified elements are amber, with "was …" underneath.

     The pane's **Side by side** box, which did nothing to a ladder, now switches to two
     columns. A merge that would draw an unchanged element twice (the rung's shape changed
     under the edit) is refused and drawn as two stacked rungs. An equal title now counts
     0.25 towards "same network", up from 0.1. See `LadderMerge` and `LadderDiff.TitleBonus`.
   - **Owner eyeball.** Open a changed LAD block, and check four things:
     - merged by default; ticking Side by side puts old beside new, and narrowing the pane stacks them;
     - Ctrl+wheel zooms and F7 steps through the changes;
     - a rung deleted with another written in its place reads as removed + added, not changed;
     - both themes.

     A dev build pointed at a scratch TIA binding starts a real Portal export of its pointer
     file (FINDINGS 2026-10-05), so look on a real project's mirror.
   - **A Portal-exported LAD specimen.** Put a few real networks into `Main_1` (contacts,
     a TON, a branch, an FC call, an edge contact) and re-export. The nine hand-written
     specimens were written against the schema, never checked against Portal's own output.
     The approved drawings will say what differs. Check the edge-memory pin name (`bit`)
     and whether LAD writes `O` parts first.
   - **The commit view** stays Raw-only for a ladder block. Drawing parent against commit
     is the same `LadderDiff`, fed from `git show <c>^:<p>`.
   - ~~**FBD** (127 networks in the fixture) shares the parser's schema, but it needs its own
     layout (boxes wired left to right, no rail).~~ — **drawn 2026-10-05 on `FbdRendering`.**
     All 127 fixture networks parse and draw within the invariants, and three blocks are
     approved drawings. See `vendors/tia/DRIVING.md`, "Graphical blocks". Left:
     - ~~**an FBD merged view.** A changed FBD network is old above new today~~ — **drawn
       once since 2026-10-05**, with modified boxes saying what they were. A network that lost
       a box is still old above new: a removed box needs a design of its own;
     - **owner eyeball** of the FBD scenarios (`40_` to `44_` in the scratch repository).
   - **Other vendors.** CODESYS `NetworkList` and TwinCAT PLCopen `<LD>` are each one parser
     in their own pack behind `TryParseLadder`. PLCopen's reader belongs in `Versio.Core/Iec/`,
     because it is a standard both use. The CODESYS one is step 1 of item 9.
9. **CODESYS File-Based Storage, shown and drawn — started 2026-10-10 on `FileBasedStorage`.**
   An `.fbsproj` folder is detected and shown as a project: a tree, ST laid out like the IDE,
   member tables, ladder drawn and diffed. It is **never synced**: there is no binding, lock,
   export or import, because the folder already is the readable source. The plan, the order,
   what the fixture measured (file headers, no declaration/body divider, `^` child folders,
   `.xml.v3` without the export envelope), and the specimens still wanted are in
   [`fbs/PLAN.md`](fbs/PLAN.md). Fixture: `fixtures/projects/RefrigeratorControl_FBS/`.
   **Built 2026-10-10:**
   - CODESYS LD/FBD drawn, from the classic mirror and from FBS alike;
   - the folder found, never adopted;
   - its tree read as the IDE's;
   - the row, the explorer and the project card;
   - ST laid out with its sections (FBS 1.0 has no declaration/body divider), and
     `.gvl`/`.enum`/`.struct` highlighted through the format.

   - **one rendering across vendors** (the owner, 2026-10-10): CODESYS and TwinCAT blocks,
     types and GVLs as member tables over their code, as TIA's are, through the neutral
     `StDeclarationTable`;
   - a changed source file's Rendered tab marks what changed, on every vendor (FINDINGS
     2026-10-10);
   - an FBS project's libraries, task configuration and tasks, devices, global text list and
     project settings drawn as tables, held to the classic mirror's YAML for the same machine,
     and the explorer's vendor pill.

   **Left, in order:**
   - TIA tag tables as tables (no real tag-table export to build from yet);
   - "open in CODESYS" from `.sidecars/.~u`;
   - CFC;
   - the owner's eyeball of the drawing (right rail?) and of the row.
10. **Versio Web, a read-only browser view of a repository on GitHub — built 2026-10-11 on
   `WebViewer`, not merged.** The design, the security rules and what each step measured are
   in [`web/PLAN.md`](web/PLAN.md). An ASP.NET Core server (`src/Versio.Web`) renders with
   Versio.Core and the three pure packs; a React page (`web/`) places that HTML and draws no
   code. Built: the HTML renderers for source, text diffs and member tables (Outpost's Step 2b,
   shared), SafeXml under every pack parser, the server, sign-in through a GitHub App, the page,
   the Arch rules, and a Dockerfile. Looked at in the built-in browser on the fixtures.
   Later the same day: two review passes (seven defects fixed), a performance pass (17 GitHub
   calls per page load down to 9, responses compressed 18x, the 33,644-line file on screen in
   1.4 s, so no paging), and the open code items closed: type links in member tables,
   catalogs and FBS projects in the tree, look-alike letters, Mermaid in a sandboxed frame,
   a comparison cap, sessions shareable through Redis, the App key behind a signing seam, a
   threat model ([`web/THREAT-MODEL.md`](web/THREAT-MODEL.md)). Then an external review's eight
   findings, all real and all fixed (FINDINGS 2026-10-11), among them a session read that could
   undo a token renewal and a per-address rate limit that was one bucket behind a load balancer;
   a deployment behind one sets `Web:TrustedProxies`. Then that review's open items and a second
   review, fourteen more, all fixed (FINDINGS 2026-10-11): among them a vendor leak (Siemens
   extensions in Core, now behind `PlcFormats.LanguageFor` and refused by `VendorSeamTests`), and
   answers built from failed reads that were drawn as "everything new" or cached.
   **Left, all needing the owner or a deployment:**
   - **register the GitHub App** (on GitHub: read-only Contents and Metadata, a callback at
     `/auth/callback`) and run sign-in against real GitHub; until then the server runs
     anonymously on public repositories, which was checked against real GitHub;
   - decide who hosts it (the same question as Outpost §16); that picks the key vault behind
     `IAppSigner` and the key ring, and Redis if more than one instance;
   - build the Docker image on a host that has Docker (none on the machine that wrote it);
   - one look in a normal browser at a Markdown page with a Mermaid diagram (the built-in
     browser refuses sub-frames, so the page embedding the frame was not observed);
   - the external penetration test and a cost alarm, once it is deployed;
   - later, on purpose: the desktop drawing with the shared HTML renderers, and the choice of
     renderer moved into Core for both (today mirrored, pinned by a test).
7. **Wait for a specimen, not a build.** C5 (`DELETE_UNREADABLE`) and `native_partial` close when
   reality produces an object nobody has observed. They cannot be manufactured. See
   [`phase1/IMPORT-PLAN.md`](phase1/IMPORT-PLAN.md).
8. **Not this queue.** TIA HMI / safety (`F_*`) / technology / know-how-protected
   import / compile-after-import / VCI / Project Server. TIA **hardware** Comment
   apply shipped 2026-08-20 ([`vendors/tia/DRIVING.md`](vendors/tia/DRIVING.md) §4.4);
   PlugNew and topology stay off this queue. TIA data-block SCL is export-only
   (`SCL_REVIEW`); instance-DB generate-from-source is unmeasured. TwinCAT I/O **export** catalogs shipped
   2026-08-21 ([`vendors/twincat/DRIVING.md`](vendors/twincat/DRIVING.md) §4.5).
   Comment apply, mappings, NC / safety / C++ / visualisation stay off this queue,
   plus TwinCAT folder move (host still only sets `Name`) —
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

- **TIA data-block SCL is export-only.** `*.scl` beside a DB is `SCL_REVIEW`: readable,
  lock glyph, never applied back. Change start values in Portal or in the SimaticML.
  The Rendered pane tables the native Interface (2026-08-26); that view does not write
  back. Instance-DB `GenerateBlocksFromSource` is unmeasured (23 of CleaningStation's 24).

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
  ~~Nothing graphical is drawn~~ — since 2026-10-05 a **TIA LAD** block's Rendered tab draws
  its networks, the committed copy beside the working one (item 6). Raw stays XML.
  **TIA FBD** joined it the same day. CFC, SFC, and other vendors' LD and FBD are still
  highlighted XML.
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
| 3 | ~~Bundle PortableGit, or require Git for Windows?~~ **Bundled.** PortableGit 2.55.0.5 ships in the installer; F5 still uses PATH. GPL notices in `third_party/THIRD-PARTY-NOTICES.txt`. | Installer size and GPL — handled |
| 4 | Where does the update feed live? | Packaging |
| 5 | Is a **resident CODESYS session** worth building? | 17–31 s vs sub-second on every save |
| 6 | Can CODESYS run on a shared server? *(a licensing call, not engineering)* | PLC CI |

~~**7. Which vendor comes second?**~~ — **answered 2026-08-19: TIA Portal.** The seam held.
~~TwinCAT as the third pack~~ — **closed 2026-08-19**: PLC-software loop green on a `%TEMP%`
copy of `TC3_PlcSample_reStrText`. Studio 5000 remains additive, not this queue.

**Design record, not this queue.** [`outpost/PLAN.md`](outpost/PLAN.md) is Versio Outpost:
a packaged worker that backs up and, on confirmation, restores controller programs, with a
React page and an agent on the same API. §16 of that file is still open. It is not an item
above until it is put there.

**Design record, not this queue.** [`project/PLAN.md`](project/PLAN.md) is Versio Project:
a second process in the Versio install, one PLC project beside the IDE, with online status,
a confirmed download, and one MCP server whose clients can edit the mirror and cannot touch
the controller. §15 of that file is still open. It is not an item above until it is put there.

---

## How to start

1. Read `CLAUDE.md` end to end — the rules are load-bearing and each one names what it cost.
2. **This page, item 2** — the owner eyeballs (item 1, the CODESYS gate, ran green on
   2026-08-19; item 3, the UX remainder, built 2026-08-25 with its visual checks folded
   into item 2). Item 5 first artifacts: double-click `Build_Package.bat` for unsigned
   `Setup.exe` and MSI in `artifacts\releases`. Do not re-derive this list from the
   archive.
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
