# Versio

**Git-native version control for CODESYS projects.**

A CODESYS `.project` file is a binary compound document. Git can store it, but it cannot diff it, merge it, or tell you what changed — so version control degrades to "upload a zip with a date in the name". Versio keeps a **textual mirror** of the project inside the same repository, kept in sync automatically, so git becomes genuinely useful for PLC work: line diffs, blame, history, review.

> **Status: pre-alpha, no UI yet.** The CODESYS pipeline and the domain core are built and tested against a real project; the desktop app is not. See [What works today](#what-works-today).

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
| Change detection — watcher + safety poll + overflow rescan | ✅ `FileChangeWatcher` |
| Design system tokens, light and dark | ✅ `src/Versio.App/Theme/` |
| **Desktop UI** — shell, panels, diff view | ⛔ not built |

Known limitations are recorded honestly in [docs/PHASE0-FINDINGS.md](docs/PHASE0-FINDINGS.md) — including one real CODESYS mutation (`Core2` in task configuration) that keeps task config **diff-only** for now.

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
│  │  └─ Watching/              debounce and job coalescing
│  │
│  ├─ Versio.Infrastructure/    The only project that touches the outside world.
│  │  ├─ Codesys/               process host, install locator, script extractor
│  │  ├─ Git/                   git.exe client
│  │  └─ Platform/              clock, machine identity, process probe
│  │
│  └─ Versio.Scripts/           IronPython 2.7, run inside CODESYS. Embedded into
│                               Versio.Infrastructure as resources.
├─ tests/
│  ├─ Versio.Core.Tests/        352 unit tests — pure, no IO, ~100 ms
│  └─ Versio.Integration.Tests/  21 tests against real CODESYS and real git, ~23 s
├─ tools/                       PowerShell harnesses used during Phase 0
├─ fixtures/projects/           the CODESYS project the suite runs against
└─ docs/
   ├─ PLAN.md                   the full architecture plan
   └─ PHASE0-FINDINGS.md        measured results and known limitations
```

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
| **CODESYS argument quoting** | `--profile="A B C"`, never `"--profile=A B C"`. `ProcessStartInfo.ArgumentList` produces the latter, and CODESYS exits 1 in under a second with no output. |
| **`--noUI` requires `--profile`** | It is a hard precondition, not a preference. |
| **`Mutex` has thread affinity** | It cannot be acquired and released across an `await`. A named `Semaphore` avoids that but loses abandonment recovery, which crash recovery depends on. |
| **Import runs in two phases** | Creating objects then immediately calling `import_native` makes CODESYS enumerate a dictionary it is still mutating — and it **swallows the exception**, so the import silently does nothing and the run looks clean. |
| **Never rewrite GUIDs in an export** | Only `Timestamp` is scrubbed. Rewriting identity GUIDs breaks visualisations. Masking them for a *comparison* is fine; writing masked ones to disk is not. |
| **WPF `TreeView` does not virtualize by default** | Measured at 5,002 nodes: **94.7 ms virtualized vs 10,635 ms at the defaults**. Needs `IsVirtualizing`, `VirtualizationMode=Recycling` and a `VirtualizingStackPanel` items panel. |
| **A framework-dependent exe needs `DOTNET_ROOT`** | If the SDK is installed per-user rather than machine-wide, `Versio.exe` cannot find the runtime and shows a "must install .NET" dialog. Self-contained publish removes this entirely — which is why §13.2 specifies it. |

---

## Prior art

[greenforge-labs/codescribe](https://github.com/greenforge-labs/codescribe) (Apache-2.0) solved a version of this problem first and its source was read in full while planning. Versio takes its object-type GUID table and its `Timestamp`-only scrubbing rule directly, with attribution, and improves on it in three places: interfaces and several other types are exported, `import_native` replaces in place rather than deleting and recreating, and object creation is driven by parsing the declaration rather than requiring a native seed file.

## Licence

Not yet chosen. The codescribe-derived portions are Apache-2.0 and require attribution in any distribution.
