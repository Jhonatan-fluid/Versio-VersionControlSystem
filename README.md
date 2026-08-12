# Versio

**Git-native version control for PLC projects.**

A PLC project file is a binary compound document. Git can store it, but it cannot diff it, merge it, or tell you what changed — so version control degrades to "upload a zip with a date in the name". Versio keeps a **textual mirror** of the project inside the same repository, kept in sync automatically, so git becomes genuinely useful for PLC work: line diffs, blame, history, review.

**CODESYS 3.5 is implemented today.** Rockwell Studio 5000, Siemens TIA Portal and Beckhoff TwinCAT are the intended next vendors, and the code is organised so adding one is additive — a rule enforced by a test that reads assembly metadata, not by a paragraph here.

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
| Import native XML **in place**, GUID preserved | ✅ no delete-and-recreate |
| Create objects that do not exist yet | ✅ POU / FB / FUNCTION / INTERFACE / DUT / GVL |
| Drive CODESYS headlessly from C# | ✅ `LocalCodesysRunner` |
| Git status / branches / stage / commit / diff / push | ✅ `GitCliClient` over real `git.exe` |
| Direction lock, hash gate, coalescer, commit gate | ✅ pure logic, heavily tested |
| Open-project detection, behind the vendor seam | ✅ `IPlcProjectFormat` answers where the marker is and what it says; a stale one from a crashed IDE is ignored, one from another machine is believed |
| **Export while the project is open in the CODESYS IDE** | ✅ exports a copy — the vendor lock is a sidecar on the path. Proven against a real CODESYS holding the original |
| **Import while it is open** — waits and names who has it | ✅ it cannot be made to work: the IDE would revert it on the next save. Neither direction marks the binding failed, and the breaker is never fed |
| Retry automatically when the IDE closes | ⛔ the lock file vanishing is a watchable event; today the next save retries |
| Sync orchestrator — locks, loop defences, commit gate | ✅ 24 scenario tests |
| Change detection — watcher + safety poll + overflow rescan | ✅ `FileChangeWatcher`, driving the sync engine |
| Staleness stated, not implied — "2 projects not yet exported" | ✅ answered by the commit gate, so the two cannot disagree |
| **The project view follows the repository by itself** | ✅ 18 policy tests + 9 against a real filesystem; rebuilt only when something it shows changed |
| Design system tokens, light and dark | ✅ `src/Versio.App/Theme/` |
| Logging — ring buffer, correlation ids, heartbeat, rolling file | ✅ 20 tests; "Open logs folder" selects today's file |
| **The export narrates itself** — directory swap with both file counts, every skip and its reason | ✅ asserted against the log a real export produces, across two runs |
| Shell — repo/branch dropdowns, rail, changed files, diff, collapsible log | ✅ opens a real repo, shows real git state |
| Diff view — gutter, line numbers, syntax highlighting for 11 languages | ✅ ST, C#, Python, XML, JSON, YAML, Markdown, PowerShell, JS/TS, SQL |
| **New files diff as entirely added**, not as "no diff available" | ✅ `--no-index` against `/dev/null`, all lines green |
| **Show full file** — every line, changes still highlighted | ✅ large `-U`, so it stays a diff rather than becoming a file viewer |
| Diff view survives a 2.5 MB / 33,644-line visualisation export | ✅ rows built only when realised; parse off the UI thread |
| **App-wide progress bar** — one signal for git, export and import alike | ✅ counted `BusyTracker`, indeterminate, with a caption |
| CODESYS runs with no visible window | ✅ `CREATE_NO_WINDOW` **and** `SW_HIDE` — different subsystems |
| Repository validation — walks up to `.git`, refuses non-repos with a reason | ✅ handles subdirectories, bare repos and linked worktrees |
| PLC state — toolchain, lock, staleness and heartbeat, folded into the project view | ✅ facts, not controls: the export, import and tracking all run on their own |
| **Vendor seam** — `IPlcRunner`/`PlcJob`/`PlcResult`, enforced from assembly metadata | ✅ 22 architecture tests, proven able to fail |
| Multi-vendor detection — registry, per-binding vendor, mixed repositories | ✅ `PlcFormats`; Rockwell/Siemens/TwinCAT are additive |
| Application icon — 8 sizes, hand-tuned below 48px, embedded in the exe | ✅ 12 tests over the `.ico` itself |
| **Automatic tracking** — every project found, with its `.gitattributes`/LFS rules | ✅ 13 refusal tests + real-repo integration |
| **`.gitignore` written on adoption**, created if the repository has none | ✅ covers `.versio/local/` (locks, backups, scratch copies) and the toolchain's `.opt` files; project-scoped, never repository-wide |
| **Automatic export** when the project changes | ✅ 11 files in 16.7 s on the fixture, lock taken and persisted |
| Direction lock in plain English, shared by the panel and the log | ✅ `SyncNarrative` — every blocking state names its remedy |
| Heartbeat during a 17–31 s export | ✅ elapsed time in the panel, not silence |
| WPF binding-failure tracing — `Versio.exe --trace-bindings <file>` | ✅ the one silent failure class, made loud |
| **Automatic import** when the mirror changes and no lock forbids it | ✅ the project is copied aside first; a failed backup fails the import |
| Change detection over the mirror's real contents | ✅ `ContentHash.OfTree`; a hand-edited `.st` is seen, an untouched tree is not |
| Side-by-side diff and word-level intra-line diff | ⛔ inline only |
| **One project view** — the full CODESYS tree with changes marked, plus every other changed file | ✅ replaced the PLC panel and the changed-files list |
| A project with a clean mirror is not listed | ✅ unless it needs attention — never exported, behind, locked, failed. The empty state says why rather than going blank |
| **Lazy object rendering** — a row is built when something can reach it, not before | ✅ `TreeRow.Children` on first access; the tree survives a refresh without collapsing |
| **Export coverage stated, not assumed** — `_coverage.md` + every skip in the manifest | ✅ a gap is distinguished from a deliberate omission; an old manifest reports "unknown", never "complete" |
| **Library references exported** (`<manager>.libraries.yaml`) | ✅ **the gap is closed.** One file per library manager, beside the object that owns it — 21 references on the fixture's application, 13 on its project. Hand-written YAML on both sides, no third-party dependency. Export-only: change libraries in the IDE |
| Template generation | ⛔ needs a new operation and script — not assembly |
| A second vendor (TwinCAT / Studio 5000 / TIA Portal) | ⛔ the seam is ready and unproven |
| **Git side panel** — staging, commit message, fetch/pull/push | ✅ one operation at a time across every button |
| **The commit gate, enforced** — a stale export is exported and staged first | ✅ push releases the direction locks, commit does not |
| **Stash** — push with a message, apply, pop, drop with a confirmation | ✅ parsed from git's own format; 6 integration tests against real git |
| **Recent commits with click-to-diff** | ✅ the commit's patch opens in the same pane as everything else |
| **Per-hunk stage and unstage** | ✅ the hunk is copied verbatim and applied to the index; the working tree is never touched |
| **Branch creation** — validated against git's own rules as you type | ✅ creates from HEAD and switches; 35 unit tests over the rules, and the dialog says *which* rule was broken |
| **Clone with verification** — URL, Verify, destination, progress | ✅ Clone stays dead until `ls-remote` passes, and editing the URL shuts it again |
| **Discard** — per file, after a confirmation naming it | ✅ refuses an untracked file with an explanation; releases the direction lock when the file was inside a mirror |
| Branch switching refused while a direction lock is held | ⛔ git refuses a dirty-tree checkout on its own; the explanation is missing |
| **Project tree** — CODESYS structure, roll-up badges, search | ✅ 28 unit tests + built from the export this repository commits |
| Config nodes — one tree node per library, layer switcher, context menu | ⛔ not built. The library YAML now exists and parses (`LibraryCatalog`), so a version bump lands under the device that owns it; what is missing is a node per library. Device and IO YAML are still unwritten |
| Packaging | ⛔ not built |

**[docs/STATUS.md](docs/STATUS.md)** has the current state and the route to completion.
**[docs/PHASE0-FINDINGS.md](docs/PHASE0-FINDINGS.md)** records the measurements and the known limitations — including one real CODESYS mutation (`Core2` in task configuration) that keeps task config **diff-only** for now.

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
│  │  ├─ Iec/                   IEC 61131-3, shared by every vendor: ST codec, declarations
│  │  ├─ Locking/               lock records, reboot-survival recovery policy
│  │  ├─ Logging/               ring buffer, correlation ids, heartbeat
│  │  ├─ Plc/                   the vendor seam: IPlcRunner, PlcJob, PlcResult, PlcFormats
│  │  │  └─ Codesys/            one vendor: GUID table, XML shape, profiles, wire protocol
│  │  ├─ Projects/              the manifest, the export-coverage report, the library
│  │  │                         catalog, and the CODESYS-shaped project tree
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
│  │  │  └─ Codesys/            process host, install locator, script extractor
│  │  └─ Watching/              the OS-level file and repository watchers
│  │
│  └─ Versio.Scripts.Codesys/   IronPython 2.7, run inside CODESYS. Embedded into
│                               Versio.Infrastructure as resources. The pure modules
│                               (text, types, yaml, libraries) import no scriptengine
│                               and run on CPython too.
├─ tests/
│  ├─ Versio.Core.Tests/        799 unit tests — pure, no IO, ~290 ms
│  ├─ Versio.App.Tests/         24 tests that build the real dialogs against the real
│  │                            stylesheet on an STA thread — no window is ever shown
│  └─ Versio.Integration.Tests/ 101 tests against real CODESYS and real git, ~7 min
├─ tools/                       PowerShell harnesses used during Phase 0, plus
│                               New-VersioIcon.ps1 which generates the app icon
├─ assets/                      versio.svg (reference drawing) and versio.ico
│                               (the build input — generated, never hand-edited)
├─ fixtures/projects/           the CODESYS project the suite runs against
└─ docs/
   ├─ PLAN.md                   the full architecture plan
   ├─ STATUS.md                 where the project is and what remains
   └─ PHASE0-FINDINGS.md        measured results and known limitations
```

`Versio.App` holds the WPF shell: `Theme/tokens/` for the design tokens, `Theme/controls/` for
control styles, `ViewModels/`, `Views/`, and `Spikes/` for the tree performance harness
(`Versio.exe --spike --measure`).

### Vendor boundaries

Versio started CODESYS-only and will grow to other PLC toolchains, so the code is split
three ways and the namespaces say which is which:

| Namespace | Scope | Example |
|---|---|---|
| `Versio.Core.Plc` | **Neutral contracts** every vendor implements | `IPlcRunner`, `PlcJob`, `PlcResult`, `PlcFormats` |
| `Versio.Core.Plc.Codesys` | **One vendor.** Object-type GUIDs, native XML shape, `CODESYS.exe` invocation, the ScriptEngine protocol | `CodesysObjectType`, `ProfileResolver` |
| `Versio.Core.Iec` | **The IEC 61131-3 standard**, shared by every vendor | `StTextCodec`, `StDeclarationParser` |
| everything else | **Vendor-neutral.** git, diff, sync, locking, watching, logging, bindings | `SyncOrchestrator`, `GitCliClient` |

Structured Text is a *standard*, not a CODESYS feature — putting it under `Iec` means a
second vendor inherits the codec, the declaration parser and the syntax highlighting rather
than reimplementing them.

**Adding a vendor** means implementing `IPlcRunner`, `IPlcProjectFormat` and `IPlcToolchain`,
and registering the format with `PlcFormats`. Nothing in `Sync/`, `Git/`, `Locking/`,
`Watching/`, `Projects/` or `Diff/` may change — **if it does, vendor knowledge has leaked past
the seam, and the leak is the bug.**

That rule is now enforced by `VendorSeamTests`, which reads assembly metadata and fails the
build on a leak. It had to be: the rule was stated from the start and was not true — the sync
orchestrator was built directly on CODESYS types until 2026-08-12.

### The one architectural rule

**`Versio.Core` never touches the outside world.** No `System.IO`, no `Process`, no `DateTimeOffset.Now`, no UI framework. Everything external sits behind an interface in `Abstractions/`, and `Versio.Infrastructure` is the only project that implements them.

That is not tidiness for its own sake — it is what makes the risky parts (the lock model, the loop defences, the state machine) testable without CODESYS, without git, without sleeping, and without a UI. It is also why the test suite is roughly 1:1 with source by line count and still runs in a tenth of a second.

---

## Building and testing

**Requirements**

| | |
|---|---|
| .NET SDK | 9.0.317 (pinned in `global.json`) |
| git | 2.x with `git-lfs` |
| CODESYS | 3.5 SP22 — **integration tests only**, unit tests need none |

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

The unit suite is pure and runs anywhere. The integration suite drives a real CODESYS installation and a real `git.exe`, and **skips cleanly** when either is absent:

```bash
dotnet test
```

### Running the CODESYS pipeline by hand

`tools/` holds the harnesses used to validate everything before the C# runner existed. They remain the fastest way to poke at CODESYS directly.

```bash
pwsh tools/Test-RoundTrip.ps1 -Project fixtures/projects/RefrigeratorControl.project -Profile "CODESYS V3.5 SP22 Patch 2" -MutateFirst
```

`-MutateFirst` edits an exported `.st` before importing it. **Use it.** Without a deliberate change, a round-trip comparison can pass while the import silently did nothing — a green result that cannot fail is worse than a red one.

> The profile string cannot be guessed from the install folder: `CODESYS 3.5.22.20` on disk is profile `CODESYS V3.5 SP22 Patch 2`. Read it from `…\CODESYS\Profiles\*.profile.xml`, or let `CodesysLocator` find it.

---

## Dependencies

Deliberately few. Every one is a thing that can break a build or a publish.

| Package | Where | Why |
|---|---|---|
| xunit, xunit.runner.visualstudio | tests | test framework |
| Microsoft.NET.Test.Sdk | tests | test host |
| Xunit.SkippableFact | tests | integration tests skip cleanly without CODESYS |

**`Versio.Core` and `Versio.Infrastructure` have no third-party dependencies at all.**

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
| **Windows raises a `.git` directory event for every write inside it** | Including the lock files git creates around each invocation. Treating a bare `.git` event as a real change made lock-file churn alone wake the watcher — caught by the integration suite, not by reading the code. Nothing is lost by dropping it: a write to `.git/index` also raises an event naming `.git/index`. |
| **CODESYS leaves `.opt` files beside a project** | An export or an open writes `<name>-AllUsers.opt` and `<name>-<user>-<machine>.opt`, and they **persist** — the second puts a username and machine name into git by its filename alone. Adoption ignores them. A clean open/close leaves no `.~u`; only a crash does. |
| **A project open in the IDE is refused, and `<project>.~u` says so** | CODESYS writes that file beside the project while it holds it — user, machine, pid, timestamp — and refuses any second instance with *"currently in use by 'x' on 'y'"*. Both export and import fail cleanly and change nothing, but `CODESYS.exe` still **exits 0**, so success must be judged from the script's own `rc` and `done` events. Read the `.~u` first and skip the 17–31 s launch. |
| **CODESYS argument quoting** | `--profile="A B C"`, never `"--profile=A B C"`. `ProcessStartInfo.ArgumentList` produces the latter, and CODESYS exits 1 in under a second with no output. |
| **`--noUI` requires `--profile`** | It is a hard precondition, not a preference. |
| **`Mutex` has thread affinity** | It cannot be acquired and released across an `await`. A named `Semaphore` avoids that but loses abandonment recovery, which crash recovery depends on. |
| **Import runs in two phases** | Creating objects then immediately calling `import_native` makes CODESYS enumerate a dictionary it is still mutating — and it **swallows the exception**, so the import silently does nothing and the run looks clean. |
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
