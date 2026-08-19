# Versio

**Git-native version control for PLC projects.**

A PLC project file is a binary compound document. Git can store it, but it cannot diff it, merge it, or tell you what changed — so version control degrades to "upload a zip with a date in the name". Versio keeps a **textual mirror** of the project inside the same repository, kept in sync automatically, so git becomes genuinely useful for PLC work: line diffs, blame, history, review.

**CODESYS 3.5 is implemented today. TIA Portal is the second pack** — seen, probed, and
round-tripped for PLC software once Openness membership is granted. Rockwell Studio 5000 and
Beckhoff TwinCAT remain intended next vendors. The code is organised so adding one is additive
— a rule enforced by a test that reads assembly metadata, not by a paragraph here.

> **Status: pre-alpha, and the round trip works automatically.** A project in the repository is tracked without ceremony, exported to its reviewable mirror when it changes, and edits to that mirror flow back into the project — driven by the direction lock, proven end to end against a real project with real CODESYS. See [What works today](#what-works-today).

---

## The idea in one picture

```
  MyMachine.project  ──── export ────►  MyMachine/
  (binary, LFS)                          ├─ Simulation.st          ← readable, diffable
        ▲                                ├─ PLC_PRG.xml            ← graphical POU, native
        │                                ├─ Glob_Var.gvl.st + .xml
        └──────────── import ────────────┤ Visualization.vis.xml
                                         └─ _manifest.json         ← GUID → path → hash
```

Both directions run automatically and deterministically. A per-project **direction lock** is what makes that safe: once one side is authoritative, the other is refused until the work is pushed or discarded. Nobody is present to arbitrate, so the lock, the hash gate, the self-write suppressor and the circuit breaker are the defence rather than defence in depth — and the project is copied aside before an import ever writes to it.

---

## What works today

| Capability | State |
|---|---|
| Export a project to per-object `.st` / native `.xml` | ✅ verified on a real project |
| **Deterministic export** — same project, byte-identical output | ✅ 3 consecutive runs, all 11 files |
| Import edited `.st` back into a project | ✅ round-trip proven byte-identical |
| **An import is never applied without being agreed to** | ✅ an edit to the mirror is detected, exported nowhere and written nowhere until a person confirms it. The project panel says which project is waiting and what would be rewritten; a dialog names it again before anything is written; and a copy of the `.project` is kept first. Declining leaves the offer up rather than forgetting it |
| **One repository, one Versio** | ✅ a second instance is told which window already has it, rather than opening beside it and letting two orchestrators write `repo.json` over each other. A crashed instance never locks anybody out: the marker is checked against a live process, and a corrupt or unwritable one means the repository opens anyway |
| **Ten copies of every project, and a way back to them** | ✅ one is taken before every import and the last ten survive. A *Restore a saved copy…* button appears on a project that has them, listing each by local time and how long ago. **Restoring keeps a copy of what it replaced**, so going back one copy too far is recoverable rather than final. Refused while the IDE holds the project |
| Import native XML **in place**, GUID preserved | ✅ no delete-and-recreate |
| Create objects that do not exist yet | ✅ POU / FB / FUNCTION / INTERFACE / DUT / GVL |
| **Delete a source file and the object leaves the project** | ✅ **textual and native alike.** Deleting is the one thing Versio does that destroys work, so it is built as refusals: five checks before it is even proposed, **the confirmation dialog naming which objects will really go and which will really stay**, and a final check inside CODESYS that the object still matches what was exported — **an object somebody edited in the IDE since is kept**, because a missing file says a file was deleted, not that the object was abandoned. A textual file is rebuilt and compared byte for byte; a native one is re-exported and compared against the normalised hash the export recorded (manifest schema 5), since native bytes churn between exports. A native object exported before that hash existed is refused until the next export |
| **Rename a source file and the object is renamed, not rebuilt** | ✅ **including a POU.** The import renames and moves the object itself, GUID intact, rather than destroying it and building a new one — **and repoints any task that ran it**, because a task's POU list is stored by NAME and a rename does not update it. That was measured after the fact: the object survived and its task went on naming a POU that no longer existed. A POU's name lives inside its declaration, so renaming one rewrites the whole file — it is paired on the **implementation**, which a rename does not touch (manifest schema 6), and the new declaration is then applied to the renamed object. Ambiguity is refused rather than guessed: two objects sharing a body cannot be told apart, so both stay a delete and a create. Renaming only the file is recognised as changing nothing and says so, and a move the toolchain silently declines is caught and reported rather than counted. A rename that also **edits** the body matches no hash at all, so it is scored on similarity — against the departed file's content at HEAD, or against a fingerprint the export recorded when the mirror was never committed and git has no old copy. The last of three passes, in order of certainty, and the only one that says in the dialog that it used a judgement and which instrument made it |
| Drive CODESYS headlessly from C# | ✅ `LocalCodesysRunner` |
| Git status / branches / stage / commit / diff / push | ✅ `GitCliClient` over real `git.exe` |
| Direction lock, hash gate, coalescer, commit gate | ✅ pure logic, heavily tested |
| Open-project detection, behind the vendor seam | ✅ `IPlcProjectFormat` answers where the marker is and what it says; a stale one from a crashed IDE is ignored, one from another machine is believed |
| **Export while the project is open in the CODESYS IDE** | ✅ exports a copy — the vendor lock is a sidecar on the path. Proven against a real CODESYS holding the original |
| **Import while it is open** — waits and names who has it | ✅ it cannot be made to work: the IDE would revert it on the next save. Neither direction marks the binding failed, and the breaker is never fed |
| **Retry automatically when the IDE closes** | ✅ the vendor's open-project marker is watched, so its deletion — the IDE letting go — re-submits both directions through the ordinary gates. Closing CODESYS *is* the retry; a marker rewrite while the IDE works stays silent |
| Sync orchestrator — locks, loop defences, commit gate | ✅ 46 scenario tests |
| **The app says when it is working** — progress bar, live heartbeat, and the row reading `exporting…` | ✅ a run announces itself when it passes the last gate, so a refused or coalesced save never flashes a bar |
| Change detection — watcher + safety poll + overflow rescan | ✅ `FileChangeWatcher`, driving the sync engine |
| Staleness stated, not implied — "2 projects not yet exported" | ✅ answered by the commit gate, so the two cannot disagree |
| **The project view follows the repository by itself** | ✅ 18 policy tests + 9 against a real filesystem; rebuilt only when something it shows changed |
| Design system tokens, light and dark | ✅ `src/Versio.App/Theme/`; switching at runtime repaints open windows, which it never did until it was measured |
| Logging — ring buffer, correlation ids, heartbeat, rolling file | ✅ 21 tests; timestamps are local and the file rolls on the local day, so the panel and the file agree; "Open logs folder" selects today's file |
| **The log panel filters** — level as four chips, category as a multi-select with live counts | ✅ a set rather than a minimum level, because "warnings and errors only" is the request a threshold cannot express |
| **An idle app stops writing about it** — quiet status polls are summarised, not logged one by one | ✅ ~540 lines an hour became 11; the per-trigger counts survive, since a working-tree poll finding nothing is the only signal for tuning the change filter |
| **The export narrates itself** — directory swap with both file counts, every skip and its reason | ✅ asserted against the log a real export produces, across two runs |
| Shell — repo/branch dropdowns, rail, changed files, diff, collapsible log | ✅ opens a real repo, shows real git state |
| Diff view — gutter, line numbers, syntax highlighting for 11 languages | ✅ ST, C#, Python, XML, JSON, YAML, Markdown, PowerShell, JS/TS, SQL |
| **New files diff as entirely added**, not as "no diff available" | ✅ `--no-index` against `/dev/null`, all lines green |
| **Show full file** — every line, changes still highlighted | ✅ large `-U`, so it stays a diff rather than becoming a file viewer |
| Diff view survives a 2.5 MB / 33,644-line visualisation export | ✅ rows built only when realised; parse off the UI thread |
| **App-wide progress bar** — one signal for git, export and import alike | ✅ counted `BusyTracker`, with a caption. **An export now counts**: the exporter walks the tree once to get a denominator and reports each object as it starts it, so the bar moves and names what it is on. Everything else stays indeterminate |
| CODESYS runs with no visible window | ✅ `CREATE_NO_WINDOW` **and** `SW_HIDE` — different subsystems |
| Repository validation — walks up to `.git`, refuses non-repos with a reason | ✅ handles subdirectories, bare repos and linked worktrees |
| **The repository dropdown remembers** — recent repositories across restarts | ✅ `%LOCALAPPDATA%\Versio\settings.json`, atomic write, capped at ten. The remote is recorded while the repository is open, so one that has vanished offers *clone it again* / *remove from list* / *cancel* rather than a folder picker. Startup falls back to the most recent repository still on disk |
| **Exports what changed while Versio was closed**, when the repository opens | ✅ after the toolchain is probed, the locks restored, the projects scanned and staleness evaluated — in that order, because exporting earlier would run against a lock not yet read |
| PLC state — toolchain, lock and heartbeat | ✅ environment (toolchain, version, profile) moved to Settings → Help → System information; a failure is reported on the project's own row with a *Try again* beside it. No permanently disabled buttons |
| **Vendor seam** — `IPlcRunner`/`PlcJob`/`PlcResult`, enforced from assembly metadata | ✅ 22 architecture tests, proven able to fail |
| Multi-vendor detection — registry, per-binding vendor, mixed repositories | ✅ `PlcFormats`; Rockwell/Siemens/TwinCAT are additive |
| Application icon — 8 sizes, hand-tuned below 48px, embedded in the exe | ✅ 12 tests over the `.ico` itself |
| **Automatic tracking** — every project found, with its `.gitattributes`/LFS rules | ✅ 13 refusal tests + real-repo integration |
| **`.gitignore` written on adoption**, created if the repository has none | ✅ covers `.versio/local/` (locks, backups, scratch copies) and the toolchain's `.opt` files; project-scoped, never repository-wide |
| **Automatic export** when the project changes | ✅ the default, and the point of the mirror: the diff exists to be read *before* a commit. 11 files in 16.7 s on the fixture, lock taken and persisted. A 10 s minimum interval floors how often the toolchain starts and the coalescer holds one pending job per project, so N saves cost one export |
| **An export landing mid-review does not interrupt it** | ✅ the tree is reconciled, not rebuilt — objects appear and disappear while the row you selected stays selected |
| Direction lock in plain English, shared by the panel and the log | ✅ `SyncNarrative` — every blocking state names its remedy |
| Heartbeat during a 17–31 s export | ✅ elapsed time in the panel, not silence |
| WPF binding-failure tracing — `Versio.exe --trace-bindings <file>` | ✅ the one silent failure class, made loud |
| **Automatic import** when the mirror changes and no lock forbids it | ✅ the project is copied aside first; a failed backup fails the import |
| Change detection over the mirror's real contents | ✅ `ContentHash.OfTree`; a hand-edited `.st` is seen, an untouched tree is not |
| Side-by-side diff and word-level intra-line diff | ⛔ inline only |
| **One project view** — the full CODESYS tree with changes marked, plus every other changed file | ✅ replaced the PLC panel and the changed-files list |
| A project with a clean mirror is not listed | ✅ unless it needs attention — never exported, behind, locked, failed. The empty state says why rather than going blank |
| **Lazy object rendering** — a row is built when something can reach it, not before | ✅ `TreeRow.Children` on first access; the tree survives a refresh without collapsing |
| **Export coverage stated, not assumed** — `_coverage.md` + every skip in the manifest | ✅ a gap is distinguished from a deliberate omission; an old manifest reports "unknown", never "complete"; and **an object the export could not write at all is reported rather than counted as covered** — it reaches neither the records nor the skips, so until 2026-08-16 a lost object left a verdict of "everything reaches the mirror" |
| **Library references exported** (`<manager>.libraries.yaml`) | ✅ **the gap is closed.** One file per library manager, beside the object that owns it — 21 references on the fixture's application, 13 on its project, **all 34 placeholders**. Hand-written YAML on both sides, no third-party dependency. Export-only: change libraries in the IDE. A **fixed-version** reference records its version, company and title parsed from its own name, which is project state — reading them off `managed_library` reports what the exporting machine has *installed*, and answers nothing at all for a library it does not have |
| Template generation | ⛔ needs a new operation and script — not assembly |
| A second vendor — TIA Portal | ✅ the second pack; PLC software round-trips once Openness membership is granted. TwinCAT / Studio 5000 are not started |
| **Git side panel** — staging, commit message, fetch/pull/push | ✅ one operation at a time across every button |
| **Changed files named as objects** — `FB_Motor · POU`, not `src/…/POU/FB_Motor.st` | ✅ resolved through the export manifest, so it costs no extra read. A file the manifest does not describe stays a path and is **never** guessed at. A library-reference file carries a lock: editing it does not reach the project |
| **Change summary and drafted commit subject** — *"1 GVL and 1 POU changed in RefrigeratorControl"*, and a **Draft** button that writes `Modify FB_Motor, Glob_Var` | ✅ counted by object, not by file — one GVL is two files and one edit. Both describe exactly what the commit button will record, and Draft refuses to overwrite anything you have typed |
| **Merge / pull request button** — GitHub, GitLab, Azure DevOps, Bitbucket, Gitea | ✅ opens the forge's own page with both branches **pre-filled and nothing submitted**. Says *pull request* or *merge request* as that forge does. Turns into **Go to** on its own when one already exists, found through the forge's hidden refs — **no API token, nothing stored**. A host it cannot identify gets **no button**, never a guess; set `"forge"` in `repo.json` for a self-hosted instance |
| **The commit gate, enforced** — a stale export is exported and staged first | ✅ push releases the direction locks, commit does not |
| **History panel** — commits with hash, local date and time, author and subject; expand for the files, click a file for its diff | ✅ its own rail button, and lazy at four levels: the log is not read until the panel is opened, then a page at a time; a commit's files when it is expanded; a patch when a file is clicked |
| **Stash** — push, apply, pop, drop | ✅ **not as buttons.** The six permanent controls were withdrawn and the operations kept; they drive the auto-stash now, which is where a stash is actually wanted — at the moment a branch switch would otherwise strand uncommitted work. A manual stash UI is still deliberately absent |
| **Per-hunk stage and unstage** | ✅ the hunk is copied verbatim and applied to the index; the working tree is never touched |
| **Branch creation** — validated against git's own rules as you type | ✅ creates from HEAD and switches; 35 unit tests over the rules, and the dialog says *which* rule was broken |
| **Clone with verification** — URL, Verify, destination, progress | ✅ Clone stays dead until `ls-remote` passes, and editing the URL shuts it again |
| **Discard** — per file, after a confirmation naming it | ✅ refuses an untracked file with an explanation; releases the direction lock when the file was inside a mirror |
| **Nothing is interrupted mid-flight** — a guard decides whether closing, switching branch or switching repository may happen right now | ✅ `InterruptionGuard`, pure, 93 unit tests. An export may be cancelled and an import may not: the export stages and swaps, the import writes into the binary project |
| **Closing waits, or refuses** | ✅ an export offers *wait* or *stop it*; an import refuses outright until it finishes or times out. Before this, closing orphaned a hidden CODESYS still holding the project |
| **Every operation can actually be cancelled** | ✅ the plumbing reached `KillTree` all along and every caller passed `CancellationToken.None`. 25 call sites now carry a real token |
| **The repository and branch dropdowns are guarded** | ✅ shut while an import runs, and they snap back whenever a switch does not happen — a refused checkout used to leave the control naming a branch you were not on |
| **Auto-stash on a branch switch** | ✅ uncommitted work is stashed with a marker, recorded in `.versio/local/autostash.json` with the direction locks it released, and offered back — restore, leave, discard — on returning to that branch. The stash is proven to exist by a set difference over `git stash list`, because `git stash push` on a clean tree exits 0 and creates nothing |
| Branch switching with work in the tree | ✅ the guard asks, the auto-stash saves it with the direction locks it released, and returning to the branch offers it back — restore, leave, or discard. A stash that git did not actually create abandons the switch rather than checking out over unsaved work |
| **Project tree** — CODESYS structure, roll-up badges, search | ✅ 34 unit tests + built from the export this repository commits |
| **Methods and properties under the object that owns them** | ✅ manifest schema 4 records each member's owner GUID and the tree nests by it — a chain, since a property owns its accessors. Before this a function block's methods were its siblings, and four `InitRoom` methods on four different rooms were four rows in one folder. A mirror exported earlier carries no parents and stays flat until it is re-exported |
| Config nodes — one tree node per library, layer switcher, context menu | ⛔ not built. The library YAML now exists and parses (`LibraryCatalog`), so a version bump lands under the device that owns it; what is missing is a node per library. Device and IO YAML are still unwritten |
| Packaging | ⛔ not built |

**[docs/STATUS.md](docs/STATUS.md)** has the current state and the route to completion.
**[docs/SAFEGUARDS.md](docs/SAFEGUARDS.md)** is what Versio refuses, asks about or waits for —
the interruption guard, the auto-stash, and the ways this could lose work. Eight of its nine
phases are built, and so is the ninth. What is left in its scenario list is §5.H, a repository
that disappears while it is open.
**[docs/phase1/GIT-PANEL-PLAN.md](docs/phase1/GIT-PANEL-PLAN.md)** is the git panel's functionality and UX.
Eleven of its twelve phases are built; what is left is the ahead/behind previews and a list of
smaller polish items.
**[docs/vendors/codesys/DRIVING.md](docs/vendors/codesys/DRIVING.md)** is how CODESYS is actually driven — the ScriptEngine
surface, the command line, both sides of the JSON-lines protocol, every script, and the export
and import mechanics. Its §12 is a standing audit of that integration: what is broken, what is
merely stale, and where the C# and the python have drifted apart.
**[docs/phase1/PHASE0-FINDINGS.md](docs/phase1/PHASE0-FINDINGS.md)** records the measurements and the known
limitations — including the one real CODESYS mutation it found: `Core2`, a task's CPU core
affinity, rewritten by `import_native` without being asked. **That one is fixed** — task
configuration imports through the typed API now and is no longer diff-only — so read that
document as a record of what was measured in Phase 0, not as a list of what is still true.
**[docs/phase1/IMPORT-PLAN.md](docs/phase1/IMPORT-PLAN.md)** is where the import side is
being finished, and it is the active plan: eleven numbered gaps, what each measurement closed,
and the order the evidence argues for building the rest in.
**[docs/phase1/PLAN.md](docs/phase1/PLAN.md)** is the full architecture plan behind all of it.

---

## Repository layout

```
Versio.sln
├─ src/
│  ├─ Versio.Core/              Domain + orchestration. Pure — no IO, no processes, no clock.
│  │  ├─ Abstractions/          IClock, IFileSystem, ILockStore, IGitClient
│  │  ├─ Bindings/              project ↔ export-directory relationship, adoption, orphans
│  │  ├─ Diff/                  unified-diff parser and the syntax tokenizer
│  │  ├─ Git/                   porcelain v2 parser, progress parser, .gitattributes planner
│  │  ├─ Hashing/               content and export-tree hashing
│  │  ├─ Iec/                   IEC 61131-3, shared by every vendor: ST codec, declarations,
│  │  │                         the standard's object-type names
│  │  ├─ Locking/               lock records, reboot-survival recovery policy
│  │  ├─ Logging/               ring buffer, correlation ids, heartbeat
│  │  ├─ Plc/                   the vendor seam: IPlcRunner, PlcJob, PlcResult, PlcVendor,
│  │  │                         PlcFormats — and nothing that names a brand
│  │  ├─ Projects/              the manifest, the export-coverage report, the library
│  │  │                         catalog, the object-type dispatch and the project tree
│  │  ├─ Sync/                  state machine, hash gate, suppressor, breaker, commit gate
│  │  └─ Watching/              debounce, coalescing, and when the shell re-reads git
│  │
│  ├─ Versio.Infrastructure/    The only project that touches the outside world.
│  │  ├─ Bindings/              repository scan, repo.json, adoption side effects
│  │  ├─ FileSystem/            Windows filesystem, JSON lock store
│  │  ├─ Git/                   git.exe client
│  │  ├─ Logging/               rolling file sink
│  │  ├─ Platform/              clock, machine identity, process probe
│  │  ├─ Plc/                   toolchain registry, stand-in for an absent toolchain
│  │  └─ Watching/              the OS-level file and repository watchers
│  │
│  ├─ Versio.Plc.Codesys/       ONE VENDOR, pure. GUID table, XML shape, profiles, wire
│  │                            protocol, import planner, CODESYS's own tree vocabulary.
│  │                            Versio.Core has no reference to this — that is the rule,
│  │                            and the compiler is what enforces it.
│  │
│  ├─ Versio.Plc.Codesys.Infrastructure/
│                               ONE VENDOR, driven. Process host, install locator, script
│                               extractor, and CodesysPack.Register().
│     └─ Scripts/               IronPython 2.7, run inside CODESYS, embedded into this
│                               assembly as resources. The pure modules (text, types,
│                               decl, yaml, libraries, tasks, native_equiv) import no
│                               scriptengine and run on CPython too, which is what the
│                               parity tests use.
│  ├─ Versio.Plc.TiaPortal/     ONE VENDOR, pure. Directory payload, import planner,
│  │                            SCL header, native-XML scrubber.
│  ├─ Versio.Plc.TiaPortal.Infrastructure/
│  │                            ONE VENDOR, driven. Locator, runner, TiaPortalPack.
│  └─ Versio.Plc.TiaPortal.Host/ ONE VENDOR, net48 exe. Loads Openness from the install.
├─ tests/
│  ├─ Versio.TestSupport/       not a test project: the three helpers more than one suite
│  │                            needs (TestPaths, TestLogger, VendorSeamScanner)
│  ├─ Versio.Core.Tests/        1,392 unit tests — pure, no IO, under a second
│  ├─ Versio.Integration.Tests/ 147 tests against real git and the real filesystem, ~10 s.
│  │                            NO CODESYS: that is a different project now, so this one
│  │                            needs no filter and cannot accidentally cost half an hour
│  ├─ Versio.App.Tests/         547 tests that build the real windows and panels against
│  │                            the real stylesheet on an STA thread — none is ever shown
│  ├─ Versio.Plc.Codesys.Pure.Tests/ 260 pure vendor tests — the planner, the codecs, the
│  │                            protocol — under a second, needing nothing
│  ├─ Versio.Plc.Codesys.Parity.Tests/ 92 CPython parity tests over the vendor's python,
│  │                            ~15 s, no CODESYS — python on PATH, skips cleanly without
│  ├─ Versio.Plc.Codesys.Tests/ 54 tests against real CODESYS, ~30 min. The CODESYS gate
│  │                            before a commit, and the only suite that needs that toolchain
│  ├─ Versio.Plc.TiaPortal.Pure.Tests/ 15 pure vendor tests — format, planner, protocol
│  └─ Versio.Plc.TiaPortal.Tests/ 4 tests against real TIA Portal; skip with a count when
│                               Openness membership or the install is missing
├─ tools/                       PowerShell and python harnesses. tools/tia/ is the second
│                               vendor's round-trip and P7 harness; the rest are CODESYS
│                               probes. New-VersioIcon.ps1 generates the app icon.
├─ assets/                      versio.svg (reference drawing) and versio.ico
│                               (the build input — generated, never hand-edited)
├─ fixtures/projects/           the PLC projects the suite runs against, plus their
│                               committed export mirrors. RefrigeratorControl.project
│                               (CODESYS) and CleaningStation_V20_V21/ (TIA Portal,
│                               a directory) are GIT LFS — a clone without git-lfs
│                               gets 130-byte stubs and the toolchain gate skips
│                               rather than opening a pointer. This repository's
│                               .versio/repo.json ignores the TIA tree so opening
│                               Versio here does not start a 37 s Portal export
├─ phase0/                      GITIGNORED scratch, created on demand by the tools/ harnesses
│                               and safe to delete whole — it reached 150 MB of scratch
│                               project copies before it was cleaned out on 2026-08-17. The
│                               probe logs worth keeping (the primary measurements behind
│                               every P-numbered claim) were archived to docs/phase1/probes/
├─ .versio/                     Versio's own state for this repository, because it tracks
│                               its own fixtures. repo.json is committed; local/ is not
└─ docs/
   ├─ PHASE2.md                 THE FORWARD QUEUE — what is left, in order, and the
   │                            decisions already taken with their why
   ├─ STATUS.md                 where the project is; audits and the findings index
   ├─ FINDINGS.md               111 defects with their evidence, newest first — the
   │                            engineering log, split out of STATUS.md when it reached
   │                            77% of that file
   ├─ SAFEGUARDS.md             what Versio refuses, asks about, or waits for
   ├─ phase1/                   THE PHASE 1 ARCHIVE, closed 2026-08-17 — four completed
   │                            plans (PLAN, IMPORT-PLAN, GIT-PANEL-PLAN,
   │                            PHASE0-FINDINGS), each marked done, plus probes/ holding
   │                            the raw measurements behind every P-numbered claim.
   │                            Reasoning and evidence, never current state
   └─ vendors/                  ONE VENDOR's documentation per pack
      ├─ codesys/DRIVING.md     how CODESYS is driven end to end, and the audit of it
      └─ tia/DRIVING.md         how TIA Portal is driven — Openness, host, occupancy
```

`Versio.App` holds the WPF shell: `Theme/tokens/` for the design tokens, `Theme/controls/` for
control styles, `ViewModels/`, `Views/`, and `Spikes/` for the tree performance harness
(`Versio.exe --spike --measure`).

Two things inside it are split across files for a reason worth knowing before editing either.

**`Theme/controls/Controls.xaml` is a running order, not a stylesheet.** The styles live in
`Primitives.xaml` (converters, icon glyphs, `IconText`, the motion storyboards, the focus rings)
and five leaves beside it — `Buttons`, `Inputs`, `Rows`, `ScrollBars`, `Chrome`. **Each leaf
merges `Primitives.xaml` itself**, and that line is load-bearing: a `StaticResource` resolves
against its own dictionary and its own merges, never against a sibling the parent merged
alongside it. Drop the merge and the file still compiles, then throws at the first render.
`StyleSheetResolutionTests` renders every keyed style in the merged result, which is what turns
that back into a build-time answer.

**`Views/Panels/` holds all four panels** — the three the rail switches between (git, history,
project) and the log console — as `UserControl`s. `MainWindow.xaml` is 936 lines and keeps each
one's row, name and visibility, because the rail decides which is showing.

Three consequences, each of which has cost something at least once: a `UserControl` owns its own
XAML name scope, so `window.FindName` cannot see inside a panel; a collapsed one has not applied
its template, so its contents are not in the visual tree until it has been shown once; and **a
card's outer margin belongs on the hosted control rather than inside it**, or the element
`FindName` returns and the element that paints stop being the same rectangle — which is invisible
on screen and immediately visible to a geometry test. `Views/RowTemplates.xaml` exists because a
`UserControl` cannot see the resources of the `Window` hosting it.

The log console was the last one out and the only one with real code-behind: it auto-scrolls on
append and swaps its chevron on expand. What stayed behind is the half a panel cannot own — the
`GridSplitter` above it and the `RowDefinition` it resizes are the window's grid, so both sides
listen to `IsExpanded` independently rather than one calling the other.

### Vendor boundaries

Versio started CODESYS-only and will grow to other PLC toolchains, so the code is split
three ways and the namespaces say which is which:

| Project | Scope | Example |
|---|---|---|
| `Versio.Core` → `Plc/` | **Neutral contracts** every vendor implements | `IPlcRunner`, `PlcJob`, `PlcResult`, `PlcFormats` |
| `Versio.Core` → `Iec/` | **The IEC 61131-3 standard**, shared by every vendor | `StTextCodec`, `StDeclarationParser`, `IecObjectTypeNames` |
| **`Versio.Plc.Codesys`** | **One vendor, pure.** Object-type GUIDs, native XML shape, the import planner, the ScriptEngine protocol, CODESYS's own words for its tree | `CodesysObjectType`, `ProfileResolver` |
| **`Versio.Plc.Codesys.Infrastructure`** | **One vendor, driven.** Finding the install, starting `CODESYS.exe`, unpacking the embedded python | `LocalCodesysRunner`, `CodesysPack` |
| **`Versio.Plc.TiaPortal`** | **One vendor, pure.** Directory payload, import planner, SCL header, native-XML scrubber | `TiaPortalProjectFormat`, `TiaPortalImportPlanner` |
| **`Versio.Plc.TiaPortal.Infrastructure`** | **One vendor, driven.** Finding Portal, starting the net48 host | `LocalTiaPortalRunner`, `TiaPortalPack` |
| **`Versio.Plc.TiaPortal.Host`** | **One vendor, net48.** Loads Openness from the install; Siemens DLLs are never shipped | `HostApp` |
| everything else | **Vendor-neutral.** git, diff, sync, locking, watching, logging, bindings | `SyncOrchestrator`, `GitCliClient` |

**Those are projects, not folders, and that is the whole point.** Until 2026-08-17 the vendor
lived in the namespace `Versio.Core.Plc.Codesys` *inside* `Versio.Core`, so the rule below was
checkable only by a reflection test reading namespace strings — and such a test cannot see a
static call, a string literal, or a type hiding inside a generic until somebody teaches it to,
which took three separate goes. `Versio.Core` now has **no project reference** to either pack, so
a leak is a compiler error.

Structured Text is a *standard*, not a CODESYS feature — putting it under `Iec` means a
second vendor inherits the codec, the declaration parser and the syntax highlighting rather
than reimplementing them.

**Adding a vendor** means a new project implementing `IPlcRunner`, `IPlcProjectFormat` and
`IPlcToolchain`, one `ProjectReference` from `Versio.App`, and one `Register()` call in
`App.xaml.cs` beside the existing `CodesysPack.Register()`. Nothing in `Sync/`, `Git/`,
`Locking/`, `Watching/`, `Projects/` or `Diff/` may **name** a vendor — **if it does, vendor
knowledge has leaked past the seam, and the leak is the bug.** The second vendor proved the
seam itself had to grow: a project that is a directory needs `PayloadPathFor` and a tree
copy, and textual files that are not `.st` need a highlighter mapping. Those landed as
defaults and extra extensions, with no brand in the orchestrator. See
[`docs/vendors/tia/DRIVING.md`](docs/vendors/tia/DRIVING.md).

Three things enforce that, and all three had to be built because the rule was stated from the
start and was not true:

- **The project graph.** `Versio.Core` cannot see a vendor pack, so the compiler refuses.
- **`VendorSeamTests` / `VendorSeamInfrastructureTests`**, which read assembly metadata —
  signatures, generic arguments, string constants and the literals compiled into method bodies.
  The first version read signatures only and was green while three vendor names sat in neutral
  namespaces as strings.
- **`SecondVendorTests`**, which stands up a second vendor and drives it, because
  `PlcFormats.Register` had **zero callers** until 2026-08-16 — the mechanism the whole
  architecture rests on had never once run.

### The one architectural rule

**`Versio.Core` never touches the outside world.** No `System.IO`, no `Process`, no `DateTimeOffset.Now`, no UI framework. Everything external sits behind an interface in `Abstractions/`, and `Versio.Infrastructure` is the only project that implements them.

That is not tidiness for its own sake — it is what makes the risky parts (the lock model, the loop defences, the state machine) testable without CODESYS, without git, without sleeping, and without a UI. It is also why the unit suite is roughly 1:1 with source by line count and still runs in under a second.

---

## Building and testing

**Requirements**

| | |
|---|---|
| .NET SDK | 9.0.317 (pinned in `global.json`) |
| git | 2.x with `git-lfs`, **on `PATH`** — Versio resolves `git` from `PATH` and drives it for
  everything, so a machine without it starts and says so rather than working. Nothing is
  vendored: that is what makes Git Credential Manager, SSH agents, 2FA and corporate proxies
  behave exactly as they do in your own terminal. |
| CODESYS | 3.5 SP22 — **`Versio.Plc.Codesys.Tests` only**; the other four suites need none |

```bash
dotnet build
```

```bash
dotnet test tests/Versio.Core.Tests
```

> If `dotnet build` reports **"No .NET SDKs were found"** while an SDK is plainly installed,
> the `dotnet` on `PATH` is the machine-wide host and the SDK is a **per-user** install. They
> are separate directories and the host does not look in the other one. Use
> `%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe`, or set `DOTNET_ROOT` to it — the same install
> layout that makes a framework-dependent `Versio.exe` fail to start, noted again below.

**Nine suites, and they need different things:**

| Suite | Tests | Needs | Runs in |
|---|---|---|---|
| `Versio.Core.Tests` | 1,392 | nothing — pure, no IO, no clock, no UI | ~0.3 s |
| `Versio.Integration.Tests` | 147 | a real `git.exe` and the real filesystem | ~10 s |
| `Versio.Arch.Tests` | 14 | nothing — it reads assembly metadata, IL, and the project files | instant |
| `Versio.App.Tests` | 547 | Windows (WPF). Builds the real dialogs against the real stylesheet on an STA thread; **no window is ever shown** | ~50 s |
| `Versio.Plc.Codesys.Pure.Tests` | 260 | nothing — the vendor's planner, codecs and protocol, pure | under a second |
| `Versio.Plc.Codesys.Parity.Tests` | 92 | `python` on PATH, no CODESYS — **skips cleanly without python, so check the count** | ~15 s |
| `Versio.Plc.TiaPortal.Pure.Tests` | 15 | nothing — format, planner, protocol, scrubber | instant |
| `Versio.Plc.TiaPortal.Tests` | 4 | **a real TIA Portal**; skips cleanly without Openness membership | seconds when skipped |
| `Versio.Plc.Codesys.Tests` | 54 | **a real CODESYS**, and **skips cleanly** without one | **~30 min** |

The first six are the loop to work in. The last is the gate before a commit — and it is a
separate project precisely so that running the loop cannot cost you half an hour by accident.
Of the 386 tests that used to sit behind that half-hour wall, **334 needed no toolchain at
all**; they surface in seconds now.

```bash
dotnet test
```

### Running the CODESYS pipeline by hand

`tools/` holds the harnesses used to validate everything before the C# runner existed. They remain the fastest way to poke at CODESYS directly.

```bash
pwsh tools/codesys/Test-RoundTrip.ps1 -Project fixtures/projects/RefrigeratorControl.project -Profile "CODESYS V3.5 SP22 Patch 2" -MutateFirst
```

`-MutateFirst` edits an exported `.st` before importing it. **Use it.** Without a deliberate change, a round-trip comparison can pass while the import silently did nothing — a green result that cannot fail is worse than a red one.

**The verdict is per layer, because the layers have different fidelity rules.** Textual `.st` and export-only `.yaml` must be byte-identical and are fatal if they are not; native `.xml` is *expected* to differ, because `import_native` legitimately advances counters, reorders unordered collections and re-mints internal GUIDs. A single byte verdict over both printed `Lossless: False` on a perfectly healthy project, which is worse than useless — see [docs/vendors/codesys/DRIVING.md](docs/vendors/codesys/DRIVING.md) §6.6.

```
  textual (.st)        LOSSLESS - byte-identical
  export-only (.yaml)  LOSSLESS - byte-identical
  native (.xml)        5 file(s) differ - EXPECTED, not verified here
  structure            intact - nothing added or removed
  probe                SURVIVED - the harness is not vacuous

  VERDICT: PASS
```

Native fidelity is *semantic*, and that check lives once, in C#: `dotnet test tests\Versio.Core.Tests --filter RealRoundTripEquivalence`. Refresh its committed inputs from a real run with `-UpdateEquivalenceFixtures`.

> These harnesses start CODESYS directly and **do not take the machine-wide lock** the app uses, so running one while Versio is open on the same repository can put two headless CODESYS instances on one profile. Close the app first.

> The profile string cannot be guessed from the install folder: `CODESYS 3.5.22.20` on disk is profile `CODESYS V3.5 SP22 Patch 2`. Read it from `…\CODESYS\Profiles\*.profile.xml`, or let `CodesysLocator` find it.

### Running the TIA Portal pipeline by hand

Same layered-verdict rule. Default project is the committed fixture. Close Versio **and**
TIA Portal first. Openness membership and the host AllowList are required; see
[`docs/vendors/tia/DRIVING.md`](docs/vendors/tia/DRIVING.md).

```
pwsh tools/tia/Test-RoundTrip.ps1 -MutateFirst
```

That writes the committed `CleaningStation_V20_V21` tree. For a disposable copy, pass
`-Project` at a tree under `.versio/local/tia/` — never the OneDrive original.

---

## Dependencies

Deliberately few. Every one is a thing that can break a build or a publish.

| Package | Where | Why |
|---|---|---|
| xunit, xunit.runner.visualstudio | tests | test framework |
| Microsoft.NET.Test.Sdk | tests | test host |
| Xunit.SkippableFact | tests | integration tests skip cleanly without CODESYS |

**`Versio.Core` and `Versio.Infrastructure` have no third-party dependencies at all.**

**One tool is a prerequisite rather than a package: `git-lfs`.** The `.project` fixtures and
the TIA Portal `System/*.plf` payload are stored in LFS, so a clone on a machine without it
gets 130-byte pointer files instead. Nothing fails at build time — the CODESYS suite fails
later, complaining that a project it was handed is not a project; the TIA gate **skips**
when `TestPaths.TiaPortalProject()` sees a pointer.

Two absences are decisions, not omissions:

- **No LibGit2Sharp.** Git is driven through `git.exe` and porcelain v2. This removes the native `git2-*.dll` payload that breaks `PublishSingleFile`, and it means Git Credential Manager, SSH agents, 2FA and corporate proxies behave exactly as they do in the engineer's own terminal.
- **No Python runtime.** CODESYS ships its own IronPython 2.7; the scripts are embedded resources extracted to a content-hashed temp directory at run time, so they can never go stale against the app version.

---

## Things worth knowing before touching the code

Each of these cost real debugging time and is now pinned by a test.

| | |
|---|---|
| **UTF-8 without BOM, everywhere** | IronPython 2.7's `json.load` fails on a BOM with *"No JSON object could be decoded"*. PowerShell 5.1's `-Encoding utf8` writes one. |
| **`StandardOutputEncoding` must be set** | .NET otherwise decodes a subprocess using the console codepage and mangles `Kühlschrank Regelung.st`. |
| **`git status` needs `-z`** | Without it git quotes and escapes paths containing spaces or non-ASCII — both routine in CODESYS object names. |
| **`git status` writes to `.git/index`** | It rewrites the stat cache. Anything that refreshes *because* the index changed will refresh forever. `StatusRefreshPolicy` ignores metadata events for a second after each refresh; working-tree events are never suppressed. |
| **A directory swap invalidates the watcher watching it** | The export stages into `<mirror>.versio_staging` and swaps, which kills the `FileSystemWatcher` handle on the old directory — *Access is denied*, events lost. The rescan that follows is correct and reports **every file in the tree** as changed, because they are new files. Each one used to cost a `git status` and a full-tree hash before the direction lock refused it: quadratic, in the one place a project is large. `BeginOwnWrite` is the window that makes our own writes free; a genuine overflow still costs one job per file. |
| **Windows raises a `.git` directory event for every write inside it** | Including the lock files git creates around each invocation. Treating a bare `.git` event as a real change made lock-file churn alone wake the watcher — caught by the integration suite, not by reading the code. Nothing is lost by dropping it: a write to `.git/index` also raises an event naming `.git/index`. |
| **Not every CODESYS object name is a name** | An external-file reference is identified as `{GUID}\|C:\Users\<user>\...\file.txt`, and percent-encoding that produced a 120-character filename carrying a username and a directory layout into a shared repository. A path-like or over-long name is shortened to its last segment plus a six-character digest (`license.txt~45a921`); the manifest keeps the real name. Reversibility is not lost, because `unsafe_file_stem` was never called — imports resolve objects through the project tree, not by parsing filenames. |
| **The IDE rewrites its lock marker every few seconds** | And not in place: it writes `<project>.~u~` — trailing tilde — and renames it over `.~u`, so a rule covering `.~u` alone left an unignored file appearing and vanishing in the working tree. Every rewrite also moves the *folder's* timestamp. Measured over 20 idle seconds: 7 events for `.~u~`, 4 for `.~u`, 4 for the folder. Left alone this was a `git status` every 2.5 s for as long as an engineer had the project open. |
| **CODESYS leaves `.opt` files beside a project** | An export or an open writes `<name>-AllUsers.opt` and `<name>-<user>-<machine>.opt`, and they **persist** — the second puts a username and machine name into git by its filename alone. Adoption ignores them. A clean open/close leaves no `.~u`; only a crash does. |
| **A project open in the IDE is refused, and `<project>.~u` says so** | CODESYS writes that file beside the project while it holds it — user, machine, pid, timestamp — and refuses any second instance with *"currently in use by 'x' on 'y'"*. Both export and import fail cleanly and change nothing, but `CODESYS.exe` still **exits 0**, so success must be judged from the script's own `rc` and `done` events. Read the `.~u` first and skip the 17–31 s launch. |
| **CODESYS argument quoting** | `--profile="A B C"`, never `"--profile=A B C"`. `ProcessStartInfo.ArgumentList` produces the latter, and CODESYS exits 1 in under a second with no output. |
| **`--noUI` requires `--profile`** | It is a hard precondition, not a preference. |
| **`Mutex` has thread affinity** | It cannot be acquired and released across an `await`. A named `Semaphore` avoids that but loses abandonment recovery, which crash recovery depends on. |
| **Import runs in two phases** | Creating objects then immediately calling `import_native` makes CODESYS enumerate a dictionary it is still mutating — and it **swallows the exception**, so the import silently does nothing and the run looks clean. |
| **A manifest is read by a different language than wrote it** | `_manifest.json` is written by IronPython 2.7 inside CODESYS and read by System.Text.Json. `warnings` was written as records and declared as strings, and the whole-document `Deserialize` threw — so **one field's type disagreeing discarded all 56 objects** and the panel drew a flat list of paths with nothing said anywhere. `Parse` now reads field by field and element by element, and names what it could not read. The fixture has no warnings, which is why `"warnings": []` passed every test for the life of the property. |
| **The export tree holds three kinds of file** | The project's objects; Versio's own bookkeeping (`_manifest.json`, `_coverage.md`); and **export-only** files that are real project state written for review with no trustworthy import path (`*.libraries.yaml`). The importer walks the tree and imports everything it finds, so anything in the second or third group must be declared in `versio_types.py` — `VERSIO_FILES` or `EXPORT_ONLY_SUFFIXES` — or CODESYS is handed markdown or YAML as native XML. It fails with a warning rather than loudly, which is worse. |
| **`libman.get_libraries()` is not the library API** | It returns *names* — `"#Standard"` — with no version, company or parameters, and looks like a success. `libman.references` carries the real thing, and must be **iterated**: `references[i]` indexes by id, so `references[0]` raises on any project whose first reference has a non-zero id. Reading zero references out of a manager holding 21 is the symptom. |
| **A library reference's `id` is re-minted on every open** | Measured across two runs of an untouched project: 68 differing lines, **all of them ids**, nothing else moved. Same class as the `__VisualizationStyle` GUID below. What a reference *resolves* to is machine state too — `Standard, *` becomes `3.5.22.0` only because that version is installed here — so the export records what the project **asks for**, never what it got. |
| **Not every CODESYS GUID is stable** | The project-level `__VisualizationStyle` is re-minted on **every open**. Recording it in the manifest made every export produce a diff. Exported objects' GUIDs are stable and are identity; do not assume the same of an object you only observed once. |
| **Never rewrite GUIDs in an export** | Only `Timestamp` is scrubbed. Rewriting identity GUIDs breaks visualisations. Masking them for a *comparison* is fine; writing masked ones to disk is not. |
| **`git apply --cached` reads the index, not the working tree** | It is the *index* that a patch's context is checked against, so a file saved between a diff being read and a hunk being staged does not invalidate the hunk — what gets staged is still what was on screen. The staleness that does matter is the index moving, and that is refused. Never pass `--unidiff-zero`: it disables the check that makes both true. |
| **WPF `TreeView` does not virtualize by default** | Measured at 5,002 nodes: **94.7 ms virtualized vs 10,635 ms at the defaults**. Needs `IsVirtualizing`, `VirtualizationMode=Recycling` and a `VirtualizingStackPanel` items panel. |
| **An `.ico` is eight drawings, not one** | Windows does not downscale gracefully. The mark is redrawn per size, with a simplified variant below 48px — the 1-unit plate stagger is half a pixel at 32px, too small to see and large enough to smear every edge. Run `tools/New-VersioIcon.ps1 -PreviewDirectory …` and **look at the 16px output** before believing a change. |
| **A framework-dependent exe needs `DOTNET_ROOT`** | If the SDK is installed per-user rather than machine-wide, `Versio.exe` cannot find the runtime and shows a "must install .NET" dialog. Self-contained publish removes this entirely — which is why §13.2 specifies it. |

---

## Prior art

[greenforge-labs/codescribe](https://github.com/greenforge-labs/codescribe) (Apache-2.0) solved a version of this problem first and its source was read in full while planning. Versio takes its object-type GUID table and its `Timestamp`-only scrubbing rule directly, with attribution, and improves on it in three places: interfaces and several other types are exported, `import_native` replaces in place rather than deleting and recreating, and object creation is driven by parsing the declaration rather than requiring a native seed file.

## Licence

Not yet chosen. The codescribe-derived portions are Apache-2.0 and require attribution in any distribution.
