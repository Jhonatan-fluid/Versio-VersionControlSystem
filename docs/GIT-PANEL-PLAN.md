# Git panel — functionality and UX plan

**Date:** 2026-08-13 · **Scope:** `ShellViewModel` git region, `MainWindow.xaml` `GitPanel`,
`IGitClient`/`GitCliClient` where the panel needs something git can already do but the
interface does not expose, plus one new dialog.

---

## Where this stands

**Eleven of twelve phases are done**, on branch `GitPanel`, plus an audit pass over all of them.
Each has a commit and an entry in the **phase log** at the end of this document saying what
landed, what was decided, and what contact with the code changed.

| | Phase | Landed |
|---|---|---|
| ✅ | **GP-0** — git presence probe, commit-identity preflight | `0a183d0` |
| ✅ | **GP-A** — notifications | `2ce279a` |
| ✅ | **GP-B** — remote state machine, publish, detached HEAD | `f71a60b` |
| ✅ | **GP-C** — credential detection, sign in, forget credential | `938f67e` |
| ✅ | **GP-D** — background remote polling | `437e0d1` |
| ✅ | **GP-E** — per-file staging, two scrollable panes | `a7cc553` |
| ✅ | *Audit* — dead API removed, fixtures consolidated, a flake fixed | `485cddb` |
| ✅ | **GP-F** — one commit button, three behaviours, gating | `ac954b4` |
| ✅ | **GP-G** — conflicts UI, diverged exit (§6, §9b) | `81865b7` |
| ✅ | **GP-H** — branch identity, default branch, delete dialog (§7) | `15e24d3` |
| ✅ | **GP-J** — PLC-aware naming, summary and draft (§9c, and §10.1 with it) | `1a01953` |
| ✅ | **GP-I** — merge-request button, five forges (§8) | `1a01953` |
| — | GP-K / GP-L — previews and polish (§9d, rest of §10) | next |

**GP-J was taken before GP-I**, out of the order this table used to imply. The table was a
sequence, not a ranking, and §9c is the item this document itself calls *"the thing no other git
client can do"* and *"highest value of anything here"* — while §8 opens a pre-filled browser page
and carries the most external surface in the plan (five forge templates, undetectable self-hosted
instances, PR-ref probing two of the five forges do not support). GP-J's data dependency was also
already satisfied and sitting in the same process, and it subsumes §10.1, which would otherwise
have been rewritten by it.

**Four things are still owed to a human**, because nothing here can see the screen or reach a
network: the credential-helper window's stacking order, a timed `ls-remote` against a real
remote, the §13 scripts for anything visual, and — new with GP-I — **the merge-request button
against a real GitHub or GitLab account**. That last one cannot be automated here at all: a
reachable remote and a hostname a forge is detectable from are mutually exclusive offline, so the
suite proves the git half against a bare repository and the URL half against a parser, and the
join between them is §13's GP-I step 5.

**How to read the rest.** Sections §1–§13 are the plan, written before the work and left as
written — each now says at the top whether it is built. The **phase log** at the end is the
history: what was actually done, and every decision that turned out differently from the plan.
Where the two disagree, the phase log is right.

---

## What the code actually does today

**Re-verified 2026-08-13 against `a083356` on `Clean-up-and-continue-dev`**, after the
safeguards work landed. Two rows changed and one was deleted outright; the git panel itself is
substantially untouched, so most of this plan stands as written.

| Claim | Where | Reality |
|---|---|---|
| The panel reports what happened | `ShellViewModel.cs:1690-1760` | **Still true.** `Pull`/`Push`/`Fetch` set `StatusSummary` and *then* call `RunRefreshAsync`, which overwrites it with `"No changes"` / `"N changed"` (`:2790`). A successful push still produces no visible confirmation at all. |
| Failures are reported | same | **Still true.** Only the ones that `return` early survive, into a caption-sized status-bar line in secondary grey. |
| Upstream state is known | `PorcelainV2Parser.cs:113` | **Still true.** `Branch.Upstream` is parsed and the shell throws it away; `AheadBehind` (`:2782`) renders `""` for *both* "in sync" and "no upstream". |
| The branch entry says "no upstream" | `ShellViewModel.cs:88` | **Still true.** `BranchEntry.Tracking` is computed and bound by nothing. |
| Behind-ness is visible | `ShellViewModel.cs:2782` | **Still true.** `↓2` in a caption-sized mono glyph, indistinguishable at a glance from `↑2`, which needs no action. |
| Staging is whole-list only | `ShellViewModel.cs:1530` | **Still true.** `StageAsync` already takes `IReadOnlyList<ChangeRow>`; only the selection is missing. |
| Rows survive a refresh | `ShellViewModel.cs:2440` | **Still true.** `UpdateStagingLists` still `Clear()`s and rebuilds both lists on every refresh. §4's prerequisite stands unchanged. |
| Conflicts are handled | `Sync/ConflictProbe.cs`, `SyncNarrative.cs:79` | **Changed.** The *engine* now handles them: `IConflictProbe` and `SyncVerdict.BlockedByConflict` refuse both sync directions mid-merge, and `TreeState()` (`:2282`) feeds conflicted paths to the guard. **The git panel still shows nothing** — no banner, no section, no gate on commit. §6 is now purely the UI half. |
| The branch list covers the repository | `GitCliClient.cs:91` | **Still true.** `for-each-ref refs/heads` — local branches only. A remote-only branch is invisible. |
| There is a notion of a main branch | `Binding.cs:44` | **Still true.** `RepoConfig` gained nothing; no default-branch field anywhere. |
| Versio knows a change is a POU | `ProjectManifest.cs` | **Still true.** The manifest maps every exported path to an object name and type; the panel renders raw paths. §9c unchanged. |
| ~~Pull is unguarded~~ | `ShellViewModel.cs:1677-1682` | **Fixed, and this row is retired.** `fce3f90` wired `GuardIntent.Pull` once the auto-stash existed to back it, with a comment saying exactly that. See §9a. |

### What landed that this plan can now use

| | |
|---|---|
| `IGitClient.RemoteUrlAsync(remote, ct)` | `GitCliClient.cs:229`. **§8 no longer needs to add it** — the forge work reads the remote through this. |
| `IGitClient.GitDirectoryAsync(ct)` | Resolves `.git` correctly for linked worktrees and submodules. |
| `Guarding/AutoStash.cs`, `AutoStashNarrative.cs` | A real auto-stash with an identity and a sidecar. **§7c's dirty-tree refusal can now offer to stash instead of refusing** — see §7c. |
| `Sync/ConflictProbe.cs` + `SyncVerdict.BlockedByConflict` | The engine half of §6, already built and narrated. Bind to it; do not build a second detection path. |
| `Core/Settings/AppSettings.cs` + a JSON store | A real settings home. Machine-level preferences have somewhere to live; per-repository ones still belong in `RepoConfig`. |
| `SAFEGUARDS` phases 1–6 and 8 ✅ | Including **phase 6: the git-write suppression window and the conflict gate**. This clears GP-G's blocker entirely — see pre-flight. |

### Two facts that shape everything below

- **`StatusSummary` is doing two jobs.** It holds *state* ("3 changed") and *events*
  ("Pushed.") on one property with one lifetime. That is the root cause of the clobbering in
  row 1 — fixing it by re-ordering `await`s would leave the same trap set for the next caller.
  §1 separates them instead.
- **`Ready`** (`ShellViewModel.cs:1506`) is `_git is not null && !Busy.IsBusy` — one shared
  flag, so the moment any operation starts every git button goes dead together, including a
  17–31 s CODESYS export. That is correct and is not touched. Every gating rule in §5 is
  **additional** to it, never a replacement.

### One new structural concern

`ShellViewModel.cs` is now **3,202 lines**, up from 2,304 when this plan was first written. It
holds the repository, the branches, the diff pane, the staging lists, the guard, the auto-stash,
the history hook and every git command.

**This plan adds a tracking state machine, a poller, a credential state, per-row selection, a
branch-deletion flow and a forge button to that same file.** Left alone it lands somewhere near
4,500 lines, and the class stops being reviewable.

Recommendation, to be taken **before GP-E, not after**: extract the git-panel surface into a
`GitPanelViewModel` owned by the shell — the staging lists, the commit message, the remote
commands, `TrackingState`, the poller and the credential banner. The shell keeps repository
identity, the guard and the panel switching. It is a move, not a rewrite: the commands already
funnel through `Ready` and `RaiseGitCommandStates`, which is the seam.

Not a phase of its own — it is the first commit of GP-E, and everything after it lands in a file
that has room. **It is also the riskiest single commit in this plan**, for a reason specific to
WPF, and it is specified in full at §4a.

---

## How this work is done

Standing rules for every phase below, so each section can describe *what* it builds without
restating *how*. A phase is not done until all six hold.

### 1 · Where code goes

The split this repository already enforces, and the reason it is worth keeping:

| Layer | Holds | Test cost |
|---|---|---|
| `Versio.Core` | Every decision: state machines, policies, parsing, classification. **No IO, no clock of its own, no WPF.** | ~0.3 ms each, 919 tests in 286 ms |
| `Versio.Infrastructure` | Everything that touches the world: `git.exe`, the filesystem, the toolchain | Integration only |
| `Versio.App` | View models, windows, styles. As thin as the decision above it allows | STA harness, ~3 s |

**The rule that makes it pay:** anything with a rule in it belongs in Core, even when it feels
like UI. `NotificationLog` (§1), `RemotePollPolicy` (§3), `TrackingState` (§2), the credential
failure classifier (§12c) and the forge URL builder (§8) are all Core types with App wrappers,
not App types. The wrapper holds a `DispatcherTimer` or an `ObservableCollection`; the thing
worth testing holds none.

New files land where their siblings already are — `Core/Notifications/`, `Core/Git/`,
`App/ViewModels/`, `App/Views/`. No new top-level folder without a reason stated in the commit.

### 2 · Comments

House style, followed throughout the existing code and not negotiable in new code:

- `<summary>` says **what it is**, in one sentence.
- `<remarks>` says **why it is shaped this way** — and specifically, *what the obvious
  alternative was and why it lost*. Every non-obvious decision in this plan has one, and each
  reads as arbitrary without it: probe instead of fetch, sticky failure toasts, `-d` before
  `-D`, checkbox separate from list selection, `TrackingState` instead of two booleans.
- A comment that states a rule the code does not enforce is a defect. This codebase has been
  bitten by exactly that — three comments claiming `.versio/local/` "is gitignored" while
  nothing wrote the rule.
- Measurements go in the comment as numbers, not adjectives. "94.7 ms virtualized vs 10,635 ms
  at the defaults", not "much faster".

### 3 · Logging

Every phase logs. The conventions already in the code:

| What | Level | Category |
|---|---|---|
| A button, menu item or shortcut fired | Info | `Ui`, via `logger.Click("Git.Push")` |
| A git command's intent and outcome | Info | `Git` |
| A refusal, a gate, a guard verdict | Info | `Git` / `Sync` — a decision that stopped somebody is the most valuable line in a log |
| Routine detail: probe results, parse counts, timings | Debug | `Git` |
| Anything that failed | Error, with the exception | its own category |

Plus three rules this plan adds to:

- **Correlate.** Multi-step operations open `logger.BeginOperation(...)` so every line from one
  push — gate, export, stage, commit, push, lock release — shares an id. Already done in
  `CommitAsync`; the new multi-step flows (§7c delete, §12c sign-in) need it too.
- **Collapse repeats.** Anything on a timer uses the `QuietRunTracker` pattern rather than
  writing a line per tick. 540 lines an hour of "nothing happened" is how the log panel got
  drowned before.
- **Never log a secret.** `Redact` covers URLs; credential-helper stdin and stdout are not
  logged at all, at any level (§12c).

### 4 · Automated tests

Three layers, each with a job:

| Project | For | Constraint |
|---|---|---|
| `Versio.Core.Tests` | Every rule and state machine | Must stay pure and fast. If a test needs git, it is in the wrong project |
| `Versio.App.Tests` | View models, and **every new window constructed against the real stylesheet** | STA harness. This layer exists because a missing style shipped a crash that a clean build and 883 green tests both missed |
| `Versio.Integration.Tests` | Real `git.exe` against a fixture repo with a bare remote | `SkippableFact` so a machine without CODESYS still runs clean |

**Every new window gets a construction test.** Not optional — `MainWindowTests` documents at
length why: the compiler resolves no `StaticResource`, so a style that does not fit the element
asking for it is invisible until something builds the window.

**Verify the test can fail before believing it.** The README's `-MutateFirst` principle, and the
guard work applied it explicitly — marking `Export` as `MustFinish` failed 10 tests, swapping two
checks failed exactly the one test asserting that order. Each phase below names the mutation
that should break its suite.

### 5 · Verification in the running app

**Automated tests cannot see this panel, and neither can I.** There is no interactive desktop in
my environment, so nothing here can be screenshotted or clicked from my side — **every visual
and interaction change has to be eyeballed by you.** That is not a gap to apologise for, it is a
step in the process, and it works only if I hand over a checklist precise enough to run in two
minutes.

So each phase ships with a **verification script**: an ordered list of actions and the exact
observable result of each, written so that a "no" is unambiguous. Not "check the toasts work" —
*"push with the network off; a red card appears bottom-right, stays until dismissed, and its
text ends with git's own error line."*

The per-phase scripts are collected in **§13**.

Where a state is hard to reach by hand — no upstream, diverged, expired credentials, conflicts —
§13 gives the shell commands to manufacture it against a scratch repository. A feature whose
failure path was never once seen on screen is a feature that was never tested.

### 6 · Documentation

Per §11, in the same commit as the code: XML docs on new public types, the phase marked done in
this file, and whichever of `STATUS.md` / `PLAN.md` / `SAFEGUARDS.md` / `README.md` the phase
touches. A phase that ships code and leaves the docs for later has not shipped.

### Definition of done

A phase is done when **all** of these are true:

1. Code is in the right layer, with `<remarks>` on every new public type saying why.
2. Core tests cover every rule, and the mutation named in the phase breaks them.
3. Any new window has a construction test against the real stylesheet.
4. Logging follows §3 above, and a real run of the feature was read back in the log panel.
5. The §13 verification script has been run **by you** in the real app, and passed.
6. Docs updated in the same commit; `STATUS.md`'s test counts re-derived, not remembered.
7. **The build was run with Versio.exe closed.** A build against a running app fails on the
   assembly copy with `MSB3027` *and reports warnings that are an artefact of that failure* —
   measured on 2026-08-13: 20 phantom warnings while running, 0 with it closed. A build result
   taken while the app is open is not a build result.
8. Any binding moved between view models was checked with `--trace-bindings` against a panel
   that actually showed data — see §4a. Green tests do not cover this.

---

## 1 · Notifications

> **Built** in GP-A. What follows is the design as planned; the phase log at the end records what actually landed and what changed on contact with the code.

### Design

One transient notification surface, owned by the shell, distinct from the status bar.

**Split the two jobs.** `StatusSummary` keeps only durable state — `"3 changed"`,
`"No changes"`, `"No repository open"`. Every *event* — an operation finished, an operation
failed — goes through a new notification center. The clobbering bug then cannot recur,
because the refresh no longer writes to the channel the result is on.

**Lifetimes by severity, not one timer for all:**

| Severity | Lifetime | Rationale |
|---|---|---|
| Success | 4 s, auto-dismiss | Confirmation of something the user just watched happen. It needs to be seen, not read twice. |
| Warning | 8 s, auto-dismiss | "Pulled — nothing to merge", "3 files were already staged". |
| Failure | **sticky until dismissed** | A push that failed is the single most important thing the panel can say. Auto-dismissing it is how somebody goes home believing their work is on the remote. |

**Content.** Headline + optional detail. Failures carry git's own words:
`GitResult.FailureReason` (`GitModels.cs:82`) already picks the last non-empty stderr line,
which is the one that says `rejected — non-fast-forward` or `Authentication failed`. Failure
toasts get a copy-to-clipboard affordance, because that line is what gets pasted into a chat
message asking for help.

**Placement.** Bottom-right of the content column, stacked upward, above the status bar, at
most 3 visible with an "+n more" roll-up. Not centred, not modal — the panel must stay usable
while a failure is on screen.

**De-duplication.** Posting an identical headline while one is live refreshes its timer
instead of stacking. Otherwise a retry loop builds a wall of identical cards.

### Structure

- `src/Versio.Core/Notifications/Notification.cs` — record: `Severity`, `Headline`, `Detail`,
  `PostedUtc`, `ExpiresUtc?`.
- `src/Versio.Core/Notifications/NotificationLog.cs` — pure, clock-driven: `Post(...)`,
  `Prune(now)` returns what expired, `Dismiss(id)`, de-dup rule, cap. Unit-testable in
  `Versio.Core.Tests` with the existing fake clock, no dispatcher, no WPF.
- `src/Versio.App/Mvvm/NotificationCenter.cs` — `ObservableObject` wrapping the log, one
  `DispatcherTimer` at 250 ms calling `Prune`. Thin on purpose; the logic under test is in Core.
- `MainWindow.xaml` — an `ItemsControl` toast host; `Controls.xaml` gains a `Toast` style and
  `Icon.Warning` / `Icon.Error` / `Icon.Close` keys (`Icon.Check` already exists).

### Call sites to convert

`ShellViewModel`: `StageAsync`, `CommitAsync`, `PullAsync`, `PushAsync`, `FetchAsync`,
`ApplyHunkAsync`, `DiscardAsync`, `CreateBranchAsync`, `CloneRepositoryAsync` — every
assignment to `StatusSummary` that describes an event rather than a state.

Also mirror each notification into the existing logger at its matching level, so the log
console and the notification never disagree about what happened.

### Tests

- Core: expiry by severity, de-dup refreshes rather than stacks, cap, `Prune` returns the
  removed set, a failure never expires.
- App: a successful push posts exactly one Success notification **and** `StatusSummary` still
  reads the refreshed state — the regression test for the clobbering bug.

**Mutation check:** give Failure a 4-second expiry; the "a failure never expires" test must fail.

---

## 2 · Remote state: needs pushing, needs pulling, not published

> **Built** in GP-B, including detached HEAD, which was planned for GP-H and moved. See the phase log.

These are three cases of one question — *is this branch in step with the remote?* — and all
three are currently answered by two mono glyphs that look alike. They get one state machine
and one prominent badge.

### Design

`TrackingState`, computed on the shell from `GitStatus.Branch` plus the remote list:

| State | Condition | Badge | What is offered |
|---|---|---|---|
| `NoRepository` | no repo open | — | — |
| `Unborn` | `Branch.IsUnborn` | "No commits yet" | Stage & commit |
| `NoRemote` | no remote configured | "No remote configured" *(neutral)* | *(disabled; tooltip explains)* |
| `NoUpstream` | remote exists, `Upstream is null` | **"Not on the remote yet"** *(amber)* | **Publish branch** |
| `InSync` | ahead 0, behind 0 | "Up to date with `origin/x`" *(quiet)* | — |
| `Ahead` | ahead > 0 | **"n commits to push"** *(accent)* | Push |
| `Behind` | behind > 0 | **"n commits to pull — the remote has changes you do not have"** *(amber)* | Pull |
| `Diverged` | both > 0 | **"Diverged — n to push, n to pull. Pull first."** *(amber)* | Pull |

The badge is a full-width line inside the branch card with an icon, in words, not a glyph
pair. `↓2` is information; *"the remote has changes you do not have"* is the thing the user
needs to act on, and the difference between those two is most of this section.

Amber is `Brush.Status.Modified`, matching the existing convention that behind-ness is the
one that means somebody else moved and you have not caught up (`ShellViewModel.cs:81`).

Ahead/behind counts also go on the button labels — `Pull (2)`, `Push (3)` — so the state is
readable without moving the eye off the control that resolves it.

### Git work required

`git push` on a branch with no upstream fails, so publishing gets its own button rather than a
push that sometimes means something else.

- `IGitClient.RemotesAsync(ct)` → `git remote`, so `NoRemote` and `NoUpstream` are
  distinguishable. Today they are not, and the advice differs completely.
- `IGitClient.PublishBranchAsync(remote, branch, progress, ct)` →
  `git push --set-upstream <remote> <branch> --progress`.
- Remote choice: `origin` when present, else the sole remote, else a small picker. Never
  guessed silently.
- On success, `PublishBranchAsync` releases the direction locks exactly as `PushAsync` does
  (`ShellViewModel.cs:1161`) — it *is* a push, and the lock rule is about work becoming
  visible, not about which flag was passed.

Also: bind `BranchEntry.Tracking` in the branch dropdown, which currently computes the string
and shows it nowhere.

### Tests

- Shell-level: each of the eight `TrackingState` cases from a synthetic `GitStatus`.
- `GitCliClient` integration: publish a branch into a bare fixture remote, assert the upstream
  is set and a subsequent plain push succeeds.

**Mutation check:** return `InSync` when `Upstream is null`; the `NoUpstream` case must fail.

---

## 3 · Background remote polling

> **Built** in GP-D. The 10 s base interval is still a guess — see the measurement table in the pre-flight.

The point of §2 is that behind-ness is visible. It is only *true*, though, as far as the last
fetch — so the counts need to keep themselves fresh without the user asking and without
getting in the way.

### Requirements, restated

Frequent · non-blocking · suspended during any other git action · efficient · logged every
time it runs.

### Design — a cheap probe, not a fetch

**Do not fetch on the short interval.** `git fetch` opens a connection, negotiates and
transfers objects; running that every few seconds is wasteful on a LAN and rude on a hosted
remote. The cheap question is a different command:

1. **Probe:** `git ls-remote --heads <remote> <branch>` — one line back, **no object
   transfer**. Compare its SHA against the local remote-tracking ref (`git rev-parse
   <upstream>`, a local read of a few ms).
2. **Only if they differ, fetch.** That is the only time objects move, and it is exactly when
   there is something new to learn. A real `fetch` then makes the ahead/behind counts in
   `GitStatus` exact, which drives the §2 badge.

So the steady state costs one small network round trip and one local ref read. The expensive
path runs only when the answer has actually changed.

### Interval and backoff

`git` is a process per call with no connection reuse, so each probe is a spawn plus a TLS
handshake and an auth round trip to the remote. At a fixed 3 s that is ~1,200 probes an hour
per open window — fine against a LAN git server, and enough to attract attention from a hosted
one.

**Recommendation: adaptive.** Base interval **10 s**, floor 5 s, both configurable. After a
run of probes that find nothing, the interval stretches — 10 s → 30 s → 60 s — and **snaps
straight back to the base the moment** anything changes, the user touches the git panel, or
any git command completes. A person actively working sees near-live counts; a window left open
overnight stops hammering the remote for no one.

If you would rather have a plain fixed interval, that is a one-line change to the same
component — say the word and it becomes a constant. The measurement to make the call is
"how long does one `ls-remote` take against your actual remote", which is worth taking before
picking the default.

### Not blocking the UI

- The poller owns a background loop with its own `CancellationTokenSource`. Nothing on the
  timer path touches an `ObservableCollection`; results are marshalled through the existing
  `_ui.Post` helper (`ShellViewModel.cs:1383`), which is already the rule for watcher callbacks.
- **It never raises the `BusyTracker`.** Raising it would flicker every git button dead every
  few seconds — the same reasoning that already keeps automatic status refreshes off the
  progress bar (`:2768`).
- **It never opens a diff, reloads the history, or rebuilds a list.** A probe that finds
  nothing must change exactly zero pixels.

### Not conflicting with real git work

- **Skipped entirely while `Busy.IsBusy`.** A probe competing for credentials and network
  during a push is pure noise, and a probe that started before a push must not be in flight
  during it — the loop checks the flag both before spawning and before applying its result.
- Technically the probe is already safe to overlap: `ls-remote` takes no index or ref lock and
  `GIT_OPTIONAL_LOCKS=0` is set for every invocation (`GitCliClient.cs:695`). Suspension is
  belt and braces, and it is the cheap half.
- **Suspended while a modal dialog is open** (branch delete, clone, guard prompt), for the
  same reason.

### Failure handling

Reuse `CircuitBreaker` (`Versio.Core/Sync/CircuitBreaker.cs`), keyed on the remote. An
unreachable remote — laptop on a train, VPN down, credentials expired — otherwise means a
failed probe every 10 s forever, each one logged and each one a candidate for a failure toast.
The breaker trips after N failures in the window, the poller stops, and **one** warning
notification says so with a "resume" action. Trip and resume are both logged at Info.

### Logging

Every trigger is logged, as asked, and the log stays readable:

- **Every probe → Debug**, from the poller's own background thread, never from the UI thread.
  The line carries the remote, the branch, the elapsed ms and the verdict (`unchanged` /
  `remote moved` / the failure).
- **Movement → Info**, with the old and new SHA — this is the line somebody looks for when
  asking "when did the remote change".
- **A `QuietRunTracker`-style roll-up → Info every 5 min**, so the console shows "poller alive,
  120 probes, nothing moved" instead of 120 identical Debug lines. The tracker already exists
  for exactly this problem (`Versio.Core/Logging/QuietRunTracker.cs`) and its reasoning applies
  verbatim.

**Watch the sink.** `RollingFileSink` writes synchronously with `AutoFlush = true` under a lock
(`:81,96`). At 10 s that is invisible. If the interval is dropped to the 5 s floor *and* Debug
is on, measure whether the UI thread ever waits on that lock — the fix, if it does, is a
buffered sink, not a quieter poller.

### Structure

- `src/Versio.Core/Sync/RemotePollPolicy.cs` — pure and clock-driven: given the last outcome
  and the current time, should we probe now, and what is the next interval? All the backoff
  logic, testable without waiting.
- `src/Versio.App/ViewModels/RemotePoller.cs` — the loop, the suspension checks, the git calls,
  the marshalling.
- `IGitClient.LsRemoteAsync(remote, branch, ct)` and `RevParseAsync(rev, ct)`.

### Tests

- Core: backoff stretches on quiet, snaps back on movement, respects the floor, honours the
  breaker.
- App: no probe while busy; a probe finding nothing raises no property change; a probe finding
  movement triggers exactly one fetch.

**Mutation check:** ignore the floor in the backoff calculation; the floor test must fail.

---

## 4 · Per-item staging

> **Built** in GP-E — *except §4a*, the `GitPanelViewModel` extraction, which was deliberately not done. The phase log gives the evidence.

### 4a · Extract `GitPanelViewModel` first

GP-E's first commit, before a line of checkbox code. A move, not a rewrite.

#### What moves, and what does not

| Moves to `GitPanelViewModel` | Stays on `ShellViewModel` |
|---|---|
| `Staged`, `Unstaged`, `SelectedStaged`, `SelectedUnstaged` | `RepositoryPath`, `Branches`, `SelectedBranch` |
| `CommitMessage`, `CommitSubjectLength` | The guard, the auto-stash, `ClearForContextChangeAsync` |
| `Stage`/`Unstage`/`Commit`/`Push`/`Pull`/`Fetch`/`Discard` commands | `ActivePanel`, the diff pane, `History` |
| `TrackingState`, the poller, the credential banner (§2, §3, §12c) | `Busy`, `StatusSummary`, `Sync` |

The seam already exists: every command funnels through `Ready` and `RaiseGitCommandStates`. The
child takes a reference to the shell's `BusyTracker` and its `IGitClient`, exactly as
`SyncViewModel` already takes the shared tracker — there is a working precedent in the codebase
for a child view model sharing the shell's busy state, and the test that pins it (*"the same
instance the shell binds its bar to"*) is worth copying, because that exact bug shipped once.

#### The risk, stated plainly

**A WPF binding that loses its property fails silently.** `{Binding CommitMessage}` against a
shell that no longer has `CommitMessage` produces no compile error, no exception, no test
failure, and no visible error — the TextBox simply renders empty and stays empty. Move eleven
properties and you can break the panel in eleven invisible ways while every one of the 1,251
tests still passes.

So this commit is **not** believed on a green suite. It is believed on the binding trace.

#### The verification protocol

`App.xaml.cs:325` already implements `--trace-bindings <file>`, which attaches a
`TextWriterTraceListener` to `PresentationTraceSources.DataBindingSource` at `Warning`. Use it,
and respect the trap the codebase already documented at `:274`: **a clean trace over a panel
that was never populated proves nothing.** A run that opens no repository, or never shows the
git panel, evaluates no bindings and produces an empty file — indistinguishable from success.

```bash
Versio.exe --trace-bindings C:\temp\bindings-before.txt C:\path\to\repo
```

1. Capture a trace **before** the extraction, with the git panel opened and every control
   exercised. Keep it — it is the control sample, and it establishes what "already noisy" looks
   like.
2. Extract. Update the XAML to bind through the child (`{Binding Git.CommitMessage}`).
3. Capture again, running the **same** steps.
4. **The two traces must be equivalent.** Any new `BindingExpression path error` line is a
   property that moved and a binding that did not follow it.

The proof that the run was real: the panel must have shown live data — a branch name, file
counts, a diff. If the trace is empty *and* the panel was blank, the run proved nothing and does
not count.

#### Tests

- App: `GitPanelViewModel` is constructed with the shell's tracker and **is the same instance**
  — the `SyncViewModel.Busy` test, copied.
- App: `MainWindow` still constructs against the real stylesheet (already covered; it must stay
  green through the move).
- App: a test that asserts the shell **no longer** exposes the moved properties, so a stale
  binding cannot be re-added later without something failing.
- Core: none. Nothing moves that has a rule in it — that is what makes this safe to do first.

**Mutation check:** leave one XAML binding pointing at the shell's old path; the trace comparison
must show a new path error. If it does not, the protocol above was not actually run.

#### Done when

The two traces match, the panel shows live data in the second run, and `git diff --stat` for the
commit is overwhelmingly moved lines rather than new ones. **If the diff contains real logic
changes, split them out** — a refactor that also changes behaviour is a refactor nobody can
review.

---

### Prerequisite (not optional)

`UpdateStagingLists` (`:2417`) rebuilds both collections on every refresh. Checkbox state
would be destroyed by the next file save. **It must reconcile in place**, the way `Reconcile`
already does for `Changes` — match on `Path`, `Update` the survivors, add and remove the
difference. Everything else in this section depends on it.

### Layout: two lists, both properly scrollable

The staged / not-staged separation stays. Today the split is not honest about it: `Staged` is
docked top with a hard `MaxHeight="150"` and `Unstaged` takes everything left
(`MainWindow.xaml:509-548`), so a 40-file stage is trapped in four rows while an empty
unstaged list holds several hundred pixels of nothing.

- Replace the `DockPanel` middle with a `Grid`: two `*` rows, a `GridSplitter` between them,
  a `MinHeight` on each so neither can be crushed to zero.
- Each list keeps its own `ScrollViewer` with `VerticalScrollBarVisibility="Auto"` — the
  scrollbar appears when the list overflows and takes no width when it does not.
- Default split proportional to content on first open, then remembered for the session so a
  drag is not undone by the next refresh.
- `Unstaged` already virtualizes; `Staged` must too, since it can now be tall.

### Selection model

- `ChangeRow.IsSelected` — observable, notifies the shell so counts and command states refresh.
- **Checkbox ≠ list selection.** The checkbox is the *action set*; the ListBox selection is
  *what the diff pane shows*. Clicking a row still opens its diff without touching the
  checkbox, and ticking a box does not change the diff. Conflating them is the standard way
  this feature goes wrong.
- Section header per list: tri-state master checkbox, section label, and a live
  `"3 of 12 selected"` caption.
- Commands: `StageSelectedCommand`, `UnstageSelectedCommand`, `DiscardSelectedCommand`.
  `StageAsync` already takes a list, so these are filters over the existing path.
- With nothing ticked, the selected-commands act on the whole list, so "Stage all" and
  "Stage selected" collapse into one button whose label reflects the count.

### Template split

`ChangeRowTemplate` is shared with the commit-file list (`MainWindow.xaml:664`), where a
checkbox is meaningless. Split into `StageRowTemplate` (checkbox + shared body) and keep
`ChangeRowTemplate` for read-only lists, with the glyph/path body factored into one inner
template so the "three chances for the glyph and its colour to disagree" warning at
`MainWindow.xaml:27` still holds.

### Multi-discard safety

`DiscardAsync` (`:2524`) currently refuses untracked files and confirms with the path named.
The multi-file version keeps both properties:

- Partition the selection into discardable and untracked. Discard the first set; report the
  second as skipped with the existing explanation rather than silently dropping them.
- The confirmation names the count and lists up to 5 paths, then "…and n more". A destructive
  confirmation that says "12 files" without saying which is not a confirmation.
- Direction-lock release still applies per binding, unchanged.

### Tests

- Reconcile preserves `IsSelected` across a refresh that changes an unrelated row.
- `StageSelectedCommand` sends exactly the ticked paths; nothing ticked → the whole list.
- Discarding a mixed selection discards the tracked files and reports the untracked ones.
- Master checkbox tri-state matches the children.
- Layout test (the `MainWindowTests` pattern): both lists present, both have a scroll viewer,
  neither `MinHeight` is zero.

**Mutation check:** go back to clearing the collections on refresh; the `IsSelected`-survives
test must fail.

---

## 5 · Button model and gating

> **Built** in GP-F. The gating matrix below is what shipped, with one row moved: the conflict gate landed here rather than waiting for §6, because git refuses the commit anyway and a button that says why beats one that fails.

### The buttons

Remote row (in the branch card): **Fetch · Pull · Push/Publish**
Commit row (under the message box): **Stage & commit**
Overflow menu (⋯ in the panel header): **Delete branches…** (§7), later additions

`Commit & Push` goes away. It bundles two operations with different failure modes into one
button, and its failure story is the worst in the panel: the commit succeeds, the push fails,
and one status line has to describe both. Push becomes its own deliberate act, promoted in the
remote row where the ahead-count already lives.

### `Stage & commit` semantics

The label follows the state, because one label covering three behaviours is how people commit
something they did not mean to:

| Situation | Label | Does |
|---|---|---|
| Rows ticked | **Stage & commit (3)** | Stages exactly the ticked set, then commits |
| Nothing ticked, nothing staged | **Stage all & commit** | Stages everything unstaged, then commits |
| Nothing ticked, something staged | **Commit** | Commits what is staged; touches nothing else |

The third row is the important one. Sweeping unstaged files into a commit because the user
happened to press the general-purpose button would silently undo a deliberate partial stage.

The PLC export gate (`CommitAsync`, `:1577`) runs first and unchanged — it is what makes the
default sync mode work, and its exported paths are staged regardless of the selection, since a
commit recording a project without its mirror is the exact state the gate exists to prevent.

### Gating matrix

| Button | Enabled when | Disabled tooltip |
|---|---|---|
| Stage & commit | `Ready` && (staged or unstaged or ticked > 0) && message non-empty | "Nothing to commit" / "Write a commit message first" |
| Push | `Ready` && `Ahead > 0` | "Nothing to push" |
| Publish branch | `Ready` && `NoUpstream` && has commits | "This repository has no remote" |
| Pull | `Ready` && upstream exists | "This branch has no upstream" |
| Fetch | `Ready` | "No repository open" |
| Stage / Unstage | `Ready` && the relevant list non-empty | "Nothing to stage" |
| Discard | `Ready` && ticked > 0 or a row selected | "Select the files to discard" |
| Delete branches… | `Ready` && more than one branch exists | "This repository has only one branch" |

**Every disabled button states why.** A dead control with no explanation is precisely what
sends a user to the command line, which is the thing this panel exists to avoid. In WPF that
needs `ToolTipService.ShowOnDisabled="True"` — without it the tooltip never appears and the
work is invisible.

**Two judgment calls, stated rather than buried:**

- **Pull stays enabled whenever there is an upstream**, not only when `Behind > 0` — though
  with §3 running, behind-ness is now fresh enough that gating on it would be defensible.
  Keeping it enabled means a stale count can never leave the user unable to act, and the cost
  is a pull that says "already up to date".
- **Push stays enabled when diverged**, with the tooltip warning it will be rejected.
  Pre-emptively disabling it hides the state the user has to resolve; git's own rejection
  message, now visible in a sticky failure toast, is the clearest possible explanation.

### Tests

- Core: the label and behaviour table above, driven from synthetic state — ticked rows, nothing
  ticked with a staged file, nothing ticked with none.
- Core: every row of the gating matrix, including `Diverged` leaving Push enabled.
- App: each disabled button exposes a non-empty tooltip, and `ToolTipService.ShowOnDisabled` is
  set on it — the property is the whole feature and is invisible without an assertion.

**Mutation check:** make `Stage & commit` sweep unstaged files when something is already staged;
the "commits only what is staged" test must fail.

---

## 6 · Conflicts — the UI half only

> **Built** in GP-G, together with §9b's diverged exit — they are the same problem seen from two ends.

**The engine half is built.** `IConflictProbe` / `GitConflictProbe` and
`SyncVerdict.BlockedByConflict` already refuse both sync directions while any path under a
binding is unmerged, with its sentence in `SyncNarrative.cs:79`. `TreeState()` already feeds
conflicted paths to the guard.

So this section builds **no detection and no second vocabulary**. It binds the panel to what
exists and adds only what the engine cannot: the surface. Read `SyncNarrative` before writing a
word of UI copy — two independently-worded explanations of one condition is a failure this
repository has already caught.


`HasConflicts` and `ChangeKind.Conflicted` are parsed and read by nothing. Today a
`--ff-only` pull that hits a divergence fails into a status line and leaves the user with no
route forward from inside Versio — the one workflow in the panel that dead-ends.

- **Banner** at the top of the panel when `status.HasConflicts`, in `Brush.Status.Conflict`,
  naming the count: "3 files have conflicts. Resolve them, then stage and commit."
- **A third list section**, above STAGED, holding only the conflicted paths. They are not
  "staged" or "not staged" — they are stuck, and putting them in either list misrepresents
  what git will do with them.
- **Commit is blocked** while conflicts remain, with the tooltip saying so. Git would refuse
  anyway; refusing here says why, before the attempt.
- **Per-file resolution**, minimum viable: "Keep mine" / "Keep theirs" via
  `git checkout --ours|--theirs <path>` then stage. Anything more (a three-way merge view) is
  a separate piece of work and is explicitly out of scope here.
- **Pull's failure toast gains the next step** — a `--ff-only` refusal is not a conflict yet,
  it is a divergence, and the message should say "the branch has diverged; a merge or rebase
  is needed" rather than repeating git's phrasing at somebody who did not ask for it.

Conflicts also interact with the PLC gate: a conflicted export tree must not be exported over
while unresolved. The gate already blocks on failed bindings (`CommitAsync`, `:1037`) — the
conflict case joins it.

### Tests

- Core: `HasConflicts` produces the banner state; commit is refused while any path is unmerged.
- Core: a conflicted path appears in the conflicted section and in **neither** staging list.
- Integration: a real conflicting merge in a fixture repo, then ours/theirs resolution followed
  by a successful stage.
- Integration: the `--ff-only` refusal is classified as *diverged*, not as a conflict — the two
  read alike in stderr and lead to different remedies.

**Mutation check:** allow commit while conflicts remain; the refusal test must fail.

---

## 7 · Branch identity and deletion

> **Built** in GP-H.

### 7a · Local, remote, or both

`BranchesAsync` reads `refs/heads` only (`GitCliClient.cs:91`), so a branch that exists solely
on the remote does not appear anywhere in Versio.

- Read `refs/remotes` in the same call and merge by short name.
- `BranchInfo.Existence`: `LocalOnly` · `RemoteOnly` · `Both`.
- The branch dropdown shows a badge per entry — a small glyph plus text, not colour alone:
  **local** (no remote copy — nobody else can see this work), **remote** (not checked out
  here), **synced**. Plus the existing ahead/behind, and the `Tracking` string that is
  currently computed and unbound.
- Selecting a `RemoteOnly` branch checks it out and creates the local tracking branch, which
  is what git does anyway — but the dropdown should say that is what will happen rather than
  appearing to switch to something that already exists locally.
- Ordering: current branch first, then local, then remote-only. Remote-only branches on a
  busy repository can number in the hundreds and should not bury the three the user works on.

### 7b · The default branch

Nothing in the codebase knows which branch is the trunk, and §7c needs it. Resolution order,
first hit wins:

1. `RepoConfig.DefaultBranch` — new field in `.versio/repo.json`, committed and shared, so a
   team with a `develop` trunk records it once.
2. `git symbolic-ref refs/remotes/<remote>/HEAD` — what the remote itself says.
3. `main`, then `master`, if either exists locally.
4. **Refuse.** If none resolve, the fallback-checkout in §7c is disabled with a tooltip
   pointing at the setting. Guessing the trunk and checking it out is not a guess worth making.

Settable from the panel's overflow menu ("Set as default branch") so it never requires editing
JSON.

### 7c · Delete branches

Overflow menu → **Delete branches…** → a modal dialog, following the existing
`BranchPromptWindow` / `ClonePromptWindow` pattern (own `.xaml` + code-behind, owner-centred,
constructed in an App test against the real stylesheet).

**The dialog:**

- A checkbox list of every branch, each row showing: name · existence badge (§7a) · ahead/
  behind vs the default branch · **merged / not merged** into the default branch
  (`git branch --merged`). "Not merged" is the single most useful fact here — it is the
  difference between deleting a tidy-up and deleting somebody's afternoon.
- The current branch and the default branch are listed but marked; see the rules below.
- A live **summary** as the selection changes: *"3 branches selected — 2 local + remote,
  1 local only. 1 is not merged into `main`."*

**Three actions, three distinct buttons, never one with a modifier:**

| Action | Runs | Enabled when |
|---|---|---|
| **Delete local** | `git branch -d <name>` | any selection with a local ref |
| **Delete local + remote** | `git branch -d` then `git push <remote> --delete <name>` | any selection with a remote ref |
| **Force delete** | `git branch -D` (+ remote delete when that was chosen) | only after a `-d` refusal, or with the "not merged" acknowledgement ticked |

**Safety rules — each of these is a real way to lose work:**

- **`-d` is attempted first, always.** Git refuses to delete an unmerged branch, and that
  refusal is *information*. Surface it per branch — "`feature/x` is not merged into `main`" —
  rather than silently escalating to `-D`. Force is a separate, deliberate second press.
- **Remote deletion is outward-facing and effectively irreversible for teammates.** Its
  confirmation names every branch and the remote explicitly: *"Delete 2 branches from
  `origin`? Anyone who has not fetched will keep a stale copy; anyone who has will lose
  theirs."*
- **Partial failure is reported per branch, not as one verdict.** Deleting five branches where
  the third fails must say which three succeeded, which failed, and why — a single "some
  branches could not be deleted" is useless.
- **After remote deletion, `git fetch --prune`**, or the deleted branches linger as stale
  remote-tracking refs and reappear in the dropdown looking alive.
- **The default branch cannot be selected.** It is the fallback target; deleting it has no
  recovery path inside the app.

**Deleting the branch you are on:**

Git refuses outright, so Versio handles it explicitly rather than surfacing the refusal:

1. If the current branch is in the selection, the dialog says so up front and names where it
   will move you: *"You are on `feature/x`. Versio will switch to `main` first."*
2. **A dirty working tree is no longer a refusal — route it through the auto-stash.** When
   this was written there was nothing to offer, so the dialog refused. `a3cdbfe` and `a2335b4`
   built a real auto-stash with an identity, a sidecar and lock capture, and
   `ClearForContextChangeAsync` already routes a `SwitchBranch` through it. So the fallback
   checkout takes the same path every other branch switch does, and the user gets the same
   "stash and proceed" choice they get everywhere else. **Do not build a second dirty-tree
   story for this dialog.**
3. **Refuse if there are held direction locks or a pending export.** The same reasoning as the
   commit gate: a checkout under a half-written mirror produces a tree nobody reviewed.
4. Checkout the default branch → verify it succeeded → *then* delete. If the checkout fails,
   nothing is deleted and the failure is reported.
5. If the default branch cannot be resolved (§7b), the whole operation is refused with the
   reason, not attempted.

**New `IGitClient` methods:** `DeleteBranchAsync(name, force, ct)`,
`DeleteRemoteBranchAsync(remote, name, ct)`, `MergedBranchesAsync(into, ct)`,
`DefaultBranchAsync(ct)`, `FetchPruneAsync(ct)`.

### Tests

- Core: existence classification from merged local + remote ref lists; default-branch
  resolution order including the refuse case.
- App: the dialog constructs against the real stylesheet (the `MainWindowTests` rationale);
  summary text matches the selection; force stays disabled until acknowledged; the default
  branch cannot be ticked; a dirty tree offers the auto-stash rather than disabling the
  delete.
- Integration against a fixture repo with a bare remote: delete local, delete local + remote,
  force-delete an unmerged branch, delete-current-branch with the fallback checkout, and a
  partial failure reporting per branch.

**Mutation check:** let `-D` run when `-d` was not tried first; the unmerged-refusal test must fail.

---

## 8 · Merge / pull request button — **built, as GP-I**

> Built as specified, with three changes on contact with the code, all in the phase log: the
> button is in the forge's **own vocabulary** rather than "merge request" everywhere; it has a
> **row of its own** rather than a fourth place on the fetch/pull/push line; and `RemoteUrl`
> grew a `Port`, without which every self-hosted forge on a non-default port produced a link to
> the wrong service.

A button in the branch card that changes with state: **Create merge request** → opens the
forge's own pre-filled "new MR" page in the browser; **Go to merge request** → opens the
existing one.

### Reading the forge from the repository

- **`IGitClient.RemoteUrlAsync(remote, ct)` already exists** (`GitCliClient.cs:229`, added for
  the vanished-repository offer) and reads `config --get remote.<name>.url`. Use it rather than
  adding a second way to ask. An earlier draft called for `git remote get-url` on the grounds
  that it applies `insteadOf` rewrites — **it does not**; those are applied at transport time,
  and the two commands differ only over multi-valued URLs and `pushurl`. Neither affects host
  detection, so the method already in the interface is the right one.
- Normalise both shapes into host + owner + repo. `ClonePromptWindow.xaml.cs:112-135,281-292`
  already parses the `https://` and scp-like (`git@host:owner/repo`) forms; **extract that into
  `Versio.Core/Git/RemoteUrl.cs`** so the dialog and this feature cannot drift apart. It is the
  same parsing problem twice, and one of the two copies would eventually learn about a URL
  shape the other did not.

### URL templates

| Forge | Create | Existing |
|---|---|---|
| GitHub | `/{owner}/{repo}/compare/{base}...{branch}?expand=1` | `/{owner}/{repo}/pull/{n}` |
| GitLab | `/{owner}/{repo}/-/merge_requests/new?merge_request[source_branch]={branch}&merge_request[target_branch]={base}` | `/{owner}/{repo}/-/merge_requests/{n}` |
| Azure DevOps | `/{org}/{project}/_git/{repo}/pullrequestcreate?sourceRef={branch}&targetRef={base}` | `/…/pullrequest/{n}` |
| Bitbucket Cloud | `/{owner}/{repo}/pull-requests/new?source={branch}&dest={base}` | `/{owner}/{repo}/pull-requests/{n}` |
| Gitea / Forgejo | `/{owner}/{repo}/compare/{base}...{branch}` | `/{owner}/{repo}/pulls/{n}` |

`{base}` is the default branch from §7b — which is another reason that section comes first.

**Self-hosted instances cannot be identified by hostname.** A GitLab at `git.acme.internal`
looks like nothing in particular. So: detect by hostname where it is unambiguous
(`github.com`, `gitlab.com`, `dev.azure.com`, `bitbucket.org`), and fall back to a
`RepoConfig.ForgeKind` override — committed, so a team sets it once. **When it cannot be
determined, hide the button** rather than guessing and opening a 404.

### "Does an MR already exist?" — answerable, but not from `.git`

Nothing in the local repository knows about merge requests. It is worth being blunt about that,
because the obvious implementations are wrong: parsing `.git/config` tells you nothing, and an
authenticated REST call means storing an API token, which is a whole feature with a security
surface of its own.

**There is a third way, and it reuses what §3 already builds.** Both major forges publish
their PR/MR heads as hidden refs, readable with the same credentials as a fetch:

- GitHub — `git ls-remote origin 'refs/pull/*/head'`
- GitLab — `git ls-remote origin 'refs/merge-requests/*/head'`

If any of those SHAs equals the branch tip, that number is this branch's open MR, and the
"Go to" URL can be built directly. One `ls-remote`, no token, no API client.

Honest limits, which the UI must respect:

- **Azure DevOps and Bitbucket do not expose these refs.** For those, "Go to" degrades to the
  repository's pull-request *list* page. A link to a list is honest; a fabricated MR number is
  not.
- The ref says a PR exists whose head is this commit. It does not say whether it is open,
  merged, or draft. So the label is "Go to merge request", never "1 open MR".
- A stale match is possible if two branches share a tip. Rare, and the consequence is landing
  on a real adjacent MR rather than on nothing.

Probe on the same schedule as §3, not on a timer of its own.

### Gating

Enabled only when **all** of these hold — matching what you described, made precise:

1. An upstream exists (`TrackingState` is `InSync`; not `NoUpstream`, not `Ahead`).
2. `Ahead == 0` — every commit is pushed. A "Create MR" for work that is still local produces
   an MR missing the changes it is named after.
3. The working tree is clean — no staged, unstaged, or conflicted files.
4. The branch is **not** the default branch. An MR from `main` into `main` is not a thing.
5. The forge is identified.

Each failing condition supplies the disabled tooltip: "Commit your changes first", "Push your
3 commits first", "This is the default branch".

**Safety note worth keeping in the code comment:** the URL is *pre-filled*, never submitted.
Versio opens a page with the source and target branches already chosen; a human still presses
Create in the web UI. Nothing outward-facing happens on Versio's say-so.

### Tests

- Core: URL construction for all five forges from both URL shapes, including a host with a port
  and a path prefix.
- Core: an unidentifiable host yields *no button*, never a guessed URL.
- Core: the five gating conditions, each failing one at a time and producing its own tooltip.
- Core: PR-ref matching — a tip SHA matching `refs/pull/N/head` yields N; no match yields the
  create URL; Azure and Bitbucket yield the list URL rather than a fabricated number.
- App: the button label follows the state.

**Mutation check:** default an unknown host to GitHub's template; the "no button" test must fail.

---

## 9 · Further ideas, ranked

These come from reading the code rather than from a feature list. The first three are the ones
I would argue for hardest.

### 9a · The guard over Pull — **done, not by this plan**

Two drafts of this section were wrong in opposite directions, and the code has now settled it.

- The first draft called it a safety hole and made it phase 0. Wrong: `6417f2a` had already
  deleted `Clone` and `Discard` rather than wiring them, and documented `Pull` as a deliberate
  deferral waiting on the auto-stash.
- The second draft called it a dependency this plan could not discharge. Also now wrong:
  `fce3f90` **wired it**, once `a3cdbfe` and `a2335b4` had built the auto-stash that gives its
  three verdicts something real to offer. `PullAsync` calls
  `ClearForContextChangeAsync(GuardIntent.Pull, "pulling")` at `ShellViewModel.cs:1682`.

**Nothing left to do here.** What survives is one rule for §7c: **do not invent
`GuardIntent.DeleteBranch`.** Deleting a branch you are not on leaves the working tree alone;
deleting the branch you *are* on requires a checkout, and that checkout is `SwitchBranch`, wired
since safeguards phase 4. `GuardIntent`'s own doc comment carries the rule — *every member has a
caller, or says why it does not.*

### 9b · The diverged state currently has no exit

§2 tells the user "diverged — pull first", and then `PullAsync` runs `--ff-only`
(`GitCliClient.cs:319`), which **refuses a diverged branch by design**. So the panel's advice
leads to a button that cannot work, and there is no merge and no rebase anywhere in the app.
The user's only route is a terminal.

`--ff-only` is the right default and should stay — auto-merging native CODESYS XML would
produce a file nobody reviewed, which is exactly what its comment says. But the diverged case
needs an explicit, deliberate choice offered *in words*:

- **Rebase onto the remote** — the usual answer for a feature branch, and it keeps the export
  history linear, which matters when the diff is generated text.
- **Merge the remote in** — with a warning naming any exported PLC tree that both sides
  touched, since that is where a text merge produces a file no engineer wrote.
- **Cancel**, prominently.

Blocked entirely while a direction lock is held or a project is open, via 9a.

**Cost:** medium. **Value:** removes the only dead-end left in the panel.

### 9c · PLC-aware change summaries — **built, as GP-J**

> All three uses shipped. See the phase log for what changed on contact with the code — chiefly
> that the summary describes *what the button will commit* rather than the working tree, and
> that the row carries a fourth thing this section did not ask for: whether editing the file
> reaches the project at all.

`ProjectManifest` maps every exported path to an object `Name` and `Type`
(`ProjectManifest.cs:14-29`), and `CodesysObjectType` knows POU from GVL from DUT. The git
panel uses none of it: every row is a raw path, and `src/App/POU/FB_Motor.st` is displayed as
a filename when Versio knows it is **the function block FB_Motor**.

Three uses, increasing in value:

1. **Row labels.** `FB_Motor` in primary with `POU` as a small type chip, the path in secondary
   grey beneath. Combined with §10.1's trimming fix, the changed-files list becomes readable at
   a glance instead of a column of near-identical prefixes.
2. **A change summary line** above the commit box: *"3 POUs, 1 GVL, 1 DUT changed in
   MainProject"*. That is the sentence an engineer would write themselves.
3. **A drafted commit message.** A "Draft" button that fills the box with
   `Modify FB_Motor, GVL_Alarms; add DUT_Recipe` — editable, never automatic. Commit messages
   on generated export trees are exactly where people give up and type "update", and this is
   the one place the domain knowledge makes a genuinely better message than a human in a hurry.

**Cost:** medium — the manifest is already loaded by the sync side, so this is mostly a lookup
and a template. **Value:** highest of anything in this document. It is the reason to use Versio
instead of a generic git client.

### 9d · Show what you are about to push, and about to pull

Clicking the ahead badge lists `git log @{u}..HEAD`; clicking the behind badge lists
`git log HEAD..@{u}`. `RecentCommitsAsync` already exists and takes a count and a skip — this
needs a revision range, which is a small addition to the same call.

The incoming half is the safety-relevant one. Pulling changes to an exported tree means those
changes will be imported into a `.project` — seeing which objects are about to move *before*
that happens is a genuine safeguard, not a convenience. With 9c it reads as object names.

**Cost:** small. **Value:** high on the pull side.

### 9e · Detached HEAD is displayed and not handled — **accepted**

`BranchName` becomes `"(detached)"` (`:1885`) and everything else carries on as though it were
a branch: push, pull and publish would all fail with git's own wording.

- A `TrackingState.Detached` case, which **disables the entire remote row** — fetch stays
  live, since fetching in a detached state is harmless and still informative.
- The badge says what it is in words: *"Not on a branch — you are looking at a commit. New
  work here will be lost unless you put it on a branch."*
- **The create-branch action sits in the badge**, pre-filled, because that is the only route
  out and making the user find it in a menu is the whole problem.
- Committing while detached is allowed but warned: the commit is real and reachable only from
  the reflog, which is exactly the trap.
- Interacts with §7c: the fallback checkout after deleting the current branch lands on the
  default branch, never leaves the user detached.

**Cost:** small. **Value:** a state people reach by accident from the history panel and cannot
currently get out of from inside the app.

### 9f · ~~Undo a branch delete~~ — **dropped**

Not wanted. Decided by the product owner on 2026-08-14, after GP-H shipped without it.

Kept as a heading rather than deleted so that somebody reading the numbered ideas does not
wonder what happened to it, and so the reasoning survives: the useful version is harder than it
looks — the forced and remote delete paths do not hand a SHA back the way <c>-d</c> does — and
an undo offered for some deletes and not others is worse than none. **This is a decision, not a
backlog item.**

### 9g · Commit identity preflight — **accepted, expanded into §12**

Grew into its own section, because identity turned out to be one of three separate things the
panel has to say about git's own health. See §12.

### 9h · Large binary preflight

`.project` files are binary and large; `IsLfsAvailableAsync` and `GitAttributesPlan` exist and
already reason about LFS rules. A commit that is about to include a large binary matched by no
LFS rule should say so **before** the commit, not after a push is rejected by a host size
limit — at which point the object is already in history and removing it is a rewrite.

**Cost:** small. **Value:** the failure it prevents is expensive and hard to undo.

### Tests for the items that stay in §9

`9a` is a correction and builds nothing. `9b` is tested with §6, `9e` with §7, `9g` with §12.
The three that need their own:

- **9c** Core: a changed path resolves through the manifest to object name and type; an
  unmatched path falls back to the raw path and is **never** labelled as an object; the drafted
  subject is built from names and stays under 50 characters or truncates honestly.
- **9d** Core: the revision ranges are built correctly for ahead and behind, including the
  no-upstream case where neither exists.
- **9f** App: the restore action carries the SHA the delete reported, and is offered for local
  deletes only.

**Mutation check:** let an unmatched path fall back to a guessed object type; the
"never labelled as an object" test must fail.

---

## 10 · Smaller UX fixes

> **Item 1 is built, with GP-J. The other five are not.** The path-trimming fix could not
> sensibly be done twice: GP-J replaces the row's single line of trimmed path with a name and a
> directory beneath it, which is item 1's second suggestion — *"render the directory in
> secondary grey with the filename in primary"* — arrived at from the other direction.

1. ~~**Path trimming is backwards.**~~ — **done.** `ChangeRowTemplate` used `TextTrimming="CharacterEllipsis"`,
   which trims the **tail** — so an exported PLC tree shows `src/Project/PLC/POU/Applic…` on
   every row and hides the only part that differs. Trim from the head, or render the directory
   in secondary grey with the filename in primary. Cheap, and it fixes the list with the most
   rows.
2. **Empty states.** Two empty boxes labelled STAGED and NOT STAGED say less than one line
   reading "Working tree clean — nothing to commit", and take four times the height.
3. **Show where "push" goes.** `origin · github.com/org/repo` in the branch card. The word
   "push" is meaningless without it, and on a machine with several remotes it is a real
   question.
4. **Unborn branch.** `IsUnborn` is parsed and unused; a fresh repository shows `—` and a row
   of dead buttons with no explanation of what to do first.
5. **Commit subject counter.** The bare number at `CommitSubjectLength` (`:1427`) is undecodable. Colour it at 50 and
   72 and it explains itself.
6. **Keyboard.** Ctrl+Enter already commits (`OnCommitMessageKeyDown`). Add Ctrl+Shift+P for
   push and put each shortcut in its tooltip — the discoverability is most of the value.

### Tests

- Core: head-trimming keeps the filename for a path longer than the column.
- App: the empty state appears when both lists are empty and not when either has a row.
- App: the subject counter changes brush at 50 and at 72.
- App: Ctrl+Shift+P raises the push command, and does not when the command is disabled.

**Mutation check:** trim from the tail again; the filename-visible test must fail.

---

## 11 · Documentation

Every phase lands with its documentation in the same commit, not afterwards. This codebase
carries its reasoning in `<remarks>` blocks and in `docs/`, and a phase that ships code without
them leaves the next reader with the "comment claiming a file nobody wrote" problem the
existing docs warn about.

**Per phase, in the phase's own commit:**

- **XML docs on every new public type**, in the house style: what it is in `<summary>`, and
  *why it is shaped this way* in `<remarks>`. The non-obvious decisions in this plan — probe
  instead of fetch, sticky failure toasts, `-d` before `-D`, checkbox separate from selection
  — belong next to the code that implements them, because each one looks like an arbitrary
  choice from the outside.
- **`docs/GIT-PANEL-PLAN.md`** (this file) — mark the phase done, and record anything the
  implementation taught that the plan got wrong. A plan that was never corrected by contact
  with the code is not evidence of a good plan.

**Once, at the phases that touch them:**

| Document | Update | Trigger |
|---|---|---|
| `docs/STATUS.md` | The "Desktop shell" progress line and the capability table; the header's test counts (re-derived, per its own note, not remembered) | Every phase |
| `docs/PLAN.md` | The git-panel design section: new button model, `TrackingState`, the polling design, branch deletion. §6.5's direction-lock rule now also covers publish | GP-B, GP-D, GP-F, GP-H |
| `docs/SAFEGUARDS.md` | Branch deletion joins the destructive set — local vs remote vs force, the fallback checkout, and its reuse of the auto-stash. Plus the merge/rebase choice, the poller's circuit breaker, and §5.B growing a UI half | GP-D, GP-G, GP-H |
| `README.md` | "What works today" — per-file staging, publish, branch management, live remote state, merge requests. **Also the `git.exe`-on-PATH requirement and the credential model (§12), which the dependency section argues for but the requirements table does not spell out** | GP-0, GP-C, GP-E, GP-H, GP-I |
| `.versio/repo.json` schema notes | New `DefaultBranch` field (GP-H) and `ForgeKind` (GP-I), each with the schema-version rule for reading configs written before it | GP-H, GP-I |
| `src/Versio.Core/Abstractions/IGitClient.cs` | **Correct the remark claiming PortableGit is vendored — it is not (§12a).** Doc drift in a load-bearing design argument | GP-0 |

**Also:** if `RepoConfig` gains `DefaultBranch`, follow the existing precedent in `Binding.cs`
— a new field on a committed, shared record needs its backward-compatibility behaviour stated
in `<remarks>`, exactly as `Vendor` does at `Binding.cs:30-39`.

---

## 12 · Git itself: presence, identity, credentials

> **Built**: §12a and §12b in GP-0, §12c in GP-C.

Three different things, currently conflated by having no surface at all. They fail at
different times, for different reasons, with different remedies, and the panel has to keep
them apart.

| | What it is | When it bites | Remedy |
|---|---|---|---|
| **Presence** | `git.exe` exists and runs | Everything, immediately | Install git |
| **Identity** | `user.name` / `user.email` | `commit` only — *local*, never touches a network | `git config user.email …` |
| **Credentials** | Proving who you are to the remote | `fetch` / `pull` / `push` / `ls-remote` | The credential helper, or an SSH key |

Merging these into one "login" concept would be wrong. A commit with no identity is not a
login problem, and a working `user.email` says nothing about whether the remote will accept a
push. The first-run failure story is different for each.

### 12a · Is git there at all?

**Yes, `git` on `PATH` is a hard dependency.** `GitCliClient` takes
`string gitExecutable = "git"` (`:28`) and nothing overrides it, so every invocation resolves
through `PATH`. README's requirements table already states `git 2.x with git-lfs`.

**Correction needed in the code:** `IGitClient.cs:18` claims *"PortableGit is vendored for
credentials, LFS and network operations regardless"*. **Nothing vendors PortableGit.** That
sentence is load-bearing in the argument for the CLI-over-LibGit2Sharp decision, and it is not
true of the code as it stands. Either vendor it or correct the remark — it currently reads as a
guarantee to whoever comes next.

What to build now, either way:

- **Probe once at launch**, not at first use. Today `GitVersionAsync` (`:761`) is only reached
  from the System Info window and answers *"not checked — open a repository"* when no repo is
  open — so a machine with no git at all gets a `Win32Exception` from somewhere deep instead of
  a sentence. Run `git --version` on startup, cache it.
- **Missing git is a first-class empty state** in the panel, not a toast: *"Git is not
  installed, or not on PATH. Versio drives the real git, so nothing here can work without
  it."* with the download link and the System Info window one click away.
- Record the resolved path (`where git`) in System Info. "Which git is it using" has already
  caused real confusion on machines carrying several — `VersionAsync`'s own remark says so.

### 12b · Identity

`git commit` with no `user.email` fails with a nine-line lecture — and it fails **after** the
PLC export gate has already run a 17–31 second CODESYS export (`CommitAsync`, `:1615`). A long
wait followed by a wall of text is the worst first-run experience the app can produce, and it
is entirely avoidable with one `git config --get` at repository open.

- Check `user.name` and `user.email` when a repository opens. Cheap, local, no network.
- If either is missing, a persistent (not transient) line in the commit area: *"Commits need a
  name and email. Set them once."* with two fields and a Save.
- Write with `git config --local` by default and a "use for every repository" checkbox for
  `--global`. Local by default because a work machine with a personal global identity is a
  common and annoying way to attribute commits wrongly.
- **Gate `Stage & commit` on it**, joining the §5 matrix, so the export never runs for a commit
  that cannot land.

### 12c · Credentials — what we are actually doing

**Versio does not implement login, and must not start.** This is already a stated design
decision, not a new one: README's dependency section argues that driving `git.exe` means
*"Git Credential Manager, SSH agents, 2FA and corporate proxies behave exactly as they do in
the engineer's own terminal."* That is the right call and it is worth restating, because the
obvious alternatives are all worse:

- Storing a token ourselves means owning a secret store, a rotation story, and a breach story.
- A username/password box cannot do 2FA, SSO, or device flow — which is most corporate git.
- Anything we build is a second place credentials live, and it will drift out of step with the
  terminal the engineer also uses.

**So Versio's job is: detect, explain, hand off. Never store.**

#### The problem we created for ourselves

Every invocation sets `GIT_TERMINAL_PROMPT=0` (`GitCliClient.cs:694`). That is correct for a
GUI — its comment is right that a prompt under a redirected console hangs forever with nothing
on screen. But it means that when no credential is cached, git does not ask: it **fails**, with

```
fatal: could not read Username for 'https://…': terminal prompts disabled
```

That message is *our own environment variable talking*. Which is inconvenient as a user
experience and excellent as a detector: it is an unambiguous, transport-specific signal that
means exactly "no credential is available and we suppressed the ask". No guessing.

#### The design

**1. Make `GIT_TERMINAL_PROMPT` per-call rather than global.**

- `0` — everything automatic and everything in the background: status, the §3 poller, fetch,
  and every operation the user did not personally initiate. Nothing may ever hang.
- **unset** — only for the explicit, user-pressed **Sign in** action, which is the one moment
  a prompt is wanted and expected.

This is a small change to `RunAsync`'s signature and the single most important part of this
section.

**2. Sign in via `git credential fill`, not by launching GCM directly.**

Feed `protocol=` and `host=` on stdin; git invokes whatever helper is configured, and the
helper does its own thing — GCM pops its window, a browser opens for OAuth, a token prompt
appears, whatever the engineer's setup does. Helper-agnostic plumbing, so it works for GCM,
for a corporate helper, and for a bare `store`.

Shelling `git-credential-manager` directly would be brittle (it moves, it renames, it is not
always present) and would break the moment somebody uses a different helper.

**3. Sign out / fix bad credentials via `git credential reject`.**

The genuinely maddening failure is not "no credentials" — it is *stale* credentials: an expired
PAT that the helper keeps cheerfully returning, so every push fails with `Authentication
failed` and nothing the user does from inside the app changes it. `git credential reject`
erases the entry so the next fill prompts fresh. This is the button people will need most and
the one no git GUI ever gives them.

**4. Classify the failure before reporting it.** A failed push is one of several things and
they have different remedies:

| Signature in stderr | State | Remedy offered |
|---|---|---|
| `terminal prompts disabled`, `could not read Username` | No credential cached | **Sign in** |
| `Authentication failed`, `Invalid username or password`, HTTP 401 | Credential rejected | **Sign in again** (fill after reject) |
| `Support for password authentication was removed` | Password used where a token is required | Sign in, with a note that it needs a token |
| HTTP 403, `Write access to repository not granted` | Authenticated, not authorised | Not a login problem — say so, name the repo |
| `Permission denied (publickey)` | SSH key not offered or not accepted | **Not the credential helper** — see below |
| `Could not resolve host`, timeouts | Offline or proxy | Retry; do not touch credentials |

The 403 row matters: telling somebody to sign in again when they are already signed in and
simply lack write access sends them round a loop that cannot terminate.

**5. SSH remotes are a different world.** There is no credential helper on that path — it is
keys and `ssh-agent`. `RemoteUrl` (§8) already has to distinguish the two URL shapes, so the
transport is known. For SSH, the remedy is diagnostic rather than a login button: report the
failure, offer `ssh -T git@<host>` as a check, and point at the key configuration. **Offering
"Sign in" for an SSH remote would be offering a button that cannot work** — the same sin as
§9b's "pull first" on a diverged branch.

#### Where it appears in the panel

A **sign-in state banner** in the branch card, sharing the slot with §2's tracking badge, since
both answer "what is between me and the remote":

- **Signed in** — nothing shown. Success needs no banner.
- **Sign-in needed** — amber, *"Sign in to `github.com` to push or pull."* + **Sign in**.
  Reached either from a failed operation or proactively at repository open (see below).
- **Sign-in failed** — the sticky failure toast from §1 carries git's own line, and the banner
  offers **Sign in again**, which does `reject` then `fill`.
- **Not authorised** — distinct wording, no sign-in button.

**Proactive detection at repository open.** One `git credential fill` in *query* mode against
the remote's host tells us whether a credential already exists, without a network call and
without prompting. If none exists and the repo has a remote, show the banner immediately rather
than waiting for the first push to fail. This is the "at launch" half of what you asked for.

**Interaction with the §3 poller.** An auth failure must trip the `CircuitBreaker`
**immediately, not after N failures** — retrying an auth failure every 10 seconds cannot
succeed, and it may lock an account. The breaker's threshold applies to network failures;
auth failures are terminal until the user acts. Resuming the poller is one of the things a
successful sign-in does.

#### Security notes, to go in the code

- `Redact` (`GitCliClient.cs:876`) already strips `user:token@` from URLs before logging.
  **Credential-helper stdin and stdout must never be logged at all** — not redacted, not at
  Debug. The only things logged are the host, the outcome, and the elapsed time.
- Sign-in is user-initiated, always. Nothing automatic ever triggers a prompt; §3's poller runs
  with prompts suppressed and reports rather than asks.
- The GCM window is a separate process and can appear **behind** the Versio window. Set the
  owner where the API allows, and in any case put *"Complete the sign-in in the window that
  opened — check behind Versio if you cannot see it"* in the busy caption, with a cancel.

### Tests

- Core: classify each stderr signature in the table above into the right state, including the
  403-is-not-a-login-problem case, from real captured strings.
- Core: transport detection from both URL shapes decides helper vs SSH advice.
- App: identity missing disables `Stage & commit` **before** the export gate runs — the
  regression test for the 31-second wall-of-text.
- App: an auth failure trips the poller's breaker on the first occurrence, and a successful
  sign-in resumes it.
- Manual, and worth writing down because it cannot be automated: expired PAT → push fails →
  Sign in again → reject + fill → GCM prompts → push succeeds.

**Mutation check:** classify HTTP 403 as a credential failure; the not-authorised test must fail.

---

## 13 · Verification scripts

One per phase, to be run **by you** in the real app, because nothing on my side can see the
window. Each step names an action and the exact observable result, so that a "no" is
unambiguous.

### Manufacturing the states

Most of these need a repository in a state that does not occur on demand. Use a scratch clone,
never a real project.

```bash
git init --bare /c/temp/versio-remote.git
```

```bash
git clone /c/temp/versio-remote.git /c/temp/versio-scratch
```

| State needed | How to make it |
|---|---|
| No upstream | `git switch -c feature/no-upstream` |
| No remote at all | `git remote remove origin` |
| Behind | commit and push from a second clone, then `git fetch` in the first |
| Diverged | commit in both clones on the same branch, then fetch |
| Detached HEAD | `git checkout HEAD~2` |
| No identity | `git config --local --unset user.email` |
| Conflicted | merge two branches that edited the same line |
| Auth failure | point the remote at a private URL you have no credentials for |
| Unborn | `git init` and open it before any commit |

### GP-0 · Git presence and identity

1. Rename `git.exe` out of `PATH`, launch Versio → the git panel shows the missing-git empty
   state naming PATH; **no exception dialog, no silent dead buttons**.
2. Restore git, unset `user.email` locally, open the repo → a persistent line in the commit area
   asks for name and email; **`Stage & commit` is disabled** with a tooltip saying why.
3. Fill both fields, Save → the line disappears, the button enables, and `git config --local
   user.email` in a terminal shows the value.
4. Make a change and commit → succeeds, and **the CODESYS export did not run before the refusal
   in step 2** — check the log: no export line precedes it.
5. Open System Info → git version and resolved path are both shown.

### GP-A · Notifications

1. Fetch on a healthy repo → a green card appears bottom-right and **is gone within ~4 s**.
2. Disconnect the network, push → a **red card that does not disappear**; its text ends with
   git's own error line; it has a copy button; the panel is still usable behind it.
3. Copy from it → the clipboard holds the full message.
4. Push successfully → **the green card survives the status refresh** and the status bar still
   reads the file count. This is the regression: before this phase, a successful push said
   nothing at all.
5. Trigger the same failure three times → **one card**, not three.
6. Trigger four different failures → three cards and a "+1 more" roll-up.

### GP-B / GP-C · Remote state, publish, credentials

1. On a branch with no upstream → the badge reads **"Not on the remote yet"** in amber; the
   primary button says **Publish branch**, not Push.
2. Publish → success card, badge becomes "Up to date with origin/…", and the upstream resolves
   in a terminal.
3. Remove the remote → badge reads "No remote configured"; the remote buttons are disabled and
   **each tooltip explains why on hover**.
4. Make the branch behind → badge says **"n commits to pull — the remote has changes you do not
   have"** in amber, and the button reads `Pull (n)`.
5. Diverge it → badge says diverged; **Push stays enabled** and its tooltip warns it will be
   rejected; pressing it produces a sticky red card carrying git's rejection.
6. Point the remote at a private URL with no credentials, fetch → the banner says **sign-in
   needed**, not a generic failure.
7. Press **Sign in** → the credential helper's own window appears. **Note whether it opens
   behind Versio** — that decides how loud the busy caption has to be.
8. Complete it → fetch succeeds, banner clears.
9. With a bad cached credential, press **Sign in again** → it prompts fresh rather than
   replaying the bad one.
10. Point at a repo you can read but not write, push → *"not authorised"* wording, **no sign-in
    button** — the loop that cannot terminate.
11. SSH remote with no key loaded → the advice mentions keys and `ssh -T`, and **offers no
    sign-in button**.

### GP-D · Background polling

1. Open a repo, watch the log panel at Debug → a probe line every ~10 s naming host, branch and
   elapsed ms.
2. Leave it idle 5 minutes → the interval visibly stretches, and **one roll-up line** appears
   rather than 30 individual ones.
3. Push from a second clone → within one interval the behind-count appears **without any click**,
   and the interval snaps back to the base.
4. During a push, watch the log → **no probe lines interleave**.
5. Watch the buttons during a quiet probe → **nothing flickers**, no progress bar, no dropdown
   flash. This is the one most likely to be wrong and the easiest to see.
6. Disconnect the network → probes fail, the breaker trips, **one** warning card appears, and
   the log stops repeating. Reconnect and resume → polling restarts.
7. Break authentication → the breaker trips on the **first** failure, not the fifth.

### ~~GP-E · The extraction~~ — not applicable

The `GitPanelViewModel` extraction was not done; §4a's protocol does not apply. See the GP-E
phase log for why, and the audit note for what was extracted instead. If the *remote* surface is
ever cut out — the seam that is actually clean — run §4a's trace protocol then, with the
amendment GP-0 forced: **the trace run must show the git panel**, or it comes back empty and
proves nothing.

### GP-E / GP-F · Staging and buttons

1. With 3 staged and 40 unstaged files → **both lists scroll independently**; neither is crushed;
   the splitter drags and holds.
2. Drag the splitter, then save a file in an editor → the split **does not jump back**.
3. Tick 3 of 40 → header reads "3 of 40 selected"; the button reads **Stage & commit (3)**.
4. Save a file in the editor while boxes are ticked → **the ticks survive the refresh.** This is
   the prerequisite everything else rests on.
5. Click a row (not its checkbox) → the diff opens and **no checkbox changes**.
6. Tick nothing with something already staged → the button reads **Commit**, and committing
   leaves the unstaged files unstaged. Verify with `git status` in a terminal.
7. Tick a mix of tracked and untracked, Discard → the confirmation names the count and lists up
   to 5 paths; untracked files are **reported as skipped**, not silently dropped.
8. Empty the commit message → the button disables with the message tooltip.
9. Master checkbox → tri-state matches the children in all three positions.

### GP-G · Conflicts and divergence

> Blocked on `SAFEGUARDS` phase 6 — see pre-flight. Do not run this against a repository
> holding a real PLC project until the suppression window ships.

1. Create a conflict → the banner names the count; conflicted files appear in **their own
   section** and in neither staging list.
2. `Stage & commit` is disabled with "resolve the conflicts first".
3. Keep mine / keep theirs on one file → it moves to staged and the count drops.
4. Diverge the branch and press Pull → the failure says **diverged**, offers rebase / merge /
   cancel in words, and cancel is prominent.
5. With a direction lock held → the merge and rebase options are **refused**, not offered.

### GP-H / GP-H2 · Branches

1. Branch dropdown → local-only, remote-only and synced branches are **distinguishable at a
   glance**, and remote-only ones sort last.
2. Select a remote-only branch → it says a local branch will be created, then does so.
3. Delete branches → the dialog lists every branch with merged / not-merged; the summary updates
   live; the default branch **cannot be ticked**.
4. Select an unmerged branch, Delete local → it **refuses that branch by name** and does not
   silently force.
5. Force delete → requires the acknowledgement first.
6. Delete local + remote → the confirmation names the remote; afterwards the branch is gone from
   the dropdown (prune ran) and from the remote.
7. Delete 3 where one fails → the report names **which** succeeded and which did not.
8. Delete the branch you are on, clean tree → it switches to the default branch first, then
   deletes.
9. Same with a dirty tree → the **auto-stash prompt** appears, the same one a branch switch
   shows; accepting it stashes, switches, deletes, and offers the work back.
10. The success card offers **Restore** → pressing it brings the local branch back at the same
    SHA.
11. Detach HEAD → the badge says not on a branch; the remote row is dead except Fetch; the
    create-branch action is **in the badge**.

### GP-I · Merge request — **owed to you now**

Needs a repository with a **real GitHub or GitLab remote you can push to**, which is the one
thing no test here can supply: a reachable remote and a hostname a forge is detectable from are
mutually exclusive without a network. Everything below step 5 works against any remote.

1. On a pushed, clean, non-default branch → the button reads **Create pull request** (or **merge
   request** on GitLab — it uses the forge's own word); pressing it opens the browser with source
   and target **already filled in** and nothing submitted. **Check the URL bar before pressing
   Create**: the target must be your trunk, not `master` guessed at.
2. Make an uncommitted change → disabled, tooltip says commit first.
3. Commit without pushing → disabled, tooltip counts them: *"Push your 1 commit first"*.
4. On the default branch → disabled, with its own wording.
5. **Open a PR in the browser, then leave the panel alone for a minute.** The button becomes
   **Go to pull request** on its own, with no refresh and no click — that is the background probe
   working, and it is the one part of this phase with no automated coverage end to end. Press it
   and confirm it lands on *that* request.
6. On a repository whose host is unrecognised → **no button at all**, not a disabled one.
7. Add `"forge": "GitLab"` to `.versio/repo.json` on that same repository → the button appears
   and says *merge request*. Remove it → it goes away again.
8. On an Azure DevOps or Bitbucket remote → the button reads **Open pull requests** and lands on
   the repository's list. It must never show a number.
9. A remote whose only URL is a UNC path or `C:\…` → no button.

**And one to watch for rather than to do:** the probe must not appear in the log every few
seconds. It should write a line only when the answer *changes*.

### GP-J · PLC-aware summaries — **owed to you now**

Against `fixtures/projects/RefrigeratorControl`, which has 3 POUs, 2 GVLs, 3 visualisations and
2 library managers in its manifest. Everything below is machine-checked except items 1, 6 and 8,
which are geometry and wording and cannot be.

1. **Open the repository and look at the panel before touching anything.** Rows read
   `Glob_Var` with a `GVL` chip and the path beneath, **on the first paint** — not raw paths that
   become names when you save something. This is the exact failure the phase log describes; if
   raw paths appear and then correct themselves, the subscription is broken again.
2. Change a POU and a GVL → two rows for the GVL (`.gvl.st` and `.gvl.xml`), both reading
   `Glob_Var`, told apart by the filename on the second line.
3. The summary line above the commit box says **`1 GVL and 1 POU changed in RefrigeratorControl`**
   — one GVL, not two, though it is two rows.
4. **Stage only the POU.** The summary drops to `1 POU changed in …` and the button caption drops
   to plain `Commit`. The two must agree; that is the point of the line.
5. Press **Draft** → the box fills with `Modify FB_Motor, Glob_Var`. Type a character: the button
   goes dead. Clear the box: it comes back. **Nothing you typed is ever overwritten.**
6. Change 8–10 objects at once → **does the list still read at a glance, and how many rows fit
   before it scrolls?** Rows are two lines tall now. This is the one judgement no test makes.
7. Edit `Library Manager.libraries.yaml` in the mirror → the row carries a lock glyph, and its
   tooltip says the edit does not reach the project.
8. Change `README.md` → it appears as `README.md` with **no chip** and, being at the repository
   root, **no second line** — the summary line ends `, plus 1 other file`. It must never be
   labelled as an object. **Your call to make:** a root-level file's blank second line still
   occupies its height, so those rows are as tall as nested ones with less in them. Say if that
   reads badly and it becomes a collapse on the empty case.
9. Switch to a repository with no PLC projects → no summary line at all, no chips, and the rows
   are filenames over directories.

### GP-K / GP-L · Previews and polish

1. Click the ahead badge → the commits about to be pushed are listed.
2. Click the behind badge → the commits about to arrive are listed, by object name where they
   are PLC files.
3. A long path in the changed list → **the filename is visible**, the directory is what gets
   elided.
4. Clean tree → one empty-state line, not two empty boxes.
5. Stage a large binary matched by no LFS rule → warned **before** the commit, not after.

---

## Sequencing

Each phase is independently shippable and testable.

**Phases are lettered, not numbered.** `docs/SAFEGUARDS.md` §6 already owns a numbered phase
plan for this repository (1–4 done, 5–9 pending), and two documents using "phase 6" to mean
different things is a coordination bug waiting to happen.

| Phase | Work | Why here |
|---|---|---|
| GP-A ✅ | Notifications (§1) | Everything later needs a way to report success and failure. Fixes the invisible-push bug on its own. |
| GP-B ✅ | Remote state machine + publish (§2) | Independent of staging; unblocks the gating in GP-F. |
| GP-D ✅ | Background polling (§3) | Needs §2's state to have somewhere to put its answer. Makes every count in the panel true. |
| GP-E ✅ | Reconcile the staging lists, the two-pane scrollable layout and checkboxes (§4). **§4a's `GitPanelViewModel` extraction was deliberately not done** | The reconcile is the prerequisite everything else rests on — the lists stop being rebuilt every second, so a checkbox survives an editor save. The extraction was dropped once the seam was measured: eight shell collaborators reach into `CommitAsync` alone. Evidence in the phase log; the size concern stands and the seam worth cutting is the *remote* surface. |
| GP-F ✅ | Button model + gating matrix (§5) | Needs GP-B and GP-E to gate against real state. |
| GP-G ✅ | Conflicts (§6, UI half) + the diverged-branch exit (§9b) | **No longer blocked** — safeguards phase 6 shipped both defences. One piece of work: both are "the remote and I disagree", and §2 currently advises a button that refuses. |
| GP-H ✅ | Branch identity, default branch, deletion dialog (§7). **Undo-delete (§9f) dropped by decision** | Largest single piece and the only new window; benefits from GP-A and GP-D already being in place. |
| GP-I | Merge-request button (§8) | Needs §7b's default branch for the target, and extracting `RemoteUrl` from the clone dialog. |
| GP-J | PLC-aware summaries and drafted commit messages (§9c) | Sequenced late because it is additive, not because it is low value — it is the highest-value item here. Nothing blocks it earlier if you want it sooner. |
| GP-K | Push/pull previews (§9d), large-binary preflight (§9h) | Each independent and small. |
| GP-L | Smaller UX fixes (§10) | Any time. |

**Three items are sequenced out of order because they are health, not features:**

| Phase | Work | Why here |
|---|---|---|
| **GP-0 ✅** | **Git presence probe + identity preflight (§12a, §12b)** | **First, and alone. Both are one `git config --get` and a startup `--version`, and together they remove the two worst first-run failures — a `Win32Exception` from nowhere, and a 31-second export followed by a wall of text.** |
| **GP-C ✅** | **Credential detection, sign-in and sign-out (§12c)** | With GP-B, because both are about what stands between this branch and the remote, and they share the badge slot. Must land *before* GP-D — the poller running with prompts suppressed and no credential-failure classification would hammer an unreachable remote and could lock an account. |
| ~~GP-H2~~ ✅ | Detached HEAD (§9e) — **landed early, in GP-B** | It is a `TrackingState` case, and splitting one state machine across two phases to honour a sequencing table would have been worse than moving it. Was: rides with GP-H: the same `TrackingState` work, and §7c's fallback checkout must never leave the user detached. |

---

## Pre-flight — re-checked 2026-08-13

Re-run after the safeguards work landed. The earlier pre-flight is superseded; nothing from it
survives unchanged.

### Baseline

Verified on `Clean-up-and-continue-dev` at `a083356`, working tree clean.

| | |
|---|---|
| Build | **succeeded, 0 warnings, 0 errors** |
| `Versio.Core.Tests` | **1,037 passed**, 0 failed, 0 skipped, 259 ms |
| `Versio.App.Tests` | **214 passed**, 0 failed, 0 skipped, 19 s |
| Integration | not re-run here; `STATUS.md` records 130 collected and a full pass of 111 against real CODESYS 3.5.22.20, 6 m 11 s |

`STATUS.md`'s header is **current** — it now carries these numbers and has replaced the commit
count with the command that derives it. Nothing to correct there before starting; keep it that
way by re-deriving with each phase.

*Note:* a build attempted while `Versio.exe` was running fails on the assembly copy step with
`MSB3027`, and reports warnings that are an artefact of the failure rather than real. **Close the
app before believing a build result.** The clean numbers above were taken with it closed.

### The GP-G blocker is gone

The previous pre-flight blocked GP-G on `SAFEGUARDS` §5.A, the git-write suppression window,
calling it the highest-risk unbuilt item in the repository. **It is built.** `SAFEGUARDS` phase 6
is ticked, and both halves are in the code:

- `SyncVerdict.GitWriteSuppressed` — *"Ignored: git was rewriting the working tree, so that was
  not an edit."* (`SyncNarrative.cs:79`, refused at `SyncOrchestrator.cs:772`)
- `IConflictProbe` / `SyncVerdict.BlockedByConflict` — the conflict gate (§5.B)

So **GP-G is unblocked and §6 shrank to its UI half.** The merge/rebase exit in §9b can be built
whenever it is reached. The constraint that survives is one of design, not sequencing: **the
poller fetches and never pulls** (§3). That was load-bearing when §5.A was missing; it stays
because an automatic pull is a working-tree rewrite nobody asked for, which is a different
objection and an equally good one.

### What changed under the plan

| Was | Now |
|---|---|
| Pull unguarded | Wired in `fce3f90` — §9a is done, not deferred |
| §6 builds conflict detection | Engine half exists; §6 is the UI half only |
| §7c refuses on a dirty tree | Routes through the auto-stash, like every other branch switch |
| §8 must add a remote-URL reader | `RemoteUrlAsync` already exists |
| Stash methods unreachable | Driven by the auto-stash; the "not doing" note re-argued |
| `ShellViewModel` at 2,304 lines | **3,202** — extract `GitPanelViewModel` as GP-E's first commit |

### Unchanged and still true

Every claim behind §1–§5 and §8–§12 was re-verified line by line — see the table at the top.
The git panel's XAML and its command set are substantially untouched by the safeguards work: the
same seven commands, the same `Commit` / `Commit & Push` pair, the same `MaxHeight="150"` on the
staged list, the same `Clear()`-and-rebuild in `UpdateStagingLists`.

### Still worth measuring before GP-C and GP-D

Unchanged from the last pre-flight, because neither has been done:

Record the answers here when taken, rather than deciding the constants from a guess.

| # | Measurement | Why it matters | Result |
|---|---|---|---|
| 1 ◑ | `git credential fill` with `GIT_TERMINAL_PROMPT` unset: does the helper's own UI appear, and **does its window open behind Versio**? *Half answered in GP-C: with prompts suppressed the query returns in 0.7 s and does not prompt, verified in the running app. Whether the helper's window opens behind Versio still needs a human, because it only appears when prompting is allowed.* | Decides whether §12c's busy caption has to tell the user to look behind the window. `GIT_TERMINAL_PROMPT=0` is still unconditional at `GitCliClient.cs:720`, so making it per-call is the first thing GP-C does | *not yet taken* |
| 2 ◑ | `git ls-remote --heads origin <branch>`, timed against the real remote, 5 runs | Sets GP-D's base interval | **91–95 ms against a local bare remote**, measured in the running app during GP-D. That is the floor, not the answer: a hosted remote over HTTPS adds a TLS handshake and an auth round trip per probe, and **that** number is what should set the interval. Still worth taking against your real `origin`. |
| 3 | Does `RollingFileSink`'s `AutoFlush` lock ever make the UI thread wait, at the 5 s floor with Debug on? | Decides whether §3's logging needs a buffered sink. Only worth taking if the interval actually goes that low | *not yet taken* |

Neither of the first two blocks writing code; both block *finishing* those phases with the right
constants. The third is conditional and may never be needed.

## Phase log

What each phase actually taught, recorded as it landed. A plan never corrected by contact with
the code is not evidence of a good plan.

### GP-0 — git presence and identity · done 2026-08-13

**Built:** `Core/Git/GitPresence.cs`, `Core/Git/GitIdentity.cs`;
`IGitClient.IdentityAsync` / `SetIdentityAsync`; `GitCliClient.ProbeAsync` and its
`where`/`which` executable resolution; a launch probe in `App.xaml.cs`; two banners in the git
panel; identity added to `CanCommit`; `Path` and `Commits as` rows in System Info.

**Tests:** 29 Core, 8 App (4 of them rendering the panel), 7 integration. 1,066 / 228 / 50 green.

**Corrected in passing:** `IGitClient`'s Rev 9 remark claimed PortableGit was *vendored* for
credentials and LFS. Nothing vendors it — `git` is resolved from `PATH`. The sentence was
load-bearing in the argument for CLI-over-LibGit2Sharp, so it was rewritten rather than left.

**What the phase taught — two things worth carrying forward:**

1. **The first version of the commit-gate test was vacuous, and only the mutation check found
   it.** It asserted `CommitCommand.CanExecute` was false with no repository open — true
   because `Ready` is false without a git client, and equally true with the identity check
   deleted. Reaching the identity term meant satisfying every other term of `CanCommit`: a real
   repository, a staged file, a message. **The mutation check is not a formality.** It is the
   only reason that test means anything, and it earned its place in the definition of done on
   the first phase that used it.
2. **`--trace-bindings` cannot verify this panel from a normal launch.** The trace file came
   back *empty* — the git panel starts `Collapsed`, so its bindings were never evaluated, which
   is precisely the trap `App.xaml.cs:274` documents. The substitute that cannot pass vacuously
   is `GitPanelChromeTests`: click the rail, measure, arrange, then assert. **§4a's protocol
   needs this amendment** — for the extraction, the trace run must show the git panel, or it
   proves nothing.

**Verified in the running app** against a repository with no identity: probe found
`git version 2.53.0.windows.2` at `C:/Program Files/Git/mingw64/bin/git.exe`, and the log
carried `commit identity incomplete — Git needs a name and an email address before it can
record a commit.` Both at the levels §3 specifies.

### GP-A — notifications · done 2026-08-13

**Built:** `Core/Notifications/Notification.cs` and `NotificationLog.cs` (pure, clock-driven);
`App/Mvvm/NotificationCenter.cs` with `NotificationRow`; a `Toast` style and four icon keys in
`Controls.xaml`; a toast host and overflow pill in `MainWindow.xaml`; `OnCopyNotification` in
the code-behind. **14 call sites converted** from `StatusSummary` to the new channel.

**Tests:** 15 Core, 15 App, 2 more chrome tests that render a real card. 1,081 / 245 green.

**The bug it fixes, confirmed in the code before it was changed:** `PushAsync` set
`StatusSummary` and *then* called `RunRefreshAsync`, which overwrote it with the file count. A
successful push produced no visible confirmation at all. It is fixed **by construction** rather
than by re-ordering the awaits — events and state are now different properties with different
lifetimes, so a refresh has nothing to clobber. `NotificationCenterTests.A_success_survives_the_refresh_that_used_to_overwrite_it`
is the regression.

**Decisions worth keeping:**

- **Lifetime is a function of severity, and failures have none.** Success 4 s, warning 8 s,
  failure until dismissed. The mutation that gives failures an expiry breaks two tests.
- **"Dismiss all" spares the failures.** Somebody handed a screenful of cards is exactly the
  person most likely to sweep them away unread, and one of them says a push did not happen.
- **De-duplication keys on the headline**, so three retries against an unreachable remote are
  one card rather than a wall of identical text.
- **The timer only runs while something can expire.** A sticky failure alone on screen needs no
  ticking, and neither does an idle app.
- **Every notification is mirrored into the log at the matching level.** A card the user
  dismissed and a log with no record of it is how a bug report arrives with nothing in it.

**Verified in the running app:** launched against a repository with an unreachable remote; the
window built with the new XAML and logged normally, with no `XamlParseException` and no new
errors. **The click-driven half is yours** — §13's GP-A script, steps 1–6, needs a human to press
Fetch and Push. What is machine-checked is that a posted card renders with its headline and
git's detail visible on screen (`GitPanelChromeTests`), which is what GP-0 taught: a binding
trace over a collapsed panel proves nothing.

---

### GP-B — remote state and publish · done 2026-08-13

**Built:** `Core/Git/TrackingState.cs` — `TrackingState` and `RemoteStanding` (*and a `RemoteAction`, deleted in the GP-E audit — nothing ever read it*);
`IGitClient.RemotesAsync` / `PublishBranchAsync`; `PublishCommand`; the worded badge in the
branch card; Push and Publish swapping; counts on the Pull/Push labels; `BranchEntry.Tracking`
finally bound after existing unbound since it was written.

**Tests:** 18 Core, 7 App flow (against a real bare remote), 7 integration, 2 more chrome.
**1,099 / 254 / 144 green.**

**Detached HEAD (§9e) landed here rather than in GP-H.** It is a `TrackingState` case, and
splitting one state machine across two phases to honour a sequencing table would have been
worse than moving it.

**The three states that were previously one:** in sync, no upstream, and no remote all rendered
as an *empty string* in the old `AheadBehind`. So a branch nobody else could see looked exactly
like a branch that was fully pushed. That is now three sentences, and `hasRemote` is passed in
precisely because it cannot be derived from the branch — the mutation that collapses `NoRemote`
into `NoUpstream` breaks two tests.

**Decisions worth keeping:**

- **Ahead is accent, not amber.** Work not yet pushed is the normal condition of somebody
  mid-task; colouring it like a problem makes the colour mean nothing by the time it is needed.
  Amber is reserved for *"somebody else moved"* and *"nobody can see your work"*.
- **Push stays enabled while diverged.** Git will reject it. Disabling it hides the state the
  user has to resolve, where git's own rejection — in a notification that does not expire — is
  the clearest possible explanation.
- **Pull is enabled whenever an upstream exists**, never gated on `Behind`. Behind-ness is only
  as fresh as the last fetch. The count goes on the label, where it informs without gating.
- **Publish picks `origin`, then a sole remote, then refuses.** With several remotes and no
  `origin` it says so rather than guessing — pushing a branch to the wrong remote is not a coin
  toss, and a repository with three remotes is one where somebody has already thought about it.
- **Publish releases the direction locks exactly as push does.** The lock rule is about work
  becoming visible to other people; a publish makes it visible for the first time.

**A test caught a real gap.** `Every_remote_button_explains_itself_while_disabled` failed on
first run — the Discard button carried a tooltip with no `ToolTipService.ShowOnDisabled`, so its
explanation was invisible in exactly the state it was written for. Fixed, and the test now
covers every commanded button in the panel rather than the two it was written for.

**Verified in the running app** against a repository with a remote and no upstream: opened
clean, `git remote` ran once at open as designed, and the status refresh computed a standing
with no errors. **The badge and the Publish button need your eyes** — §13's GP-B script.

---

### GP-C — credentials · done 2026-08-13

**Built:** `Core/Git/RemoteUrl.cs` (transport, host, owner, repository — GP-I reuses it);
`Core/Git/GitAuthState.cs` with `GitAuthClassifier`; `HasStoredCredentialAsync`, `SignInAsync`,
`SignOutAsync` on `IGitClient`; **`GIT_TERMINAL_PROMPT` is now per-call**; the credential banner
with Sign in / Forget credential; classification wired into every remote operation's success
and failure paths.

**Tests:** 49 Core, 8 App, 5 integration. **1,148 / 262 / 149 green.**

**Versio implements no login, and the code says why.** Driving `git.exe` is what makes GCM, SSH
agents, 2FA and corporate proxies behave exactly as they do in the engineer's own terminal.
Storing a token would mean owning a secret store, a rotation story and a breach story; a
username/password box cannot do 2FA, SSO or device flow. So the job is **detect, explain, hand
off** — through `git credential fill`, never by launching a named helper, which would break the
moment somebody uses a different one.

**The signature Versio creates itself is the best detector it has.** `GIT_TERMINAL_PROMPT=0`
makes git fail rather than ask when no credential is stored, saying `terminal prompts disabled`.
Inconvenient as an experience, unambiguous as a signal. Confirmed in the running app.

**Two states that look like authentication failures and are not**, each with a test:

- **HTTP 403** means the credential *worked* and the account lacks access. Telling that user to
  sign in again is a loop that cannot terminate, so `CanSignIn` is false and the sentence says
  *"signing in again will not change it"*. Checked **before** the 401 tests, because a 403 body
  can mention authentication.
- **An SSH key refusal** has no credential helper at all. `CanSignIn` is false there too — the
  advice names keys and `ssh -T`. Offering Sign in would be the same sin as advising a pull that
  `--ff-only` will refuse.

`repository not found` is deliberately classified as **unreachable**, not as a credential
problem: a private repository returns it for an unauthenticated request too, so it is genuinely
ambiguous, and "check the address and your connection" is safe either way.

**Security rules, enforced and verified:** nothing in the credential region logs what it sends
or receives — `git credential` speaks key=value over stdin and one of those keys is `password`,
which `Redact` would not catch. `SignInAsync` returns a result carrying only whether it worked,
never the helper's stdout. Confirmed in a real run: the log line reads
`credential for https://versio-demo.invalid: none` and carries no payload.

**One mutation is not caught by an automated test, and that is worth writing down.** Removing
the `!allowPrompt` guard — letting every git call prompt — leaves the suite green. The timing
test only bites when the host resolves and the helper actually waits; against an `.invalid`
host, DNS fails first. Catching it properly would need a remote that resolves and demands
authentication, which in a test run means either a real credential store or a window appearing
on somebody's machine. **Neither is acceptable in a suite**, so the guard is covered by the
integration test's fast-fail timing, by the classifier test for the resulting message, and by
this note — rather than by a test that pretends.

**Observed in the running app, worth knowing:** the proactive check does reach the network
through the helper (0.7 s here, against a host that does not resolve). On a slow or blocked
network that cost lands on repository open. If it ever shows, the fix is to move the check off
the open path rather than to drop it.

**Still needs a human:** whether the helper's window opens *behind* Versio. It only appears when
prompting is allowed, which is exactly the path no automated test may take. The busy caption
already says "check behind Versio" on the assumption that it does; §13's GP-B/GP-C script step 7
is where that gets confirmed or removed.

---

### GP-D — background polling · done 2026-08-13

**Built:** `Core/Sync/RemotePollPolicy.cs` (pace, backoff, halting — pure and clock-driven);
`App/ViewModels/RemotePoller.cs`; `IGitClient.LsRemoteAsync` / `RevParseAsync`; a `ModalScope`
so every prompt suspends the loop; `WakeThePoller` on any git activity.

**Tests:** 14 Core, 11 App. **1,162 / 273 / 149 green.**

**It probes; it does not fetch.** `ls-remote` returns one line and no objects; a real fetch runs
only when that differs from the local tracking ref. **And it never pulls** — that constraint is
load-bearing rather than incidental: a pull rewrites the project and its mirror under the
watcher, which `SAFEGUARDS` §5.A calls the one scenario able to corrupt a project through no
user error at all. Automatic pulling would be that corruption on a timer.

**Four rules keep it out of the way**, each with a test:

1. **Never raises the `BusyTracker`** — every git button consults that flag, so a probe that
   raised it would flicker the whole panel dead every ten seconds.
2. **Skipped while anything else runs**, checked before spawning *and* before applying a result.
   The second check is what stops a probe that started first landing mid-push; the test models
   the user pressing Push while the probe is in flight.
3. **A quiet probe changes nothing** — no property raised, no list rebuilt.
4. **A refusal halts on the first occurrence**, not after the usual run. Retrying a rejected
   credential every ten seconds cannot succeed and may lock an account. This is why GP-C had to
   land first: `GitAuthClassifier.IsTerminal` is what makes the distinction.

**Logging, as §3 specifies:** every probe leaves a Debug line with elapsed ms; movement is Info
with both commits; a run of quiet probes collapses through `QuietRunTracker` into one periodic
sentence. The test asserts fewer than 30 Information lines for 30 probes *and* at least 30 Debug
lines — both halves, because "log every trigger" and "keep the panel readable" are in tension
and the resolution is the level, not the omission.

**`_modalOpen` needed a scope, not a flag.** The compiler caught it unassigned; six call sites
each setting and clearing a bool would have one early return that forgot, silencing the remote
watch for the rest of the session with nothing to show why. `ModalScope` is a `using`.

**Verified in the running app**, end to end:

```
INF [Git] watching the remote for changes
DBG [Git] probe origin/main: unchanged in 95 ms
DBG [Git] probe origin/main: unchanged in 91 ms      <- exactly one interval later
INF [Git] origin/main moved: d6d9299 -> ed66024      <- after a push from a second clone
INF [Git] fetching
DBG [Git] git fetch --progress ok in 0.2s
DBG [Git] git rev-list --left-right --count origin/main...main ok in 0.0s
```

A colleague's push was noticed within one interval, with no click, and the ahead/behind counts
recomputed — which is the entire point of the phase.

**A real measurement came out of it:** `ls-remote` against a local bare remote is **91–95 ms**.
That is the floor rather than the answer — a hosted remote adds a TLS handshake and an auth
round trip per probe — so the 10 s default stands until somebody times it against a real
`origin`. Changing it is one argument to `RemotePollPolicy`.

---

### GP-E — per-file staging · done 2026-08-13

**Built:** `Core/Git/StagingSelection.cs` (tri-state, counts, labels — pure);
`ChangeRow.IsSelected` with a change event; **`UpdateStagingLists` reconciles in place**;
a `StageRowTemplate` with a checkbox; the two-pane grid with a splitter and min-heights; master
checkboxes, selection captions and count-bearing button labels.

**Tests:** 17 Core, 13 App (9 flow + 4 chrome). **1,179 / 286 / 149 green.**

### The §4a extraction was not done, and the reason is evidence rather than preference

§4a called for extracting a `GitPanelViewModel` first, on the grounds that `ShellViewModel` is
heading for 4,500 lines. **That seam is not clean, and the code says so.** `CommitAsync` alone
reaches `Sync`, `Busy`, `Notifications`, `_logger`, `_git`, `RunRefreshAsync`, `PushAsync` and
`StageExportedTreesAsync` — eight distinct collaborators. `StageAsync` and the selection setters
add `ShowPathAsync` and `DiscardCommand`. A child view model at that seam takes essentially the
whole shell as a constructor argument, which is a **move dressed as a decomposition**: the same
coupling, one more indirection, and a large silent-binding-failure risk for no reduction in what
anybody has to hold in their head.

What was extracted instead is the part that is genuinely separable and is where GP-E's new
complexity lives: **the selection rules, into Core**, where the tri-state and the label logic are
tested without a window. The shell grew by the wiring and shrank by the rules.

**The size concern stands and is not addressed.** If it is to be fixed, the seam worth cutting
is the *remote* surface — standing, poller, credentials, fetch/pull/push/publish — which is
already self-contained and was built in GP-B/C/D as a unit. That is a real decomposition and it
is a phase of its own, not a preamble to one.

### The chrome test caught a crash that every other check missed

`A_realised_staging_row_carries_a_checkbox` **crashed the test host**:

```
XamlParseException: Cannot find resource named 'ChangeRowTemplate'
```

`StaticResource` resolves in **declaration order**, and `StageRowTemplate` — which references
the shared row body — had been declared above it. Nothing catches that until a row is realised:
the window builds, the panel lays out, the suite passes, and **the application dies the moment
somebody clicks the Git rail on a repository with a changed file.** The app run earlier in this
phase opened cleanly for exactly the reason the binding trace keeps coming back empty — the
panel starts collapsed, so no row was ever built.

That is the third time this phase family has bitten in the same place, and the pattern is now
explicit: **for this panel, the only check that is not vacuous is one that realises a row.**
`LoadContent()` on the template in isolation is not enough either — it loses the resource
context and throws for a different reason.

### Decisions worth keeping

- **Reconcile in place is a prerequisite, not a refinement.** A refresh fires whenever the
  working tree moves; a checkbox on a row that is replaced a second later is a control that
  forgets what you told it every time you save a file. The mutation — replacing rows instead of
  updating them — breaks two tests. It also fixed something nobody had reported: the diff pane
  used to reset roughly once a second while somebody typed in their editor.
- **The checkbox is not the list selection**, and there is a test asserting each leaves the
  other alone in both directions.
- **Nothing ticked means the whole list**, which is what collapses "Stage all" and "Stage
  selected" into one button; the label carries the difference (`Stage`, `Stage all`, `Stage 3`).
- **An empty list is unticked, not fully ticked.** "Every one of no items is ticked" is true and
  invites a click that does nothing.
- **Rows unsubscribe as they leave.** The shell outlives every row, so a subscription left
  behind would keep a dead row alive for the session.

**Still needs a human:** §13's GP-E script — dragging the splitter, confirming both panes scroll
independently with a large stage, and that a tick survives a real editor save. The last of those
is machine-checked as well, but the first two are geometry.

---

### Audit · 2026-08-14, before GP-F

A read-through of everything GP-0 to GP-E added, looking for what the phases were too close to
see. Six findings, all fixed.

**1. Three pieces of API with no caller — the rule this repository already learned.** Commit
`6417f2a` deleted `GuardIntent.Clone` and `Discard` for exactly this, and left the rule in the
enum's doc comment: *every member has a caller, or says why it does not.* I broke it three times
in five phases:

| Added | Tested | Called by |
|---|---|---|
| `RemoteAction` + `RemoteStanding.Primary` | yes | nothing — the XAML swaps on `CanPush`/`CanPublish` |
| `StagingSelection.Affected` + `CanAct` | yes | nothing — the shell needs rows, not a count |
| `NotificationLog.DismissTransient` | yes | nothing — there is no "dismiss all" button |

All three are deleted. Each looked reasonable in isolation and each was a *plausible* API rather
than a used one — which is precisely why tests over them proved nothing about whether anything
asks. `RemoteUrl.UsesSshKeys` went the same way: the negation of a property that *is* used, read
only by its own tests.

**2. The same throwaway-repository fixture, four times.** `GitHealthTests`,
`RemoteStandingFlowTests`, `CredentialFlowTests` and `StagingSelectionFlowTests` had each grown
their own — and they had already drifted: one wrote empty identity values, one real ones, one
added a bare remote, one staged its files. Four copies is four places to fix a Windows
file-attribute problem and three that get missed. Now one `TempRepo`, ~220 lines of duplication
gone.

**3. A flaky test, found by the audit rather than by a red build.**
`Setting_an_identity_from_the_panel_unblocks_the_commit` passed alone and failed in the full run.
Not a defect: setting an identity spawns **four** git subprocesses, and the local `WaitUntil` gave
it ten seconds while three hundred other tests competed for the machine. That is the worst kind
of flake — it reads as a real failure and is a scheduling artefact. The three copies of
`WaitUntil` are now one `Eventually.Until(condition, what)` with a thirty-second backstop and a
message naming what never happened.

**4. `WakeThePoller` was wired to half of what the plan specified.** §3 says the backoff snaps
back when the user *touches the git panel*; it only fired on `Busy.IsBusy`. So somebody who
opened the panel after an idle hour would read counts on a sixty-second cadence — stale numbers
being exactly what they came to look at. Now woken on panel activation too.

**5. `RemotePollPolicy.QuietRun` is read only by tests**, which by the rule above wants
justifying rather than deleting: the backoff is otherwise observable only by elapsing real time,
and a rule that can only be checked by waiting is a rule nobody checks. Documented as
observability rather than quietly kept.

**6. Doc drift, three places.** §2's table had a "Primary action" column naming the deleted
enum; GP-B's log listed `RemoteAction` as built; and §13 still carried an extraction script for
work that was deliberately not done.

**Tests after: 1,175 Core** (four fewer — the deleted API's) **, 291 App, 149 integration.**

**One unreproduced failure, recorded rather than dismissed.** The app suite failed once, on one
test, on the run immediately after a real `Versio.exe` had been killed 25 seconds earlier — and
passed on **four consecutive runs afterwards**. The failing test's name was not in the captured
output, so there is nothing to point at. The likeliest explanation is a machine still tearing
that process down, but **that is a guess, and it is written down so a second occurrence is a
second data point rather than a first.** If it recurs, capture the run with
`--logger "console;verbosity=detailed"` before doing anything else.

**What this says about the phases.** Every one of these was introduced while the phase was
green, tested and documented. The pattern is consistent: designing an API and testing it feels
like finishing it, and *nothing in a green suite distinguishes a member with a caller from one
without*. Worth a scheduled read-through per few phases rather than trusting the per-phase
discipline to catch it — it demonstrably does not.

---

### GP-F — one commit button · done 2026-08-14

**Built:** `Core/Git/CommitPlan.cs` — `CommitIntent`, the label, the blocked reason and the
explanation; `CommitCommand` acting on the plan; `CommitAndPushCommand` **deleted**; the caption
and tooltip bound; the conflict gate.

**Tests:** 14 Core, 8 flow, 3 chrome. **1,189 / 301 / 149 green.**

**One button, three behaviours, and the caption is the safeguard.**

| Situation | Caption | Does |
|---|---|---|
| Rows ticked | `Stage & commit (3)` | Stages exactly those, then commits |
| Nothing ticked, nothing staged | `Stage all & commit` | Stages everything, then commits |
| Nothing ticked, something staged | **`Commit`** | **Commits only what is staged** |

The third row is why this is a type rather than an `if`. Somebody who staged two files on
purpose and left two out must not get the others swept in — they would find out by reading the
commit, by which time it is history. The plain caption is what makes the difference visible
*before* the press. The mutation that removes it breaks
`A_deliberate_partial_stage_is_not_swept_up`, which asserts against **git's own** view of what
landed rather than the view model's.

**`Commit & Push` is gone.** It bundled two operations with different failure modes, and its
failure story was the worst in the panel: the commit succeeds, the push fails, and one message
has to describe both. Push is a deliberate act of its own in the remote row, where the
ahead-count already lives — and `Committing_does_not_push` pins that.

**The staging happens before the export gate, deliberately.** So the gate's own staging lands on
top of the user's choice rather than racing it, and the `CommitStaged` case stages *nothing* —
which is the whole safeguard. A staging refusal aborts the commit rather than producing one
missing exactly the files that were ticked, which would look like it worked.

**Two design flaws the tests found, both the same shape.** The commit caption is computed from
the staging lists, and it went stale twice: once because nothing raised it when a collection
changed, and once because a row added outside `ReconcileInto` never got its tick subscription.
Both were arranged by *the one method that happened to build rows*. Now the collections
themselves are watched — a row is tracked for **being in the list**, not for having been added
by the right caller — and `Track()` is gone. Production never hit either, because production
only ever adds rows one way; that is exactly what made them worth fixing rather than working
around in the test.

**The conflict gate arrived early.** §6 is GP-G, but `CanCommit` needed a `hasConflicts` term
now: git refuses a commit over an unmerged path regardless, and a button that explains beats one
that fails. The *surface* — banner, third section, ours/theirs — is still GP-G.

**Geometry unchanged** at 372×729 with both lists at 207 px: one button where two were is
strictly less to fit. **Verified in the running app** against a repository with 2 staged and 2
unstaged — opened clean, no exceptions.

---

### GP-G — conflicts and the way out of a divergence · done 2026-08-14

**Built:** `Core/Git/MergeState.cs`; `ResolveWithSideAsync`, `MergeUpstreamAsync`,
`RebaseOntoUpstreamAsync`, `AbortMergeOrRebaseAsync`, `MergeStateAsync` on `IGitClient`; a
conflict section with keep-mine / keep-theirs / abandon; the diverged banner offering rebase or
merge; `RemoteStanding.IsDiverged`.

**Tests:** 12 flow against real conflicts. **1,189 / 313 / 149 green.**

**The panel's last dead end is closed.** §2's badge said *"diverged — pull first"* and
`pull --ff-only` refuses a diverged branch by design, so the advice pointed at a button that
could not work and the only way out was a terminal. `--ff-only` stays the default — it exists
because auto-merging native CODESYS XML produces a file nobody reviewed — but the user can now
ask for a rebase or a merge **by name**, having been told what each means. Both go through the
guard on `GuardIntent.Pull`, because both rewrite the project and its mirror together.

**No second detection path.** `IConflictProbe` and `SyncVerdict.BlockedByConflict` already own
the engine half; this binds to what git reported rather than asking again. Conflicted files get
their **own section**, because an unmerged path is reported in *both* of git's columns and is
really in neither state — listing it in staged and unstaged would be three rows for one problem.

**`MergeState` is read from git's marker files, not inferred from conflicts.** Resolving every
conflict leaves the tree clean while the merge is still *in progress* and waiting to be
committed; a panel reading "no conflicts" as "nothing happening" would offer a plain commit for
what is the conclusion of a merge.

**Abandon is not optional.** Offering an operation that can stop half-done without offering the
way back is how somebody ends up in a state they cannot describe, in a tool meant to keep them
out of the terminal. Confirmed, because unlike a discard what it destroys — conflict
resolutions — is not visible as a list of files.

### The flake finally explained, on its second sighting

The pre-flight recorded an unreproduced failure and said a second occurrence would be a second
data point. It happened, in this phase, and the cause turned out to be the same one that made
four *new* tests here time out:

**`AsyncCommand.Execute` silently returns when `CanExecute` is false.** Every git command is
gated on one shared busy flag, so a press landing while a refresh or a diff load is still
running does nothing at all — and the wait after it times out looking like a real defect. It
passed alone and failed under load because that window is a scheduling artefact.

Fixed with `Eventually.Press`, which waits for the command to be live and then presses it. **Not
a test-only concession**: it is what a user does, since the button is disabled while something
runs. A test that presses a disabled button is testing something that cannot happen. Twenty-seven
unguarded presses existed; the ones driving real repositories are all converted. Three
consecutive full runs clean afterwards.

### Two other things worth recording

**A test assertion was wrong, not the code.** Resolving with *"keep mine"* restores exactly what
HEAD already holds, so the index matches HEAD and git reports no change — the file does not
appear in Staged. Taking *"theirs"* would show one. The asymmetry is git being right.

**Two layout tests indexed `ListBox` by position** and silently started measuring the conflict
list when it arrived. Both now look up `StagedList` and `UnstagedList` by name — an
index-based lookup is exactly the kind of test that keeps passing while it stops meaning
anything.

**Verified in the running app** against a repository left mid-merge with a real conflict: opened
clean, reported `1 changed`, no exceptions. **The conflict banner and the diverged card need
your eyes** — §13's GP-G script.

---

### GP-H — branches · done 2026-08-14

**Built:** `Core/Git/BranchExistence.cs` with `BranchInventory`; `Core/Git/DefaultBranch.cs`;
`Core/Git/BranchDeletion.cs` — `BranchCandidate`, `DeletePlan`, `DeleteOutcome`;
`RepoConfig.DefaultBranch`; five methods on `IGitClient`; `BranchDeleteWindow`; the
delete flow with its guard, confirmation and per-branch reporting.

**Tests:** 38 Core, 16 App (6 window, 10 flow). **1,227 / 329 / 149 green.**

**Remote-only branches were invisible.** `BranchesAsync` read `refs/heads`, so a colleague's
branch, or one this clone had never checked out, did not exist as far as the application was
concerned — the dropdown looked like the whole repository and was half of it. Now merged by
short name, with `origin/HEAD` dropped (a symbolic ref, not a branch) and remote-only branches
sorted last so a busy remote does not bury the three somebody works on.

**Nothing knew which branch was the trunk.** Four rungs: `RepoConfig.DefaultBranch` (committed,
so a team whose trunk is `develop` records it once), then the remote's own `HEAD`, then
`main`/`master`, then **refuse**. The refusal is the important rung — guessing the trunk and
then checking it out moves somebody's working tree to a branch nobody chose. Verified in the
running app: `default branch is main (from Remote)`.

### The `-d`-before-`-D` rule turned out to be load-bearing, and a mutation proved it was not tested

The first mutation — swapping `force: false` for `force: true` — **survived**. Every test either
stopped at `CanProceed` or had already acknowledged the force, so nothing exercised the gap.

Rather than assume the rule was redundant, I checked what git actually does:

```
warning: not deleting branch 'tidy' that is not yet merged to
         'refs/remotes/origin/tidy', even though it is merged to HEAD
error: the branch 'tidy' is not fully merged
```

**`git branch --merged main` and `git branch -d` do not ask the same question.** `--merged`
compares against one branch; `-d` also refuses a branch ahead of *its own upstream*. So a branch
the dialog reports as merged — needing no acknowledgement — can still be carrying work nobody
else has. Attempting `-d` first is what turns that into a refusal the user sees rather than a
silent `-D`. `Git_gets_the_last_word_when_our_merged_check_is_wrong` constructs exactly that,
and the mutation now fails.

### That test then found a defect in every failure message in the application

Its assertion failed on the detail text, which read:

> `hint: Disable this message with "git config advice.forceDeleteBranch false"`

`GitResult.FailureReason` took the **last** non-empty stderr line, and git appends `hint:` lines
*after* the error. So the notification showed somebody how to turn the message off instead of
telling them why the branch survived — and this is the string every failure card in the panel
has been showing since GP-A. It now takes the last line that is not `hint:` or `warning:`,
falling back to the last line when git said nothing else.

### Other decisions

- **The current branch is left before it is deleted**, through the ordinary `SwitchBranchAsync`
  — so a dirty tree meets the auto-stash exactly as it does anywhere else. There is no second
  dirty-tree story for this dialog. If the switch fails, **nothing is deleted**.
- **The remote copy is only deleted when the local delete worked**, or when there was no local
  copy. Deleting the shared one while the private one survives is the wrong direction to fail.
- **`fetch --prune` after any remote delete**, or the branches linger as stale tracking refs and
  reappear looking alive.
- **The delete button moved off the remote row**, and the geometry suite is what settled it: a
  fourth button there overflowed the 380 px panel outright. It sits on the branch line now,
  which is where it belongs anyway — branch housekeeping is not part of the fetch/pull/push
  rhythm, and a destructive action where Fetch sits reads with the same weight as a fetch.
- **`ResourceKeyConverter` moved into the stylesheet.** `BranchDeleteWindow` is the second window
  to want it, and a converter declared privately in one window is invisible to every other —
  which is exactly how `SystemInfoWindow` once threw on open.

### §9f, restoring a deleted branch — dropped, not deferred

Shipped without it, and the product owner has since decided against it outright. The heading in
§9 records the decision.

---

### GP-J — PLC-aware naming, summary and draft · done 2026-08-14

**Built:** `Core/Projects/ObjectIndex.cs` (repo-relative path → the object the export wrote it
from), `Core/Projects/ObjectTypeNames.cs` (chip, singular, plural — a table, not an algorithm)
and `Core/Projects/ChangeSummary.cs` (the sentence, the drafted subject, the verb per object);
`SyncViewModel.Objects` rebuilt with the tree and announcing itself; `ChangeRow.Describe` with
`Primary` / `Secondary` / `TypeLabel` / `IsExportOnly`; a two-line `ChangeRowTemplate`; the
summary line and the **Draft** button in the commit box.

**Tests:** 40 Core, 15 App (7 row + 2 realised + 6 flow). **1,267 / 345 / 149 green**, integration
re-run in full against real CODESYS 3.5.22.20 — 0 skipped, 7 m 20 s.

### The wiring was the bug, and only the front-door test found it

Every row test, every index test and every summary test passed against a panel that **named
nothing at all**. `OpenRepositoryAsync` refreshes git and then calls `Sync.AttachAsync`, and the
bindings are loaded inside *that* — so the one refresh an open performs runs before any manifest
has been read, names nothing, and the index turns up afterwards from a path the git panel never
hears about. It stayed wrong until the working tree next moved, which on a project nobody is
editing is never: **open a repository, look at the panel, see raw paths.**

That is the fourth time this codebase has produced a method with a green suite and no reachable
caller — after `GuardIntent.Clone`, `ResetForRepositoryChange` and `TreeRow.Reconcile` — and the
first time the test that caught it was written *before* the code shipped rather than after.
`PlcAwareNamingFlowTests` opens a real repository with a real `repo.json` and a real manifest on
disk and asserts the panel's rows; deleting the one subscription turns exactly that file red and
nothing else.

**The fix is an event, not a call.** `SyncViewModel.ObjectsChanged` fires when the index is
rebuilt, and the shell subscribes once. The index is filled by a tree refresh, and a tree refresh
happens on a status refresh, on attach, after a rescan and after an adoption — four callers, of
which the panel controls one. A `DescribeRows()` after each would have been three chances to
forget, and the one that was already forgotten is the one that mattered.

### Decisions that changed on contact with the code

- **The summary describes what the button will commit, not the working tree.** §9c says "a change
  summary line above the commit box", which reads as the working tree — but the button beneath it
  commits *staged only* whenever something is staged and nothing is ticked, which is GP-F's whole
  safeguard. A line counting five objects over a button that would commit two is two controls
  describing one press. The set comes from `CommitPlan` now, so the caption and the sentence
  cannot disagree.
- **Counted by object, not by file, and that is not cosmetic.** A GVL exports to `.gvl.st` *and*
  `.gvl.xml`, so every count taken over paths reports one edit as two. `ProjectObject`'s own
  remarks already said the GUID is the identity; this is the first reader outside the tree to
  need it. The mutation — key by path — breaks two tests.
- **A fourth thing on the row that §9c did not ask for: `IsExportOnly`.** `STATUS.md` has carried
  "editing `Library Manager.libraries.yaml` changes nothing in the project" as a known limitation
  with **"and the panel does not say so yet"** beside it, parked on tree work. The changed-files
  row is where somebody is about to *stage* that edit, so it is where the sentence belongs. It is
  a glyph and a tooltip, off `ExportCoverage.ExportOnlyForm`, which already existed.
- **The type table is a table.** The tempting rule — lowercase, replace underscores, capitalise —
  yields "Gvl", "Pou", "Textlist" and "Imagepool". Engineers read those daily and a tool that
  renames them reads as one that does not know the domain. An unknown type is shown exactly as
  the exporter wrote it: `versio_types.py`'s table grows, and inventing English for a type this
  build has never seen is the same mistake as guessing an object from an extension.
- **Truncation drops whole names.** Half of `FB_MotorControl` is `FB_Motor`, which may well be a
  different function block in the same project. Over 50 characters the tail becomes "and 4 more";
  a single object whose name alone exceeds the limit is shown whole, because there is no honest
  shorter form and the counter beside the box already says it is long.
- **Draft refuses rather than replaces.** The button is dead while the box has anything in it, so
  no press can lose a half-written message. An "are you sure" over somebody's own sentence is a
  worse answer than not offering the action.
- **The tie-break in the sentence is alphabetical.** §9c's example reads "3 POUs, 1 GVL, 1 DUT",
  which is not any ordering — it is illustrative prose. Equal counts sort by name so the middle
  of the sentence does not reshuffle between refreshes, which would read as the counts changing.

### Two things worth knowing before the next phase

- **The `ChangeRow` is now two lines**, so the changed-files list is roughly twice as tall per
  row. `GitPanelGeometryTests` passes and the panes have min-heights, but how many rows fit
  before scrolling is a judgement nothing here can make — it is on the §13 script.
- **A NUL byte reached a source file.** Two spaces inside an interpolated string were written as
  `\x00`, which compiled and passed every test, made `ChangeSummary.cs` register as *binary* to
  grep and diff, and would have been committed to a repository whose whole premise is reviewable
  text. Caught by `grep` refusing to search the file. Worth a glance at `file src/**/*.cs` after
  any bulk edit — the compiler will not tell you.

---

### GP-I — merge-request button · done 2026-08-14

**Built:** `Core/Git/Forge.cs` (detection, five forges' create/existing/list templates, the
request-ref patterns and their parser) and `Core/Git/MergeRequestPlan.cs` (the five gates, the
label, the refusals); `RemoteUrl.Port` and `Authority`; `RepoConfig.Forge`;
`IGitClient.LsRemoteRefsAsync` and its `GitCliClient` half; `RemotePoller`'s `onProbed`; the
shell's `MergeRequest`, `OpenMergeRequestCommand` and `ProbeMergeRequestAsync`; the button, on
its own row in the branch card.

**Tests:** 53 Core, 11 App (8 flow + 3 poller), 5 integration against real git.
**1,320 / 356 / 154 green** at the time this landed, on a clean in-tree build with `Versio.exe`
closed: 0 warnings, 0 errors. *(The App total moved to 363 with safeguards §5.C, which followed
this. The per-phase figures here are re-derived with
`dotnet test --filter FullyQualifiedName~<TestClass>` rather than remembered — the first draft
of this line said 4 poller tests and there are 3.)*

### `RemoteUrl` was dropping the port, and every self-hosted forge needed it

`RemoteUrl` strips the port from `Host` deliberately and says why: git scopes a stored credential
to protocol and host, so a port in that key is wrong. But it was *discarded*, not set aside — and
a GitLab on `https://git.acme.internal:8443/…` therefore produced a link to
`https://git.acme.internal/…`, which is a different service or nothing at all. `Port` and
`Authority` are additive and `Host` is untouched, so the credential path is unchanged: **two
readers, two questions, one parse.** This is the case the plan's own test list asked for — "a
host with a port and a path prefix" — and it could not have been satisfied without this.

### The named mutation passed, and that was the finding

§8 says to check this rule by defaulting an unknown host to GitHub's template. It passed the
whole suite. The reason is worth more than the rule: `CreateUrl` tested `kind is Unknown` in a
**guard** *and* returned empty from the switch's **default arm**, so the arm was unreachable and
the mutation landed in dead code. Defence in depth reads as prudence and is the opposite here —
**a rule enforced in two places is a rule whose test only reaches one of them.** The guard no
longer tests for it; the switch is the single decision, and the mutation now breaks exactly
`An_unidentifiable_host_yields_no_url_at_all`.

### The wiring, again — and this time it was caught before it shipped

GP-J's finding repeated in a new shape. `RemotePollerTests` proves the loop calls whatever it is
handed; `MergeRequestFlowTests` proves the rules are right against a real repository. **Both stay
green with `onProbed: null` at the one construction site** — verified by doing it. So
`RemotePoller.AsksASecondQuestion` and `ShellViewModel.Poller` are internal, and one test asserts
the link. That is a construction assertion and a weak one in general; it is the right strength
here, because the specific thing that breaks is a constructor argument.

The related trap was avoided rather than fixed: the forge would naturally be resolved once when
the repository opens, beside `_remote` — and `repo.json` is loaded by `Sync.AttachAsync`, which
runs *after* that. A cached forge would have been `Unknown` for exactly the repositories that
need the setting. It is a computed property instead: an enum switch over two fields, cheaper than
the notification the alternative would need. **GP-J needed an event for this; GP-I needed
nothing, by not storing the answer.**

### Decisions that changed on contact with the code

- **The forge's own vocabulary.** The plan says "merge request" throughout, which is GitLab's
  word and wrong on the other four. `Forge.Noun` returns "pull request" for GitHub, Azure,
  Bitbucket and Gitea. A button that calls a GitHub pull request a merge request reads as a tool
  built by somebody who has not used the forge it is opening.
- **Its own row, full width.** GP-H already measured that a fourth button beside Fetch overflows
  the 380 px panel. It does not belong there anyway: fetch/pull/push is one rhythm repeated all
  day, and raising a request is a deliberate act that happens once per branch.
- **Collapsed, not disabled, when the forge is unknown** — as §8 asked. Worth restating because
  it is the only control in the panel that hides rather than explaining itself: the others are
  states the user can leave by doing something, and "your remote is a UNC path" is not.
- **Gitea publishes no request refs, as far as this code is concerned.** It very likely mirrors
  GitHub's `refs/pull/*/head`, and "very likely" is not a measurement. The cost of being wrong is
  a probe that always misses and a button that silently never upgrades from Create to Go to, so
  it degrades to the list page until somebody has run one `ls-remote` against a real instance.
- **The probe runs whether or not the remote moved.** Opening a pull request does not move the
  branch head — the published ref points at a commit that already exists — so a probe conditioned
  on movement would never notice one. That is why `onProbed` sits where it does in the loop
  rather than inside `onMoved`.
- **It costs nothing in the common case.** `ProbeMergeRequestAsync` returns before spawning
  anything unless the button is actually offering to create a request on a forge with request
  refs — which excludes the default branch, a dirty tree, unpushed work, every unidentified
  forge, and Azure and Bitbucket entirely.
- **The tooltip carries the URL when the button is live.** One binding for both states: a tooltip
  that emptied itself when the button went live would take the explanation away at the moment
  somebody is deciding whether to open a browser.

### A test-isolation defect that only appears under load

`The_button_is_in_the_branch_card_and_bound_to_the_plan` passed alone and failed in the full run,
reporting the button `Visible` where it must be `Collapsed`. Not a product bug: the class builds
`MainWindow` and lays it out, and it was **not in the `theme` collection** that serialises exactly
those tests — so it raced a test swapping the palette dictionary and its bindings did not resolve.
Every other class that builds and measures the window is in that collection; this one is now, and
three consecutive full runs are green. The two remaining `MainWindow` classes outside the
collection — `MainWindowClosingTests` and `RepositoryChangeResetTests` — were checked and neither
lays out, so neither evaluates a binding and neither is exposed.

---

### Audit · 2026-08-14, phases GP-0 to GP-H

Second read-through of everything these phases added. Smaller than the first, which is the
point of doing it twice.

**1. Four multi-step flows had no operation id.** §3 of this document says they should —
*"multi-step operations open `logger.BeginOperation(...)` so every line from one push shares an
id"* — and names the delete and sign-in flows specifically. `DeleteBranchesAsync`,
`ReconcileAsync`, `SignInAsync` and `PublishAsync` all lacked it. A branch deletion writes a
dozen interleaved lines across a merged check, a prompt, a checkout and N deletes; without an id
"why did that branch survive" is answered by reading around them. Fixed.

**2. The delete flow reconstructed an enum from a display string.**

```csharp
Existence = b.IsRemoteOnly ? BranchExistence.RemoteOnly
          : b.Where == "local" ? BranchExistence.LocalOnly : BranchExistence.Both
```

`Where` is the *label shown in the dropdown*. Rewording it — "local" to "local only", say —
would have silently reclassified every branch, and nothing would have failed until somebody
deleted the wrong thing from a remote. `BranchEntry` carries the enum now.

**3. `RepoEntry.Location` had never been bound**, and its own doc comment says it is *"shown
beneath the name so two repos with the same folder name are still distinguishable"*. Exactly the
`BranchEntry.Tracking` defect again: a comment describing a rule nothing enforced.

**Found by using the application, not by reading it.** Manual verification runs left ten
throwaway repositories in the recent list, several of them called `work` — and the dropdown
showed three identical rows with no way to tell them apart. The list is normally short and the
names normally differ, which is why it survived this long.

**Also: those runs polluted the real recent list.** `ShellViewModel` defaults its settings store
to `NullAppSettingsStore` precisely so tests cannot do this, and that held — the pollution came
from launching the actual `Versio.exe` against temp repositories during verification. Cleaned.
Worth knowing before the next round of manual checks: **verify against a repository you would be
happy to see remembered**, or clear the list afterwards.

---

## Handoff

**Branch:** `GitPanel`. **State:** GP-0 through GP-H shipped and audited twice; **GP-J and GP-I
built, in `1a01953`**, with both §13 scripts outstanding.

**Both phases share one commit**, which is the only entry in that table to do so. Three source
files and all four documents carry changes from each, so splitting them would have produced a
first commit that did not build — and rewriting the phase log, the table and the counts twice to
achieve it.

**Where to start reading.** *Where this stands* at the top of this document lists every phase
with its commit. Sections §1–§13 are the plan as written before the work, each marked built or
not; the **phase log** at the end is what actually happened and every decision that changed on
contact with the code. Where the two disagree, the phase log is right.

**How to verify.** Build with `%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe` — the `dotnet` on
PATH has no SDK. **Close `Versio.exe` first**: a running instance locks `src/Versio.App/bin`, the
build fails on the copy step, and it reports phantom warnings that are an artefact of that
failure rather than real. `-p:OutputPath=<elsewhere>` builds around it when the app must stay up.

**What is left.** GP-K / GP-L only: previews (§9d), and the five remaining items of §10 now that
path trimming has gone with GP-J. Neither is blocked, and §9d is the smallest thing left in this
document — `RecentCommitsAsync` already takes a count and a skip, so it needs a revision range
and a place to put the list.

**Run the two §13 scripts first**, GP-J's and GP-I's. Both have one step that no test reaches:

- **GP-J step 1** — object names on the *first paint*, not after the first save. This was
  genuinely broken during the work and is the whole reason `Sync.ObjectsChanged` exists.
- **GP-I step 5** — the button turning into *Go to pull request* on its own, a minute after you
  open a request in the browser. It needs a real forge and a real push, which is exactly what an
  offline suite cannot have.

**What is owed to a human**, because nothing here can see the screen: the three measurements in
the pre-flight table, and §13's verification scripts for anything visual. The geometry suite
(`GitPanelGeometryTests`) covers what is arithmetic — zero-size controls, horizontal overflow,
panes crushed to nothing — and has caught two real overflows; it cannot tell you whether the
panel reads well.

**The three habits worth keeping**, each of which caught something these phases would otherwise
have shipped:

1. **Mutate before believing a test.** A vacuous commit-gate test, an untested `-d`-before-`-D`
   rule, and a poller that did not suspend were all found this way.
2. **Realise a row.** For this panel, a test that does not build a list row proves nothing — a
   `StaticResource` inside a template resolves on instantiation, and a binding trace over a
   collapsed panel comes back empty.
3. **Run the application.** Two of the last three findings came from opening it, not reading it.

---

---

## Explicitly not doing

- **A manual stash UI.** The four stash methods are no longer unreachable — the auto-stash
  drives them on every guarded branch switch — so the original argument ("implemented and
  unreachable") has expired. The decision stands on what is left of it: there is no demand for
  *manual* stashing, panel space is the scarce resource, and the automatic path already covers
  the case that was losing work. Revisit only if a workflow actually needs it.
- **A three-way merge view.** Conflict resolution in §6 stops at ours/theirs. Anything richer
  is its own project.
- **Undo for a deleted branch (§9f).** Decided against on 2026-08-14. The reasoning is under
  that heading.
- **Merging STAGED and NOT STAGED into one list.** The separation is being kept deliberately;
  both panes become properly scrollable instead (§4).
