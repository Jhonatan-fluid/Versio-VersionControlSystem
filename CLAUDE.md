# Working on Versio

Versio versions **PLC projects** in git: a vendor's binary project file is exported to a
reviewable text mirror, edits to the mirror flow back, and a direction lock decides which side
is authoritative. Losing somebody's work is the failure mode that matters, which is why so much
here is about refusing to act rather than acting.

**It is multi-vendor by design. CODESYS is the first toolchain, not the only one** — and that
distinction is the project's central architectural rule rather than an aspiration:

| Project | Scope |
|---|---|
| `Versio.Core` (`Plc/`) | Neutral contracts every vendor implements — `IPlcRunner`, `PlcJob`, `PlcResult`, `PlcFormats` |
| `Versio.Core` (`Iec/`) | The IEC 61131-3 **standard**, shared by every vendor — ST codec, declaration parser, the standard's object-type names |
| `Versio.Plc.Codesys` | **One vendor, pure.** Object-type GUIDs, native XML shape, import planner, ScriptEngine protocol, CODESYS's own tree vocabulary |
| `Versio.Plc.Codesys.Infrastructure` | **One vendor, driven.** `CODESYS.exe`, the install locator, the embedded python |
| everything else | Vendor-neutral: git, diff, sync, locking, watching, logging, bindings |

Structured Text is a standard, not a CODESYS feature. Adding a vendor means a new project
implementing `IPlcRunner`, `IPlcProjectFormat` and `IPlcToolchain`, one `ProjectReference` from
`Versio.App`, and one `Register()` call in `App.xaml.cs` beside `CodesysPack.Register()` —
**nothing in `Sync/`, `Git/`, `Locking/`, `Watching/`, `Projects/` or `Diff/` may change. If it
does, vendor knowledge has leaked past the seam, and the leak is the bug.**

**`Versio.Core` has no project reference to a vendor pack**, so that is a compiler error rather
than an opinion. `VendorSeamTests` and `VendorSeamInfrastructureTests` read assembly metadata on
top — signatures, generic arguments, string constants, and the literals compiled into method
bodies. Every part of it had to be built, because the rule was stated from the start and was not
true: the sync orchestrator was built on CODESYS types until 2026-08-12; three vendor names were
still sitting in neutral namespaces as strings until 2026-08-16; and `PlcFormats.Register` had
**zero callers** until the same day.

**The python lives in `Versio.Plc.Codesys.Infrastructure/Scripts/`** and is embedded from there.
It used to be a loose `src/Versio.Scripts.Codesys/` folder that no project owned, which is
precisely why its rebuild trap existed.

So: never write CODESYS knowledge outside the `Versio.Plc.Codesys*` projects, and never describe
Versio as a CODESYS tool. See README's *Vendor boundaries*.

---

## Read these before starting. Not after.

`docs/` is a worked engineering log, not marketing. It is where the reasoning lives, and most
questions about "why is this like this" are already answered there.

| File | What it holds |
|---|---|
| `docs/PHASE2.md` | **The forward queue** — what is left, in order, and the decisions already taken with their why. Start here for "what next" |
| `docs/STATUS.md` | Where the project is, and **Known loose ends** — an audit of the repository |
| `docs/FINDINGS.md` | **Every defect written up with its evidence**, newest first. 110 of them. Search it rather than reading it; `STATUS.md` carries the index |
| `docs/SAFEGUARDS.md` | What Versio refuses, asks about, or waits for |
| `docs/vendors/codesys/DRIVING.md` | **One vendor's docs**, beside its two projects. How CODESYS is driven end to end, and the audit of it |
| `docs/phase1/` | **The Phase 1 archive** — four completed plans, marked done, kept for reasoning and evidence: `PLAN.md` (the founding architecture), `IMPORT-PLAN.md` (thirteen gaps, the feature checklist, and the measurements — its raw probe logs are in `probes/`), `GIT-PANEL-PLAN.md`, `PHASE0-FINDINGS.md`. Read them for why, never for state |
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
- A defect worth understanding gets a **Finding** in `docs/FINDINGS.md`, at the TOP — newest
  first — with its evidence, and a line in `STATUS.md`'s index.
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

**The python scripts are EMBEDDED in `Versio.Plc.Codesys.Infrastructure.dll`.** Editing
`src/Versio.Plc.Codesys.Infrastructure/Scripts/*.py` and then running `dotnet test --no-build`
tests the script that was embedded at the last build, not the one on disk. It looks exactly like a change that
did nothing: a Phase 5 mutation check reported the acceptance test still passing with the
feature disabled, which is the most misleading answer a mutation check can give. **Rebuild after
touching python**, always.

**The suite is the safety net, so keep it honest.** `Versio.App.Tests` shells own watchers,
pollers, the log console and the repository lock — `using var shell = ...`, always, and never
construct one inline as an argument, because then there is nothing to put `using` on. Fifty-six
that did not made the suite fail one run in three on a different test each time; fixing them took
it to about one in fourteen, so **a lone red run is still worth re-running before believing it**.

**It still flakes, and what is left of it looks like real git rather than leaked objects.** The
last three failures observed were all in tests that drive git subprocesses against a temp
repository, and all of them dispose correctly. **A fourth, on 2026-08-15, was not**: it was
`AdoptAndExportTests.A_task_priority_edited_in_the_mirror_…`, a CODESYS test, once in four full
runs, passing alone immediately afterwards and in the next full run. So the remaining flake is
not only the git-subprocess class.

**Never run the integration suite with `-v q`.** That failure arrived with no message, because
quiet verbosity prints the FAIL line and swallows the assertion — so the one run that had
something to say could not say it, and it cost a full 13-minute re-run to get back to. Use
plain `--nologo`; the extra output is worth it on a suite this slow to reproduce. If you measure the rate, **do it on an idle
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

| Suite | Count | Time | Needs |
|---|---|---|---|
| `Versio.Core.Tests` | 1,346 | under a second | nothing |
| `Versio.Integration.Tests` | 143 | ~10 s | real git |
| `Versio.Arch.Tests` | 14 | instant | nothing — it reads assembly metadata, IL and the `.csproj` files |
| `Versio.App.Tests` | 509 | ~50 s, STA, no window shown | nothing |
| `Versio.Plc.Codesys.Pure.Tests` | 254 | under a second | nothing — the planner, the codecs, the protocol |
| `Versio.Plc.Codesys.Parity.Tests` | 82 | ~15 s | `python` on PATH — **skips cleanly without it, so check the count, not the colour** |
| `Versio.Plc.Codesys.Tests` | 53 | **~30 min** | **real CODESYS** — every test launches it, which is the whole cost |

**The loop to work in is everything except the last one:**

```
C:\Users\elli_\AppData\Local\Microsoft\dotnet\dotnet.exe test tests\Versio.Core.Tests
C:\Users\elli_\AppData\Local\Microsoft\dotnet\dotnet.exe test tests\Versio.Integration.Tests
C:\Users\elli_\AppData\Local\Microsoft\dotnet\dotnet.exe test tests\Versio.Arch.Tests
C:\Users\elli_\AppData\Local\Microsoft\dotnet\dotnet.exe test tests\Versio.Plc.Codesys.Pure.Tests
C:\Users\elli_\AppData\Local\Microsoft\dotnet\dotnet.exe test tests\Versio.Plc.Codesys.Parity.Tests
```

About thirty seconds together, plus fifty for `Versio.App.Tests` when the UI is in play.
`Versio.Plc.Codesys.Tests` is the gate before a commit — and any change to the python touches
the parity project's subject, so run it before believing a script edit.

**That split used to be a `--filter "Toolchain!=Codesys"` expression** over one integration
project, policed by a `FastLaneTests` whose whole job was checking the tag had been applied in
both directions. It is a project boundary now, so the filter, the tag and the test guarding them
are all gone: you cannot accidentally run half an hour of CODESYS, and you cannot accidentally
hide coverage behind a flag nobody runs. The cost is real and worth restating — a CODESYS cold
start is 17–31 s, every launch pays it in full, an import test needs three to five of them, and
they cannot run in parallel.

A red `Versio.Plc.Codesys.Tests` run is worth re-running a project at a time before reading it
as a regression — it drives real CODESYS with real timeouts.
