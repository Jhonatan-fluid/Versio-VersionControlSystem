# Versio — status and route to completion

**Date:** 2026-08-14 · **Commits:** `git rev-list --count HEAD` · **Tests:** 1,320 unit + 356 UI + 154 integration, 0 skipped

> All three re-derived on 2026-08-14, on branch `GitPanel`, after the git-panel plan's **GP-J**
> (PLC-aware naming of the changed-files rows, the change summary and the drafted commit
> subject) and **GP-I** (the merge-request button across five forges). The integration suite was
> re-run in full against real CODESYS 3.5.22.20 — **154 passed, 0 skipped** — including
> `RepositoryWatcherTests.A_deleted_file_is_reported`, the one noted below as flaky, which
> passed in this run. The five new integration tests read a forge's hidden request refs out of a
> real bare repository and need no network.

> **The commit count is the command now, not a number.** It was wrong by twelve when an audit
> first checked it, and wrong again twice in one afternoon afterwards — including once by the
> very commit that corrected it, which is the shape of the problem: a number that changes with
> every commit cannot be written down in a file that every commit touches. The test counts stay
> as numbers because they only move when the tests do, and `dotnet test` prints a total per
> suite when they have.

> One integration test, `RepositoryWatcherTests.A_deleted_file_is_reported`, is **flaky under
> full-suite load** — see Findings. It passes in isolation and failed once during a complete
> run. Known, not fixed.

---

## Where the project actually is

The risky half is done. Every hard question the plan raised about CODESYS has been answered by
measurement against a real project, and the domain logic that could lose someone's work is
written and tested without needing CODESYS, git, a clock or a UI to exercise it.

**Versio does the thing it was built for, in both directions, automatically.** A project in the
repository is tracked without ceremony, exported to a reviewable mirror when it changes, and
edits to that mirror flow back into the project — driven by the direction lock, proven end to
end against the real fixture with real CODESYS.

**And it now defends the half a mouse can reach.** Closing, switching branch, switching
repository and pulling all go through one guard; uncommitted work is stashed and offered back
with the sync state it had; an export can be killed at any instant without leaving a project
without a mirror; and what a killed run leaves behind is put right the next time the repository
opens. That is `SAFEGUARDS.md` phases 1–8, and phase 9 is features rather than guards.

```
  CODESYS pipeline    ████████████████████  proven on a real project
  Domain core         ████████████████████  1,227 tests, 278 ms
  Vendor seam         ████████████████████  enforced from assembly metadata
  Infrastructure      ████████████████████  drives real CODESYS + real git
  Desktop shell       ███████████████████░  one project view, full git surface, diff
  Packaging           ██░░░░░░░░░░░░░░░░░░  icon and publish profile only
```

### Proven by measurement, not assertion

| Question | Answer |
|---|---|
| Is the export deterministic? | **Yes** — and now enforced by a test that fingerprints the whole tree across two exports, after a regression proved the by-hand measurement was not enough |
| Does export → edit → import → export round-trip? | **Yes** for textual objects, proven with a deliberate mutation |
| Can native XML be imported without deleting the object? | **Yes** — `NativeImportHandler` replaces in place, GUID preserved |
| Can missing objects be created? | **Yes** — from a parse of the ST declaration header |
| Does the WPF tree survive a real project? | **Yes, but only when virtualized** — 94.7 ms vs 10,635 ms at the defaults |
| How long does a CODESYS launch cost? | **17–31 s.** Paid on every automatic export; the coalescer is what makes that bearable |
| Does an edited `.st` reach the project? | **Yes** — export, edit, import, verified byte-changed on the real fixture |
| Can library references be read at all? | **Yes** — `libman.references`, iterated. Not `get_libraries()`, which returns names only, and not `references[i]`, which indexes by id and raises for a position |
| Is a library reference's identity stable? | **No.** `ref.id` is re-minted on every open: 34 of 34 changed across two runs, with nothing else moving. Not recorded — the same trap as the `__VisualizationStyle` GUID, caught before it shipped this time |

### Known limitations, carried deliberately

- **Library references are export-only.** They are exported now — that gap is closed — but the
  file is written for review and never applied back: adding a library through the scripting API
  is a different operation from importing an object, and getting it wrong rewires what the
  machine compiles against. Editing `Library Manager.libraries.yaml` in the mirror changes
  nothing in the project. The coverage report says so in its own section, and the importer
  skips the file by suffix rather than feeding YAML to a toolchain that expects XML.
  The edit is not undone either: it sits in the working tree until the next export rewrites
  the file from the project. Worth knowing before somebody stages one — **and the panel says so
  now**, as of GP-J: the changed-files row carries a lock glyph and a tooltip saying the edit
  does not reach the project. That was parked on tree work here for a while, which was the wrong
  home for it: the row somebody is about to *stage* is where the warning has to be, and the
  coverage sentence could never carry it because it only appears when there are gaps and this is
  not one.
- **How a library reference *resolves* is deliberately not recorded.** The file carries what
  the project asks for — placeholder, default resolution, redirection, parameters — and not
  what it resolved to on the exporting machine. Two engineers with different libraries
  installed would otherwise produce different bytes from the same project, and neither would
  be wrong.
- **Managed (fixed-version) library references are untested.** All 34 references in the fixture
  are placeholders, so the branch that reads `version`, `company` and `title` off a managed
  library is written from the API's description and has never run against a real one.
- **Task configuration is diff-only.** `import_native` changes a task's `Core2` from `-1` to
  `-2` unasked. Reproduced three times, pinned by a test, not masked.
- **Graphical POUs export as native XML**, so their diffs will never read as well as ST.
  Inherent to the format.
- **Interfaces, text lists, image pools** have export rules written and unit tested but never
  exercised — the fixture project contains none.
- **One fixture project.** A compound/safety project and a graphical-heavy one are the obvious
  gaps in the corpus.
- **One vendor implemented.** The seam is real and enforced, but CODESYS is the only toolchain
  with a runner behind it.

---

## What works

### The sync engine

Automatic in both directions. A settled change to a `.project` exports; a settled change under
an export tree imports. Every project found in the repository is tracked automatically —
templates excepted, because a binding whose project is a template exports the template into its
own tree, which is incoherent rather than merely unwanted (R28).

**The locks are the defence, not defence in depth.** With nobody present to refuse anything,
the direction lock, the hash gate, the self-write suppressor and the circuit breaker are all
that stand between a save and an unwanted overwrite. Jobs run through the coalescer, whose
5-second cancel window and 30-second minimum interval mean a save storm produces one export
rather than one per keystroke.

**Import copies the project aside first**, into `.versio/local/backups/<binding>/`, keeping ten
per binding. A failed backup fails the import: doing the operation that can destroy the project
having skipped the step that makes it recoverable is the worst of both.

### The vendor seam

`IPlcRunner`, `IPlcProjectFormat`, `IPlcToolchain`, `PlcJob`, `PlcResult`, and the `PlcFormats`
/ `PlcToolchains` registries. Adding Rockwell, Siemens or TwinCAT means implementing those and
registering them; nothing in `Sync/`, `Git/`, `Locking/`, `Watching/`, `Projects/` or `Diff/`
may change.

`VendorSeamTests` enforces that from assembly metadata rather than describing it, and has been
proven able to fail. A binding records its own vendor; the scanner scans for every registered
format; `.gitattributes` covers every registered vendor's extensions.

### The shell

| | |
|---|---|
| **Project view** | **One panel.** Every project *with something to review* — a change, a mirror behind, a lock held, a failure, or never exported — carrying its full CODESYS-shaped object tree, with every other changed file beside them. A project whose mirror is clean is not listed, and the empty state says so rather than going blank. Search still reaches into a clean project, because the box searches the project and not the diff |
| **Git panel** | **Per-file staging** — a checkbox on every row, tri-state headers, and labels that say what will happen (`Stage 3`, `Stage all`); both panes scroll and a splitter divides them. **One commit button with three behaviours** — it stages what you ticked, or everything, or nothing at all when you have staged deliberately, and the caption says which before you press. Commit message with a 50/72 guide and Ctrl+Enter. **Where the branch stands, in words** rather than two arrows — eleven states covering unborn, detached, no remote, never published, ahead, behind and diverged, each naming its remedy. **Publish** for a branch with no upstream, which a plain push cannot do. Fetch / pull / push / discard. **Branches show where they live** — local only, remote only, or both — and remote-only ones are listed at all for the first time. A delete dialog removes them from this machine and optionally the remote, naming what is not merged and reporting per branch when one refuses. **Conflicts get their own section** with keep-mine / keep-theirs and an abandon, and a diverged branch is offered a rebase or a merge by name — before this, the badge said "pull first" and a fast-forward-only pull refuses a diverged branch, so the only way out was a terminal. **Notifications** that survive the status refresh, with failures sticky until dismissed and git's own words on them. **Credentials**: detected at open, handed off to the engineer's own helper, never stored — and a *forget credential* for the expired token no git GUI lets you clear. **The remote is watched in the background**, by `ls-remote` rather than by fetching, so a colleague's push shows up without a click. **Git's own health**: a launch probe that names PATH when git is missing, and an identity preflight so a commit is refused before a 17–31 s export rather than after it. One operation at a time across every button — a shared counted flag. **Changed files are named as objects** — `FB_Motor` with a `POU` chip and the path beneath, resolved through the export manifest, with a lock glyph on a file whose edits cannot reach the project; a file the manifest does not describe stays a path and is never guessed at. **A change summary and a drafted commit subject** — *"1 GVL and 1 POU changed in RefrigeratorControl"*, and a **Draft** button that writes `Modify FB_Motor, Glob_Var`, both counting objects rather than files and both describing exactly what the commit button will record. **A merge-request button** for GitHub, GitLab, Azure DevOps, Bitbucket and Gitea: it opens the forge's own page pre-filled and submits nothing, upgrades itself to *Go to* when the forge's hidden refs say one already exists — no API token, nothing stored — and shows **nothing at all** for a host it cannot identify. **No stash controls**: removed pending a different design; the auto-stash drives the client operations. See [`GIT-PANEL-PLAN.md`](GIT-PANEL-PLAN.md) |
| **History panel** | **Its own rail button**, not a strip inside the git panel. Every commit with its hash, local date and time, author and subject; expand one for the files it touched; click a file for what that commit did to it, in the shared diff pane. Lazy at four levels — the log is not walked until the panel is first shown, then one page of 50 at a time; a commit's files are read when it is expanded and cached for the session, because a commit's contents cannot change; a patch is read only when a file is clicked, and never cached. A merge says so rather than showing an empty list |
| **PLC state** | **No card, and no buttons.** Toolchain, version and profile were environment rather than repository state and moved to Settings → Help → System information. "Export now" is gone because a stale mirror needs no decision — it is exported when the repository opens. "Acknowledge failure" is gone as a panel-wide control: a failure belongs to one project, so it is reported on that project's row with the reason and a *Try again* beside it. What is left in the panel is transient and hides itself: the coverage warning and the live heartbeat |
| **Branch and clone** | Create a branch from HEAD and switch to it, with the name checked against git's rules while it is typed; clone a repository after verifying the remote, then open it. Both are modal dialogs built from the one stylesheet, and both are covered by tests that construct them against the real theme |
| **Commit gate** | A stale mirror is exported *and staged* before the commit. Push releases the direction locks; commit does not, and the panel says so |
| **Diff view** | Eleven languages, new files rendered entirely green, *Show full file*, rows built only when realised, **per-hunk stage and unstage** |
| **Open in the IDE** | Export runs anyway, from a copy; import waits and names who has the project. Neither marks the binding failed, and the breaker is never fed |
| **Adoption writes `.gitignore`** | Created when absent, appended when present, never overriding an existing decision. Covers `.versio/local/` — locks, backups, scratch copies — and the toolchain's `-AllUsers.opt` and `-<user>-<machine>.opt`, the second of which carries a username and machine name in its filename |
| **Export coverage** | What the export does *not* carry, said out loud: `_coverage.md` in the tree, every skip in the manifest with a stable reason, and a sentence in the panel naming what cannot be relied on. A gap is distinguished from a deliberate omission, from a file that is exported one way, and an old manifest reports "unknown" rather than "complete" |
| **Library references** | One `<manager>.libraries.yaml` per library manager, beside the object that owns it — what the project asks for, never how it resolved here. Hand-written YAML on both sides, no dependency added to either. **Export-only**, and stated where a reader will meet it: the file's own header, `_coverage.md`, the manifest's `form`, **and now the panel** — the changed-files row carries a lock glyph and a tooltip saying the edit does not reach the project. It went there rather than on the tree row (§9.3) because the row somebody is about to *stage* is where the warning has to be. `CoverageSummary` still speaks only when there are gaps, deliberately |
| **Chrome** | One app-wide progress bar with a caption, maximized on open, log console collapsed, heartbeat during long runs. **The title bar follows the theme** — painted with `Color.Surface.Base` through DWM, on every window the app opens and again on every theme switch, so the caption and the top bar are one surface rather than two. Tooltips are themed too, by an implicit style, because there are dozens of them and not one named a style |
| **Saying it is working** | A toolchain run announces itself the moment it passes the last gate, and three surfaces answer three different questions: the **bar** that the app is busy, the **row** which project it is busy with (`exporting…`, in the accent colour), and the **heartbeat** that it is still alive and how far along. A refused, coalesced or throttled save announces nothing, because it is not working |
| **Log panel** | Level as four chips (click to drop, click to restore) and category as a multi-select dropdown carrying a live count each, because the counts are what say where the volume is. **Pause freezes the feed** rather than the scroll, says so in the header, and shows what is queued behind it. The controls appear with the list they filter: collapsed, the strip is a chevron, a count and the paused pill |
| **Logging** | Ring buffer, correlation ids, rolling file from startup, crash handlers on all three .NET channels. An idle app is quiet: a completed git command writes one line, not two, and runs of unchanged status polls are summarised every five minutes instead of three lines per poll. The export narrates itself: the directory swap with both file counts, every skip with its reason, the summary, and a warning when a killed run left a staging folder behind |

---

## What remains, in dependency order

> **Picking this up?** Items 1 and 2 are done and kept for their reasoning. Items 3–8 are
> untouched: **item 3 is the smallest**, item 4 is the largest piece of user-visible value, and
> nothing below depends on anything above it except where it says so.

> **Two other plans are tracked separately, and both are ahead of this list.**
>
> **[`SAFEGUARDS.md`](SAFEGUARDS.md)** — what Versio refuses, asks about or waits for.
> **Phases 1–8 are built and verified against real CODESYS**: the guard, the cancellation
> plumbing, the close and context-switch guards, the conflict gate, the git-write window, the
> auto-stash in both directions, and a swap that can no longer leave a project with no mirror.
> **Phase 9 is what remains** — a real import confirmation, a backup restore path, and the
> read-only second instance the code already promises in a sentence. §0 is the handover note.
>
> **[`GIT-PANEL-PLAN.md`](GIT-PANEL-PLAN.md)** — the git panel's functionality and UX.
> **Eleven of its twelve phases are built**: the git presence probe and identity preflight,
> notifications, the remote state machine and publish, credentials, background remote polling,
> per-file staging, the one commit button, conflicts and the way out of a divergence, branches
> and their deletion, and — both on 2026-08-14 — **PLC-aware naming**, which is the item that
> document itself ranks highest and the one no generic git client can have, and the
> **merge-request button** across five forges. **What is left** is GP-K/GP-L: the ahead/behind
> previews and the rest of the small UX list.
>
> *This paragraph said "nothing in it is built" for a day and a half after nine phases had
> shipped, because it was written once and the header above it was re-derived without it. The
> same shape as the hand-maintained counts below: a sentence about state, in a document nobody
> re-reads from the top.*
>
> `SAFEGUARDS.md` also carries a correction worth reading before touching the sync engine:
> **`On git action` is not the default sync mode** and had not been since 2026-08-12. Exports
> follow a detected change, because the mirror exists to be reviewed *before* a commit. The
> commit gate is a backstop, not the trigger.

### 1. ~~Export what is currently missing~~ — **done**

The coverage report named it, which is what turned it from a rumour into a task. On the
fixture: two library managers, **both exported now**, and `_coverage.md` reports **0 gaps**.

- ~~Walk the library manager through the ScriptEngine and write the YAML~~ — `references`,
  iterated; one file per manager, beside the object that owns it (the plan's single
  `config/libraries.yaml` was reversed: a real project has two managers, see §5)
- ~~A YAML writer by hand~~ — `versio_yaml.py` writes it, `LibraryCatalog` reads it, neither
  side has a dependency. About 350 lines for both halves, and the format is deliberately tiny
- **Still open:** one tree node **per library**, so a version bump reads as *that library*
  rather than as "the library manager changed". The data exists and parses now; this is a tree
  feature rather than an exporter one

### 2. ~~Finish the git surface~~ — **done**

- ~~Recent commits with click-to-diff~~, ~~per-hunk staging~~ — **done**. History has since
  moved to a **panel of its own** with per-file diffs and lazy loading throughout; see the
  finding on what the old list cost on every refresh
- **Stash is withdrawn from the UI**, not finished. `StashPushAsync` / `StashApplyAsync` /
  `StashDropAsync` and the list parser are kept, tested and unused: what was wrong was six
  permanent controls in the narrowest column for something reached a few times a month. A
  different design is owed
- ~~Branch creation~~ — a dialog that validates the name **as it is typed**, against git's own
  rules, and creates-and-switches. `BranchName` is 35 unit tests in Core, because the useful
  half is the *reason* a name is refused, and git's own refusal arrives after a process spawn
  in wording written for a terminal
- ~~Clone with verification~~ — URL, **Verify** (`ls-remote`: reachability *and* credentials,
  transferring nothing), destination, then Clone with progress. **Clone stays dead until
  Verify passes**, and editing the URL afterwards shuts it again — the failure the dialog is
  shaped around is verifying one remote and cloning another. `GitCliClient.CloneAsync` is
  static and deliberately not on `IGitClient`: a clone has no repository to run in
- ~~Discard~~ — per **file**, never per list, after a confirmation naming the file. An
  untracked file is refused with an explanation rather than attempted, because git has nothing
  to restore it from. Discarding inside a mirror releases that binding's direction lock
- **What is left:** the branch dropdown still does not refuse a switch while a direction lock
  is held (plan §9.2 asks for that, with a tooltip saying why). Git refuses a dirty-tree
  checkout on its own, which covers the dangerous case but not the explanation

### 3. Retry automatically when the IDE closes  ← **start here**

Export and import both handle an open project now. The one piece left is the trigger: the
vendor's lock file disappearing is a filesystem event the watcher could see, which would make
closing CODESYS the retry and need no button at all.

Today the next save retries, and reopening the repository retries — the catch-up export runs on
open. What is missing is the case in between: the IDE is closed while Versio stays open, and
nothing notices until the next save. The panel says so rather than going quiet, which is what
`StalenessSummary` is for.

### 4. Finish the project tree

- ~~**Methods and properties under the object that owns them**~~ — **done.** Manifest schema 4
  records each member's owner GUID and the tree nests by it; see the finding. A mirror
  exported before this stays flat until its project is next exported
- **One node per library**, under its manager, parsed from the exported YAML. `LibraryCatalog`
  already reads the file; what is missing is the tree builder taking a catalog and the shell
  handing it one. This is the first piece of the plan's "config nodes parsed from YAML", and
  the only one whose data now exists
- A layer switcher in the diff pane (`ST | native`) — objects already carry several layers and
  there is no way to see the other one
- Context menu, and expansion state persisted per repository. It already survives a refresh;
  it does not yet survive the window closing

### 5. Diff view

Side-by-side mode, and word-level intra-line diff.

### 6. A second vendor

The seam is ready and unproven. The first real implementation is the only thing that will show
whether `PlcJob` and `PlcResult` are the right shape — TwinCAT is the cheapest test of that,
being CODESYS-derived, and Studio 5000 the harshest.

### 7. Packaging

- ~~Application icon~~ — **done.** `assets/versio.ico`, 8 sizes, generated by
  `tools/New-VersioIcon.ps1` and embedded via `<ApplicationIcon>`
- `third_party/` vendoring with SHA-256 pinning, offline restore
- Velopack: `Setup.exe`, MSI for managed sites, delta updates
- Code signing — **procurement lead time, not engineering time; start early**

### 8. Corpus breadth

More fixture projects. Every field bug becomes a fixture before it is fixed.

---

## Known loose ends, from an audit of the repository

Small, specific, and none of them blocking. Written down because an audit that finds things and
does not record them has to be run again.

- **Cancelling a running toolchain job is not implemented.** A change arriving mid-run is
  queued, which is correct and cheap; restarting instead would be better when the run has
  barely begun, and needs process cancellation plus cleanup of the staging folder a killed
  export leaves behind.
- ~~**Orphan findings are computed and shown nowhere.**~~ — **shown.** `FindingsSummary` is a
  line in the project panel, in the same slot and the same red as the coverage warning, and
  hidden when there is nothing to say. One sentence for one finding; a count and the first one
  for more, with the detail still in the log. They were surfaced rather than deleted because
  every one of them is a repository in a state Versio cannot resolve on its own.
- **`ToolchainState` is a public observable property with no view.** It survives because the
  runner uses its text as the "why this is unavailable" explanation. It should be a plain
  string, not a bindable one.
- **The branch dropdown does not refuse a switch while a direction lock is held** (plan §9.2).
  Git refuses a dirty-tree checkout on its own, which covers the dangerous case without
  explaining it.
- ~~**Adoption writes no line-ending rules to `.gitattributes`**~~ — **measured, and fixed.**
  The inference in this bullet was wrong: git reports such a clone as *clean*. What breaks is
  Versio's own hash gate, which reads the bytes on disk. Adoption writes `<mirror>/** text
  eol=lf` now; see the finding for the measurement both ways.
- **Hand-maintained counts in these documents drift.** The commit count was wrong by twelve,
  the project tree's test count by six, and the scenario suite's by twenty-two — all of them
  written once and never re-checked. Re-derive rather than trust: `git rev-list --count HEAD`,
  and `dotnet test --filter FullyQualifiedName~<TestClass>` for a per-area figure.
- **One integration test is flaky under full-suite load** — see the finding below. Diagnosed,
  two fixes attempted and reverted, left alone deliberately.
- **`RefrigeratorControl.~u` is in pushed history.** Untracked and ignored now, but the commit
  that carried a username, machine name and pid to `origin/main` is still there. Removing it
  means rewriting a pushed commit — the owner's call, not a maintenance task.
- **Only four windows are constructed by a test**: the two prompt dialogs, `MainWindow` and
  `SystemInfoWindow`. That is currently all of them, and the rule worth keeping is that **a new
  window ships with a test that constructs and measures it** — twice now a window has been added
  without one and twice it threw in front of somebody. The next `Popup` needs the same: its
  content is not built until it opens, so a test that constructs the parent does not reach it.

---

## Findings

Written down with their evidence, because the reasoning is worth more than the fix.

### A rule added after a repository was adopted never reached it

Found while checking the previous finding against the reporter's own repository, which is the
only reason it was found at all. Their `.gitattributes` had the two project rules and nothing
about the mirror; their `git config core.autocrlf` said `true`; and `git check-attr` on an
exported `.st` answered `text: unspecified`. The fix written an hour earlier would never have
reached them.

`AutoAdoptAsync` skips a project that is already bound — correctly, since adopting twice is not
a thing — and adoption is the only caller of the two rule planners. So the rules a repository
gets are the rules that existed on the day it was adopted, forever. Three have been added since
this repository started tracking its own fixture: the mirror's `text eol=lf`, and the staging
and trash directories the swap moves through.

Every scan now tops up the rules for **every** binding, not only the ones just adopted. Both
planners already leave an existing rule alone and report no change when everything is covered,
so it writes once after an upgrade and never again — pinned by a test that opens the same
repository twice and compares the bytes.

This is the third time in this session that a rule was correct and unreachable, and the shape
is always the same: the code that decides is fine, and nothing calls it for the case that
matters. The other two were a guard intent nothing asked about and a filter nothing supplied
globs to.

### The mirror arrives with the wrong line endings, and git says it is fine

The loose end this document has carried since the audit read: *"Adoption writes no line-ending
rules to `.gitattributes`. On a machine with `core.autocrlf=true`, a cloned mirror may read as
entirely modified. Measure it before fixing: the repo's own rule is that a plausible inference
is not a measurement."*

Measured, and **the inference was wrong in a way that matters more than being right would
have**. Against real git, a scratch repository, `core.autocrlf=true`:

```
$ git -c core.autocrlf=true clone origin clone-crlf
warning: in the working copy of 'Main.st', LF will be replaced by CRLF …

$ git status --porcelain          # in the clone
                                  # ← nothing. The tree is clean.
```

Git is perfectly happy: it normalises on the way back in, so nothing reads as modified and no
diff appears. **Versio is not**, because the exporter writes LF and the hash gate hashes the
bytes on disk:

| | |
|---|---|
| `origin/Main.st`, as exported | `6fb69762e1887a3f…` |
| the same file in a `core.autocrlf=true` clone | `c58f937fb0ab1dae…` |

Every file in a teammate's fresh clone hashes differently from what an export of the same
project would produce. The engine reads that as a mirror somebody has edited by hand — the one
state that makes the text side authoritative and points the sync the wrong way.

Adoption writes `<mirror>/** text eol=lf` now, beside the `binary -diff -merge` rules for the
project files. It is the opposite rule for the opposite reason, and they belong in the same
file: a project must never be line-merged, and a mirror exists to be. Proven the same way it
was found — with the rule in place, the same clone with `core.autocrlf=true` still on lands
byte-identical to what was committed, `6fb69762…` on both sides.

An existing rule for the same pattern is still never rewritten, so a repository that has
already decided something about its mirror keeps its decision.

### The auto-stash swallowed its own record, and `.gitignore` could not have saved it

Found by the second test written for the flow, not by reading it. Switching away twice stashed
the work correctly both times — and the second stash took `.versio/local/autostash.json` with
it, because a repository with no adopted project has no `.gitignore` rule for that directory,
so `--include-untracked` counted Versio's own record as the user's work.

The consequence is worse than untidy. That record is the **only** thing that knows which
stashes are Versio's and which branch each belongs to, so stashing it away leaves real work
sitting in `git stash list` with nothing pointing at it — and a later `pop` would restore the
*older* record over the newer one.

**`.gitignore` is the wrong instrument for this**, which is the part worth keeping. It is a
tracked file: a stash carries the rule away at exactly the moment the rule is needed, and
writing it means editing a file the user commits and shares. `.git/info/exclude` is outside the
working tree — it cannot be stashed, committed or conflicted — and it is per clone, which is
precisely what this rule is. It is written when a repository opens, before anything reaches
`.versio/local/`. Adoption still writes the `.gitignore` rule, and that is not redundant: that
one is what a *teammate* gets when they clone.

The same gap applied to the direction locks in any repository where no project had been adopted
yet. Nobody had hit it because a lock only exists once there is a binding, and a binding only
exists after adoption has written the ignore rule — the auto-stash is the first thing Versio
writes into a repository *before* adopting anything.

### The IDE's heartbeat was a `git status` every five seconds, forever — and it took two fixes

Found while checking that the log was quiet after the export-echo fix. It was not: with a
repository open and nothing happening, the app ran a `git status` roughly every 2.5 seconds and
wrote a Debug line for each.

The cause is not Versio's own timers. `_deferredTick` fires every five seconds and does nothing
when nothing is deferred, and no watcher event had settled at all. It is the **CODESYS IDE**:

```
ObjectOrientedProgramming(Extended).~u   14:00:07
… six seconds later …
ObjectOrientedProgramming(Extended).~u   14:00:12
```

CODESYS rewrites its lock marker every few seconds for as long as it holds the project. Each
rewrite is a working-tree event, each event refreshes the status, and each refresh spawns
`git.exe`. **The `.~u` file is gitignored** — adoption writes that rule itself — so it cannot
change what `git status` reports, ever. The work is guaranteed to produce the same answer.

The measurement that matters is that this is not an edge case: **an engineer with the IDE open
is the normal state of this tool**, and in that state the app spawns ~24 git processes a minute
and writes ~1,400 log lines an hour, against a design goal of eleven.

Two things make the fix tractable rather than speculative, and both already exist:

- **Versio knows which paths it told git to ignore.** Adoption writes `.versio/local/`,
  `<name>-AllUsers.opt` and `<name>-<user>-<machine>.opt` into `.gitignore`, so those are
  already a list rather than a guess.
- **The vendor seam already answers where the lock marker is.** `IPlcProjectFormat` reports it
  — that is how open-project detection works — so the rule stays vendor-neutral: *a path the
  format declares as its lock marker is not a working-tree change.*

**Fixed, and the fix was wrong twice before it was right** — which is the part worth keeping.

`RepositoryChangeFilter` learned about paths **Versio itself put in `.gitignore`**, supplied by
the vendor seam: `TransientPatternsFor` is the same call adoption makes when it writes the
rules, so what the watcher ignores and what git ignores cannot drift apart. That is the one
exception to this class's standing rule that working-tree paths are never filtered by name, and
it is safe for a specific reason — these are not names guessed to be ignored, they are the rules
this app wrote.

Then the app was relaunched and **it polled exactly as before.** Watching the repository
directly for 20 seconds said why:

```
Samples\ObjectOrientedProgramming(Extended).~u~   7
Samples                                          4
Samples\ObjectOrientedProgramming(Extended).~u    4
```

CODESYS does not write the marker in place. It writes `.~u~` — trailing tilde — and renames it
over `.~u`, so the glob covered the less frequent of the two, and **`.~u~` was in nobody's
`.gitignore`**: a file appearing and vanishing in the working tree that git had never been told
to ignore. The pattern is `.~u*` now, which fixes the ignore rule as well as the noise.

Relaunched again, and **it polled exactly as before.** The remaining trigger was the third row:
a folder's own timestamp moves when a child appears or vanishes, so filtering the child by name
left the parent's event arriving at the same rate. Dropped by the same argument the bare `.git`
event already used — the folder's event says only that *something* changed, and the child's own
event says what. A creation, deletion or rename of a folder is not a write and still gets
through.

| | |
|---|---|
| Before | ~2 `git status` per 5 s, 493 log lines in one 50-second window |
| After | **1 `git status` in 45 seconds of idle** — the one at startup |
| Still detected | a new file, in 0.5 s; its deletion, in 0.5 s; both verified in the running app |

What remains over a longer watch is the **20-second backstop tick** — 24 calls in eight idle
minutes, one every 20 s to the millisecond — which is `StatusRefreshPolicy` doing exactly what
it was written to do, since `FileSystemWatcher` drops events silently and a panel that is 20 s
stale beats one that is stale forever. It is worth stating because this document elsewhere
claims an idle app writes about eleven lines an hour: with a repository open it writes about
180, all of them the backstop's own `git status`. That is a design decision, not the bug that
was just fixed, and it is the next thing to look at if idle cost ever matters again.

The lesson is the one this document keeps recording in different clothes: **the rule was correct
in unit tests while the app still spawned git every five seconds, twice, for two different
reasons.** What closed it was a real `RepositoryWatcher` over a real directory, replaying what
CODESYS actually does — write, rename, repeat — and asserting nothing comes out, then that a
real edit beside it still does.

### An export woke the change detector once per file it had just written

Noticed in the log while checking something else, on a real project with 56 exported files: for
about forty seconds the app ran a `git status` and hashed the whole mirror roughly twice a
second, with git invocations overlapping each other — `still running` — and each one ending in
a `BlockedByLock` warning.

The mechanism is one line in the log above the storm:

```
watcher error on …\ObjectOrientedProgramming(Extended) (Access is denied.) — events were lost
```

The export stages into `<mirror>.versio_staging` and **swaps the directory into place**, which
invalidates the `FileSystemWatcher`'s handle on the directory it was watching. The error
handler does the right thing — an overflow means events were lost, so rescan everything — and
the rescan then finds all 56 files changed, because they are new files, and reports each one as
a settled change. Every one of those became a sync job: a conflict probe (a `git status`
process) and a full-tree hash, before the direction lock refused it. **n files cost n status
processes and n full-tree hashes.**

`SelfWriteSuppressor` exists for exactly this and could not do it. It registers *one* path with
*one* expected hash, consumed by the first matching event, and `ExportAsync` registers it
**after** the run completes — tens of seconds after the swap those events describe. One late
registration against dozens of echoes. It had been that way since the suppressor was written,
and it never showed up because **the direction lock was quietly doing its job**: an export only
runs while the project side is authoritative, and a mirror edit is refused in that state anyway.
Nothing was ever lost. It was pure cost, and it is quadratic in the size of the project — worst
exactly where projects are large.

The fix is a window rather than a set of hashes, because that is the shape of the problem: the
mirror is mid-write for a known stretch of time and every event in it is ours.
`BeginOwnWrite(bindingId, side)` is the same mechanism as the git-write window (§5.A), with two
deliberate differences:

- **Per binding, and per side.** A checkout rewrites the whole repository; an export rewrites
  one mirror. Holding every binding off for the 20–31 s a toolchain run takes would drop real
  edits elsewhere, and an engineer saving in the IDE *during* an export must still be seen.
- **Opened before the run and closed after the bookkeeping**, because the echoes are raised by
  the swap in the middle and settle whenever the watcher's quiet period lets them — which is
  long after the toolchain has exited.

Nine tests, five of which fail with the gate reverted. The one that matters is the ninth: it
drives a **real export** through the orchestrator and asks, from inside the run, whether the
window is open — every other test opens it by hand and would pass against an export that never
called it. That is the mistake this repository has now shipped three times, and it fails when
the call is removed.

Not fixed, and worth knowing: the watcher's response to a lost-events error is still to report
every file in the tree individually. The window makes that free for our own writes; a genuine
overflow — a network share, antivirus — still produces one job per file.

### The repository dropdown could not do the one thing it exists for

Reported from the app, with a screenshot of the control open and holding exactly one row:
*"the repo drop down is supposed to store recent repos… currently it doesnt store anything."*

`RecentRepositories` was an `ObservableCollection` on the view model, appended to when a
repository opened and dropped when the process exited. Nothing wrote it anywhere and nothing
read it back, so on every start it contained precisely the repository the app had opened by
itself — and a control whose entire purpose is jumping between repositories could not jump
anywhere. It had looked right in every session where somebody opened a second repository by
hand, which is every session it was ever demonstrated in.

Underneath it was a gap rather than a bug: **Versio had no machine-level state at all.** Every
store it owns belongs to a repository and lives in that repository's `.versio/`. Which
repositories this user has opened is not repository state — it names paths that exist on one
disk — so there was nowhere for it to go. `%LOCALAPPDATA%\Versio\settings.json` is that
place now, beside the logs, written through the same atomic temp-and-replace `JsonLockStore`
uses and read with the same rule: unreadable costs a dropdown's contents and nothing else.
The theme is the next thing that should join it — it is currently reapplied as `System` on
every start, which is a preference the app forgets just as thoroughly.

Worth pairing with the finding below about *Open logs folder*: writes under `%LOCALAPPDATA%`
are redirected when the app runs from an MSIX/AppContainer, so a packaged build and a plain one
can end up with two separate recent lists. Nothing to fix today — packaging is unbuilt — but it
is the same environment fact, and it now applies to a second file.

Three decisions in it are worth keeping:

- **The remote is recorded at open time, not when it is needed.** By the time it is wanted —
  offering a vanished repository back — there is no `.git` left to read it out of. Promoting an
  entry inherits the old URL when the new read comes back empty, so opening a repository while
  its remote is unreachable cannot erase the only thing that could restore it.
- **A missing repository is a question, not a refusal.** Every row in that dropdown is a
  repository the app told the user they could come back to, so one that has vanished offers
  *clone it again*, *remove from list* and *cancel* — where the generic path says "not a Git
  repository" and opens a folder picker. Which options exist is a pure function of whether a
  URL was recorded, because a *Clone it again* button on a repository that never had a remote
  is a button that cannot work.
- **Nothing is removed on discovery, and nothing deletes a folder.** A disconnected network
  drive and a deleted clone are indistinguishable from here, and quietly forgetting the second
  also forgets the URL that could bring it back. The label says *Remove from list*, never
  *Delete*, because deleting the folder is what the user is afraid the button does.

One existing test had to change rather than being made to pass: `A_repository_that_cannot_be_
opened_leaves_the_dropdown_where_it_was` asserted that a dropdown row which will not open
raises `RepositoryRejected`. It now asserts the missing-repository question instead — the
guarantee it was written for, that the dropdown never points at a repository that did not
open, is unchanged and still asserted.

Startup gained a third fallback while this was wired: an explicit path argument, then the
working directory *if it is inside a repository*, then the most recent repository still on
disk. Launched from a desktop shortcut the working directory is wherever Windows put it, and
the app opened on an empty window beside a dropdown that had forgotten everywhere the user had
ever been.

Five of the seven new view-model tests fail when persistence or the missing-repository check is
reverted, and they come in through `OpenAsync` and `SwitchRepositoryAsync` rather than through
the store — this repository has been bitten three times by a method that was perfectly tested
and called by nothing.

**And the first version of this shipped a bug of its own, caught by running the suite.** The
view model defaulted to the real store, so every test that opened a throwaway repository
rewrote the developer's own recent list: two `versio-reset-…` temp folders were sitting in
`settings.json` when it was found. The default is a store that remembers nothing, the real one
is wired by the application host and nowhere else, and there is now a test that opens a
repository through the default constructor and asserts the user's settings file is byte-for-byte
unchanged.

### One field's type disagreed, and the panel lost fifty-six objects without saying so

Reported from the app against a real customer project, with a screenshot of the CODESYS Devices
tree beside a screenshot of Versio: *"is not rendering at all in a correct format."* The panel
showed `0 object(s) · 60 changed file(s)`, `coverage unknown`, and sixty rows of raw
`Device/Plc Logic/BuildingApplication/…` paths where a tree belonged.

`versio_export.py` has always written `warnings` as records — `{"code": …, "path": …,
"detail": …}` — and `ProjectManifest.Warnings` was declared `IReadOnlyList<string>`.
`JsonSerializer.Deserialize` threw on the first record, `Parse` caught the `JsonException` and
returned `Empty` exactly as its own documentation promised, and **one field's type disagreeing
discarded all fifty-six objects**. Everything downstream then behaved correctly on an empty
manifest: no object nodes, so every exported file fell through to the unmapped-file list as a
raw path, and `ExportCoverage.Of` reported schema 1 and answered "unknown".

Three things had to line up for this to survive a whole feature:

- **The fixture produces no warnings.** `RefrigeratorControl` has none, so `"warnings": []`
  is what every test used — and an empty array is valid for both shapes. `_manifest.json` is
  written by IronPython inside CODESYS and read by System.Text.Json, and nothing on either
  side of that boundary had ever seen a non-empty one.
- **The property was read by nothing.** `Warnings` was populated, exposed, and consumed
  nowhere in the app, so no test, no view and no log line ever looked at it. It could only
  ever cost something, never earn it.
- **The failure was silent by design.** Returning `Empty` for an unreadable manifest is the
  right instinct — the window must not come down over a convenience file — but `Empty` is
  indistinguishable from a manifest that genuinely describes nothing, so the panel had nothing
  to report and reported nothing.

Fixed in three parts, and the third is the one that matters. `ExportWarning` is now the record
the exporter writes. `Parse` reads **field by field and element by element**, so a field this
build cannot make sense of costs that field and leaves the rest standing — a killed export that
truncates one object record loses one row rather than the tree. And `Parse(json, out problems)`
names what it skipped, which `SyncViewModel` logs at Warning, gated by the tree signature so a
broken export says so once rather than per refresh.

Verified against the reporter's own manifest: `schema=3 objects=56 skipped=4 warnings=2
problems=[]`, coverage `CompleteByDesign` rather than `unknown`. Seven new tests, each checked
against the mutation that should break it — reverting to a whole-array parse fails the
per-element test, removing the per-field `catch` fails the per-field test.

### A function block's methods were drawn as its siblings, and four InitRooms sat in one folder

Same report, one level down. CODESYS draws `clsLight (FB)` with an expander and its methods
inside it; Versio drew `clsLight`, `Light`, `SetLifeTime` and — in the rooms folder — four
separate rows called `InitRoom`, with nothing saying which room each belonged to.

The export writes a member **beside** its owner, as `clsLight.Light.xml` next to `clsLight.st`,
so ownership was expressed in a filename and nowhere else. The manifest recorded where a
method's *file* sat and not what the method *belonged to*, and a tree assembled from paths
therefore could not have got this right.

The fix is manifest **schema 4**: `record()` writes the owner's GUID as `parent`, and the tree
builder nests by it. A GUID rather than the owner's name or its path, for the reason identity is
a GUID everywhere else here — a rename must not re-parent anything, and reading ownership back
out of `clsLight.Light.xml` is a guess that any name containing a dot defeats. It is a chain
rather than one level, because a property owns its accessors.

Three consequences were not obvious and are each pinned by a test:

- **`PruneUnchanged` tested `IsFolder`.** Objects had no children until now, so an unchanged
  function block was treated as a leaf and dropped — taking an edited method with it. It tests
  for children now.
- **The search had to move after the nesting.** Filtering leaves first would match a method and
  drop the block that leads to it, leaving the match unreachable. A node that matches keeps all
  its members; one that does not survives only for the members that do.
- **A cycle in a manifest is possible.** It is a file on disk and can say anything, and an
  object claiming its own descendant as its owner would recurse forever inside a background
  build — which presents as the panel simply never appearing again. The walk carries a visited
  set.

An older manifest carries no parents, and that reads as a flat project rather than as an error:
a mirror starts nesting when its project is next exported. Verified by exporting the reporter's
project with real CODESYS — `schema=4 objects=56 withParent=22`, every parent GUID resolving to
an object in the same manifest, `InitRoom` owned by `clsBetterRoom`, `clsCellarRoom` and
`clsCheapRoom` respectively.

The committed fixture was re-exported rather than hand-edited, and the fresh export was compared
byte for byte against the committed one: **`_manifest.json` alone differs, by the schema line**.
That is also the strongest determinism check the export has had — eleven files, two runs,
months apart, identical.

**The corpus is the finding underneath both of these.** One fixture project, with no members and
no warnings, cannot see either bug. Every field report should become a fixture, and this one is
the first candidate.

### Two of the three panels used half the card they sat in, because Dock defaults to Left

Reported from the app: *"the git still, notice how is not using the full panel."* The git panel's
rows stopped about 310 px into a 557 px card, leaving a column of dead surface down the right of
every row in it.

One default did it. The card held a `DockPanel`; **`DockPanel.Dock` defaults to `Left`**, and only
the last child fills. The three panels are listed git, history, project — so the *project* panel
filled the card and the other two were docked left at whatever width their content happened to
want. The git panel was 310 px wide because that is what a branch card and two button rows add up
to.

Nothing about it looked broken from the inside: nothing was misaligned, nothing overflowed,
nothing threw, and every test passed. It is a panel correctly sized to the wrong thing — and it
had been that way since the panels were split, hidden while the git panel still carried the stash
controls and enough width to look deliberate.

The card is a two-row `Grid` now: the title, then one filling row that all three panels share,
with exactly one visible at a time. The test arranges each panel *through its rail button* and
asserts the width the panel renders at equals the card's, which is the assertion that would have
caught this at any point in its life. Checked by pinning the git panel back to its content width,
where it fails.

### Two things overlapping, and a test suite that could not see either

Both reported from the running app, and neither was reachable by anything in the suite: it
constructed the window and measured it once, at a comfortable width. **An overlap is not a
failure in WPF.** Two controls sitting on top of each other measure and arrange perfectly well —
the layout did exactly what it was told.

**The log console ran underneath the rail.** The rail lived inside the body *row*, so it stopped
at the bottom of the panels, while the log console — a sibling row — spanned the full width of
the window beneath it. Two surfaces of the same `Surface.Base` meeting at a T-junction under the
rail icons, which is precisely what "the logs bar overlapping the left side bar" describes. The
rail is app-level navigation, so it now lives in the outer grid and spans the body and the log;
the log starts where the content starts. The status bar still spans the full width, which is
where a status bar belongs.

**A section label and its buttons shared one Grid cell.** `NOT STAGED` with `Discard` and `Stage
all`, `STAGED` with `Unstage all`, the panel title with its count badge, `COMMIT MESSAGE` with
its length counter, and `DIFF` with a checkbox and a 420 px path. A right-aligned child in a
shared cell grows *leftwards across* the label rather than pushing it, and neither knows the
other is there — so in a 380 px panel the label and the first button touched. Every one of those
headers is two columns now: the label takes what is left and trims, the buttons keep their width.

The tests assert **rendered geometry** — bounds transformed into a common ancestor, at a 1000 px
window — because that is the only form in which "these two things are on top of each other" can
be written down. They also select the git panel through the rail first: a collapsed panel
arranges to nothing, which would have made every assertion vacuously true. All three fixes were
checked by putting the old layout back, where each fails.

Two smaller things went with it. The branch card's *Fetch / Pull / Push* are compact: at the full
32 px they set the height of the whole card and pushed the file lists down for nothing. And
`Size.Rail.Width` is finally what sets the rail's width, instead of a hardcoded 52 sitting beside
an unused token.

### An editor that corrupted every em-dash in a file it was only reformatting

Not a design finding, and worth recording anyway because the damage was invisible in the diff
that mattered and glaring on screen: the commit-box placeholder rendered as
`Summary of the changeâ€¦`.

`MainWindow.xaml` was edited in a few places with PowerShell's `Get-Content -Raw` /
`Set-Content`, which reads with the system's ANSI codepage and writes UTF-8. Every one of the
file's 18 em-dashes and ellipses — all of them in tooltips and comments — round-tripped into
mojibake, and a BOM was added to a file that never had one. **Nothing caught it.** The build was
clean, all 116 tests passed, the binding tracer was empty, and XAML is perfectly happy to render
a corrupted string. It took a screenshot of the app.

Repaired by re-decoding: the file's only non-ASCII characters were the mojibake sequences
themselves, so reading the UTF-8 bytes back through CP1252 is exact rather than a guess. Verified
by counting `…` and `—` against the previous commit — 18 in both — and by checking the first three
bytes are `3C 57 69` rather than a BOM.

The lesson is narrow and worth keeping: **edit source files with an editor that preserves
encoding, not with a shell round-trip.** If a shell must do it, `[IO.File]::ReadAllText` /
`WriteAllText` with an explicit `UTF8Encoding($false)` is the pair that does not corrupt anything.

### The stylesheet's own argument, not applied to four controls

`Controls.xaml` opens by naming the four things that made the first pass look like a Win32 app:
visible borders on every control, the system highlight brush for selection, hard dividers, and
colour emoji. It then fixes all four — for the controls it retemplates. Four it never touched
kept every one of the problems the header describes.

**Checkboxes and radio buttons were WPF's own.** The settings popup's three theme radios, *Show
full file*, *+ untracked*, and every row of the category dropdown were drawing Aero's shaded
square and shaded circle — sized for a 2009 desktop, lit as though from above — on a dark
surface. Sixty lines each: a 16 px box with `Radius.Control` and the icon font's check glyph, a
16 px ring with an accent dot. The label is inside the hit area now too, so a click no longer
has to find a 13 px square.

**Both TreeViews painted selection in Windows' highlight blue.** `ListBoxItem` was retemplated
in the first pass *because* the system highlight is the loudest tell — and the project tree, the
surface somebody looks at all day, was not a ListBox. It was a TreeView, so it kept the stock
template: highlight-blue selection and the Aero expander triangle. When the history panel was
added it inherited exactly the same thing. `TreeRowItem` is what `RowItem` already is for the
lists — a tinted fill plus an accent bar — with a chevron rotated by a trigger rather than two
glyph keys that can drift apart.

Two details in it are deliberate and worth keeping:

- **`IsSelected`, not `IsSelectionActive`.** The stock template greys the selection out when
  focus leaves the tree, which happens the moment somebody clicks a file to read its diff — so
  the file whose diff is on screen looked unselected. The row stays lit here.
- **`IsExpanded` is not set by the style.** Both trees bind it two-way per row, and that binding
  is what drives the project tree's lazy children and the history's demand-driven file lists. The
  retemplate puts a `ToggleButton` on the same property, so the test that matters asserts the
  round trip in both directions: expanding a node in the UI must still reach the row, or the
  panels quietly stop loading anything and nothing else looks wrong.

**And a style can be right and unused.** The tree tests build their own `TreeViewItem`, which
proves the template and nothing about the app. A second test asserts both TreeViews actually
name it — because the project tree needs a repository open before it has a single row, so a
missing `BasedOn` would have shipped with every test still green. Checked by removing it from
one of the two, where it fails. Same for the implicit styles: they are keyed by type, nothing in
any view refers to them, and deleting one changes dozens of controls without a single lookup
failing.

### A top bar crowded into its left third, with two control heights in one row

Reported from use: *"the repo and branch bars should cover more real estate horizontally so it
looks more balanced, currently very heavy to the left."* Three separate causes, all visible at
once and none of them nameable without opening the file.

**One flexible column, in the wrong place.** Six columns, five of them `Auto`, and the single
`*` sat on the *branch buttons* — so on a 2560 px window the slack piled up in the middle while
both dropdowns stayed pinned at their hardcoded 230 px and 200 px. Two `*` columns now carry
the dropdowns, bounded with `MaxWidth` so a wide window widens them without handing one of them
half the screen, and a third `*` before Settings is what holds the right-hand cluster right.

**Two heights in one row.** `Size.Control.Height` was 28 and `IconButton` hardcoded 32, so every
dropdown in the bar sat four pixels shorter than the buttons beside it. That is the ragged
baseline, and it is the reason the bar read as unfinished even where the spacing was right. Both
read the token now, at 32, with `Size.Control.Compact` (24) named once for controls inside a
strip that is itself dense — the log header, the section-label rows — rather than a third value
picked per view.

The test measures both controls after `Arrange` instead of reading the styles, because two
controls can name the same token and still render differently if one of them also sets a padding
or a border. Checked by putting the old values back: it fails.

**The mark existed and was nowhere.** `assets/versio.svg` — three plates and a status badge — was
drawn for the `.ico` and never appeared in the app, which showed the word "Versio" in accent
blue instead. The geometry is now also `Theme/controls/Brand.xaml` at 20 px, and the SVG's header
names all three places that carry it. A `ControlTemplate` rather than a `DrawingImage`, and not
as a matter of taste: a `Freezable` in a resource dictionary resolves its `DynamicResource` once
and is not re-resolved when the palette underneath is replaced — the trap `ThemeManager` already
documents — so a `DrawingImage` mark would have kept the colours of whichever theme started
first.

**And accent was doing six jobs.** Brand, commit hashes, ahead/behind, watch state, the busy
caption and every category in a scrolling log. A colour that marks six things marks none of
them, so it is now spent on selection, the primary action, the count badge and the progress bar,
and the rest moved to `Text.Secondary` or `Text.Disabled`. The one that gained meaning rather
than losing colour is divergence: `BranchEntry.BrushKey` says `Status.Modified` when a branch is
*behind*, because that is the state a pull answers, and secondary when it is merely ahead —
which is normal and should not be painted like a problem.

### The app themed everything except the strip Windows draws, and every tooltip in it

Reported from use, and it is the first thing anybody sees: *"the top windows bar should follow
color theme, so if its dark theme then it goes dark as well."* A dark shell under a white
caption, with the app's own top bar — `Surface.Base`, an exact colour — immediately below it.

Two surfaces were unreachable from the stylesheet, for two different reasons:

**The caption belongs to the window manager.** No style, template or brush reaches the
non-client area; the only way in is `DwmSetWindowAttribute`. So the colours are handed over
directly — `CAPTION_COLOR`, `TEXT_COLOR` and `BORDER_COLOR`, with `USE_IMMERSIVE_DARK_MODE`
behind them for Windows 10, where the three colour attributes fail and dark mode is the whole
effect. `Surface.Base` is passed rather than settling for immersive dark mode alone, because
that gives a generic dark grey that is *close* to the top bar and not equal to it, and a seam
one shade off looks worse than an honest contrast.

**Tooltips are popups, and there are dozens of them.** Every one is a plain string on the
element it explains, and not one named a style — so every one was the stock WPF tooltip: a
near-white slab with a hard border, opening over a dark application. An implicit style dresses
all of them at once, including the ones nobody has written yet. That is also why the style is
pinned by a test: nothing in any view refers to it, so deleting it changes the look of dozens
of controls and no lookup anywhere fails.

**Two halves, and the second is the one that gets forgotten.** Painting on load is correct
until the user switches theme with windows open, at which point every caption keeps the colour
it was born with until the app restarts — the same shape of half-working theme switch already
recorded below, one strip further out. So `ThemeManager.Apply` repaints every open window as
part of the swap, and a test asserts it: two windows built, theme switched, both repainted.

**The reach is what is tested, not the paint.** The DWM call needs a shown window with an HWND,
and this suite deliberately shows nothing. What the tests assert instead is that every window
which loads gets to the painter — a plain `Window` and a real dialog, separately, because "it
works on the main window" is exactly how this fails — and they do it by raising `Loaded` by
hand, since a class handler fires on `RaiseEvent` whether or not anything is on screen. The
byte order gets its own test: COLORREF is `0x00BBGGRR`, and reversed, the caption comes out a
warm brown that reads as somebody's decision rather than a bug. The interop itself was
confirmed separately against the running app: all four attributes return `S_OK` on 10.0.26200.

**A class handler rather than four constructors.** `EventManager.RegisterClassHandler` on
`Window.Loaded`, registered once at startup, covers the shell, all three dialogs, the spike
window, and the fifth window that would otherwise have been the one somebody forgot. Two
surfaces remain honestly out of reach and are listed rather than hidden: `MessageBox` is a
Win32 dialog, and `OpenFolderDialog` is the shell's own.

### Twenty seconds of work with nothing on screen, and four separate reasons for it

Reported from use, and precisely: *"I save, the PLC tree immediately shows the changed file,
and after a few seconds the changes get highlighted. I'm assuming the export is running, but
there is no visual indication of this."*

The reading was exactly right. The `.project` appears the instant git notices it — about 300 ms
— and the diff people are actually waiting for appears when CODESYS finishes, 17–31 seconds
later. In between, the app looked idle while doing the most expensive thing it ever does.

What made it worth a full investigation is that **every mechanism for showing it already
existed, and all four were disconnected.** None of them was missing; each was wired to
something that could not deliver it.

| Mechanism | Why nothing appeared |
|---|---|
| The app-wide progress bar | `SyncViewModel` takes a `BusyTracker` and **defaults to constructing its own**. The bar binds to the shell's. So the bar never moved for *any* sync work — including the catch-up and commit-gate exports, which both carefully open a scope for it |
| `ProjectRow.Busy` and the `"exporting…"` state | Documented in the class — *"it says exporting… for 17–31 seconds"* — and **assigned by nothing**. `RefreshStalenessAsync` even guards with `!p.Busy`, a condition that could never be true |
| The heartbeat | `HeartbeatService.Track` was called from exactly one place, the commit-gate path. The automatic save path started no tracker, so `Report` found nothing to attach to and returned |
| The progress detail itself | `PlcJob.OperationId` defaulted to a **fresh random value**. The runner tags its phases and counts with the job's id; the heartbeat is keyed by the operation's. Two unrelated strings, so every phase the toolchain reported was dropped — on *every* path, including the one that did start a heartbeat, which could therefore only ever show elapsed seconds |

The last one is the most instructive: an end-to-end pipeline, complete at both ends, discarding
its payload at the final hop because two ids that had to be equal were each independently
correct.

**The announcement is a notification, not a wrapper.** It would be simpler for the caller to
put a bar around its call to `HandleChangeAsync`, and it would be wrong: the overwhelming
majority of calls are refused by a gate in milliseconds — a save that changed nothing, a
coalesced burst, a throttled repeat — and a bar flashing on every settle is worse than no bar
at all. `RunStarted` fires after the last gate, `RunFinished` from a `finally` so a failure or
a throw cannot leave the bar up. A bar that never goes away is indistinguishable from a hang,
which is the very confusion being fixed.

**The row is shown and marked, not hidden.** Hiding it until the export finished was the other
option and it is the wrong one: the `.project` really is modified in git the moment you save,
and a panel that disagrees with `git status` is how somebody concludes Versio lost their work.
One further defect fell out of choosing that: re-exporting a project whose file matches HEAD
made its row **vanish for the whole run**, because the "show what has something to review" rule
had nothing to show. The tree said so in its own log — `1 project(s) hidden as in sync` — while
CODESYS was running against it. A run in flight now counts as a reason to be visible.

### Two loop defences that had never actually run

Found while tracing the above. Both are in the coalescer, both were written correctly, and
neither had anything driving it.

**A queued follow-up was dropped, and the coalescer was left believing a job was running.**
`OnJobCompleted()` returns `true` to mean *a change arrived while that run was going and must
be handled now*, and sets the slot as running again on the strength of the caller obeying it.
The caller discarded the result. So the follow-up never ran — and worse, from that moment every
later change on that binding answered `Queue`, then `Coalesced`, for the rest of the session.

It stayed invisible because **the commit gate is synchronous and skips the coalescer entirely**,
so exporting still worked at the one moment anybody checked. The plan's worked example — four
saves during one export producing two exports — produced one.

**A throttled change waited for something that did not exist.** `TryStartThrottled()` is
documented as *"used by the idle tick"*; there was no idle tick and no caller anywhere. The
pending flag that "guarantees a final export once things go quiet" guaranteed nothing precisely
when things went quiet: the change waited for the next save, or for somebody to commit. There
is a five-second tick now, and the deferred job re-enters the whole pipeline rather than being
replayed, so the hash gate can say the reason to run has gone.

### "Cancel and restart" cancelled nothing, and ran two CODESYS instances instead

The most consequential of the three, and it was found by watching, not by reading.

A save arriving in the first five seconds of a run returned `CancelAndRestart` — *"cheap to
restart this early, and it avoids 40 s of work we know is stale"*. Sound reasoning, and the
mechanism was never built: **no cancellation is plumbed through to a running toolchain job**.
The orchestrator's only way to act on the instruction was to start a second export beside the
first.

Observed doing exactly that. One `git checkout` of the project file produced two settled
changes 1.8 s apart, both inside the window, and the log shows two exports running
concurrently against one project:

```
23:11:23.548  {4da031c5} Project change (automatic, Project)
23:11:25.351  {81e9666a} Project change (automatic, Project)
…
23:12:09.269  {4da031c5} exported 12 object(s) in 45.6s
23:12:32.748  {81e9666a} ExportSucceeded accepted
```

**45.6 s for a job that takes 17–31 s alone** — the two instances contending for the profile
and the shared temp state, which is the same contention already documented for the integration
suite.

The branch is gone. A change arriving during a run is always queued, and the `cancelWindow`
option went with it rather than being left as a knob that does nothing. Queueing costs almost
nothing in return: an export reads the file as it opens it, so a save landing mid-run is
usually captured by that very run, and the follow-up then finds nothing at the hash gate and
costs a file read instead of a toolchain launch. **Restarting is still worth having** — it just
has to cancel the process first, and killing CODESYS mid-export leaves a staging folder the
exporter already warns about. That is a separate piece of work, not a comment.

**The unit test passed throughout.** It asserted that the coalescer *returns* `CancelAndRestart`
in the window, which it faithfully did. A component tested alone cannot see what its caller does
with the answer, and "the caller cannot honour this verdict" is not a property any test of that
component can express. The test now asserts the property that matters and could have caught it:
**a second job is never started while one is in flight.**

### The history was re-read on every refresh, into a panel four rows tall

The commit list lived at the bottom of the git panel, under the stash, with `MaxHeight="150"`
in a 380 px column that also carried the branch card, the commit box, two file lists and six
stash controls. Four rows, and every one of them a single line holding a hash, a subject and a
relative age — the date and time the commit was actually made were not on screen anywhere.

What it cost is the part worth writing down. This ran on every manual refresh, every refresh
triggered by git's own metadata moving, and every branch change:

```csharp
await LoadBranchesAsync();
await LoadCommitsAsync();     // git log
await LoadStashesAsync();     // git stash list
```

**No part of that asked whether anybody was looking.** The rail could be on the project panel —
it is the default, and it is where the work happens — and a commit, a checkout or a fetch still
spawned two git processes to refill a list that was not on screen. The one interaction the list
did offer, clicking a commit, fetched the commit's *entire* patch, because whole-commit was the
only granularity there was. On a commit touching an exported CODESYS tree that is hundreds of
files in one string, to read one of them.

So history moved out to its own rail button, and everything about it is now demand-driven at
four separate levels: the log is not walked until the panel is first shown, and then one page
of fifty at a time; a commit's files are read when it is expanded; a patch is read when a file
is clicked. A refresh behind a hidden panel *marks* it and reads nothing — deferring the cost
without deferring the correctness, which is the only version of lazy that is safe here. A
commit that did not appear the moment it was made would look like a commit that had failed.

The file lists are cached and the patches deliberately are not. **A commit's hash is its
content**, so its file list cannot go stale and may be held for the session; its patches are
unbounded in size and are held only by the diff pane, one at a time. The same caching would be
wrong one panel to the left, where the working tree changes under you.

Three things surfaced while building it:

| Found | Why it mattered |
|---|---|
| `--name-status` quotes paths | Without `-z`, git renders `Kühlschrank Regelung.st` as `"K\303\274hlschrank Regelung.st"` — quotes and octal escapes included. This repository's own fixture corpus has both spaces and umlauts in its object names, so the panel would have asked git for the diff of a path that does not exist. The same class of bug as the mangled subprocess output already recorded here: an encoding assumption at a process boundary |
| *Show full file* would have swapped what the pane was answering | Its re-read keys off the shown path, which for a historical diff is a caption — `PLC_PRG.st @ 1a2b3c4` — not a path. Left alone it would have quietly re-read the *working tree* version of that file and rendered it under a header naming a commit. Caught before it shipped; the pane now records which source it is showing, and a refresh leaves a historical diff alone entirely |
| A collapsed panel passes the window test | `MainWindowTests` constructs and measures the whole window, and would still have passed with a broken `DataTemplate` in it: a `StaticResource` inside a template is resolved when a row is *realised*, not when the XAML is parsed. The new test switches to the panel through the rail button, fills it, expands a commit and asserts a real `TreeViewItem` came out with a height. Written the obvious way it would have been one more vacuous pass on the pile below |

The stash controls came out at the same time and are not replaced: six controls for something
reached for a few times a month, in the column that most needed the room. The client-side
operations — push, apply, pop, drop, and the list parser — are kept and still covered, because
what was wrong with the stash was the shelf it sat on, not the operations.

### A window that never opened, and three instruments that called it clean

The log panel gained a category dropdown - a `ToggleButton`, because it has to stay pressed
while its popup is open - wearing `GhostButton`, whose `TargetType` was `Button`. WPF refuses
that at the moment the style is set:

```
'Button' TargetType does not match type of element 'ToggleButton'
MainWindow.xaml line 1005
```

So `MainWindow`'s constructor threw, and **the application started, opened its log file, probed
the toolchain and never showed a window**. The build was clean and 883 tests were green while it
did that.

What makes it worth writing down is not the mistake, which is ordinary, but that **every
instrument in place reported success**, each for a different reason:

| Instrument | Why it said nothing |
|---|---|
| The compiler | XAML becomes BAML without resolving a single `StaticResource`. A style reference is a key until something asks for it |
| `Versio.App.Tests` | Covered the two prompt dialogs and the stylesheet. `MainWindow` was the one window nothing constructed - on the reasoning that it opens on every launch and could not go unnoticed |
| The resource-key tests | Assert `GhostButton` **exists**, which it did. The defect was a key that does not *fit* the element asking for it, which no lookup can see |
| `--trace-bindings` | Reported nothing, twice, and it was believed. **A window that never opens evaluates no bindings**, so the trace is empty for the same reason it is empty when everything is correct |

That last row is the thirteenth vacuous pass in this document, and the most embarrassing one,
because it was read as evidence *while the app was failing to start*. The diagnosis offered
first was also wrong: the empty trace was blamed on the MSIX filesystem-virtualization finding
already recorded here, on the strength of it being a documented gotcha in the same area. It was
not that - the app's own log was landing at the real path the whole time. **A documented
explanation that fits is still a guess until it is checked**, and checking cost one command:

```
MainWindowTitle  :
MainWindowHandle : 0
```

The fix is one word - `GhostButton` targets `ButtonBase`, so it dresses a `ToggleButton` and a
`Button` alike - and everything derived from it still targets `Button`, which stays legal.

The real fix is `MainWindowTests`: one construction of the real window, measured so templates
are applied, verified to fail on the reverted bug. It is the cheapest possible regression test
for the whole file - every `StaticResource` in `MainWindow.xaml` resolved on every run, in
200 ms - and it should have existed from the day the two dialogs got theirs.

### The same blind spot again, in the dialog added after it was written up

Found only because the crash above forced a real launch. `SystemInfoWindow` uses
`{StaticResource BoolToVisibility}` inside its row `DataTemplate`, and the converter was
declared in **`MainWindow.Resources`** - a different window, which cannot see it. Opening
Settings > Help > System information threw:

```
Cannot find resource named 'BoolToVisibility'. Resource names are case sensitive.
  at FrameworkTemplate.LoadTemplateXaml ... at FrameworkElement.MeasureCore
```

**This one was not introduced by the change that found it.** It shipped with the dialog, and
`Versio.App.Tests` covered the two prompt dialogs and not this one - so the lesson "a modal
nobody has clicked has never been parsed" was recorded, and then the next modal was added
without applying it. A blind spot named in a document is still a blind spot.

Two things it teaches beyond the fix:

- **A resource referenced from inside a template is not resolved until the template is
  applied**, which is Measure, not construction. A test that only constructed the window would
  have passed with the bug in place, so the tests measure and arrange.
- The converter now lives in the stylesheet, declared once, and `MainWindow`'s private copy is
  gone. A shared resource kept in one window's `Resources` is the same shape of bug as the
  private `Theme.xaml` merge that broke the theme switch - resolution finds it from one place
  and nowhere else, and the failure is invisible from the file that looks correct.

### An idle log that was mostly its own noise

Reported from use: the panel was drowning in `git status`. Measured from the log rather than
argued about - the status backstop runs every 20 s forever, and each run wrote **three** Debug
lines: the command, its duration, and a verdict of `status unchanged`.

```
17:29:29.287 DBG [Git] git status --porcelain=v2 --branch --untracked-files=all -z
17:29:29.333 DBG [Git] git status --porcelain=v2 --branch --untracked-files=all -z ok in 0.0s
17:29:29.335 DBG [Git] status unchanged (Backstop)
```

That is **about 540 lines an hour from an app with nothing to do**, and the entries that matter
scroll out behind them. The panel keeps 5,000 rows, so roughly nine hours of idling evicts
everything real.

Both halves were already trying to be quiet and both had picked the wrong lever. The verdict
line had been *demoted to Debug* - which is not the same as not writing it - and the command
pair had never been questioned at all.

- **A completed command needs one line, not two.** The pre-command line only ever earned its
  place for a command that hangs, so that is now the only case that gets it: after one second,
  `git fetch ... - still running`. Every routine call in this app finishes in tens of
  milliseconds, so in normal use it writes nothing.
- **A run of nothing is one fact, not N.** `QuietRunTracker` counts the quiet polls and says so
  every five minutes: `no change in 15 checks over 5m (12 Backstop, 3 WorkingTree)`. An idle
  hour goes from ~540 lines to 11.

**The per-trigger counts are the part worth keeping.** A *Backstop* poll finding nothing is the
definition of routine; a *WorkingTree* poll finding nothing means `RepositoryChangeFilter`
passed an event git does not care about, which is the only signal for tuning it - and it is
visible only in aggregate. A generic "collapse repeated lines" rule in the sink would have cut
the same volume and thrown that away.

Fourteen tests, and deleting the interval gate fails four of them, including both that assert
silence - the property most easily made vacuous, since a tracker returning `null` unconditionally
passes any test that only counts what it suppressed.

### The CODESYS lock marker was committed, and the ignore rule had guessed its name

Reported from use as "why is `control ~u` in my changes?". Three separate things, and only the
first is cosmetic.

**The rule matched nothing.** This repository's hand-written `.gitignore` carried
`*.project.~*`, which is the plausible reading. CODESYS **replaces** the extension:
`RefrigeratorControl.project` is held by `RefrigeratorControl.~u`. The product code has always
had this right - `CodesysProjectFormat.OccupancyMarkerFor` uses `ChangeExtension`, with the
measurement in its remarks, and adoption writes the scoped form for repositories it sets up.
Only this repository was wrong, because its file was typed by hand before that code existed.
Exactly the shape already recorded for `.versio/local/`: **a rule that is true only where
somebody arranged it separately.**

**The file was tracked, so the rule was irrelevant anyway.** `git check-ignore` reported no
match even after the pattern was fixed - ignore rules do not apply to tracked files, and
`git rm --cached` is the other half of the fix. Worth remembering: a corrected `.gitignore` that
appears to do nothing is usually this.

**It had been committed and pushed.** The marker's contents are a username, a machine name and a
pid, and it went to `origin/main` in a commit made while testing. That is the same harm already
documented for the `.opt` files, arriving by a route nobody had guarded. Removing it from
history means rewriting a pushed commit, so it is left as the owner's call - but the guard is
now in place for the next one.

### A panel about changed files, opening with a version number and two dead buttons

Reported from use, and correct on every count. The card at the top of the project panel was
four unrelated things wearing one border:

| What it showed | What it actually was | Where it went |
|---|---|---|
| Toolchain name, version, profile, status dot | Environment — identical for every repository, changing only when somebody installs something | Settings → Help → System information |
| "1 project, tracked." | A fact about the repository | The panel's own summary |
| "The project file is authoritative…" | State of **one** project | The project's row |
| *Export now*, *Acknowledge failure* | Two real actions, disabled almost always | Gone; see below |

**A permanently disabled button teaches people to stop looking at that corner of the screen** —
the same failure mode as a coverage line that congratulates itself on every refresh, which this
codebase had already argued about once.

*Export now* is gone because **nothing about a stale mirror needs deciding.** A project edited
while Versio was closed was never seen by the watcher, and the answer is always "export it", so
that now happens when the repository opens. The ordering is the guard, and it is the whole
feature: toolchain probed → orchestrator rebuilt and its **locks restored from the previous
session** → projects scanned → watcher started → staleness evaluated → *then* export. Exporting
a step earlier would run against a direction lock that had not been read yet. It goes through
the same `SatisfyGateAsync` the commit gate uses, so the lock, the hash gate, the breaker and
the open-project check all apply unchanged. Verified from the log of a real launch:

```
watching 1 tracked project(s) for changes          ← attach finished
commit gate: ExportRequired …                      ← staleness evaluated
catch-up: 1 project(s) changed while Versio was not running; exporting
…19.6 s… exported 12 object(s) → every tracked project is exported and up to date
```

*Acknowledge failure* is gone as a **panel-wide** control, not as a concept. The gate behind it
is deliberate and unchanged — a failed binding syncs no further until a person has seen the
failure — but a repository holds several projects and the panel could not say which one had
failed. The failure is now reported on its own row, and selecting it shows the toolchain's own
words with one button that clears the failure and retries. Acknowledging and retrying are one
button because they are one intention: two would mean clearing a failure and then wondering why
nothing happened.

One wording fix fell out of it. `SatisfyGateAsync` logged "exporting stale bindings before the
commit", which was true when only the commit gate called it and false for the two callers added
since — in a log read precisely when somebody is working out what the app did on its own.

### Recovery forgot what had been exported, and it cost a toolchain run every launch

Reported from use: the project sat in the panel reading "project ahead" with nothing changed
under it, launch after launch. The panel was telling the truth; the state behind it was wrong.

Recovery decides what to do with a lock left by the previous session. When it does **not**
re-adopt — the ordinary case, because the previous session ended cleanly — it replaced the lock
with a blank one. That blank threw away `LastSynced` along with the direction, and the commit
gate compares the project's hash against `LastSynced.ProjectHash`. An empty one reads as *"the
project has changed since it was last exported"* for a project nobody had touched.

The sequence, from one launch's log:

```
NothingToRecover — binding was clean; now Clean        ← LastSynced wiped here
tree: … 1 project(s) hidden as in sync                 ← still correct at this point
ExportRequired — the project has changed since it was last exported
catch-up: 1 project(s) changed while Versio was not running; exporting
```

So every open ran CODESYS for 17–31 s to rebuild a mirror that was already byte-correct, and
left the binding holding a fresh `ProjectAuthoritative` lock — which is why the row then read
"project ahead" and stayed until somebody pushed.

**Releasing the direction and forgetting what was exported are different facts**, and only the
first is recovery's business. The release now carries `LastSynced` and `Baseline` forward.

**It was latent for as long as exporting was a button.** Nobody pressed *Export now* on a
project they had not changed, so nobody saw the gate's answer. Making the export automatic on
open turned a bookkeeping error into twenty seconds of CODESYS on every launch — which is how
it got found, and an argument for automation surfacing bugs rather than creating them.

Verified across two consecutive launches after the fix: `catch-up: every mirror is up to date`,
no toolchain run, and `1 project(s) hidden as in sync`.

### The log said one time in the panel and another in the file

Two renderings of one instant. `LogViewModel.Time` called `ToLocalTime()`; `LogEvent.Format()`,
which is what the file sink writes, formatted the raw UTC stamp. So an entry read as 17:14 in
the app and 00:14 in the file — and the file is the copy somebody opens hours later, while
correlating against the CODESYS log and their own memory of when it happened. The file names
were UTC-dated too, so an afternoon's work in Pacific time lived in a file named for tomorrow.

Both now render local, and the file rolls on the **local** day so its name matches the stamps
inside it. `LogEvent.Timestamp` stays UTC: ordering, rolling and the tests all want one
unambiguous instant, and only the rendering was ever the problem.

**The first test written for it was vacuous, and only breaking the code showed it.** It built
the timestamp with a *local* offset — where `ToLocalTime()` is the identity and both renderings
are identical by construction. It passed against the bug it was written to catch. Rebuilt with
a UTC-offset stamp, as the real logger produces, it fails properly. That is the thirteenth
vacuous pass caught this way, and the second in two days from the same root cause: a test whose
input cannot distinguish the two behaviours it is comparing.

### The theme switch changed a colour nothing was looking at

Reported from use: the Light/Dark buttons in Settings did nothing. Two independent causes, and
the second one is the more interesting.

**One: the main window carried its own copy of the theme.** `MainWindow.xaml` merged
`Theme.xaml` into `Window.Resources`, and `App.xaml` merges it into `Application.Resources`.
WPF resolves upwards and stops at the first hit, so the window found its private palette and
never reached the application's — the one `ThemeManager` swaps. Every dialog, having no private
copy, followed the switch correctly, which is why the branch dialog opened dark over a light
window. The merge is gone: one theme, one place.

**Two: replacing the palette repainted nothing.** Measured with a probe rather than reasoned
about, and the numbers are the finding:

```
after Apply(Dark)   Color.Surface.Base = #FF1D2026      the palette did change
                    Brush.Surface.Base = #FFFFFFFF      the brush did not
                    window background  = #FFFFFFFF      nor did anything painted with it
```

`Semantic.xaml` defines its brushes as `SolidColorBrush` with `Color="{DynamicResource
Color.Surface.Base}"`, and the comment above `ThemeManager` said that is what makes the whole
window re-theme without a restart. It is not: a `Freezable` living in a resource dictionary
resolves its `DynamicResource` once, and replacing a *sibling* dictionary underneath it does
not re-resolve it. So the swap updated a colour that no live brush was reading any more.

**It had never worked, and it looked like it did** — because `Apply(System)` runs at startup
*before* any window exists, so the first resolution of every brush already sees the right
palette. Only a switch made while the app is running has anything to re-resolve, and that is
the one thing nobody had automated.

The fix rebuilds the whole theme dictionary and replaces it in `Application.Resources`, so
every brush is a fresh instance and every `DynamicResource` consumer in an open window is
invalidated. Four tests now cover it, and reverting to the palette-only swap fails three of
them.

### A diagnostic that verified an empty window

`Versio.exe --trace-bindings <file> C:
epo` never opened the repository. The startup code
took "the first argument that does not start with `--`" as the repository path, which is the
trace file.

The cost was not the missing repository — it is logged — but what it did to the instrument.
That flag exists to verify the UI, and every clean binding trace taken with it was a clean
trace over an **empty** window: no repository, no project rows, no git panel contents, so most
bindings had never been evaluated at all. A verification that quietly checks nothing is worse
than no verification, because it gets reported as a pass. Both of this session's earlier
"clean trace" runs were exactly that, and were re-run properly afterwards.

Flags that consume a value are listed rather than inferred: "skip whatever follows a flag"
breaks on `--spike C:
epo`, which is how the spike is actually run.

### A project with nothing to review stayed on screen

Also reported from use: a project sitting in the panel with no changes under it and a badge
reading 0.

It was doing what it was told. A project stayed visible when its sync state was anything other
than `Clean`, and the commonest state in the tool's whole life is `ProjectAuthoritative` — the
direction lock taken by a successful export and held until the work is **pushed**. So a project
that had been exported, committed and left alone sat permanently in a panel whose stated
premise is "everything with something to review", with nothing to review.

The rule now asks whether there is something to *do*: never exported, failed, conflicted, text
ahead (an import is outstanding), or stale. A held lock with everything up to date is a fact
about state, not a task, and it is already reported in the panel header in its own sentence.

### The UI had no automated coverage, and the first test written found a bug

`--trace-bindings` is a good instrument and it has one blind spot that matters: **it can only
report on windows that were opened.** A modal nobody has clicked has never been parsed, so its
XAML has never been checked against the stylesheet at all — a renamed style is not a compile
error, not a binding warning, and not anything until a user clicks the button that opens it.

Adding two dialogs made that gap load-bearing, so `Versio.App.Tests` exists now: a WPF test
project that constructs the real windows against the real `Theme.xaml`, loaded by pack URI from
the app assembly so it cannot drift from what ships. Deliberately breaking a style reference
fails **all ten** of the branch dialog's tests, which is the class of failure that would
otherwise have surfaced weeks later in front of somebody.

**The first run found a real defect.** `BranchPromptWindow` validated on `TextChanged`, which
never fires for a box's initial value, so the dialog opened with a **blank message area and a
dead Create button** — no explanation of what it wanted. Invisible by inspection, because a
blank label looks exactly like an empty one, and invisible to the binding tracer, because
nothing was bound. One call to `Revalidate()` in the constructor.

Three things had to be got right before any of it ran, and each fails in a way that names
something other than the cause:

- **an STA thread** — xunit runs tests on MTA threads, where a `Window` constructor throws;
- **an `Application` carrying the theme** — every `StaticResource` resolves by walking up to
  `Application.Resources`, and without one the XAML parse dies on the first style;
- **the *same* STA thread every time** — the theme's brushes are `Freezable`s owned by the
  thread that made them, so a fresh thread per test dies inside the XAML parser with
  `Dispatcher.VerifyAccess` in the stack and nothing pointing at the real problem. That was the
  first version of the harness.

### `async void` handlers and a test host that aborted

Driving the clone dialog by raising `Click` passed eight tests and then **aborted the test
host** — not failed, aborted, with no test named.

The handlers are `async void`, which is what WPF requires of an event handler. On the UI thread
an `await` posts its continuation back to the dispatcher, so raising a click only *starts* the
work: the rest runs whenever the dispatcher next pumps, which is during some later test, against
a window whose test has already finished.

Two changes, and both are improvements to the product rather than concessions to the test:

- the work moved into `VerifyAsync()` and `CloneAsync()`, leaving the `async void` handlers as
  one-line wrappers that start them. An `async void` that anything else calls is un-awaitable
  by everything, including the next feature that wants to reuse it;
- `DialogResult` is set through a guard, because it exists only on a window shown with
  `ShowDialog` and throws on any other. `ClonedPath` is set first regardless — that is the
  result; closing is how it gets delivered.

The harness gained a `Run(Func<Task>)` that waits **from the calling thread**, leaving the
dispatcher free to pump its own continuations. Waiting on it from inside itself deadlocks
instead.

### Validating a branch name is about the message, not the verdict

Git refuses a bad branch name perfectly well. It does it *after* a process spawn, in a sentence
written for a terminal — `fatal: 'feature branch' is not a valid branch name` — which arrives in
a status bar after the dialog has closed, naming neither the offending character nor what to
type instead.

`BranchName.Validate` is therefore in Core, with the rules transcribed from
`git check-ref-format` and a **reason for every refusal**: which character, or which rule. A
check returning a bare `false` would grey out a button and leave the user with nothing to
change — worse than no validation at all, because git would at least have said something.

It does **not** replace git's check, and the tests say so: a name that passes here can still be
refused for existing already, or for colliding with a ref path — `feature` and `feature/x`
cannot both be branches. The failed create is reported rather than assumed away.

The per-segment rules are the ones worth having a test for. `feature/.hidden` and
`feature/x.lock` are both refused by git, and neither is caught by looking at the name as a
whole: refs are paths, and git applies those rules to each component. Deleting the loop that
walks the segments fails five tests.

### The library reference id changes on every open, and the API is not the one it looks like

Two measurements, both cheap, both of which would have been expensive to discover later.

**The obvious API is the wrong one.** `libman.get_libraries()` exists and returns 21 items for
the fixture's application manager, which looks exactly like success. They are **strings** —
`"#Standard"`, `"#Util"` — with no version, no company, no flags and no parameters. The one
that carries the reference is `libman.references`, and it must be **iterated**, not indexed:
`references[i]` looks up by *id*, so `references[0]` raises on every project whose first
reference does not happen to have id 0. The first attempt read **zero** references out of a
manager holding 21, and reported no error at all, because the probe swallowed the exception.

**`ref.id` is re-minted on every open.** Two consecutive runs over an untouched project:

```
run 1   #Standard  id 1886582003        run 2   #Standard  id 1665927582
run 1   #Util      id 4015974643        run 2   #Util      id 1820137668
```

68 differing lines across the two dumps, and **every one of them was an id**. Nothing else
moved. So it is not recorded — the same decision, for the same reason, as the project-level
`__VisualizationStyle` GUID that broke export determinism hours after it was added. The
difference is that this one was caught by measuring *before* writing the exporter rather than
by a determinism test afterwards, which is the cheaper end of the same lesson.

**What resolution a reference gets is a fact about the machine, not the project.** The fixture
asks for `Standard, * (System)` and resolves it to `Standard, 3.5.22.0 (System)` — a wildcard
answered by whatever is installed here. Recording the resolved value would make the mirror
differ between two engineers sharing one repository, with neither of them wrong, and would move
the hash gate on a machine difference. The file therefore carries the **request** — placeholder,
default resolution, redirection, parameters, flags — and says in its own header what it leaves
out and why. `resolution_info` and `resolver_guid` go the same way.

Verified end to end afterwards: two full exports of the fixture, 14 files each, **byte-identical
across the whole tree**, and `_coverage.md` reporting **0 gaps** where it reported 2.

### A test that passed on broken code and failed on correct code

The reader for `*.libraries.yaml` is hand-written, and so is the writer, and they never meet in
one process. So the parser was broken deliberately to see which tests noticed — and the answer
was a genuinely useful surprise.

**Break: stop closing the nested `parameters:` mapping.** All 24 tests still passed. The test
that claimed to protect it asserted that parameters did not leak into the *next* reference —
which the list dash prevents on its own, whatever the mapping code does. It could not have
failed. The property that actually needs a parser is a **sibling key after the mapping**, and
the fixture cannot exercise it because the emitter writes `parameters` last.

Rewritten against a hand-written document with a key after the map, the new test **failed on
the correct code**: keeping one pending nested map and reading it at flush time discarded it the
moment a sibling key followed. The parser now stores nested maps into the item as they are
found. So the bug was real, the fixture could never have shown it, and the first test written
for it proved nothing — three separate ways to have shipped it.

A second break was more instructive than the fix: deleting a "close the open mapping" line
changed **nothing anywhere**. Indent already decided where a key belongs, and the line was a
second mechanism agreeing with the first — code no test could ever fail on. It was removed
rather than kept as defence, with the reason written where it stood.

And a third: the rule that drops a nameless half-entry survived being deleted, because
truncating the fixture never produces one — `name` is the first key of every item. It has its
own test now, from a document that does.

**The habit this reinforces:** a green suite over a committed fixture proves the fixture, not
the code. Every rule that only fires on input the fixture cannot contain needs a document
written to reach it.

### A log test that broke because the export got better

`ExportLoggingTests` asserted the swap line contained `"0 existing file(s) removed, 12 written
in their place"`, and the summary line `"walked 18, wrote 10, skipped 6"` with `"2 not
implemented"`. Exporting two more files turned a correct improvement into a failing string
comparison, which tells the reader nothing about what changed or whether it was wanted.

The numbers were there for a real reason — the counts have to *track reality*, and a swap that
printed the same pair twice would prove nothing — so they were not simply deleted. They are now
derived from the things they describe: the file count comes from the export directory on disk,
and the summary is checked against the `_manifest.json` **that same run wrote**. Both describe
one export, so they must agree, which is a stronger claim than any literal and one that does
not need editing the next time the exporter learns to write a file. The two properties worth
pinning by hand survive: the two swap lines must **differ**, and the fixture still walks 18
objects.

**The rule of thumb:** assert against another record of the same event, not against a number
typed into the test. A literal is right until the code improves, and then it is noise.

### The export's own narrative never reached the log

Asked directly whether the logs were sound for copying, exporting and replacing an export
directory, the honest answer turned out to be no — and the gap was invisible from the code,
because every line that existed was correct. The problem was the lines that did not exist.

**The most destructive step in an export had no log line at all.** The staging swap deletes the
entire export directory and renames a new one into its place:

```python
if os.path.isdir(out_root):
    shutil.rmtree(out_root)
os.rename(staging, out_root)
```

Nothing emitted, nothing logged. If it half-failed, the log would say the export succeeded and
the directory on disk would disagree, with no line anywhere connecting the two.

**And the script's protocol events were thrown away after being counted.** They drove progress,
the changed-path list and the warnings, and were then summarised as `events=24` — a number that
says something happened and nothing about what. So every skip, every object written and every
summary the exporter produced was absent from the log in readable form, in a codebase whose
stated rule is that a silent failure is a bug even when the code is correct.

Both are fixed. An export now reads:

```
replacing the export directory …\RefrigeratorControl: 12 existing file(s) removed, 12 written in their place
export directory now holds 12 file(s)
walked 18, wrote 10, skipped 6 (of which 2 not implemented), 0 warning(s)
not exported (NOT_IMPLEMENTED): Device/Plc Logic/Application/Library Manager [LIBRARY_MANAGER]
```

Levels are chosen by volume and by who needs them: one line per exported object is Debug,
because a real project has thousands of them; skips, the summary and the filesystem swaps are
Info, because they are bounded and each answers a question somebody asks afterwards. A leftover
staging folder is a **Warn** — it means a previous export was killed, which is worth knowing at
the moment it is discovered rather than never.

Two smaller ones went with it. Pruned project backups are now named as they are deleted, because
a backup that vanished with no line saying so is indistinguishable from one that was never made
— exactly the question being asked when somebody goes looking for it. And the skip path is
computed once and shared by the manifest and the log line, after the log showed
`Device\Plc Logic\Application/Library Manager` with both separators in one path.

The test asserts against the log a real export produces, and it asserts **both** runs: the first
removes nothing because there was nothing there, the second removes what the first wrote. A
count that did not track reality would print the same number twice. Verified to fail with the
swap emit removed.

### Recording a GUID broke export determinism, hours after it was added

`_coverage.md` shipped with the skip records carrying the object's GUID, which looked like free
information. It is not: **CODESYS re-mints the project-level `__VisualizationStyle`'s GUID on
every open.** Two consecutive exports of the same file:

```
run 2   09cd3618-c2e4-4e49-888a-02e46902a7b0
run 3   63754151-d4ae-45bd-9876-9d0db2d723eb
```

So `_manifest.json` differed on **every single export** — a spurious diff in every commit,
forever, which is precisely the noise the `Timestamp` scrub exists to remove. Reintroduced by
the report written about coverage.

**The determinism test added alongside it did not catch this**, because it compared
`_coverage.md`, and the report carries no GUIDs. A test written in the same hour as the feature,
against the same mental model, checked the half that could not fail.

Phase 0's gate 0.2 measured byte-identical exports across three runs **by hand**. Nothing in the
automated suite enforced it, which is how a determinism regression got committed at all. The
test now fingerprints the **whole export tree** across two exports of one project and compares
every file — verified to fail against the reverted fix before it was believed.

The GUID is gone from skip records. A skipped object has nothing pointing at it, so path and
type are identity enough; for an *exported* object the GUID stays, because there it is what
makes a move a move rather than a delete and an add.

**The general shape:** a value that is stable in every case you look at is not thereby stable.
This one was stable for five of six skipped objects, and the sixth only reveals itself if you
export twice and compare — which is the one thing the narrower test did not do.

### Exporting a copy works, and the lock is on the path

Measured, because it decides whether the tool can work at all while an engineer has CODESYS
open — the normal way of working:

| Question | Answer |
|---|---|
| Can the `.project` be copied while CODESYS holds it? | **Yes** — no sharing violation |
| Does exporting the copy work while the original is locked? | **Yes** — 16.7 s, full export, 10 files |
| Is the result identical to exporting the original? | **Yes**, all 11 object files byte-for-byte |

The lock is a sidecar on the path (`<project>.~u`), so a copy at another path is unencumbered.
The only difference between exporting the original and exporting a copy was the unstable GUID
above — which was not caused by the copy, and is now gone.

Two constraints that fall out of this and are easy to get wrong:

- **The copy must keep the same filename.** The manifest records
  `os.path.basename(job["project"])`, so exporting `P.project` instead of
  `RefrigeratorControl.project` would rewrite that field and churn the mirror.
- **The copy belongs in `.versio/local/`**, which is already gitignored, so it never reaches
  git and never appears in the panel.

This makes export-while-open a solvable problem rather than a refusal. Import is not, and
should not be: the IDE holds its own copy in memory and would silently revert an import on the
next Ctrl+S, so it must fail and say so.

### `.versio/local/` was gitignored in three comments and nowhere else

Found by re-reading the plan against the code after building the ignore rules. The engine says
`.versio/local/` "is gitignored" in three separate places, and §5 of the plan is explicit that
it must be: it holds the direction locks, session state, project backups — megabytes of binary
snapshots — and now the scratch copy taken while the IDE has a project open. A committed lock
pulled onto a colleague's machine is, in the plan's words, a bug factory.

Nothing wrote the rule. **This repository had one because a person typed it by hand**, which is
precisely why nobody noticed: every test and every manual check ran here. A repository somebody
else adopted a project into got the vendor's `.opt` rules and nothing else, so their locks and
backups sat untracked, visible in the panel, and one *stage all* from being committed.

The same shape as the library manager this morning: a comment asserting a property that nothing
enforces, true only where somebody had arranged it separately. Adoption now writes the rule, and
the test asserts through git — including that `.versio/repo.json` is **still** reported, because
it is committed on purpose and an ignore rule that swallowed it would break every clone's
baseline.

### What CODESYS actually leaves beside a project, and why it belongs in `.gitignore`

Written down because the first account of it in this document was **wrong**. The scratch folder
after an export was said to hold a `.~u` — inferred from the lock file measured earlier, not
looked at, because the test had cleaned up before anyone could see. Measured properly:

| | |
|---|---|
| Open and close cleanly | **Nothing left.** `<name>.~u` exists only while the project is held, and is removed on close |
| Killed while open | `.~u` survives — the stale case the pid probe already handles |
| Export or open | `<name>-AllUsers.opt` and `<name>-<user>-<machine>.opt`, and these **persist** |

The `.opt` files are per-user settings rather than locks, which is why they outlive the session
that made them — and why they matter here. Untracked in a repository, they turn up in
`git status`, in the project panel as loose rows, and in anything that stages everything.

**The per-user one is worse than noise: its filename carries the username and the machine
name.** Committing it writes both into history permanently, and produces a different file for
every engineer who opens the project — a file that can only ever conflict.

So adoption now writes `.gitignore` as well as `.gitattributes`, **creating it when absent**,
which is the ordinary starting point for somebody who has kept PLC projects in a folder until
now. The rules come from the vendor through `IPlcProjectFormat.TransientPatternsFor` and are
**scoped to the project** — `plc/Machine-*.opt`, never a repository-wide `*.opt`. A repository
belongs to other people too, and ignoring every file of an extension on the strength of one
adopted project is not Versio's decision to make.

An existing decision is never overridden, negations included: a repository saying
`!plc/Machine-*.opt` has been configured deliberately, and adding the positive rule underneath
would quietly reverse it. Re-adoption adds nothing, so the file does not grow on every scan.

The integration test asserts against **git**, not against the file it just wrote: it creates
all three leftovers and checks `git status` no longer reports them, while the project itself
still is reported — an ignore rule that swallowed the thing being tracked would be a far worse
bug than the noise it removes. Verified to fail with the patterns removed.

**The habit worth keeping:** the wrong version of this was a reasonable inference from a real
measurement taken minutes earlier, and it read as fact. Looking cost one command.

### Working while the IDE has the project open

Built on the measurements below. The behaviour now:

| | |
|---|---|
| **Export** | Runs. The project is copied to `.versio/local/open/<binding>/` keeping its own filename, and the copy is exported — the vendor lock is a sidecar on the path, so a copy elsewhere is unencumbered |
| **Import** | Waits, and says who has it. It cannot be made to work: the IDE holds its own copy in memory and the next save would silently revert whatever was imported |
| **Either** | `BlockedByOpenProject` — **not** a failure. No failed binding, nothing to acknowledge, and the circuit breaker is never fed |

**The check runs before the coalescer and the breaker**, which a test caught: placed after them, six blocked saves tripped a breaker with a threshold of two and halted the project for the session — turning the wait into exactly the sticky failure the verdict was introduced to remove. It runs *after* the hash gate and the direction lock, because "nothing changed" and "the lock forbids this direction" are more specific answers, and reporting the IDE instead would be noise on every settle.

The staleness rule mirrors `LockRecoveryPolicy`, because the problem is the same. A marker naming a dead process **on this machine** is stale and ignored — a crashed IDE must not block sync until somebody deletes a file they have never heard of. A marker from **another machine** is always believed: its pid means nothing here, and a project on a share really can be open on somebody else's desk. Guessing free would put two writers on one project.

**It sits behind the vendor seam.** `IPlcProjectFormat` gained `OccupancyMarkerFor` and `ParseOccupancy`; `Sync/` knows only that something may be held and by whom. A vendor that cannot answer reports free, so registering a format never makes its projects unsyncable.

Two things the integration test caught that nothing else would have:

- **The scratch folder is emptied, not the copy deleted.** Removing only the file we created left the toolchain's own files behind for good. The first write-up of this named the wrong file — `.~u`, inferred rather than looked at, because the test had already cleaned up. Measured properly afterwards: a clean open/close leaves **nothing**, and it is the *export* that leaves `<name>-AllUsers.opt` and `<name>-<user>-<machine>.opt`. See the finding below.
- **The test's own CODESYS launch used `ArgumentList`**, which quotes the whole `--profile=A B C` pair — the trap already documented in the README and covered by `ArgumentBuildingTests`. It exits 1 in under a second with no output, and the symptom was two silently skipped tests. It now uses `LocalCodesysRunner.BuildArguments`, which also means the test launches CODESYS the same way the product does.

Not built, and deliberately: **automatic retry when the IDE closes.** The `.~u` disappearing is a filesystem event the watcher could see, which would make closing CODESYS the trigger and need no button. Today the next save retries, and so does reopening the repository. Worth doing, not worth blocking this on. *(Written when an "Export now" button existed; it has since been deleted in favour of exporting on open.)*

### A project open in the CODESYS IDE — measured, and it is the normal case

The everyday scenario: an engineer saves in CODESYS and leaves the project open. Versio's
watcher sees the `.project` write and starts an export. What happens was unknown, so it was
measured rather than reasoned about — a second CODESYS holding the project open, then a real
export and a real import against it.

**CODESYS refuses, cleanly, in both directions.**

```
StandardError: The selected project is currently in use by 'elli_' on 'ROG-JVM'.
```

| | |
|---|---|
| Export while open | Fails at `projects.open()` in **6.9 s**. No manifest written, no partial tree |
| Import while open | Fails identically. The `.project` is **byte-for-byte unchanged** — measured, not assumed |
| Reported correctly? | **Yes.** `CODESYS.exe` exits **0** in both cases, but `PlcResult.Succeeded` requires the script's own `rc` *and* its `done` event, so neither is mistaken for success |

That last row is the one that could have gone badly, and it did not: the process exit code is
worthless here, and `Succeeded` already ignored it.

**There is an on-disk lock, and it is cheap to read.** CODESYS writes `<project>.~u` beside the
project while it holds it, and removes it on close:

```
elli_                 user
ROG-JVM               machine
45812                 process id
639221606143479369    timestamp
```

Which is very nearly Versio's own `LockRecord` — machine, user, pid — and carries the same
staleness problem: a CODESYS that crashed leaves the file behind, so the pid has to be probed
before the lock is believed. `LockRecoveryPolicy` already does exactly that reasoning for
Versio's locks.

**The defect this exposes is not the refusal, it is what Versio does with it.** A refusal
arrives as a failed run, and a failed binding stops syncing until somebody acknowledges it
— then a button called *Acknowledge failure* on the panel, now *Try again* on the failing
project's own row. So the most common working state in the tool's whole life — the IDE
open — turns into a sticky failure requiring a button press, over and over. "The project is
open" is not a failure; it is a *wait*.

Worth stating plainly: even if CODESYS allowed the write, importing into an open project would
be wrong. The IDE holds its own copy in memory, and the next Ctrl+S would silently revert the
import. The direction lock cannot help — it arbitrates between Versio's two directions, and the
IDE is a third writer it has never heard of. CODESYS's own lock is what saves us, and it is
luck rather than design that it does.

**The fix, designed but not built:**

1. Pre-flight the `.~u` file before launching CODESYS. It costs a file read instead of 17–31 s,
   and it turns the common case into an instant, quiet refusal.
2. Probe the pid before believing the lock, or a crashed IDE blocks sync until somebody deletes
   a file they have never heard of.
3. A distinct verdict — *blocked by an open project* — that does **not** mark the binding failed
   and does **not** trip the circuit breaker, and that retries when the lock clears.
4. A sentence naming who has it: *"RefrigeratorControl.project is open in CODESYS (elli_ on
   ROG-JVM). Close it and Versio will export automatically."*
5. **It goes behind the vendor seam.** `.~u` is a CODESYS fact, and `Sync/` may not learn it —
   so the question this poses is which contract owns it, most likely `IPlcProjectFormat`.

### A comment claimed a file nobody wrote

`LIBRARY_MANAGER` was skipped by the exporter with the comment *"captured in
config/libraries.yaml instead"*. Nothing has ever written that file. So a library reference —
adding one, bumping a version — changed nothing in the mirror, moved nothing through the hash
gate, and reached no diff and no commit. On the fixture there are **two** library managers, and
neither has ever been exported.

The tool's claim is that git can tell you what changed in a PLC project. For library references
it quietly could not, and nothing anywhere said so: an object that is deliberately not exported
and an object nobody has written the code for look identical from the outside, which is
*absent*.

Reported before it is fixed, and deliberately in that order. Every export now writes
`_coverage.md`, the manifest records each skip with a stable reason code, and the reasons
separate the two cases — `SERVICE_OBJECT`, `TEMPLATE_ONLY` and `COVERED_ELSEWHERE` lose
nothing; `NOT_IMPLEMENTED` is a hole. A stated gap is one somebody can work around. An unstated
one is a wrong answer to the only question the tool exists to answer.

The manifest schema went to 2 for this. An empty skip list means two different things — nothing
was skipped, or nobody was counting — and reading a schema-1 manifest as full coverage would
turn every old export into a clean bill of health.

### Adding a file to the export tree broke the importer, and no test noticed

The importer walks the export tree and treats every file it finds as an object, knowing about
`_manifest.json` by name, in two separate places. Adding `_coverage.md` beside it meant the
importer handed a markdown file to CODESYS as native XML:

```
NATIVE_IMPORT_FAILED  _coverage.md  Data at the root level is invalid. Line 1, position 1.
```

Nothing was corrupted, and that is the trap. The cost is that **every** import would carry a
failure warning that is always present and always meaningless — which is precisely how a real
native-import failure gets read past.

**The existing export→import round-trip test passed with the bug in place.** It was re-run with
the fix deliberately reverted to check, and passed again: it asserts the project is reached and
never looks at the warnings. The reserved names now live in one frozen set beside the exporter's
own type table, so the next file added is added for both halves at once, and a new test asserts
no warning names a bookkeeping file — verified to fail without the guard before it was believed.

The general lesson is about the shape of the mistake rather than the file: **the export tree has
two kinds of contents, and only one of them is the project.** Nothing in the code said so until
now.

### The integration suite had been lucky about running two CODESYS instances

xunit runs test *classes* in parallel by default, and four classes already drove a real
headless CODESYS. Adding a fifth that launches five exports tipped it over:
`ProjectTreeFromRealExportTests` began failing in a full run and passing on its own, with the
export exiting in **0.9 s** instead of 17 — two instances contending on the profile and the
shared temp state, and the loser dying with nothing useful to say.

The race was always there. Nothing had changed about it except the odds, which means the suite
had been passing on luck for as long as those four classes have existed.

Found only by running the whole suite **from a fresh clone** rather than from the working tree
it was written in — a habit worth keeping, since it is also the first thing anybody taking this
over will do. The CODESYS-driving classes now share an xunit collection and run one at a time.
The cost is wall clock, and it is the right trade: a slow suite that is believed beats a fast
one that is not.

### `git apply --cached` reads the index, not the working tree

Written down because the first test of per-hunk staging asserted the opposite, and **failed**.
It edited a file after reading its diff, then expected git to refuse the now-stale hunk. Git
applied it happily: `--cached` takes the *index* as the patch's pre-image, so a save landing
between the diff being read and the button being pressed does not invalidate anything — what
gets staged is still exactly the hunk that was on screen, which is the behaviour you want and
not the one the test claimed to protect.

The staleness that does matter is the index moving, and that is refused. Both are now pinned,
and the pair is why `--unidiff-zero` is not passed: git's context check is the only thing
standing between a patch and the wrong offset.

Worth keeping as a finding rather than a fixed test, because a passing test would have
documented a protection that does not exist.

### Lazy tree rows are proportional to what is reachable, not to nothing

`TreeRow` built its whole subtree in its constructor, which was affordable only while the tree
was filtered to what had changed. Rendering the full CODESYS structure that way would build a
row per object — thousands on a real machine — before the window drew anything.

Children are now built on first access. **That is not zero cost, and it is worth being precise
about why:** WPF evaluates `Children` when it realises a container, because it cannot decide
whether to draw an expander without asking. A visible collapsed folder therefore still costs
one row per child; its grandchildren cost nothing. The win is O(objects in the project) →
O(visible rows × branching), which is the whole point, and claiming more than that would be
the same kind of decorative virtualization the diff view already had once.

### A view that rebuilds on every refresh drops the selection

The project tree is rebuilt whenever git's status is re-read — which, now that it is the only
view, is several times a minute while somebody works. Most of those refreshes find nothing:
they fire because a file was saved in an editor.

Rebuilding anyway would clear the TreeView's selection over and over, which is precisely the
regression the changed-files list was reconciled in place to avoid; the fold would have
reintroduced it one panel to the left. The tree is now derived from a signature — the changed
paths, the search text, the bindings and each manifest's write time — and an identical
signature rebuilds nothing. Measured live: **five refreshes, two rebuilds**, and the three
no-op saves in between rebuilt nothing.

Expansion state is kept separately, keyed by path, and records both what was opened and what
was closed. One set cannot express "the user deliberately shut a folder the default rule wants
open", and the default rule changes its mind every time that folder's change state moves.

### A file under an export tree that the manifest does not describe

`_manifest.json` changes on every export and describes the other files rather than appearing
among them, so no object node claims it. Dropping it would have been worse than showing it: a
file git reports as changed, in a directory Versio owns, appearing nowhere in the panel is
indistinguishable from Versio having lost it. Such files hang off their project as plain rows.

The unit test that asserts every changed path is represented exactly once did **not** fail when
the rule was deliberately removed — the loose-file fallback caught the file at the top level
instead, so coverage held by a second mechanism while placement was wrong. Placement is
asserted separately, and the coverage test was re-verified against a different break (removing
the claim on manifest files, which duplicates every object) before it was believed.

### The hash gate could not see a text edit

Change detection for the mirror hashed `_manifest.json` — a file written by an export and by
nothing else. An engineer editing `Simulation.st`, the entire purpose of the mirror, left it
byte-identical, so the gate returned `NoChange` and the import was refused before the direction
lock was ever consulted. **Import could not have worked at all.**

The 24 scenario tests could not catch it: they simulated a text edit by rewriting
`_manifest.json`, the one file no human ever touches, so both sides agreed about a signal that
does not occur in the field. Fixed with `ContentHash.OfTree` over the tree's real contents, and
`ManifestHash` renamed to `MirrorHash` because the old name described the proxy rather than the
thing — which is how the bug got its blast radius.

**The first draft of the test written to prove the fix passed against the broken gate.** A
fresh binding has never recorded its mirror, so it reports a change whatever the gate does. It
now starts from a synced harness. That is the same vacuous pass one level up, found only by
deliberately breaking the code and re-running.

### The vendor seam was documented but not true

`SyncOrchestrator` was built directly on `ICodesysRunner`, `CodesysJob`, `CodesysOperation`,
`CodesysResult` and `CodesysProgress`, and `Abstractions/` — a folder whose entire purpose is
neutrality — held two brand-named interfaces. A second vendor would have meant rewriting the
engine, which is exactly what the rule existed to prevent. `IPlcToolchain` had been declared
from the start and never implemented, so the application assembled a toolchain out of vendor
parts instead: a locator, a profile resolver, an install record and a runner, five brand names
in a view model with no business knowing any of them.

A rule nothing checks is a rule that decays. It is now enforced from assembly metadata.

### The diff view's virtualization was decorative

`DiffView` is a virtualized `ListBox` whose class comment claimed rows were built on a
background thread. Neither was doing anything: `Rebuild` eagerly constructed a `Grid`, two
gutters and a syntax-tokenized `TextBlock` for every line before the list saw them. Selecting
the fixture's 2.5 MB, 33,644-line visualisation locked the window.

Made visible by the new-file fix that came before it in the same session — until untracked
files produced a diff at all, that file rendered nothing and returned instantly.

### `CreateNoWindow` does not hide a GUI child

It sets `CREATE_NO_WINDOW`, which suppresses the console a *console-subsystem* child would
allocate. `CODESYS.exe` is a GUI application and under `--noUI` still created a top-level
window. `WindowStyle.Hidden` passes `STARTF_USESHOWWINDOW` with `SW_HIDE`, which is what a GUI
child honours. Both flags are set: they cover different subsystems and neither replaces the
other.

### A wrong diagnosis, and the vacuous check that confirmed it

"Open logs folder" reported *Location is not available* for a folder that plainly existed. The
cause was **filesystem virtualization**: the app had been launched from inside an
MSIX/AppContainer, where writes under `%LOCALAPPDATA%` are redirected into that package's
`LocalCache`. Versio wrote to a real folder; Explorer, outside the container, was handed a path
that does not exist from where it stands.

The check that "confirmed" the first diagnosis was vacuous: `Start-Process explorer.exe`
reported success because Explorer *launched* — it then failed on its own and put up its own
dialog. **Process-started is not folder-opened**, and that was the entire question. Launch
context is part of the environment, alongside the per-user .NET install already recorded here.

### `objectCount` in the manifest is a file count

`versio_export.py` writes `len(records)`, and there is one record per *file*. It therefore
differs from the number of distinct objects exactly when an object exports to more than one
layer — which the fixture does, via its GVL. Found by an assertion that trusted the field's
name. Nothing depends on it today; the integration test asserts against distinct GUIDs.

### A flaky watcher test, and two fixes that made it worse

`RepositoryWatcherTests.A_deleted_file_is_reported` failed once during a full run and passes
in isolation every time. The root cause is a fixed `Thread.Sleep(250)` waiting for
`FileSystemWatcher` to arm — an assumption that holds on an idle machine and stops holding
under the full suite, where CODESYS exports are hammering the same disk.

Replacing the sleep with a deterministic arming probe — write a file, wait for the watcher to
report *it*, then act — made the test fail **consistently**. Windows raises a single
directory-level notification when several files in one folder change at once, so the probe and
the test's own file coalesced into one event for `src` rather than two for the files, and the
assertion looks for a file by name. Moving the probe to the repository root did not help
either.

**Reverted.** A flake observed once is not worth destabilising a working test for at a
stopping point, and two attempts had already turned "fails rarely" into "fails always". The
diagnosis is recorded here so the next attempt starts from it rather than from the sleep.

It is also a second sighting of the directory-level coalescing already documented for `.git`:
it applies to working-tree folders too, and any test asserting on a per-file event is making
an assumption Windows does not guarantee.

### Committed configuration has to be byte-stable

`repo.json` is committed by design — it is how a teammate gets the same project mapping — and
two things made it churn:

- **CRLF.** `System.Text.Json` indents with `Environment.NewLine`, so it was written CRLF on
  Windows while `.gitattributes` stores it LF. The file showed as modified the moment anyone
  cloned, and again every time Versio touched it.
- **Enums by ordinal.** Without a converter, `vendor` would have serialised as `0`. A reviewer
  reading that in a diff learns nothing, and inserting a vendor into `PlcVendor` —
  alphabetically, say — would have silently repointed every existing binding at a different
  toolchain, first noticed as an export driven by the wrong tool.

Both fixed and pinned by a test that reads the bytes back.

### A vacuous check, caught in the act

While investigating whether the export's line endings would churn through git, the first
measurement used `grep -c $'
'` and reported that all 34 lines of a `.st` file contained a
carriage return. They contain none: the pattern matched every line rather than CR-containing
ones. Measured again with `tr -cd` **and a known-CRLF control file**, the export is LF
throughout and round-trips through git unchanged.

The fix that was almost applied — rewriting the export's line endings — would have churned a
proven pipeline to solve a problem that did not exist. The control file is what separated the
two, and it cost one line.

### The fake filesystem was not a filesystem

`FakeFileSystem.EnumerateFiles` was a bare prefix match, so `plc/A.project` enumerated as part
of its own export tree `plc/A`. A test written against that would have proven the opposite of
what it claimed.

---

## Verifying the UI, not just the engine

Two instruments, covering different halves of the problem.

**`Versio.App.Tests` constructs the real windows against the real stylesheet.** A WPF test
project on an STA thread with `Theme.xaml` loaded by pack URI from the app assembly, so a style
renamed in the stylesheet and not in a dialog fails the build rather than the first click. It
covers what the tracer cannot: a modal nobody has opened has never been parsed. 24 tests over
the two dialogs, and a deliberately broken style reference fails every one of them.

**`Versio.exe --trace-bindings <file>`** writes every WPF data-binding failure to a file. A
broken binding in WPF is otherwise **silent** — a mistyped path or a missing converter produces an
empty control and no error anywhere — which made it the one class of failure in this codebase
that was not loud.

> ⚠️ **Its clean result means nothing unless a window actually opened.** The tracer writes only
> when a binding fails, so "no file" is produced both by a clean window and by *no window at
> all* — and the second case happened: with `MainWindow` throwing during construction, two
> consecutive runs produced an empty trace that was read as a pass. Check that the process has
> a window before believing it:
>
> ```powershell
> Get-Process Versio | Select-Object MainWindowTitle, MainWindowHandle
> ```
>
> A handle of `0` means the window never opened and the trace is worthless. **Launch with
> `Start-Process`**, not from a detached shell — a background-launched instance was alive,
> logging and window-less, which is the same empty trace for a third reason.

The app must be launched **outside** the tooling container for UI work; see the virtualization
finding above.

---

## Open decisions

| # | Question | Blocks |
|---|---|---|
| 1 | Who else runs this, and on what kind of machine? | Whether signing and an MSI are needed at all |
| 2 | Budget and legal entity for a code-signing certificate? | External distribution |
| 3 | Bundle PortableGit, or require Git for Windows? | Installer size and GPL compliance work |
| 4 | Where does the update feed live? | Phase 3 packaging |
| 5 | Is a **resident CODESYS session** worth building? | Now that export is automatic, this is the difference between 17–31 s and sub-second on every save |
| 6 | Can CODESYS run on a shared server? *(a licensing call, not engineering)* | PLC CI, the most valuable Phase 5 item |
| 7 | Which vendor comes second? | Whether `PlcJob`/`PlcResult` are the right shape |

---

## The disciplines that are load-bearing

Worth stating because they are what make the rest cheap, and they are easy to erode.

**`Versio.Core` touches nothing external.** No `System.IO`, no `Process`, no
`DateTimeOffset.Now`, no UI framework. That is why the lock model, the loop defences and the
state machine are tested in milliseconds rather than by causing real crashes.

**Vendor knowledge stops at the seam.** Enforced by a test that reads assembly metadata, not by
a paragraph in a README — because it was a paragraph in a README, and it had already stopped
being true.

**Green results are distrusted until they can fail.** Fifteen vacuous passes have been caught
this way — most recently an empty `--trace-bindings` file read as a clean UI while the window was
failing to construct at all, and a style-instantiation test that passed with the style deleted,
because a missing resource casts to a null `Style` and assigning null is legal — counting the determinism test that compared the one file with no GUIDs in it, and
the two the library reader gave up when it was broken on purpose: a parameters test that could
not have failed because a list dash already prevented the leak it asserted, and a
drop-the-nameless-entry rule that survived deletion because the fixture cannot produce a
nameless entry. Before those:
a round-trip that succeeded because the import silently did nothing; equivalence tests
that "passed" by skipping; a tree spike that measured a collapsed tree; a `Core2` mutation that
looked absent because the import had errored; a scenario suite that simulated text edits by
rewriting the one file no human touches; a shell check that reported success because a
process started rather than because it did its job; a line-ending measurement whose pattern
matched every line rather than the ones it claimed to count; and a coverage assertion over the
workspace tree that survived the deliberate removal of the rule it existed to protect, because
a fallback one level up caught the file in the wrong place; and an export-import round trip that
passed with the importer feeding a markdown file to CODESYS, because it asserted the project was
reached and never read the warnings.

The instruments get the same treatment. The clean `--trace-bindings` run over the folded panel
was believed only after a deliberately misspelt binding was added, the app rerun, and the
listener seen to write the error — because the tracer reports nothing at all when it is happy,
and "no file" is otherwise indistinguishable from "no listener attached".

**A silent failure is a bug even when the code is correct.** The rolling file sink was written,
reviewed and tested by eye — and never instantiated, so nothing reached disk while the log
panel made it look as though logging worked.

**Findings are written down with their evidence.** `docs/PHASE0-FINDINGS.md` records the
numbers, the failures and the dead ends — including the ones that reversed a plan decision.
