# Versio

**Git-native version control for CODESYS projects.**

A CODESYS `.project` file is a binary compound document. Git can store it, but it cannot diff it, merge it, or tell you what changed — so version control degrades to "upload a zip with a date in the name". Versio keeps a **textual mirror** of the project inside the same repository, kept in sync automatically, so git becomes genuinely useful for PLC work: line diffs, blame, history, review.

> **Status: pre-alpha, and the round trip works.** A project can be tracked from the panel, exported to its reviewable mirror, edited as text, and imported back into the `.project` — driven by the real sync engine and proven end to end against a real project with real CODESYS. See [What works today](#what-works-today).

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

Export is automatic and deterministic. Import applies edited text back into the project. A per-project **direction lock** makes the round trip safe: once one side is authoritative, the other is refused until the work is pushed or discarded.

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
| Sync orchestrator — locks, loop defences, commit gate | ✅ 24 scenario tests |
| Change detection — watcher + safety poll + overflow rescan | ✅ `FileChangeWatcher`, wired to the sync panel |
| **"2 projects not yet exported"** — staleness stated, not implied | ✅ watcher-driven, answered by the commit gate |
| **Changed-files panel follows the repository by itself** | ✅ 18 policy tests + 9 against a real filesystem |
| Design system tokens, light and dark | ✅ `src/Versio.App/Theme/` |
| Logging — ring buffer, correlation ids, heartbeat, rolling file | ✅ 20 tests; "Open logs folder" selects today's file |
| Shell — repo/branch dropdowns, rail, changed files, diff, collapsible log | ✅ opens a real repo, shows real git state |
| Diff view — gutter, line numbers, syntax highlighting for 11 languages | ✅ ST, C#, Python, XML, JSON, YAML, Markdown, PowerShell, JS/TS, SQL |
| **New files diff as entirely added**, not as "no diff available" | ✅ `--no-index` against `/dev/null`, all lines green |
| **Show full file** — every line, changes still highlighted | ✅ large `-U`, so it stays a diff rather than becoming a file viewer |
| Diff view survives a 2.5 MB / 33,644-line visualisation export | ✅ rows built only when realised; parse off the UI thread |
| **App-wide progress bar** — one signal for git, export and import alike | ✅ counted `BusyTracker`, indeterminate, with a caption |
| CODESYS runs with no visible window | ✅ `CREATE_NO_WINDOW` **and** `SW_HIDE` — different subsystems |
| Repository validation — walks up to `.git`, refuses non-repos with a reason | ✅ handles subdirectories, bare repos and linked worktrees |
| PLC sync panel — toolchain probe, project discovery, binding state | ✅ real scan, no sample data |
| Vendor seam — `IPlcToolchain`, `Iec` vs `Plc.Codesys` vs neutral | ✅ 11 tests |
| Application icon — 8 sizes, hand-tuned below 48px, embedded in the exe | ✅ 12 tests over the `.ico` itself |
| **Adopt a project from the UI** — guards, `.gitattributes`/LFS rules, binding | ✅ 13 refusal tests + real-repo integration |
| **Manual export from the UI**, driving `SyncOrchestrator` | ✅ 11 files in 16.7 s on the fixture, lock taken and persisted |
| Direction lock in plain English, shared by the panel and the log | ✅ `SyncNarrative` — every blocking state names its remedy |
| Heartbeat during a 17–31 s export | ✅ elapsed time in the panel, not silence |
| WPF binding-failure tracing — `Versio.exe --trace-bindings <file>` | ✅ the one silent failure class, made loud |
| **Manual import from the UI** — propose, review the file list, confirm | ✅ unconfirmed pass proven to leave the `.project` byte-identical |
| Change detection over the mirror's real contents | ✅ `ContentHash.OfTree`; a hand-edited `.st` is seen, an untouched tree is not |
| Side-by-side diff and word-level intra-line diff | ⛔ inline only |
| A diff preview *inside* the import confirmation | ⛔ shows which files change, not what changes in them |
| Template generation | ⛔ needs a new operation and script — not assembly |
| **Git side panel** — staging, commit message, fetch/pull/push | ✅ one operation at a time across every button |
| **The commit gate, enforced** — a stale export is exported and staged first | ✅ push releases the direction locks, commit does not |
| Stash, per-hunk staging, recent-commits list | ⛔ not built |
| Project tree, packaging | ⛔ not built |

**[docs/STATUS.md](docs/STATUS.md)** has the current state and the route to completion.
**[docs/PHASE0-FINDINGS.md](docs/PHASE0-FINDINGS.md)** records the measurements and the known limitations — including one real CODESYS mutation (`Core2` in task configuration) that keeps task config **diff-only** for now.

---

## Repository layout

```
Versio.sln
├─ src/
│  ├─ Versio.Core/              Domain + orchestration. Pure.
│  │  ├─ Abstractions/          IClock, ICodesysRunner, IGitClient, IFileSystem…
│  │  ├─ Bindings/              project ↔ export-directory relationship, orphan scanning
│  │  ├─ Codesys/               job contracts, JSON-lines protocol, profile resolution
│  │  ├─ Equivalence/           semantic comparison of native XML
│  │  ├─ Git/                   porcelain v2 parser, progress parser, models
│  │  ├─ Locking/               lock records, reboot-survival recovery policy
│  │  ├─ Model/                 CODESYS object types (GUID table)
│  │  ├─ Serialization/         ST codec, XML scrubber, path mapper, declaration parser
│  │  ├─ Sync/                  state machine, hash gate, suppressor, breaker, commit gate
│  │  └─ Watching/              debounce, job coalescing, and the rules deciding
│  │                            when the shell re-reads git
│  │
│  ├─ Versio.Infrastructure/    The only project that touches the outside world.
│  │  ├─ Codesys/               process host, install locator, script extractor
│  │  ├─ Git/                   git.exe client
│  │  └─ Platform/              clock, machine identity, process probe
│  │
│  └─ Versio.Scripts/           IronPython 2.7, run inside CODESYS. Embedded into
│                               Versio.Infrastructure as resources.
├─ tests/
│  ├─ Versio.Core.Tests/        603 unit tests — pure, no IO, ~290 ms
│  └─ Versio.Integration.Tests/  59 tests against real CODESYS and real git, ~110 s
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
| `Versio.Core.Plc.Codesys` | **One vendor.** Object-type GUIDs, native XML shape, `CODESYS.exe` invocation, the ScriptEngine protocol | `CodesysObjectType`, `ProfileResolver` |
| `Versio.Core.Iec` | **The IEC 61131-3 standard**, shared by every vendor | `StTextCodec`, `StDeclarationParser` |
| everything else | **Vendor-neutral.** git, diff, sync, locking, watching, logging, bindings | `SyncOrchestrator`, `GitCliClient` |

Structured Text is a *standard*, not a CODESYS feature — putting it under `Iec` means a
second vendor inherits the codec, the declaration parser and the syntax highlighting rather
than reimplementing them.

**Adding a vendor** means implementing `IPlcToolchain` and `IPlcProjectFormat` in
`Versio.Core/Plc/`, plus a runner in `Versio.Infrastructure`. Nothing in `Sync/`, `Git/`,
`Locking/`, `Watching/` or `Diff/` should need to change — **if it does, vendor knowledge
has leaked past the seam, and the leak is the bug.**

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
| **CODESYS argument quoting** | `--profile="A B C"`, never `"--profile=A B C"`. `ProcessStartInfo.ArgumentList` produces the latter, and CODESYS exits 1 in under a second with no output. |
| **`--noUI` requires `--profile`** | It is a hard precondition, not a preference. |
| **`Mutex` has thread affinity** | It cannot be acquired and released across an `await`. A named `Semaphore` avoids that but loses abandonment recovery, which crash recovery depends on. |
| **Import runs in two phases** | Creating objects then immediately calling `import_native` makes CODESYS enumerate a dictionary it is still mutating — and it **swallows the exception**, so the import silently does nothing and the run looks clean. |
| **Never rewrite GUIDs in an export** | Only `Timestamp` is scrubbed. Rewriting identity GUIDs breaks visualisations. Masking them for a *comparison* is fine; writing masked ones to disk is not. |
| **WPF `TreeView` does not virtualize by default** | Measured at 5,002 nodes: **94.7 ms virtualized vs 10,635 ms at the defaults**. Needs `IsVirtualizing`, `VirtualizationMode=Recycling` and a `VirtualizingStackPanel` items panel. |
| **An `.ico` is eight drawings, not one** | Windows does not downscale gracefully. The mark is redrawn per size, with a simplified variant below 48px — the 1-unit plate stagger is half a pixel at 32px, too small to see and large enough to smear every edge. Run `tools/New-VersioIcon.ps1 -PreviewDirectory …` and **look at the 16px output** before believing a change. |
| **A framework-dependent exe needs `DOTNET_ROOT`** | If the SDK is installed per-user rather than machine-wide, `Versio.exe` cannot find the runtime and shows a "must install .NET" dialog. Self-contained publish removes this entirely — which is why §13.2 specifies it. |

---

## Prior art

[greenforge-labs/codescribe](https://github.com/greenforge-labs/codescribe) (Apache-2.0) solved a version of this problem first and its source was read in full while planning. Versio takes its object-type GUID table and its `Timestamp`-only scrubbing rule directly, with attribution, and improves on it in three places: interfaces and several other types are exported, `import_native` replaces in place rather than deleting and recreating, and object creation is driven by parsing the declaration rather than requiring a native seed file.

## Licence

Not yet chosen. The codescribe-derived portions are Apache-2.0 and require attribution in any distribution.
