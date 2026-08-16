# Working on Versio

Versio versions **PLC projects** in git: a vendor's binary project file is exported to a
reviewable text mirror, edits to the mirror flow back, and a direction lock decides which side
is authoritative. Losing somebody's work is the failure mode that matters, which is why so much
here is about refusing to act rather than acting.

**It is multi-vendor by design. CODESYS is the first toolchain, not the only one** — and that
distinction is the project's central architectural rule rather than an aspiration:

| Namespace | Scope |
|---|---|
| `Versio.Core.Plc` | Neutral contracts every vendor implements — `IPlcRunner`, `PlcJob`, `PlcResult`, `PlcFormats` |
| `Versio.Core.Plc.Codesys` | **One vendor.** Object-type GUIDs, native XML shape, `CODESYS.exe`, the ScriptEngine protocol |
| `Versio.Core.Iec` | The IEC 61131-3 **standard**, shared by every vendor — ST codec, declaration parser |
| everything else | Vendor-neutral: git, diff, sync, locking, watching, logging, bindings |

Structured Text is a standard, not a CODESYS feature. Adding a vendor means implementing
`IPlcRunner`, `IPlcProjectFormat` and `IPlcToolchain` and registering with `PlcFormats` —
**nothing in `Sync/`, `Git/`, `Locking/`, `Watching/`, `Projects/` or `Diff/` may change. If it
does, vendor knowledge has leaked past the seam, and the leak is the bug.** `VendorSeamTests`
reads assembly metadata and fails the build on one. It had to be written: the rule was stated
from the start and was not true — the sync orchestrator was built directly on CODESYS types
until 2026-08-12.

So: never write CODESYS knowledge outside `*.Plc.Codesys`, and never describe Versio as a
CODESYS tool. See README's *Vendor boundaries*.

---

## Read these before starting. Not after.

`docs/` is a worked engineering log, not marketing. It is where the reasoning lives, and most
questions about "why is this like this" are already answered there.

| File | What it holds |
|---|---|
| `docs/STATUS.md` | Where the project is, **Known loose ends** (an audit of the repository), and **Findings** — every defect written up with its evidence |
| `docs/PLAN.md` | The full architecture plan |
| `docs/SAFEGUARDS.md` | What Versio refuses, asks about, or waits for |
| `docs/CODESYS.md` | How CODESYS is driven end to end, and the audit of it |
| `docs/CODESYS-IMPORT-PLAN.md` | **The active plan.** Eleven numbered import gaps, what each measurement closed, and the order to build the rest in — which is deliberately not 1, 2, 3 |
| `docs/GIT-PANEL-PLAN.md` | The git panel's functionality and UX |
| `docs/PHASE0-FINDINGS.md` | What Phase 0 measured. **A record, not a live limitation list** — read its supersession notes |
| `README.md` | Repository layout, vendor boundaries, the one architectural rule |

**This is a rule because ignoring it has already cost.** An audit was asked for and re-derived
from scratch what `STATUS.md` already had under *"Known loose ends, from an audit of the
repository"* — including a suite flake whose three failing test classes were named there by
name. An hour went into rediscovering it.

---

## The rules

### Comment the reasoning, not the code

This codebase's comments explain **why**, and they are worth more than the code they sit
above — a paragraph on why a gate sits where it does, what broke before it was added, what a
future reader would otherwise try. Match that. A comment restating what the line does is noise;
a comment naming the defect that line prevents is the point.

When you move code, **the reasoning moves with it.** After any extraction, check phrase by
phrase that no argument was dropped — line-level diffing will not show it, because rewrapping
hides the loss.

### Anything that runs must say so in the log

Every operation the app performs writes to the log: what it is doing, against what, and how it
ended. `VersioLogger` with a `LogCategory`, correlation ids through `BeginOperation`, and a
heartbeat for anything long enough that silence would look like a hang.

**A silent failure is a bug even when the code is correct.** The rolling file sink was written,
reviewed, tested — and never instantiated, so nothing reached disk while the log panel made it
look as though logging worked.

Quiet when idle, loud when working. Runs of unchanged polls collapse into one periodic line; a
refused, coalesced or throttled sync says nothing, because nothing happened.

### Read the .md files before starting any work

See the table above.

### Double-pass everything you write

**After you finish, go back over what you changed as if somebody else wrote it.** Not a re-run
of the tests — a second read. This session's second passes each found something the first missed:

- a defensive null-check silently dropped by a refactor that was supposed to change nothing,
- two paragraphs of reasoning lost in a method extraction,
- three moved event handlers that **no test touched at all** — disabling all three left the
  whole suite green,
- a test that would have gone on passing while quietly checking one thing fewer.

Green tests are not the double pass. Ask instead: what did I change that nothing is watching?
Then break it deliberately and confirm something fails.

**Moved a subscription? Delete it and run the suite.** Three extractions in one session each
replaced a direct call with an event and a subscription, and deleting that subscription left the
whole suite green all three times — at 467, 475 and 477 tests. A pure move is the most likely
kind of change to lose its coverage silently, because nothing about it looks like new behaviour.
If nothing goes red, the move is not finished. `CollaboratorSeamTests` exists for this and takes
one row per collaborator.

### Commit in coherent chunks

One commit per idea, each building and passing on its own. Not one giant commit, and not a
commit per file. The message says **why**, what it cost, and what was rejected — the git log
here is part of the documentation.

If a refactor turns up an unrelated defect, fix it in its own commit.

### Update the .md files when you finish

Any change that alters what the docs claim updates the docs in the same session.

- Test counts in `README.md` and `docs/STATUS.md` go stale silently — re-derive, never trust.
- A defect worth understanding gets a **Finding** in `STATUS.md`, with its evidence.
- A structural change gets whatever a future reader needs before editing that file.
- A loose end that is closed gets struck through, not deleted, with what actually happened.

### Run the app

Tests prove things resolve, lay out and are reachable. They do **not** prove the app looks
right or that a dialog opens. Launch it:

```
dotnet build src/Versio.App/Versio.App.csproj
start "" "src/Versio.App/bin/Debug/net9.0-windows/Versio.exe" "C:/GIT/Versio"
```

It cannot be screenshotted from here — no interactive desktop — so read
`%LOCALAPPDATA%\Versio\logs\versio-<date>.log` for what happened, and **filter to the launch
window**: stack-trace lines carry no timestamp, so an old exception grepped out of context
reads as a live one. Zero `ERR` lines after startup means the window constructed, the panels
realised and the bindings resolved.

Then ask the owner to eyeball anything visual. **Close the app before rebuilding** — a running
`Versio.exe` locks its own binaries, and a running test host locks the test DLLs.

---

## Practical notes that cost time to learn

**Build with the user-local SDK.** `dotnet` on PATH is runtime-only:

```
C:\Users\elli_\AppData\Local\Microsoft\dotnet\dotnet.exe
```

**Never round-trip source through PowerShell.** `Get-Content` / `Set-Content` corrupts non-ASCII
and adds a BOM; this repository is full of em-dashes and box-drawing characters. Use Python with
explicit `encoding='utf-8', newline='\n'`, or the editing tools.

**Never edit source through a shell heredoc.** Python-in-bash eats backslashes and backticks
silently. Write the script to a file and run it.

**The suite is the safety net, so keep it honest.** `Versio.App.Tests` shells own watchers,
pollers, the log console and the repository lock — `using var shell = ...`, always, and never
construct one inline as an argument, because then there is nothing to put `using` on. Fifty-six
that did not made the suite fail one run in three on a different test each time; fixing them took
it to about one in fourteen, so **a lone red run is still worth re-running before believing it**.

**It still flakes, and what is left of it looks like real git rather than leaked objects.** The
last three failures observed were all in tests that drive git subprocesses against a temp
repository, and all of them dispose correctly. If you measure the rate, **do it on an idle
machine and leave it alone while it runs** — a measurement taken while a second `dotnet test` is
running measures that instead.

**`Versio.Core` never touches the outside world** — no `System.IO`, no `Process`, no
`DateTimeOffset.Now`, no UI. That is what makes the lock model and the state machine testable
without CODESYS, git, or sleeping. `VendorSeamTests` fails the build on a leak.

**Windows coalesces file-change notifications per directory.** Any test asserting on a per-file
event is assuming something Windows does not guarantee.

---

## Build and test

```
C:\Users\elli_\AppData\Local\Microsoft\dotnet\dotnet.exe build Versio.sln
C:\Users\elli_\AppData\Local\Microsoft\dotnet\dotnet.exe test Versio.sln
```

`TreatWarningsAsErrors` and `EnforceCodeStyleInBuild` are on: the build is warning-free and
stays that way.

| Suite | Count | Time |
|---|---|---|
| `Versio.Core.Tests` | 1,451 | under a second |
| `Versio.App.Tests` | 480 | ~50 s, STA, no window shown |
| `Versio.Integration.Tests` | 205 | ~10 min, real CODESYS and real git |

A red integration run is worth re-running a project at a time before reading it as a
regression — it drives real CODESYS with real timeouts.
