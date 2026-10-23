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
| `Versio.Plc.TiaPortal` | **One vendor, pure.** `.apXX` conventions, directory payload, import planner, SCL header, native-XML scrubber |
| `Versio.Plc.TiaPortal.Infrastructure` | **One vendor, driven.** Portal locator, net48 host spawn, Openness session |
| `Versio.Plc.TiaPortal.Host` | **One vendor, net48 exe.** Loads Openness from the install; never shipped as Siemens DLLs |
| `Versio.Plc.TwinCat` | **One vendor, pure.** `.tsproj` conventions, directory payload, import planner, ST header |
| `Versio.Plc.TwinCat.Infrastructure` | **One vendor, driven.** TcXaeShell locator, net9 host spawn, Automation Interface session |
| `Versio.Plc.TwinCat.Host` | **One vendor, net9 x64 exe.** COM into `TcXaeShell.DTE.17.0`; never shipped as Beckhoff DLLs |
| `Versio.Web` + `web/` | **The read-only web viewer** (`docs/web/PLAN.md`). A composition root like `Versio.App`, but references only the *pure* packs and names vendors only in `WebHost.RegisterFormats`; the React page draws no code. Arch tests enforce both |
| everything else | Vendor-neutral: git, diff, sync, locking, watching, logging, bindings |

Adding a vendor means a new project implementing `IPlcRunner`, `IPlcProjectFormat` and
`IPlcToolchain`, one `ProjectReference` from `Versio.App`, and one `Register()` call in
`App.xaml.cs` beside `CodesysPack.Register()` —
**nothing in `Sync/`, `Git/`, `Locking/`, `Watching/`, `Projects/` or `Diff/` may learn a
vendor's name. If it does, vendor knowledge has leaked past the seam, and the leak is the
bug.** A directory project (TIA Portal, TwinCAT) widens `IPlcProjectFormat.PayloadPathFor`
and `IFileSystem.CopyTreeAsync`; that is the seam growing, not a brand leaking.

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
| `docs/STATUS.md` | **Where the project is, and what is next.** The only live status page. Start here — the next session's first act is *What is next* item 1. |
| `docs/FINDINGS.md` | **Every defect written up with its evidence**, newest first. Search it rather than reading it. |
| `docs/SAFEGUARDS.md` | What Versio refuses, asks about, or waits for. Phases 1–9 built; §5.H deferred on purpose. |
| `docs/vendors/codesys/DRIVING.md` | **One vendor's docs**, beside its two projects. How CODESYS is driven end to end, and the audit of it |
| `docs/vendors/tia/DRIVING.md` | **One vendor's docs**, beside its three projects. How TIA Portal is driven. PLC-software loop is done; §12 is evidence; coverage skips stay out of scope |
| `docs/vendors/twincat/DRIVING.md` | **One vendor's docs**, beside its three projects. How TwinCAT 3 is driven. PLC-software loop is done; I/O is export-only; coverage skips stay out of scope |
| `docs/phase1/` | **The Phase 1 archive** — four completed plans, marked done, kept for reasoning and evidence: `PLAN.md` (the founding architecture), `IMPORT-PLAN.md` (thirteen gaps, the feature checklist, and the measurements — its raw probe logs are in `probes/`), `GIT-PANEL-PLAN.md`, `PHASE0-FINDINGS.md`. Read them for why, never for state |
| `docs/archive/` | **Completed later plans** — Phase 2 queue, UX plan, session handoffs, the long STATUS diary. Record, not state |
| `README.md` | Repository layout, vendor boundaries, the one architectural rule |

**This is a rule because ignoring it has already cost.** An audit was asked for and re-derived
from scratch what [`docs/archive/STATUS-2026-08.md`](docs/archive/STATUS-2026-08.md) already had
under *"Known loose ends, from an audit of the repository"* — including a suite flake whose
three failing test classes were named there by name. An hour went into rediscovering it.
Search [`docs/FINDINGS.md`](docs/FINDINGS.md) and the live [`docs/STATUS.md`](docs/STATUS.md)
before deriving state from the archive.

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
  first, with its evidence. Search that file; do not add an index to `STATUS.md`.
- A structural change gets whatever a future reader needs before editing that file.
- A loose end that is closed gets struck through, not deleted, with what actually happened.

### Run the app

Tests prove things resolve, lay out and are reachable. They do **not** prove the app looks
right or that a dialog opens. Launch it:

```
dotnet build src/Versio.App/Versio.App.csproj
start "" "src/Versio.App/bin/Debug/net9.0-windows/Versio.exe" "C:/GIT/Versio"
```

The shell itself has no interactive desktop, so first read
`%LOCALAPPDATA%\VersioUser\logs\versio-<date>.log` for what happened, and **filter to the launch
window**: stack-trace lines carry no timestamp, so an old exception grepped out of context
reads as a live one. Zero `ERR` lines after startup means the window constructed, the panels
realised and the bindings resolved.

**It can be looked at, with the owner's leave.** Since 2026-09-21 the computer-use tools can
see and drive the window (the installed build is in the Start menu as "Versio"). One look at
the four panels found twelve things no test could — FINDINGS 2026-09-21. Ask before taking the
screen: it is the owner's desktop, and it may have a game or their own work in front. Two
traps: `open_application("Versio")` **starts a second instance** when one is running (which now
hands over and exits, but still), and a second Versio on the same repository hands over to the
first rather than opening — point a dev build at a different repository.

Then ask the owner to eyeball anything visual you could not look at yourself. **Close the app before rebuilding** — a running
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

**And in that script, build the whole new text first, write it to a temp file, then
`os.replace`.** `io.open(path, "w")` **truncates before it validates its own arguments** — a
bad `newline=` value emptied a 1,495-line test file on 2026-08-18 and raised `ValueError`
afterwards, so the traceback said nothing about the file being gone. It came back from git
because it was committed; an uncommitted edit would not have. The same shape as the two rules
above: the damage happens before the thing that reports it.

**The python scripts are EMBEDDED in `Versio.Plc.Codesys.Infrastructure.dll`.** Editing
`src/Versio.Plc.Codesys.Infrastructure/Scripts/*.py` and then running `dotnet test --no-build`
tests the script that was embedded at the last build, not the one on disk. It looks exactly like a change that
did nothing: a Phase 5 mutation check reported the acceptance test still passing with the
feature disabled, which is the most misleading answer a mutation check can give. **Rebuild after
touching python**, always.

**A mutation check that restores from a backup must touch the file afterwards.** MSBuild
decides what to rebuild by timestamp. A backup made with a plain copy is older than the build
of the mutant, so restoring it makes the project look up to date and the **mutant stays in the
binary**. On 2026-10-10 one restored scanner mutation leaked into five later checks and into
the "unmutated" baseline: everything went red and proved nothing. `os.utime(path, None)` after
the restore, or `touch`, and re-run the baseline before believing any of it.

**The suite is the safety net, so keep it honest.** `Versio.App.Tests` shells own watchers,
pollers, the log console and the repository lock — `using var shell = ...`, always, and never
construct one inline as an argument, because then there is nothing to put `using` on. Fifty-six
that did not made the suite fail one run in three on a different test each time; fixing them took
it to about one in fourteen, so **a lone red run is still worth re-running before believing it**.

**The residual flake's root cause was found and fixed on 2026-08-26**: nothing serialised the
shell's status refreshes, so a stale `git status` reading could apply after a fresh one and
the next guard reasoned about a tree that did not exist — `AutoStashFlowTests` failed one run
in three on exactly this, a different test each time, tracking machine load. `RunRefreshAsync`
now funnels every refresh through one gate; the measurement (18 consecutive green class runs,
a fully green 672-test suite run) is in FINDINGS. **A lone red run is still worth re-running
before believing it** — one afternoon's measurement retires a mechanism, not the habit — and
the CODESYS-side one-off (`AdoptAndExportTests`, 2026-08-15, once in four runs) was never part
of this mechanism and remains unexplained.

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
| `Versio.Core.Tests` | 1,831 | under a second | nothing |
| `Versio.Integration.Tests` | 176 | ~10 s | real git |
| `Versio.App.Tests` | 797 | ~3.6 min on 2026-09-22, 8 m 38 s on 2026-10-05, 10 m 46 s on 2026-10-19, 10 m 42 s on 2026-10-22 (idleness not checked); STA, no window shown; it was ~50 s once | nothing |
| `Versio.Arch.Tests` | 36 | instant | nothing — it reads assembly metadata, IL and the `.csproj` files |
| `Versio.Plc.Codesys.Pure.Tests` | 395 | under a second | nothing — the planner, the codecs, the protocol |
| `Versio.Plc.Codesys.Parity.Tests` | 100 | ~15 s | `python` on PATH — **skips cleanly without it, so check the count, not the colour** |
| `Versio.Plc.TiaPortal.Pure.Tests` | 98 | instant | nothing — format, planner, protocol, LAD and FBD parser |
| `Versio.Plc.TiaPortal.Tests` | 5 | seconds when skipped; minutes when Portal runs the import gate | **real TIA Portal** — skips cleanly without Openness membership or LFS content. Import Fact is green |
| `Versio.Plc.TwinCat.Pure.Tests` | 23 | instant | nothing — format, planner, protocol |
| `Versio.Plc.TwinCat.Tests` | 6 | seconds when skipped; ~5 min when XAE runs the import gate | **real TwinCAT XAE** — skips cleanly without `TcXaeShell.DTE.17.0` or the fixture. Import Fact is green |
| `Versio.Web.Tests` | 359 | a few seconds | nothing — GitHub is faked; one test starts the built server as its own process |
| `web/` (vitest) | 127 | ~20 s | the pinned Node in `web/.node` (`dotnet build src/Versio.Web -p:BuildWeb=true` fetches it), then `npm test` in `web/` |
| `Versio.Plc.Codesys.Tests` | 55 | **~30 min**; 41 m 14 s, 55/55, on 2026-10-11 | **real CODESYS** — every test launches it, which is the whole cost |

The TIA gate fixture is `fixtures/projects/CleaningStation_V20_V21/` (Git LFS). Never the
OneDrive original. `docs/vendors/tia/DRIVING.md` is this vendor's driving notes. The TwinCAT
gate fixture is `fixtures/projects/TC3_PlcSample_reStrText/` (no LFS). Work only on a **copy**.
`docs/vendors/twincat/DRIVING.md` is that vendor's driving notes.

**The loop to work in is everything except the last one:**

```
C:\Users\elli_\AppData\Local\Microsoft\dotnet\dotnet.exe test tests\Versio.Core.Tests
C:\Users\elli_\AppData\Local\Microsoft\dotnet\dotnet.exe test tests\Versio.Integration.Tests
C:\Users\elli_\AppData\Local\Microsoft\dotnet\dotnet.exe test tests\Versio.Arch.Tests
C:\Users\elli_\AppData\Local\Microsoft\dotnet\dotnet.exe test tests\Versio.Plc.Codesys.Pure.Tests
C:\Users\elli_\AppData\Local\Microsoft\dotnet\dotnet.exe test tests\Versio.Plc.Codesys.Parity.Tests
C:\Users\elli_\AppData\Local\Microsoft\dotnet\dotnet.exe test tests\Versio.Plc.TiaPortal.Pure.Tests
C:\Users\elli_\AppData\Local\Microsoft\dotnet\dotnet.exe test tests\Versio.Plc.TwinCat.Pure.Tests
C:\Users\elli_\AppData\Local\Microsoft\dotnet\dotnet.exe test tests\Versio.Web.Tests
```

**The web viewer's page** is not in that loop because it needs Node: build it with
`dotnet build src/Versio.Web -p:BuildWeb=true` (fetches the Node pinned in `web/node.props`,
refuses a hash mismatch, runs `npm ci` and `vite build`), and test it with `npm test` in `web/`
using that Node (`web/.node/node-v<version>-win-x64` on PATH). Release builds always build it.
To look at it against the fixtures without GitHub or an App, see `tests/Versio.Web.Tests/FixturePreview.cs`.

About thirty seconds together, plus about three and a half minutes for `Versio.App.Tests` when
the UI is in play (measured 2026-09-22 on `main` and on `UiLook` alike; it was fifty seconds
once, and nobody noticed it grow).
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
