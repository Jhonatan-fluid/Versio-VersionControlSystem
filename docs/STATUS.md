# Versio — status and what is next

**Date:** 2026-10-25 · **This is the live page.** Where the project is, and what is next, live
here and only here. Search [`FINDINGS.md`](FINDINGS.md) for defects. Completed plans and session
handoffs are records in [`phase1/`](phase1/) and [`archive/`](archive/) — read them for why,
never for state.

**Branch `UiLook` (2026-09-21/22) is not merged.** Twenty commits from one look at the running
window: all twelve findings fixed, plus two defects found under them (a second Versio wrote no
log file; the diff parser read a removed `--` line as a file header). Everything is tested and
the working loop is green; the visual half is item 2's first bullet. FINDINGS 2026-09-21 has
the list.

**Continue from here — item 11 of *What is next*: the five IDE gates the review's fixes still
owe (2026-10-24), then item 10, the web viewer's real-GitHub gates, then item 2, the owner's
eyeballing of the desktop.** An external review's forty-one findings — sixteen of them ways to
lose work — were verified and fixed on 2026-10-24, forty-three commits, and the reviewer's second
round of twelve (three of them fixes to the first round's fixes) on 2026-10-25, twelve commits
more, a third round of four the same day (two of them what the earlier fixes had left),
four commits, a fourth round of seven the same evening (two of them ways to lose a
project), seven commits, a fifth of six (three P1, one of them a hole the third round's
own fix opened), six commits, a sixth of five (three of them the fifth round's fixes
one case short), five commits, and a seventh of three (a restore that adopted what it put
back), four commits; FINDINGS has all seven lists, newest first. Item 1 is done:
`Versio.Plc.Codesys.Tests` ran on `Siemens2` on 2026-08-19 evening — **54/54 green in
39 m 33 s, zero skips**, on an idle machine with the IDE closed, over the Core seam
changes (`ExportHeldViaCopy`, the held-wait, occupancy-then-hash, the locked-pointer
skip). That run is also the counted gate `LOCK-1` owed. Item 5 (packaging) has unsigned
per-user `Setup.exe` / MSI from `Build_Package.bat` and, since 2026-10-27 on `AutoUpdate`,
an update feed: `Publish_Release.bat` publishes, installed copies offer it; signing
remains. **Item 3 (the
UX remainder) is built and tested as of 2026-08-25** — UX-6 find boxes, UX-7 `Ctrl+F` /
Escape, UX-8 copy-a-commit, UX-12 amend-while-unpushed — and its visual half joins the
eyeball list in item 2. Do not start from `archive/` or a handoff.

**Tests:** 4,462 across twelve projects (plus 211 vitest in `web/`), last counted 2026-10-27
on `AutoUpdate` after the publish fix and the Try-again adoption (Core 2,088, Integration 260, App 858, Arch 42, Web 504) — re-derived by summing the table below; the toolchain-gated
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
| `Versio.Core.Tests` | 2,088 (nothing skipped) | under a second | nothing |
| `Versio.Integration.Tests` | 260 | ~1 m (2 m 18 s loaded) | real git |
| `Versio.App.Tests` | 858 (re-counted 2026-10-27) | 17 m, 853/858, 2026-10-27 evening on `AutoUpdate`, built with `-p:OutDir` into a scratch folder outside the tree because the running app locked `src/Versio.App/bin` (four red were `repository root not found` — `RenderChoiceParityTests` and three `SourceProjectCatalogTests` climb from the build output to the repository and could not; all four green from an in-tree scratch output; the fifth was the 21 s catch-up wait in `SourceProjectTests`, green alone as before); 11 m 58 s, 854/856, 2026-10-27 on `AutoUpdate` (one 30 s catch-up wait, green alone; one clipboard test, `CLIPBRD_E_CANT_OPEN`, environmental as on 2026-10-25); 10 m 57 s, 843/846, 2026-10-25 night (three catch-up waits red, all green alone and in their classes; FINDINGS 2026-10-25 seventh round); **12 m 15 s, 845/845, idle, 2026-10-25 night**; 9 m 3 s, 838/842, 2026-10-25 night (the CODESYS run beside it; the four red are the clipboard tests, `CLIPBRD_E_CANT_OPEN`, red alone and on the untouched base too — another program held the clipboard; FINDINGS 2026-10-25 fifth round); 12 m 49 s, 840/841, 2026-10-25 evening (nothing else of this session running; `SourceProjectTests.A_program_with_no_variables…` red after 13 s for the second full run today, green alone and in its class — a watch item, FINDINGS 2026-10-25 fourth round); 13 m 29 s, 835/836, loaded, 2026-10-25 (the third round's CODESYS run beside it; a 21 s catch-up wait in `SourceProjectTests` failed and passed alone in 4 s); 13 m 9 s, 835/836, loaded, 2026-10-25 (the CODESYS gate ran beside it; the one 30 s catch-up wait failed and passed alone in 4 s); **12 m 22 s, 833/833, idle, 2026-10-24**; 11 m 23 s the same day loaded (three gates beside it; one 30 s catch-up wait failed and passed alone in 4 s) and 16 m 30 s under three builds; 10 m 45 s (loaded: mutation checks ran beside it) and 10 m 36 s on 2026-10-23; 12 m 19 s, 10 m 42 s and 9 m 5 s on 2026-10-22; 10 m 46 s on 2026-10-19; 10 m 58 s on 2026-10-16; 6 m 35 s on 2026-10-10; 8 m 38 s on 2026-10-05; ~3.6 min on 2026-09-22 | nothing |
| `Versio.Arch.Tests` | 42 | instant | nothing |
| `Versio.Plc.Codesys.Pure.Tests` | 405 | under a second | nothing |
| `Versio.Plc.Codesys.Parity.Tests` | 104 | ~15 s | `python` on PATH — skips cleanly without it, so check the count |
| `Versio.Plc.TiaPortal.Pure.Tests` | 108 | instant | nothing |
| `Versio.Plc.TiaPortal.Tests` | 5 | seconds when skipped; 3 m 1 s on 2026-10-25 when Portal is there but refuses the host (1 passed, 4 skipped, not an Openness member); minutes when Portal runs the import gate | **real TIA Portal** — skips without Openness membership or LFS content |
| `Versio.Plc.TwinCat.Pure.Tests` | 27 | instant | nothing — the host's text rules are compiled in as source |
| `Versio.Plc.TwinCat.Tests` | 6 | seconds when skipped (4 s, 6 skipped, 2026-10-25); ~5 min when XAE runs the import gate | **real TwinCAT XAE** — skips without `TcXaeShell.DTE.17.0` or the fixture |
| `Versio.Web.Tests` | 504 | ~25 s | nothing — GitHub is faked |
| `web/` (vitest) | 211 | ~21 s on 2026-10-24 (the review page waits for its reads, not ticks; it was ~3 min) | the pinned Node in `web/.node` |
| `Versio.Plc.Codesys.Tests` | 55 | **~30 min**; 41 m 14 s, 55/55, on 2026-10-11 on `WebViewer`; 1 h 22 m, 53/55, on 2026-10-25 on `VersioWeb` loaded (the two red are item 11's `<masked-guid>` regression) | **real CODESYS** |

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
| [`web/PLAN.md`](web/PLAN.md) | Design record for Versio Web — a browser view of a repository on GitHub, drawn by the same Core renderers, and since slice 2 a pull request reviewer with writes on. Its Step 1 is Outpost's Step 2b. Not the live queue |
| [`web/FINISH.md`](web/FINISH.md) | **The live queue for Versio Web** (2026-10-23): every gap between the page and a finished product — GitHub's look, the pull request page at parity, every desktop drawing — and the order they land in |
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

**Packaging ships Setup.exe and an MSI, both per-user, and an update feed** (item 5,
signing still open).
`Build_Package.bat` uses a cached PortableGit 7z and a pinned vpk when they are
already on disk, publishes a self-contained win-x64 payload (git, both vendor hosts,
.NET 9), and packs. Bundled git pins Git Credential Manager so PortableGit's
helper-selector does not ask on every fill. Settings and logs live in
`%LOCALAPPDATA%\VersioUser\`, not in the Velopack install directory, so a reinstall
does not wipe the recent-repository list. `Build_Package.bat` asks how to bump
`VersioVersion` (last / middle / first number, keep, or type one) before packing;
System information shows that product version. Re-packing the same number still
overwrites `artifacts/releases` (`vpk --legacyConsole --yes`). **Auto-update, 2026-10-27:**
`Publish_Release.bat` asks for the version and the notes (Notepad, prefilled from the
commits), packs, and publishes to the private `fluidstackio/versio-releases`; an installed
Versio offers it at launch and from the gear menu, reading the repository with the stored
git sign-in (FINDINGS 2026-10-27), and restarts through the close guard (SAFEGUARDS 3.4).
Per-user now, because a per-machine install needs an administrator for every update.
Not yet seen end to end: a published release installed over an older one on a second
machine, and a read-only user's access. Signing is still open. The CODESYS gate ran on `Siemens2` (item 1, done). Owner eyeballs
and the UX remainder are still the product remainder beside this.

```
  CODESYS pipeline    ████████████████████  proven on a real project
  TIA PLC-software    ████████████████████  SCL / UDT / tag tables; coverage skips elsewhere
  TwinCAT PLC-software████████████████████  ST; I/O export-only; coverage skips for NC / safety / C++
  Domain core         ████████████████████  vendor-blind, tested without a toolchain
  Vendor seam         ████████████████████  enforced by the project graph + metadata
  Desktop shell       ███████████████████░  git surface, tree, explorer; UX remainder below
  Packaging           ████████████████░░░░  per-user Setup.exe + MSI, update feed; signing open
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
5. **Packaging.** Unsigned `Setup.exe` and MSI, both per-user, from `Build_Package.bat`.
   The bat asks how to bump `VersioVersion` (last / middle / first number, keep, or type
   one). System information shows that product version. PortableGit is bundled (decision 3,
   closed). ~~The update feed~~ **built 2026-10-27** on `AutoUpdate`: `Publish_Release.bat`,
   GitHub Releases in `fluidstackio/versio-releases`, the launch and gear-menu check. Left:
   publish the first release with the check in it, install it on a second machine, publish
   one more and watch it offered, downloaded and restarted into; and a read-only account's
   access (FINDINGS 2026-10-27 says what was and was not measured). **Code signing**
   remains. Sequenced last on 2026-08-17, on purpose; first artifacts 2026-08-23.
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
10. **Versio Web, a browser view of a repository on GitHub, read-only at first and a pull request reviewer since slice 2 — built 2026-10-11 on
   `WebViewer`, since merged.** **[`web/FINISH.md`](web/FINISH.md) is the queue, and its Steps 0–10
   landed between 2026-10-23 and 2026-10-24 on `VersioWeb`:** GitHub's shape in place of the
   desktop shell (with Versio's mark and palette, not GitHub's, since the owner's first look on
   2026-10-24 — `tokens.css` is the desktop palette, `VersioMark` the header), the pull request page at parity (comments everywhere, landing on the right
   GitHub line; checks with their annotations; the merge box; the lists and the form; commit
   comments), every desktop drawing for every vendor through Core's one renderer choice, links that
   stay in context and reveal their target, "used by", and the operations work (audit lines on every
   write path, the live head's checks, both flakes retired, the App permission list). **What is
   left there is the owner's: the real-GitHub gates on a throwaway repository, writes on, merge
   last** — and the named future renderers and anchors (ladder elements, catalog rows, the
   desktop's own ST links). The history below is how it got here. The design, the security rules and what each step measured are
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
   **2026-10-14: the page restructured into the desktop's workbench shell** — the owner asked
   for the desktop's look over GitHub's; the rail (Project / History / Compare) swaps one panel
   card beside the pane, whose header is now the desktop's `PaneHeading` grammar. Phase 1 of
   five; the plan and the remaining phases (commit card, compare as tree marks, shared theme
   tokens, Explorer) are in [`web/PLAN.md`](web/PLAN.md), *The workbench restructure*.
   **2026-10-16: the owner's product shape recorded and its first piece built** (*The product
   shape* in [`web/PLAN.md`](web/PLAN.md)): `Web:Repositories` scopes the server to an admin's
   list — off the list is "not found" to everyone, `/api/repos` lists the scope filtered per
   caller, and the home page is that list, or the sign-in gate when `Web:AnonymousAccess` is
   off. Still open there: Okta sign-in, the Documentation tab, MRs and Releases tabs, the
   settings UI.
   **Later the same day: the MRs and Releases tabs** — two rail tiles, four read-only routes;
   a pull request's *What changed* opens the existing compare view at `base...headSha`, so a
   PR's PLC changes are reviewed as member-table and ladder diffs. **And the Documentation
   tab** — the tree filtered to documents under their folders; text through `/view`, PDF
   embedded from the new `/raw` route (its self-XSS rules on `Documents.cs` and in
   THREAT-MODEL), Office formats as downloads. Left of the product shape: Okta sign-in, the
   settings UI.
   **2026-10-17: pull request review designed, slice 1 started on `PrReview`** — the owner wants
   PRs reviewed in Versio Web with everything GitHub's page does, the PLC code drawn. Design,
   safety rules and six slices in [`web/PLAN.md`](web/PLAN.md), *Pull request review*; reviews
   and approvals in the person's own name, comments kept on GitHub. Done so far: **the merge-base
   fix** — "What changed" compared against the base branch's tip, so later base commits read as
   reverted (FINDINGS 2026-10-17). **Then slice 1, read-only review, built:** the sealed review
   snapshot (C1: re-read until head, base and merge-base agree; re-authorised on every use;
   expires with the user token), the file inventory from Versio's own tree diff with GitHub's
   patches deciding where line comments may go, the live state polled for "stale", checks (ETag
   polled), threads paged to completion at both levels, the conversation and commits, and the
   page — `/owner/repo/pull/N`, threads under their lines on Raw, rendered views labelled partial.
   Twenty mutations, each killed. Looked at in the built-in browser on the fixture preview
   (`/acme/plant/pull/1`), desktop and phone. **Its real-GitHub gate is the owner's**: register
   the App (read permissions are enough for slice 1, plus Pull requests and Checks read) and look
   at a real pull request whose base moved after branching. **Slice 2 built the same day**:
   conversation comments, line comments from a gutter click on Raw (checked against GitHub's own
   hunks for the snapshot, refused when the pull request moved), file-level comments, replies,
   edits, deletions and resolving threads — each in the person's own name, behind the kill switch
   (off by default, opted in per repository), the forgery checks, the node-id scope check and a
   per-session limit, reserved in the operation ledger so a duplicate is one write and a lost answer
   is reconciled, never re-sent. Twenty-seven mutations (twenty server-side, seven on the page), each
   killed or explained (the body cap has a second wall). Looked at in the preview with a signed-in preview session. **Its real-GitHub gate is
   the owner's**: the App needs Pull requests and Issues write; then post, lose the network
   mid-post, and resolve on a throwaway repository. Not run yet: `RedisOperationLedger` against a
   real Redis. **Slice 3 built 2026-10-19**: comments sit on member rows and ladder networks.
   Parsers record each row's and network's file lines (TIA and CODESYS XML with line info, ST
   declarations shifted to the file, a File-Based Storage header counted); renderers emit
   `data-anchors` and return the same anchors; `AnchorMatcher` finds a comment again by key plus
   revision or content, finds a renumbered network as Moved, and refuses whenever two candidates
   exist. The server resolves a clicked element from its own rendering, posts it at the element's
   own lines inside GitHub's hunks (or on the file), writes the tag, and places threads on
   drawings; a tag that disagrees with GitHub's current line places nothing. Element-level
   anchors deferred (PLAN.md). Seen in the preview: a comment written on network 2 lands, moved,
   on the renumbered network 1. **Slice 4 built 2026-10-19**: a drawing says which changed lines
   it shows. Each changed line of every file of the object needs evidence from its renderer — a
   drawn source line, or a member-table row drawn as changed whose shown fields account for every
   token on the line — or it is a gap the page lists with its reason and a link to Raw; "complete"
   only with none. XML tables and ladders stay partial (no field evidence yet), which is the rule
   working, not a defect. Reviews: one-shot Comment / Approve / Request changes with `commit_id`;
   drafts into a pending review created with `commitOID`; an older pending review submitted only
   as historical, its commit named; discard; the author offered Comment only. The dialog totals
   files never opened here and files with gaps, and informs without blocking. Viewed marks,
   reviewers, and "changes since your last review" are on the page. The move path is deferred
   (one pending review per person per pull request). Its gate, the owner's: approve as a second
   user while a third pushes mid-approval; submit an older draft historically; see the
   same-line shown-plus-unshown edit listed. **Slice 5 built 2026-10-20**: merge requests are
   opened from Versio (two branches of the repository, its template, draft or not), and their
   title, description, base, state, draft flag, labels and assignees edited. Every edit names what
   it replaces and is refused when a colleague changed it since; a base change makes the review
   stale, as a push does. Opening one is never repeated for one click: a lost answer is found by
   its tag. Its gate, the owner's: a pull request opened in Versio is identical on github.com; a
   base change forces the reload. **Slice 6 built 2026-10-20**, the last: merge, squash or
   rebase (as the repository allows), update branch and auto-merge, behind their own switch
   `Web:Writes:Merge`. Each names the reviewed commit as GitHub's own precondition, and nothing is
   merged past required reviews or checks — for an administrator either: only CLEAN, UNSTABLE or
   HAS_HOOKS merge. Branch deletion stays deferred (no atomic precondition). Its gate, the owner's,
   and the release gate: a merge with a stale head is refused; a merge succeeds; auto-merge is
   turned on only with the yes to later commits, and a push by someone without write access turns
   it off (one by a writer does not — GitHub's rule, corrected here 2026-10-20). Then the external penetration test with
   writes on, before anyone but the owner has them. **The PR review plan's six slices are built;
   every real-GitHub gate is the owner's to run.**
   ~~**A loose end seen once, 2026-10-19:** `PerformanceTests.A_page_load_asks_GitHub_for_each_thing_once`
   failed one full Web run and passed three after; not investigated. A suspect, unproven: the
   loader's resolution cache is written after its single-flight finishes, so a request arriving in
   between resolves again.~~ **Closed 2026-10-24 (FINISH Step 10):** the suspect was right.
   `SingleFlight` retires its key in a `finally` before the caller's `cache.Set`, so a request in
   between resolved again. The write moved inside the flight; the old binary failed once in ten
   class runs, the fixed one passed 32 of 32 — FINDINGS 2026-10-24.
   ~~**A second, seen 2026-10-20:** the review page's vitest waits a fixed number of short ticks for
   reads to land (`settle` in `PullPage.test.tsx`). With a mutation run's builds loading the machine,
   six slice 5 tests failed together and one failed again later; on an idle machine 86/86, twice.
   Timing, not behaviour, by every sign — but a wait-for-condition would retire it.~~ **Closed
   2026-10-24:** `settle` waits for the page to be at rest — no stubbed read in flight for three
   ticks — with a ceiling only a hang reaches; the file went from about three minutes to eighteen
   seconds, which is what the fixed ticks had been costing.
   ~~**The `PerformanceTests` flake above was seen a second time, 2026-10-20**, once in a full Web run
   right after the GraphQL-errors change; three runs after it were green (340/340 each). Still not
   investigated; the resolution-cache race stays the suspect. **A third sighting the same day**,
   once in a full run; four runs after it green (348/348). Three in a day is no longer rare enough to
   leave: it is the next loose end to take up. **A fourth, 2026-10-23**, once in a full Web run on
   the coverage-rules branch while an integration run shared the machine; alone and in a second full
   run, green (364/364). Still the resolution-cache suspect, still not investigated.~~ Closed, see above.
   **Left, all needing the owner or a deployment:**
   - **register the GitHub App** (on GitHub: read-only Contents and Metadata, a callback at
     `/auth/callback`) and run sign-in against real GitHub; until then the server runs
     anonymously on public repositories, which was checked against real GitHub;
   - decide who hosts it (the same question as Outpost §16); that picks the key vault behind
     `IAppSigner` and the key ring, and Redis if more than one instance;
   - build the Docker image on a host that has Docker (none on the machine that wrote it);
   - ~~one look in a normal browser at a Markdown page with a Mermaid diagram~~ — **seen
     drawing in the owner's Chrome 2026-10-16**, after that look found a white box: three
     stacked defects, all fixed (stub frame files outliving every rebuild, `hidden` losing to
     `display:block`, a hidden frame measuring itself as 40px; FINDINGS 2026-10-16);
     ~~and at the Docs tab's embedded PDF~~ — **seen drawing in the owner's Chrome 2026-10-16**,
     after its first look found the viewer's own frame lock blocking it (FINDINGS 2026-10-16);
   - the external penetration test and a cost alarm, once it is deployed;
   - later, on purpose: the desktop drawing with the shared HTML renderers; ~~and the choice of
     renderer moved into Core for both (today mirrored, pinned by a test)~~ — **moved 2026-10-23**.
   **2026-10-23: the choice of what a row draws is Core's** (`RenderChoice.Decide`), called by
   the desktop pane and by `ViewService`, after the owner's FBS library pull request drew its
   catalogs and empty objects as raw metadata on the web (FINDINGS 2026-10-23): the web now draws
   the project card, the three catalogs (Versio's YAML and the vendor's own files), the task and
   device cards, the empty-object note in a pull request, the whole file under a table whose
   format knows no body, a `.html` as `kind: "webpage"` served sandboxed by `/raw`, and a notice
   over any native file no model draws. `RenderChoiceParityTests` runs both hosts over every
   fixture row; `/tree` nodes carry the desktop's row whole. The page's side (framing the web
   page, drawing the new kinds and the tree fields) is the `web/` rebuild's.
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

11. **The five IDE gates the review's fixes owe, and two follow-ups — 2026-10-24.** FINDINGS
    2026-10-24 has the forty-one items. Owed: ~~`Versio.Plc.Codesys.Tests` over items 5 (the
    import reads a DUT as written) and 31 (task retargeting within the Application)~~ — **ran
    2026-10-25 on `ba8dbdf`: 53/55 in 1 h 22 m**, loaded (the owner's CODESYS GUI, Portal and
    the installed Versio stayed open; none holds the CODESYS mutex while idle). Items 5 and 31
    pass under real CODESYS —
    `A_declaration_only_object_edited_in_the_mirror_reaches_the_project` and
    `A_POU_renamed_in_the_mirror_keeps_its_identity_rather_than_being_rebuilt`, which asserts
    the task list names the renamed POU; the second-Application case of 31 stays parity-only.
    **The two red are a regression from item 20's own fix** (`4b83f89`): both normalisers mask a
    GUID to `<masked-guid>`, and the new sibling chunker reads that as an open tag, so an
    unordered container holding GUIDs never closes, its children are never sorted, and the two
    vis files CODESYS re-mints on import compare as changed —
    `An_import_and_a_re_export_leave_the_mirror_byte_identical` (`Live_Visu.vis.xml`,
    `Visualization.vis.xml`, then `_manifest.json`) and
    `A_member_inside_a_folder_round_trips_and_moves_nothing_else` (the same two files).
    Reproduced without CODESYS on the embedded python as plain CPython: two swapped siblings
    compare equivalent with plain values and not with GUIDs. It is the second review round's
    item 7, fixed by that round's session (`2d602ec`, the placeholder is `{masked-guid}` on
    both sides); **re-run on `5cb51ef` with that python embedded: the two red tests and the
    item 5 and 31 tests, 4/4 in 9 m 7 s.** The CODESYS gate is settled for the first round.
    **Re-run once more on `85f88d1` — the third round's item 4 changed the delete check's
    python — with the three real-CODESYS deletion tests beside those four: 7/7 in 22 m 34 s,
    the owner's IDEs open (FINDINGS 2026-10-25, the third round's item 4).** **And once more on `ef3b469` — the fifth round's item 1 changed the delete check's
    python again — the same seven: 7/7 in 26 m 33 s, the owner's IDEs open (FINDINGS
    2026-10-25, the fifth round's item 1).**
    `Versio.Plc.TwinCat.Tests` over 23 (does XAE accept an empty `ImplementationText`?) and the
    `ide_pid` half of 28 — **cannot run here: 6 of 6 skipped in 4 s, no `TcXaeShell.DTE.17.0`
    (2026-10-25)**; `Versio.Plc.TiaPortal.Tests` over 27 (the Comment marker is 300
    characters now) and the other half of 28 — **cannot either: 1 passed, 4 skipped in 3 m 1 s
    (2026-10-25)**; the locator passes because only V21's PublicAPI has a `net48` folder, and
    each of the four Portal tests launched the host and was refused, the account not being in
    the `Siemens TIA Openness` group. Both gates are still owed, on a machine that has the
    IDE. Follow-ups, named and not done: ~~a Clean binding
    with an **unconfirmed** mirror edit exports over it on the next project save
    (`A_project_save_does_not_export_over_an_unconfirmed_mirror_edit`, skipped in Core;
    `SyncStateMachine.EnterConflict` has no caller)~~ — closed 2026-10-25 by the second
    round's item 1 (a gate beside the direction lock refuses the export; `EnterConflict` still
    has no caller, on purpose, and dev.md says so) — and branch deletion still addresses the
    hardcoded "origin" after item 39.

12. **The review's second round, twelve items, all fixed — 2026-10-25.** FINDINGS 2026-10-25
    has the list. Two of them touch the embedded python and so the CODESYS gate: item 3 (the
    rename's task walk stops at a nested Application) and item 7 (the GUID mask no longer
    breaks the sibling sort the delete check reads — the two red of item 11's 53/55). The gate session re-ran those two red tests, with item 11's 5 and 31, on the new
    embedded python: 4/4 in 9 m 7 s on `5cb51ef`; item 11 carries the result.
    The TIA and TwinCAT gates owe nothing new; the TIA import Fact ran green over the branch
    in passing. Still not done: branch deletion addresses the hardcoded "origin" (the first
    round's item 39).

13. **The review's third round, four items, all fixed — 2026-10-25.** FINDINGS 2026-10-25,
    the newest entry, has the list: a semicolon on its own line merged two ST declarations
    (a regression from the second round's item 9), a reversed `.gitattributes` range made
    the commit preflight throw, a non-ASCII file name was matched as characters where git
    matches bytes, and the delete check could no longer read the `nhash` every existing
    mirror carries, because the normaliser had changed twice without it. Item 4 changed the
    embedded python; item 11 carries the CODESYS re-run over it. Still not done: branch
    deletion addresses the hardcoded "origin" (the first round's item 39).

14. **The review's fourth round, seven items, all fixed — 2026-10-25.** FINDINGS 2026-10-25,
    the newest entry, has the list. Two P1: a backup whose tree copy stopped partway was
    listed and restorable, and restoring it deleted the half of the live project the copy
    never had; "merge upstream" (and rebase) adopted a merge that reached a tracked project,
    recording the mismatch as synced so the next save exported over the incoming logic. Five
    P2: an excluded staged rename committed its deletion, a modify/delete conflict could not
    keep the deleted side, a Stop during the scratch copy's hash left the binding stuck and
    Coalesced, applying a web suggestion rewrote the unselected lines of a mixed-ending file,
    and a CRLF suggestion whose fence ended its line was not recognised. Nothing touches the
    embedded python, so the CODESYS gate owes nothing new. Still not done: branch deletion
    addresses the hardcoded "origin" (the first round's item 39).

15. **The review's fifth round, six items, all fixed — 2026-10-25.** FINDINGS 2026-10-25,
    the newest entry, has the list. Three P1: the third round's legacy-hash fallback let
    the lossy line-sort hash authorise the deletion of a modified object (an older hash now
    vouches only where its normalisation was lossless for that object; the rest wait for a
    re-export — item 11 carries the CODESYS re-run), nested export roots were accepted and
    the outer export deleted the inner project, and the TIA host wrote a plan's payload into
    the first PLC when its target could not be found. Three P2: a selected-file first commit
    failed on `restore --staged` with no HEAD, the commit page drew a renamed-and-edited file
    as wholly added, and one malformed `_manifest.json` aborted repository discovery. The
    TIA gate is still owed on a machine in the Openness group. Still not done: branch
    deletion addresses the hardcoded "origin" (the first round's item 39).

16. **The review's sixth round, five items, all fixed — 2026-10-25.** FINDINGS 2026-10-25,
    the newest entry, has the list. Three are the fifth round's fixes one case short: the
    nested-root check now reads every project the scan found, tracked or not (the outer one
    is refused); the TIA host refuses a file for a PLC that is gone even when one PLC is left;
    the renderer pairs a renamed object by the explicit old/new path rather than by suffix.
    Two new: a paused rebase is continued with `rebase --continue` instead of a plain commit,
    and `repo.json` with null collections or null entries is read without a crash. No python
    touched, so the CODESYS gate owes nothing new; the TIA gate is still owed. Still not
    done: branch deletion addresses the hardcoded "origin" (the first round's item 39).

17. **The review's seventh round, three items, all fixed — 2026-10-25.** FINDINGS 2026-10-25,
    the newest entry, has the list. P1: restoring an auto-stash adopted the restored mirror
    edit as synced, so the next save exported over it — the restore no longer adopts and
    raises the import the edit owes. P2: a throw from inside a run (two bindings sharing
    one state.json.tmp) left the coalescer's slot held for good — the run answers Failed
    and releases it, the JSON writer serialises writers and retries a replace a scanner
    holds; a mirror subfolder moved out raised no change — the watcher turns folder events
    into the files they carried and the poll reports a tracked file that is gone. No python
    touched. Still not done: branch deletion addresses the hardcoded "origin" (the first
    round's item 39), and the merge twin of the sixth round's item 4.

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
| 4 | ~~Where does the update feed live?~~ **GitHub Releases in the private `fluidstackio/versio-releases`**, read with the stored git sign-in (2026-10-27). | Packaging — answered |
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
