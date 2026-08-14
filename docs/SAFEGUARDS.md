# Versio — safeguards

**Date:** 2026-08-13 · **Phases 1–8 built; phase 9 is what remains** — start at §0

| | |
|---|---|
| Document | **The interruption and destructive-action plan.** What Versio must refuse, ask about, or wait for — and why each answer is the one it is |
| Status | **Phases 1–8 built and verified against real CODESYS** (§6) — the guard and its narrative, the cancellation plumbing, the dialog, the close guard, the context-switch guard, the repository reset, the conflict gate, the git-write window, the auto-stash in both directions, and the swap that can no longer leave a project with no mirror. **Phase 9 is what remains**, and it is features rather than guards: a real import confirmation (§5.C), a backup restore path (§5.E), a read-only second instance (§5.G). **§0 is the handover note.** Every "today" below is a reading of the code as it was when that section was written, with each phase's *"as built"* notes appended where it was planned |
| Related | [`PLAN.md`](PLAN.md) — the architecture · [`STATUS.md`](STATUS.md) — where the project is · [`PHASE0-FINDINGS.md`](PHASE0-FINDINGS.md) — what was measured |

---

## Start here

Versio's dangerous half is well defended and its safe half is not. The direction lock, the hash
gate, the self-write suppressor and the circuit breaker all sit **below** the UI and are heavily
tested; the actions a user can take with a mouse — switch branch, switch repository, close the
window — reach none of them.

This document is the plan for closing that. It is deliberately shaped like the rest of the
codebase: **a pure decision object in `Versio.Core` that answers "may this happen right now",
a sentence for every refusal, and side effects confined to `Versio.App`.**

The four scenarios in §3 are the ones that prompted it. §5 lists nine more found while reading
the code, two of which (§5.A and §5.D) can lose work with no user error at all.

---

## 0. Handover — where this stands

**Branch:** `GitPanel`. **Built:** phases 1–8 — see *Phase 7, as built* at the end of §4 and
*Phase 8, as built* below it. **Next: phase 9** (§5.C import confirmation, §5.E backup restore,
§5.G read-only second instance) — features rather than guards, and each its own conversation.

> **Work since, on the git panel, that touches this document.** Six phases of
> [`GIT-PANEL-PLAN.md`](GIT-PANEL-PLAN.md) have landed and three of them interact with the
> safeguards here:
>
> - **A background remote watch** (GP-D) probes with `ls-remote` and fetches when the remote
>   moves. It **never pulls**, and that is a safeguard rather than a preference: a pull rewrites
>   the project and its mirror under the watcher, which is §5.A — the one scenario here that can
>   corrupt a project through no user error at all. Automatic pulling would be §5.A on a timer.
> - **It suspends while anything else runs**, including while a guard prompt is on screen. A
>   background fetch landing while somebody decides whether to stash their work would change the
>   thing they are deciding about.
> - **It stops dead on an authentication refusal** rather than after the usual run of failures.
>   The circuit-breaker threshold is for things that might recover; a rejected credential is not
>   one, and retrying it every ten seconds may lock an account.
> - **Credentials are never stored** (GP-C). Versio hands off to the engineer's own helper
>   through `git credential fill` and keeps nothing. Nothing in that path logs what it sends or
>   receives — `git credential` speaks key=value over stdin and one of those keys is `password`,
>   which `Redact` would not catch.
>
> §6's phase table is unchanged: none of this is a safeguards phase.

Everything below §1 is the original plan, kept as written, with each phase's *"as built"* notes
appended where it was planned. Read §6 for the order and what is done.

### Verified how

| | |
|---|---|
| 1,175 Core + 291 UI unit tests | `dotnet test tests/Versio.Core.Tests`, `…/Versio.App.Tests` |
| 149 integration tests | Real git and **real CODESYS 3.5.22.20**; 0 skipped, 7 m 43 s |
| The application itself | Started, driven, closed. Two bugs found this way that no test caught |
| Every new behaviour | Deliberately broken and the tests re-run, per the README's `-MutateFirst` rule |

> **The `dotnet` on PATH here has no SDK.** Use `%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe`, as
> the README warns. And **a running `Versio.exe` locks `src/Versio.App/bin`**, so the App project
> cannot be rebuilt while it is open — `-p:OutputPath=<somewhere else>` builds and tests around it.

### The lesson worth inheriting

Three separate times in this work, a unit test proved a method and not its caller, and each was
caught only by deliberately deleting the call:

- `GuardIntent.Clone`, `Pull` and `Discard` were defined, given labels and verdicts, covered by
  theory tests over the enum — and called by nothing.
- `ResetForRepositoryChange` had nine passing tests; deleting its call site from
  `OpenRepositoryAsync` left all nine green.
- `TreeRow.Reconcile` had eight passing tests; reverting `RefreshTreeAsync` to clear-and-refill
  left all of them green.

**A test entered through the front door of the thing under test cannot tell you the thing is
reachable.** Where it matters, there is now a test that comes in from where the user does — which
is why `Versio.App.Tests` takes `Xunit.SkippableFact` and opens real repositories.

The same shape produced the worst bug of the work: the close guard shipped with every test green
and **Versio could not be closed at all**. Only running the application found it.

### Known gaps, stated rather than implied

- **The conflict probe's wiring** is verified by compilation and review, not a test. The natural
  test would launch a real 17–31 s CODESYS export whenever the wiring broke (§5.B).
- **`BusyTracker.WhenIdleAsync`'s race re-check** is reasoned, not tested — the race cannot be
  scheduled deterministically from a test (phase 3).
- **No mouse has driven the guards.** Every path below the click is exercised with real git and
  real bindings; the `ComboBox` interaction itself is not.
- **`GuardIntent.Pull` is implemented and deliberately unwired** — its verdicts all resolve to the
  auto-stash. Phase 7 wires it and deletes `ShellViewModel.AskAboutWork`.

### Starting phase 9

§5.C, §5.E and §5.G are the three that remain, and none of them is a guard: each adds a
capability the app describes and does not have.

- **§5.C** is the smallest and the most dishonest as it stands: `RequireImportConfirmation` is
  `true` by default and the only caller answers `Confirmed = true` with the comment *"there is
  nobody to ask"*. There is somebody to ask now — the prompt from phase 2 renders any question
  with named answers.
- **§5.E** is a window over the ten backups per binding that already exist and are reachable by
  nothing.
- **§5.G** is the sentence `LockRecovery` already produces — *"opening read-only"* — which
  nothing implements.

Nothing in phase 9 is blocked. §5.H (the repository disappearing while it is open) is still
unwritten and is the one remaining item in §5 that nobody has costed.

### Starting phase 7 (kept for its reasoning)

§4 is the full design and was written before any of this was built; it still holds. The first
concrete step is one line: `GitLogParser.StashFormat` needs `%H` and `StashEntry` needs a
`CommitId`, because **`stash apply <sha>` works and `stash drop <sha>` does not** — measured, §4.9b.
Everything else in §4 hangs off having that stable identity.

Phase 7's two prerequisites (§5.A and §5.B) are both built, so it is unblocked.

**Both of those steps are now done** — the identity and the whole pure layer. Pick it up at
*Phase 7, as built*, below §4.10.

---

## 1. What already exists, and where the gap is

**Genuinely solid, below the UI:**

| Defence | Where |
|---|---|
| Direction lock, hash gate, self-write suppressor, circuit breaker, in that order | `SyncOrchestrator.AttemptAsync` — `src/Versio.Core/Sync/SyncOrchestrator.cs:342` |
| Export stages into `<mirror>.versio_staging` and swaps only on success | `src/Versio.Scripts.Codesys/versio_export.py:409` |
| The project is copied aside before an import writes to it; a failed backup fails the import | `src/Versio.Core/Sync/SyncOrchestrator.cs:806` |
| An abandoned lock is never auto-cleared; recovery re-validates against content hashes | `src/Versio.Core/Locking/LockRecovery.cs` |
| Two CODESYS runs cannot overlap on one machine | `src/Versio.Infrastructure/Plc/Codesys/LocalCodesysRunner.cs:39` |
| Cancellation kills the whole CODESYS process tree | `LocalCodesysRunner.cs:304`, `:551` |

**The gap is at the top, and it is three facts:**

1. ~~**`Ready` guards buttons, not the dropdowns.**~~ **Closed in phase 4.** `Ready` was consulted
   by every git command, and neither the repository nor the branch `ComboBox` bound `IsEnabled` to
   anything at all — so a selection went straight to a checkout or a repository open underneath
   whatever was running.

2. ~~**There is no `Closing` handler anywhere in `Versio.App`.**~~ **Closed in phase 3.**
   `Exit += shell.Dispose()` released watcher handles and returned, so a CODESYS child launched
   with `CREATE_NO_WINDOW` was orphaned, invisible, and — if it was an import — halfway through
   writing a binary `.project`.

3. ~~**Every UI call site passes `CancellationToken.None`.**~~ **Closed in phase 2.** The plumbing
   beneath was already complete — `GitCliClient.RunAsync` links the caller's token
   (`GitCliClient.cs:767`), and the CODESYS runner links it and kills the process tree — but
   nothing at the top ever created a token to pass down, so the cancellation feature was fully
   built and entirely unreachable.

---

## 2. Shared machinery

Four pieces. Everything in §3–§5 is a consumer of these.

### 2.1 `Versio.Core/Guarding/` — a pure interruption gate

```
InterruptionGuard.Evaluate(GuardRequest, WorkSnapshot, WorkingTreeSnapshot) → GuardVerdict
```

| Type | Carries |
|---|---|
| `GuardRequest` | `Intent`: `SwitchBranch`, `SwitchRepository`, `CloseApp`, `Clone`, `Pull`, `Discard` |
| `WorkSnapshot` | Every in-flight operation, each with a `WorkKind` and an `Interruption` |
| `WorkingTreeSnapshot` | Dirty paths, conflicted paths, held direction locks per binding |
| `GuardVerdict` | `Proceed` · `Ask(GuardPrompt)` · `Refuse(sentence)` |
| `GuardPrompt` | Title, body, and an ordered `GuardChoice[]` — Stash / Discard / Cancel the run / Wait / Cancel |

Plus `GuardNarrative`, mirroring `SyncNarrative`, so every blocking state names its remedy in one
place shared by the dialog, the status bar and the log. That pattern already exists and already
pays; this is the second thing that needs it.

**Why pure.** The decision is the part that must be right, and it is the part hardest to exercise
through a UI. Pure means the whole matrix — export running, plus dirty tree, plus held lock, plus a
conflicted mirror — is a table-driven unit test with no git, no CODESYS, no clock and no window.
That is the same argument that made `SyncOrchestrator` testable, and it is why the risky logic in
this codebase is trustworthy.

### 2.2 `BusyTracker` grows a kind and a cancel handle

`BusyTracker` is already the single authority on whether Versio is doing something. It is simply
never asked the right question.

```
Begin(string what)                                                       // today
Begin(WorkKind kind, string what, CancellationTokenSource? cts = null)   // proposed
```

`IsBusy` and `Message` keep their exact current shape, so no existing binding moves. New members:
`Snapshot()` (returns the `WorkSnapshot` the guard needs), `CancelAll()`, `HasUninterruptible`.

### 2.3 A real `CancellationTokenSource` per user-initiated operation

Replacing `CancellationToken.None` at every UI call site. **Cancellability is asymmetric, and the
asymmetry is the whole design:**

| Operation | Interruption | Why |
|---|---|---|
| **Export** | `Cancellable` | Stages into `<mirror>.versio_staging` and swaps only on success. Killing it loses nothing but time. |
| **Import** | `MustFinish` | Writes into the binary `.project`. A kill mid-write can leave it unopenable; the backup exists but has no restore path in the UI (§5.E). |
| clone / fetch / status / diff | `Cancellable` | Read-only, or writes somewhere nothing depends on yet. |
| push | `MustFinish` | Cancelling is safe locally but can leave the remote half-updated. |
| commit / stage / discard | `MustFinish` | Sub-second, and git holds its own index and ref locks. |

So the prompt for a running **import** offers *wait*, never *cancel* — and says how long it has
been going, which `HeartbeatService` already produces.

### 2.4 A three-way prompt hook

`ConfirmDestructive` is `Func<string, string, bool>` (`ShellViewModel.cs:1219`) and cannot express
Stash / Discard / Cancel. Add `Func<GuardPrompt, GuardChoice?>? AskGuard` beside it, backed by a
`GuardPromptWindow` in the style of `BranchPromptWindow` — which is what makes it testable on the
existing STA harness without ever showing a window.

Unset means "no", as `ConfirmDestructive` already does. That is the right way round for a wiring
mistake to fail.

---

## 3. The four named scenarios

### 3.1 Branch switch with a dirty working tree

**Today.** `SelectedBranch`'s setter fires `CheckoutAsync` immediately (`ShellViewModel.cs:287`).
`CheckoutAsync` (`:1604`) runs `git checkout` and, on failure, writes git's message to the status
bar. Three problems: nothing is asked beforehand, **the dropdown is left showing the branch we are
not on**, and `STATUS.md` already records the explanation as missing.

**Plan.**

- Evaluate the guard *before* the checkout, not after git refuses. Prompt:
  *Stash and switch* / *Discard everything and switch* / *Cancel*.
- Stash is the default and the recommended choice. The full design is §4.
- Discard reuses `ConfirmDestructive` with the file count named, and refuses untracked files the
  same way `DiscardAsync` (`:1458`) does — git has no copy to restore them from.
- **Revert the dropdown selection whenever the switch does not happen**, under
  `_suppressSelectionSideEffects`. This is a bug today, independent of everything else here.

**The sub-case that actually destroys work.** Git *permits* a dirty checkout when the dirty paths
do not collide with the target branch. That carries a held direction lock across the switch:
`BindingLock.Baseline.HeadCommit` now names a commit on the other branch, and a `TextAuthoritative`
lock surviving the switch means the next automatic sync can **import branch A's mirror into branch
B's project**. The lock files live under `.versio/local/`, which is gitignored, so nothing about a
checkout disturbs them.

→ Record the branch in `BindingLock`; refuse a switch while any binding holds a lock, offering to
push or discard first; and treat a branch mismatch found during `RecoverAsync` as a recovery
decision with its own sentence.

### 3.2 Repository switch leaving stale state

**Today.** `OpenRepositoryAsync` (`ShellViewModel.cs:616`) calls `Sync.AttachAsync` and **never
calls `Sync.Detach()`**. `Detach()` (`SyncViewModel.cs:534`) does exactly the right thing and runs
only on application exit.

What survives a repository switch:

| Surviving state | Consequence |
|---|---|
| `_expansion` — tree expansion, keyed by repo-relative path | The comment inside `Detach()` describes this precise bug — *"'docs' is open in both… would open folders in the new repository because of a click in the old one"* — and nothing prevents it on a switch |
| `CommitMessage` | A message typed for repository A sits in repository B's box, beside a live Commit button |
| `SelectedDiff`, `SelectedDiffPath`, `_shownCommit`, `ShowFullFile` | The diff pane shows the previous repository's file, or a commit that does not exist here |
| `_coverage`, `_treeSignature`, `TreeSearch`, `_staleBindings` | Coverage and staleness reported against the wrong repository |
| `SelectedProject`, restored by path | `plc/Machine.project` exists in both repositories, so this silently selects a different project |

`Changes` reconciles correctly (`Reconcile`, `:1767`), `RescanAsync` clears `Projects` and
`Findings`, and `History.Attach` calls `Reset()`. Those three are already right.

**Plan.**

- Call `Sync.Detach()` before `AttachAsync`, and add a single `ResetForRepositoryChange()` on the
  shell for its own repo-scoped fields — one method, so a field added later has an obvious home. A
  test asserts every repo-scoped field is null or empty after it, and fails when a new one is not
  covered.
- **Do not silently bin a typed commit message.** Persist it per repository (`repo.json` already
  exists and is already per-repository) or ask. Quietly discarding someone's typing is the thing
  they will actually notice.
- Write a divider into the log (`— switched to <repo> —`) rather than clearing it. The log and its
  file sink are application-scoped by design.

### 3.3 Switching repository or branch while something is running

**Today.** Nothing. The dropdowns do not consult `Busy`. Switching repositories mid-export replaces
`_orchestrator` and `_git` underneath a run that is still going; `RunFinished` then fires against a
`SyncViewModel` pointing somewhere else, leaking its entry in `_runScopes` and looking for a row
that no longer exists.

**Plan.**

- Bind both `ComboBox.IsEnabled` to a new `CanChangeContext` (`!Busy.HasUninterruptible`), so the
  dangerous half is unreachable rather than merely refused after the fact.
- For cancellable work the guard asks: *Cancel the export and switch* / *Wait for it to finish* /
  *Stay here*. Cancelling routes the CTS into the plumbing that already kills the CODESYS tree.
- For an import: *Wait* and *Stay* only, with the elapsed time and the project name.
- Make `OnRunFinished` tolerate a detached view model rather than relying on it never happening.

### 3.4 Closing the app while something is running

**Today.** The window closes, `Exit` disposes the watchers, the process dies, and any CODESYS child
is orphaned — hidden, holding the project, and mid-write if it was importing.

**Plan.**

- Add `MainWindow.OnClosing`, same guard, `Intent.CloseApp`: *Cancel the export and close* /
  *Wait — N seconds so far* / *Don't close*. `e.Cancel = true` while waiting, with the existing busy
  caption as the wait UI.
- **An import in flight is a hard refusal, not a confirmation.** It is the one write that can
  destroy work git cannot restore, and "are you sure" is the wrong question when the honest answer
  is "twenty more seconds".
- Handle `SessionEnding` separately — Windows will not wait indefinitely. Best effort: kill the
  CODESYS tree deliberately rather than orphan it, and flush lock state so the next open recovers
  cleanly.
- On every path out, kill orphaned CODESYS children explicitly. `KillTree` already exists.

---

## 4. The auto-stash, in full

The stash is what makes §3.1 a *choice* rather than a refusal, so it has to be right in both
directions: taking it must be detectable, and returning to the branch must offer it back.

The operations already exist and are already tested — `StashListAsync` (`GitCliClient.cs:535`),
`StashPushAsync` (`:548`), `StashApplyAsync` (`:577`), `StashDropAsync` (`:588`) — with six
integration tests. `STATUS.md` records stash as *withdrawn from the UI, pending a different design*.
**This is that design.** Stash stops being a panel of buttons and becomes a thing that happens to
you at exactly the moment it helps.

### 4.1 Why a sidecar record, and not just the stash list

Three reasons the stash list alone cannot carry this:

1. **`stash@{n}` is positional and moves.** Dropping `stash@{0}` renumbers everything below it —
   the doc comment on `StashEntry.Ref` (`GitLogParser.cs:34`) says so, and requires that anything
   acting on one re-reads the list first.
2. **The subject is textual bookkeeping.** `git stash push -m "…"` produces `On <branch>: …`, which
   `SplitSubject` (`GitLogParser.cs:139`) parses reliably — but a user's own stash can wear the same
   shape, and identifying our own work by pattern-matching a human-readable string is how you
   eventually pop the wrong one.
3. **The stash does not carry the sync state.** `--include-untracked` deliberately excludes
   *ignored* files, so `.versio/local/` — the direction locks, the backups — stays exactly where it
   is while the mirror it describes is stashed away. Restoring only the files would restore the
   working tree into a lock state that no longer matches it (§4.7).

So: **the stash is the storage, and a machine-local sidecar is the record.**

### 4.2 What is recorded

`.versio/local/autostash.json` — gitignored, machine-local, beside `state.json`, written through
the same atomic temp-file-and-replace `JsonLockStore` uses. A stash never leaves the clone, so a
machine-local record is the correct scope. New `JsonAutoStashStore` in
`Versio.Infrastructure/FileSystem/`, mirroring `JsonLockStore` including its schema field.

```
AutoStashRecord
  Branch            "feature/pump-control"   (or "detached@<sha>", §4.6)
  StashCommitId     full SHA — the stable identity
  Message           the marker written into the stash
  CreatedUtc
  HeadCommit        HEAD when the stash was taken
  IncludedUntracked
  Locks             per-binding direction + baseline hashes, captured at stash time  ← §4.7
```

Stored as a **list per branch**, not a single entry: switching away from a branch twice without
returning is legitimate, and silently overwriting the first record would strand the first stash.
Restore is offered newest-first, with the count stated.

The stash message carries a marker for anyone reading `git stash list` in a terminal:

```
versio-autostash <branch> @ <shortHead>
```

The marker is a courtesy to a human; **the sidecar is the authority.** They are never allowed to
disagree, because only one of them is ever consulted for a decision.

### 4.3 Taking the stash

```
1. Read `git stash list` → SHA set BEFORE
2. StashPushAsync(marker, includeUntracked: true)
3. Read `git stash list` → SHA set AFTER
4. New SHA = AFTER \ BEFORE
```

**Step 4 is not bookkeeping, it is the correctness check.** `git stash push` on a clean tree prints
*"No local changes to save"* and **exits 0 without creating an entry**. Trusting the exit code would
record a stash that does not exist, and then offer to restore it. The set difference both proves an
entry was created and yields the stable SHA in one read.

If no new SHA appears, or the push fails: **abort the switch.** Do not fall through to the checkout,
and never fall through to the discard. A stash that did not happen and a switch that happened anyway
is the worst available outcome.

### 4.4 Detecting a stash that is already there

Before taking one, and shown in the prompt rather than discovered afterwards:

- **Ours, for this branch** — the sidecar has live entries. Say so: *"You have 1 stash saved for
  this branch already."* Taking a second is allowed; the list is LIFO.
- **Theirs** — stashes with no sidecar entry. Never touched, never counted, never restored.
  Mentioned once in the prompt so the number in `git stash list` is not a surprise.
- **Ours, but gone** — a sidecar entry whose SHA is no longer in the list, because someone popped or
  dropped it from a terminal. Clear the entry, log it at Info, say nothing in the dialog. This is a
  normal thing for a git user to do and should not read as an error.

### 4.5 Returning to the branch

After **any** successful checkout — the dropdown, `CreateBranchAsync`, or a pull that moved HEAD —
look up the sidecar for the branch just arrived at:

```
entries = sidecar[branch]
live    = entries where StashCommitId is still in `git stash list`
```

Re-resolving the positional `stash@{n}` by matching SHA at this moment, never storing it. Then, if
`live` is non-empty:

> **Stashed work for this branch**
> Versio saved 3 changed files when you switched away 12 minutes ago.
> *Restore it* · *Leave it stashed* · *Discard it*

| Choice | Action |
|---|---|
| **Restore it** | `StashApplyAsync(ref, drop: true)` — `pop`, which drops **only** when the restore succeeded. The client already delegates to git for exactly this reason: apply-then-drop as two commands would destroy the only copy after a conflicted restore. |
| **Leave it stashed** | Nothing. The entry survives and is offered again on the next arrival. |
| **Discard it** | `StashDropAsync` behind `ConfirmDestructive`, naming the file count. Destructive and irreversible; already logged at Warn by the client. |

Restoring is **offered, never automatic**. The same principle as the direction lock: an abandoned
lock is never auto-cleared, because the work it guards is still work. Silently mutating someone's
working tree as a side effect of picking a branch from a dropdown is the same mistake wearing a
friendlier hat.

### 4.6 Failure modes, each with its answer

| Case | Answer |
|---|---|
| `stash push` fails, or creates nothing | Abort the switch. Never fall through to checkout or discard. |
| Nothing to stash (clean tree) | The guard never asks in the first place — `WorkingTreeSnapshot` says the tree is clean and the verdict is `Proceed`. |
| **`pop` conflicts** | Git leaves the stash in place and marks paths unmerged. **Keep the sidecar entry** — the work still exists and is still ours. Report the conflict, and let §5.B refuse both sync directions until it is resolved. |
| Stash popped or dropped from a terminal | SHA is gone from the list. Clear the entry, log at Info, stay quiet. |
| Branch deleted while its stash is held | Orphaned entry. **Never auto-drop** — surface it in `Findings`, which already exists for exactly this class of "needs a human". |
| Stash taken on a detached HEAD | Key on `detached@<sha>`; offer restore when HEAD returns to that commit. Refusing to stash mid-switch is worse than an imperfect key. |
| Two auto-stashes, same branch | Kept as a list, restored newest-first, count stated in the prompt. |
| Two Versio instances, or external `git stash` use | SHA re-resolution absorbs renumbering. This is why the ref is never stored. |
| Sidecar corrupt or unreadable | Same as `JsonLockStore`: log it, treat as empty, carry on. The stashes themselves are still in git and still listed — only the offer to restore is lost. |
| `state.json` and `autostash.json` disagree about a binding | The sidecar loses. Locks are the authority on sync state; the stash record is a snapshot taken at one moment. |

### 4.7 What the stash must not carry — and what that costs

`--include-untracked` excludes ignored files, so **`.versio/local/` does not travel**. The direction
locks, the backups and the scratch copies stay put while the mirror they describe is stashed away.

That is the correct behaviour — machine-local state should not be smuggled between branches inside a
stash — but it has a consequence that must be handled explicitly:

> After stashing a dirty mirror, the lock still says `TextAuthoritative` with baseline hashes
> describing content that is no longer on disk. The commit gate would then report a mirror as stale
> that is, on this branch, perfectly clean.

So taking an auto-stash **also captures and releases the affected direction locks**, recording their
direction and baselines in the `Locks` field of the record. Restoring re-establishes them. That is
what makes it a round trip rather than a file copy: the user gets back the working tree *and* the
sync state they left, and neither one alone is enough.

### 4.8 The watcher must not react to either end

Both the stash and the restore are bulk filesystem writes across the mirror. Left alone, the
`FileChangeWatcher` sees them, the hash gate agrees something genuinely changed, and the orchestrator
can start an export — or worse, an import — off the back of a checkout.

This is the same defect as §5.A and takes the same fix: a suppression window around the whole
operation, and a re-baseline from the new HEAD afterwards instead of treating the difference as a
user edit. **§4 cannot ship before §5.A**, and the ordering in §6 reflects that.

### 4.9 API changes needed

| Change | Where | Why |
|---|---|---|
| `StashFormat` gains `%H`; `StashEntry` gains `CommitId` | `GitLogParser.cs:85` | The stable identity. Currently only `%gd %gs %cr` are read, and none of the three is stable. |
| `AutoStashRecord`, `AutoStashState`, `IAutoStashStore` | new, `Versio.Core` | Pure records plus an abstraction, so the policy is testable without a filesystem. |
| `JsonAutoStashStore` | new, `Versio.Infrastructure/FileSystem/` | Mirrors `JsonLockStore`, atomic replace included. |
| `AutoStashPolicy` | new, `Versio.Core/Guarding/` | Pure: given a branch, a sidecar and a stash list, what should be offered? Every row of §4.6 is one test. |

`apply` accepts any commit that looks like a stash, so restore works from the SHA directly. `drop`
requires a positional `stash@{n}`, which is precisely why it is re-resolved at the moment of use and
never stored.

### 4.9b Measured, not assumed

The three git behaviours this design rests on, checked against real git 2.x on a scratch repository
rather than taken from memory:

| Claim | Measured |
|---|---|
| `stash push` on a clean tree exits 0 and creates nothing | `No local changes to save`, **exit 0**, `stash list` empty. **The exit code cannot be trusted** — hence the SHA set difference in §4.3 |
| `%H` yields a stable id, and `-m` produces a parseable subject | `stash@{1}\|2a4b8cdc…\|On master: versio-autostash main @ abc1234` — so `SplitSubject` reads the branch off our own stashes correctly |
| `apply` takes a SHA; `drop` does not | `git stash apply <sha>` → **exit 0**. `git stash drop <sha>` → `error: '<sha>' is not a stash reference`, **exit 1** |

That last row is the reason the positional ref is re-resolved at the moment of use. A design that
stored `stash@{n}` and dropped it later would work in every test with one stash in the list and fail
the first time a user had two.

### 4.10 Tests

- `Versio.Core.Tests` — `AutoStashPolicy` across every row of §4.6. Pure, no IO.
- `Versio.Core.Tests` — `ParseStashes` with `%H` present, and the existing subject shapes unchanged.
- `Versio.App.Tests` — `GuardPromptWindow` renders three choices against the real stylesheet.
- `Versio.Integration.Tests` — against real git: stash → switch → return → restore round trip; a
  conflicting pop leaves both the stash and the sidecar entry intact; a stash dropped externally
  clears its entry; `stash push` on a clean tree creates nothing and the set difference is empty.

### Phase 7, as built — so far

Two of the five steps have landed, both of them pure and both of them prerequisites for
everything else.

**1 · The identity.** `StashFormat` leads with `%H` and `StashEntry` carries `CommitId`. Before
this, nothing in a stash entry could name one particular stash twice: `%gd` is positional and
renumbers, `%gs` is a sentence a user's own stash can imitate exactly, and `%cr` is relative to
now. Two tests cover it, and one of them asserts the **format and the parser agree about the
field count** — they are one decision written in two places, and a field added to one and not
the other silently shifts every column.

**2 · The pure layer.** `src/Versio.Core/Guarding/AutoStash.cs` — `AutoStashRecord`,
`AutoStashState`, `StashedLock`, and the rules:

| | |
|---|---|
| `NewStash(before, after)` | The set difference from §4.3. `git stash push` on a clean tree exits 0 and creates nothing, so this is what proves an entry exists — and a null answer means abandon the switch, never fall through to the checkout |
| `OnArrival(branch, state, live)` | Restorable versus stale, with the positional ref **resolved from the SHA at the moment of use** and never stored |
| `BeforeTaking(...)` | Ours and theirs, counted separately, with the sentence that says both |
| `Orphans(...)` | A stash whose branch was deleted. Surfaced, never auto-dropped |
| `KeyFor(branch, head)` | `detached@<sha>` when there is no branch name |
| `With` / `Without` | The record set, newest first |

19 tests, one per row of §4.6 plus the corners. Verified able to fail: identifying our own
stashes without the branch filter fails exactly the test that asserts another branch's stash is
not offered here.

**3 · The store.** `IAutoStashStore` in `Versio.Core/Abstractions/`, `JsonAutoStashStore` in
`Versio.Infrastructure/FileSystem/`, writing `.versio/local/autostash.json` through the same
atomic temp-and-replace `JsonLockStore` uses. It decides nothing. Two details are deliberate:
the direction is serialised **by name**, so a schema change cannot silently renumber every
stashed lock; and an unreadable record is logged as an **error** rather than a warning, with
wording that says the stashes are still in git and how to reach them by hand — nobody's work is
lost, but the knowledge of which stashes were ours is, and that is what stops the app offering
somebody else's back. Six tests against a real filesystem, including that the file lands under
the `.versio/local/` prefix `.gitignore` already covers.

**4 · The lock round trip.** `SyncOrchestrator.CaptureLocks()`, `ReleaseCapturedLocksAsync` and
`RestoreCapturedLocksAsync`. Built before the shell flow deliberately: both halves of that flow
need it, and shipping either half without it produces a lock that claims work no longer on disk.
Three decisions in it are worth knowing:

- **Capture and release are separate calls.** The stash can still fail to be created, and a
  lock released against a stash that does not exist is work nobody can find. The caller
  releases only once the stash's commit id is in hand.
- **The release is written straight into the lock table, not through the state machine.** There
  is no trigger for "the work went somewhere else": push and discard both mean the work has
  *landed*, and reusing either would record a reason that is untrue and move the baseline as
  though the world had seen it.
- **A binding that has since taken a lock of its own keeps it.** Days may have passed, on
  another branch, and whatever is claimed now was claimed by real work. Reported back to the
  caller rather than overwritten.

Seven tests, and the one that matters — a newer lock is not overwritten — fails when the check
is removed.

**5 · Taking and restoring, in the shell.** The guard now sees the real working tree, so
`StashAndProceed` is reachable and `ClearForContextChangeAsync` carries it out;
`OfferStashedWorkAsync` runs after **every** successful checkout. Two things came out of
building it that the design did not anticipate:

- **`.versio/local/` had to be excluded in `.git/info/exclude`, not just `.gitignore`.**
  Found by the second test: an auto-stash taken while that directory was visible to git
  swallowed the record naming the *previous* stash, leaving real work in git with nothing
  pointing at it. `.gitignore` cannot fix that — it is a tracked file, so a stash carries the
  rule away exactly when it is needed. `info/exclude` is outside the working tree and cannot be
  stashed, committed or conflicted. Written when a repository opens, before anything reaches
  `.versio/local/`; adoption still writes the `.gitignore` rule, which is what a *teammate*
  gets when they clone.
- **§4.8 was wrong about the window, and so was the correction in the previous handover.** A
  stash and a restore rewrite every modified file in the repository, exactly like a checkout —
  so the repository-wide `BeginExternalWrite` is right after all, and both ends use the same
  `WhileGitRewritesTheTreeAsync` wrapper the checkout does. `BeginOwnWrite` is for an export
  writing one binding's mirror, which is a different shape.

Six tests against a real repository, because every interesting failure here is about what git
actually did. The one that matters most — a stash that was never created must abandon the
switch — is called directly rather than through the dropdown: with a clean tree the guard
answers `Proceed` and never asks, so the front door cannot reach that branch, and it is the
branch that decides whether a checkout happens over unsaved work. It fails when the set
difference is replaced by the exit code.

One existing test was **inverted**: `A_dirty_tree_is_not_asked_about_yet` asserted that the
guard did not offer a stash, which was right while nothing could stash. It now asserts the
offer is made.

**What is left:**

6. Taking and restoring for real, in the shell: `StashPushAsync` with the marker, the set
   difference, the lock capture and release (§4.7), and the arrival prompt on **every**
   successful checkout — the dropdown, `CreateBranchAsync`, and a pull that moved HEAD.
**6 · `GuardIntent.Pull`, wired.** It had existed, been tested and been deliberately unwired
since the day the guard was written, because every verdict it produces resolves to a stash that
did not exist. A pull is the worst path to meet a dirty tree on — it rewrites the project and
its mirror together — and it is guarded now, with a test that fails when the call is removed.
`AskAboutWork` is gone with it: it existed only to pass `WorkingTreeSnapshot.Clean`, and became
a synonym for `Ask` the moment there was something to offer.

**Phase 7 is complete.** The second and later stashes on one branch are offered in turn now,
newest first, each answer moving on to the next, and the prompt says how many are waiting
behind it. Restoring or discarding one stops there and leaves the rest for the next arrival:
a `pop` renumbers every `stash@{n}` below it, so the refs captured a moment earlier would name
the wrong entries — and two pops into one working tree would land two sets of edits with no way
to tell them apart.

### Phase 8, as built

**§5.D — the swap.** `rmtree(out_root)` then `os.rename(staging, out_root)` left a window with
**no mirror at all** — not stale, not partial, none — and `rmtree` over the fixture's 2.5 MB
visualisation tree is not instantaneous. It is three renames now: mirror to
`<mirror>.versio_trash`, staging to mirror, delete the trash. Each is atomic on NTFS, so at
every instant between them the mirror is either the old tree or the new one. What a kill can
leave behind is a trash directory holding the previous export, which is recoverable, rather
than nothing, which is not.

**§5.F — the ignore plan.** `<mirror>.versio_staging/` and `<mirror>.versio_trash/` are in it
now, alongside `.versio/local/` and the vendor's own leftovers. Both directories exist for
milliseconds in the happy case and survive a killed run, and until this landed **this
repository had the staging rule only because somebody typed it by hand** — an adopted user
repository had nothing, so a killed export left an untracked staging tree that *Stage all*
would cheerfully commit. The names live in `BindingConventions` and a test reads
`versio_export.py` to check the two languages still agree about them.

**The sweep at open — and the reason it is not a delete loop.** The exporter clears its own
leftovers on the *next* run, which covers nothing until an export happens; a repository can be
opened, read, committed from and closed again without one. So `SwapRecovery` reads the three
directories and decides:

| On disk | What it means | What happens |
|---|---|---|
| mirror only | a finished export | nothing |
| mirror + staging | killed before the swap | delete the staging tree; it is a partial export by definition |
| mirror + trash | killed after the swap, during the tidy-up | delete the trash; the export is intact |
| **trash, no mirror** | **killed between the two renames** | **restore the trash as the mirror** |

That last row is the whole point. The obvious implementation — clear anything left over —
destroys the last copy of the mirror in exactly the case the three-rename swap was built to
survive, and it would do it quietly, at startup, in a repository nobody had touched. Eight pure
tests cover every combination, including the two that must do nothing: a project that has never
been exported, and a missing mirror with only a staging tree beside it, which is not restorable
because a staging tree is not a previous export.

The sweep runs twice during attach, before and after the scan. Before, so adoption cannot start
an export over a mirror sitting in the trash; after, because the scan is where a project that
is not yet in `repo.json` becomes a binding — which is precisely the first-adoption case where
this would otherwise go unnoticed. The front-door test arranges the dangerous combination on
disk and opens the repository through `OpenAsync`; it fails when the restore is replaced by a
delete.

---

## 5. Other scenarios that can break

Ordered by how badly they end.

### A. A pull or checkout rewrites the project *and* the mirror under the watcher — **BUILT**

`git pull` changes both sides of a binding at once. The watcher fires, the hash gate correctly
agrees both changed, and the orchestrator can trigger an export or an import from **git's** write
rather than the user's. `SelfWriteSuppressor` covers only `ChangeSource.ExportedText` from our own
exports (`SyncOrchestrator.cs:404`) — it has no concept of a git-authored write.

→ A suppression window around every ref-moving git operation — `NoteExternalStart` already exists for
`StatusRefreshPolicy`, and the sync watcher needs its equivalent — and afterwards a re-baseline of
both hashes from the new HEAD rather than treating the difference as a user edit.

**This is the only item here that can corrupt a project through no user error at all.**

**As built.** `SyncOrchestrator.BeginExternalWrite(what)` returns a scope; while one is held, every
change is dropped as `GitWriteSuppressed`. It is checked **before the hash gate** — unlike almost
everything else in that method — because git really did change the content, so asking "did anything
change" first answers yes and sends a pull's new mirror down the import path. Counted rather than
flagged: a pull that stages the mirror afterwards is two nested writes.

Suppressing is only half. `AdoptExternalStateAsync` then takes the state git left as the truth
without running anything — the project and mirror both came from the same commit and agree with
each other, because the commit gate refuses to record one without the other. Without it the engine
keeps measuring against the previous commit's hashes and exports on the next real save for a change
nobody made. **Bindings holding a lock are refused**, not adopted: unsynced work is something the
new commit does not contain, and the guard above the UI already prevents the checkout — which is
exactly why this refuses rather than assuming it.

`SyncViewModel.WhileGitRewritesTheTreeAsync` wraps `CheckoutAsync` and `PullAsync`. **8 Core tests.**
Verified able to fail: removing the suppression fails three, and adopting over a held lock fails
the one that guards it.

### B. Merge conflicts inside the mirror get imported into the project — **BUILT**

`GitStatus.HasConflicts` is parsed and exposed (`GitModels.cs:68`) and **nothing in `Sync/` or the
App ever reads it**. Conflict markers in a `.st` file are, to the importer, just text.

→ Refuse both directions while any path under a binding is unmerged. New
`SyncVerdict.BlockedByConflict` with its `SyncNarrative` sentence. Cheap, and it is also the backstop
for a conflicted `stash pop` (§4.6).

### C. Imports run with no human confirmation, contradicting the default

`RequireImportConfirmation = true` is the shipped default (`SyncOrchestrator.cs:17`), and the only
caller sets `Confirmed = true` with the comment *"there is nobody to ask"* (`SyncViewModel.cs:1692`).
The gate is switched on and always answered yes. The backup makes that defensible; the option is
nonetheless currently a fiction.

→ Either wire a real confirmation — §2.4 provides one — or make the setting honest. Prompting is the
better answer: this is the direction that writes a binary file the user cannot diff afterwards.

### D. The mirror can vanish entirely in the swap window

`versio_export.py:469` does `rmtree(out_root)` and then `os.rename(staging, out_root)`. A kill, crash
or power loss between those two statements leaves **no mirror at all** — and on the 2.5 MB /
33,644-line visualisation tree the suite already exercises, `rmtree` is not instantaneous.

→ Rename the old tree to `<mirror>.versio_trash`, rename staging into place, then delete the trash.
Three renames and no window in which nothing exists. Clear a leftover `.versio_trash` on the next
run, exactly as staging is already cleared and announced.

### E. There is no backup restore path

Ten backups are kept per binding under `.versio/local/` (`BackupsKept`, `SyncOrchestrator.cs:78`) and
nothing in the UI can list or restore one. After a corrupting import — the scenario the backups exist
for — the user has an unusable project and no way to reach the copy that would fix it.

### F. `*.versio_staging/` is not in the adoption ignore plan

`TransientPatternsFor` returns `<stem>.~u` and `<stem>-*.opt` (`CodesysProjectFormat.cs:80`);
`RepoSetup` adds `.versio/local/` (`RepoSetup.cs:149`). Neither covers the staging directory. **This
repository has the rule only because somebody typed it into `.gitignore:51` by hand** — an adopted
user repository will not have it, so a killed export leaves an untracked staging tree that "stage
all" will happily commit. Same class of problem as the three comments that described
`.versio/local/` as gitignored while nothing wrote the rule.

→ Add the pattern to the ignore plan, and sweep leftover staging and trash directories at open.

### G. Two Versio instances on one repository

`LockRecovery.HeldByLiveSession` produces the sentence *"held by a running Versio session (pid N);
opening read-only"* — and **there is no read-only mode**. The second instance opens fully writable.
The CODESYS mutex prevents concurrent toolchain runs, but not two orchestrators writing `repo.json`
and `state.json`.

→ Implement the read-only mode the sentence already promises, or a single-instance-per-repository
guard. A sentence that describes behaviour nothing implements is worse than no sentence.

### H. The repository disappears while it is open

Network share drops, folder renamed or deleted. `RepositoryKind.Inaccessible` is evaluated at *open*
time only; a live repository going away leaves a dead watcher and every git call failing with a raw
message in the status bar. Needs a detected-and-explained state.

### I. A tracked project file is deleted or renamed outside Versio

The binding points at a path that no longer exists and the commit gate hashes a missing file.
`OrphanScanner` finds orphaned *mirrors*; the inverse case needs the same treatment.

### J. Disk full, or a read-only repository, during a swap or a backup

The backup failure path is handled well and refuses rather than proceeding
(`SyncOrchestrator.cs:815`). The export swap in IronPython is not: a failed `os.rename` after a
successful `rmtree` is §5.D with a different trigger, and the same fix covers both.

### K. A deferred job firing just after a commit

The five-second `_deferredTick` (`SyncViewModel.cs:966`) can release a throttled export moments after
a commit lands, dirtying the tree that was just committed. The commit gate exports first, so this is
not a correctness bug — but *"I committed and it immediately went dirty again"* reads as one.
Suppress deferred work for the duration of a git write.

### L. The toolchain is probed once per repository

`ProbeToolchainAsync` runs at attach. CODESYS uninstalled, upgraded, or a profile removed mid-session
is not noticed until a run fails. The *Try again* affordance on the project's own row probably covers
this adequately — worth confirming rather than changing.

---

## 6. Order of work

| Phase | Contents | Why here |
|---|---|---|
| **1 ✅** | `InterruptionGuard`, `GuardNarrative`, `WorkKind`/`Interruption`. Unit tests only, nothing wired | Pure and fast. Everything below depends on it. |
| **2 ✅** | A real `CancellationTokenSource` per operation; `GuardPromptWindow`; the `AskGuard` hook | The two mechanisms every scenario needs. |
| **3 ✅** | **Close guard** (§3.4) | Highest damage per line: an orphaned CODESYS mid-import is the worst outcome on this list. |
| **4 ✅** | **Context-switch guard** (§3.3) — dropdowns disabled for uninterruptible work, prompt for the rest | Second highest, and reuses phase 3 wholesale. |
| **5 ✅** | **`ResetForRepositoryChange`** (§3.2) | Self-contained, no new machinery, immediately visible. |
| **6 ✅** | Git-write suppression window (§5.A) and the conflict gate (§5.B) | Correctness, and both are **prerequisites** for phase 7 — see §4.8 and §4.6. |
| **7** | **Branch-switch guard with the full auto-stash** (§3.1, §4), plus the branch recorded in `BindingLock` | Needs the prompt from phase 2 and both defences from phase 6. |
| **8 ✅** | Swap via trash-rename (§5.D), staging in the ignore plan (§5.F), leftover sweep at open | Small, independent, and §5.D closes a real window. |
| **9** | Import confirmation made real (§5.C), backup restore UI (§5.E), read-only second instance (§5.G) | Features rather than guards. Each is its own conversation. |

Phases 6 and 7 are ordered deliberately: **the auto-stash cannot ship before the suppression window
and the conflict gate**, or restoring a stash becomes a way to trigger an unwanted import, and a
conflicted `pop` becomes a way to import conflict markers into a project.

Tests follow the existing split: guard and stash policy in `Versio.Core.Tests` (pure, ~0.3 s), the
prompt window in `Versio.App.Tests` on the STA harness, and the stash round trip, conflict gate and
suppression window in `Versio.Integration.Tests` against real git.

### Phase 1, as built

`src/Versio.Core/Guarding/` — four files, no IO, no clock, no UI:

| File | Holds |
|---|---|
| `ActiveWork.cs` | `WorkKind`, `Interruption`, `WorkKinds.InterruptionOf`, `ActiveWork`, `WorkSnapshot` |
| `GuardVerdict.cs` | `GuardIntent`, `GuardDecision`, `GuardReason`, `GuardChoice`, `HeldLock`, `WorkingTreeSnapshot`, `GuardVerdict` |
| `InterruptionGuard.cs` | `Evaluate(intent, work, repo, target)` — the five-step ordering |
| `GuardNarrative.cs` | `GuardPrompt`, and every sentence: `Title`, `Body`, `Describe`, `Label`, `Action` |

**93 new unit tests**, taking `Versio.Core.Tests` from 838 to 931 in ~380 ms. Two decisions were
made during implementation that the plan above had left open:

- **`Interruption` is derived from `WorkKind`, never passed alongside it.** A call site that had to
  state both would eventually state the wrong one, and the silent failure direction is an import
  cancelled because somebody typed `Cancellable`. An unrecognised kind falls through to
  `MustFinish`, so forgetting to extend the switch costs a refusal rather than a half-written
  project — asserted by a test that casts an undefined enum value.
- **A refusal offers no choices at all**, where a question offers Cancel last, always. If no option
  would make the action safe, presenting one is a lie with a button on it.

The suite was verified able to fail before being believed, per the `-MutateFirst` principle in the
README: marking `Export` as `MustFinish` failed 10 tests, and swapping the conflict and lock checks
failed exactly the one test that asserts that ordering.

One name changed from the plan above: `RepoSnapshot` became **`WorkingTreeSnapshot`**, because
`Versio.Core.Bindings.RepoSnapshot` already exists and means "what the repository scan found". The
new name is the better one anyway — everything in it describes the condition of the working tree.

### Phase 2, as built

**`BusyTracker` now owns the cancellation.** `Begin(WorkKind, string, CancellationTokenSource?)`
returns a `BusyScope` carrying the token, so a call site that has opened a scope cannot then forget
to pass one — the token is already in the variable it is holding. `IsBusy` and `Message` kept their
shape, so no existing binding moved. New: `Snapshot()`, `HasUninterruptible`, `CancelCancellable()`.

**Call sites migrated** — 15 existing scopes given a `WorkKind`, and **25 arguments now carry a real
token** (22 `busy.Token`, plus a linked source for the catch-up and one apiece for the automatic and
deferred sync runs).

**Twelve `CancellationToken.None` deliberately remain**, and they are not an oversight: short
read-only calls with no scope of their own — `LoadBranchesAsync`, the commit-patch and diff reads,
`RefreshTreeAsync`'s status read, the gate evaluations that run *before* the commit scope opens —
plus the fallback on the one scope that is nullable by design (an automatic refresh does not raise
the bar). None of them is a run worth cancelling, and inventing a scope for each would put the
progress bar back on every keystroke, which is a bug this codebase already fixed once.

Three findings came out of doing it:

- **A checkout had no busy scope at all.** The one operation that rewrites every file in the working
  tree was invisible to the progress bar and to anything asking whether Versio was busy, so two
  quick selections in the branch dropdown could overlap. It has one now (`GitWrite`).
- **The automatic export's token was unreachable**, and this is the one that mattered. A run is
  begun by the watcher but announces itself from the orchestrator's `RunStarted` event — after the
  call that started it, deliberately, so a refused or coalesced save never flashes a bar. A scope
  minting its own token there would have produced one connected to nothing, and *"stop it and
  continue"* would have silently done nothing for **the single most common case of something
  running**. `SyncViewModel` now registers a source per binding before the call, and the scope
  adopts it — which is what the `external` parameter on `Begin` is for. The tracker cancels an
  adopted source but never disposes it.
- **`RunDeferredAsync` had the same hole** and cannot say in advance which bindings it will run, so
  it registers a fallback the run scope uses when there is no per-binding entry.

**`CancelCancellable()` skips work that must finish**, rather than relying on the guard never
asking. The guard is the courtesy; the filter is the guarantee — this is the only method in the app
that can kill a CODESYS process tree.

**Cancellation stopped being an error.** `ReportError` reports `OperationCanceledException` at Info
as "Cancelled.", and the deferred sync catches it separately. A red error line for something the
user just asked for teaches them the button is broken.

**`GuardPromptWindow`** renders a verdict and returns a choice. It contains no `if` about locks,
exports or dirty trees — which options exist, their order and every word come from the guard and
the narrative. A `MessageBox` could not do this: the questions have three answers, and "Yes" is not
an answer to "stash or discard". The button row is the reverse of the verdict's order, so the
recommended choice lands on the right under the Enter key. A refusal has no choices, so it shows a
single OK that acknowledges without returning a decision.

**`ShellViewModel.Ask(intent, target)`** is the single entry point: it gathers the work snapshot and
the tree state, evaluates, logs the one-line narrative, and puts the question through the `AskGuard`
hook. Unset means Cancel, as unset already means "no" for `ConfirmDestructive`. **Nothing calls it
yet** — phases 3 and 4 do.

**18 new tests** (`Versio.App.Tests` 125 → 143). Verified able to fail: removing the
must-finish filter from `CancelCancellable` failed the test that asserts an import survives a
cancel-all. The first version of the button-mapping test was wrong about its own indices and was
rewritten to assert *every* button maps to its choice, since a test that pressed one button could
pass with the mapping inverted.

### Phase 3, as built

`MainWindow.OnClosing` exists. Closing Versio no longer kills the process out from under a hidden
CODESYS.

**The decision is in the view model, not the window.** `ShellViewModel.RequestCloseAsync()` returns
whether the window may go; the window's whole job is three lines — veto unconditionally, await the
answer, close again — because `Window.Closing` is synchronous and the answer is not. `_closeApproved`
stops the second pass asking again, which would otherwise ask a user who chose "wait" whether they
wanted to wait for the thing they had just finished waiting for. Putting it here is what lets the
whole matrix be tested with no window, no dispatcher and no dialog.

**`BusyTracker.WhenIdleAsync()`** is what "wait for it to finish" is made of. It re-checks `IsBusy`
*after* subscribing: operations end on threadpool threads, so the last one can finish between the
test and the subscription, and the notification would already have been raised to nobody. Waiting
on that would hang until something unrelated started and stopped — which on an app the user has
just asked to close is never. **This one guard is reasoned rather than tested**: the race cannot be
scheduled deterministically from a test, and a test that pretended to would only be asserting its
own timing.

**Watchers are released before waiting.** Otherwise a save settling during the wait starts a fresh
export and the app sits there working after being told to stop. `SyncViewModel.Quiesce()` stops the
watcher and the five-second deferred tick without `Detach()`'s clearing — a run is still in flight,
its completion still has to be reported, and the user is still looking at the window.

**`SessionEnding` does not go through the guard at all.** Windows gives a process a few seconds and
will not read a dialog, so `AbandonForShutdown()` cancels *everything*, including the import —
`CancelEverythingForShutdown()` is the one place `CancelCancellable`'s guarantee is deliberately set
aside. It is not a good outcome; it is the better of the two available, because the alternative is a
hidden CODESYS orphaned and still holding the project after its parent has gone. The shutdown is
**not vetoed**, though `SessionEndingCancelEventArgs` would allow it: an application that blocks a
shutdown to protect its own work is one people learn to force-quit, and the direction locks are
already on disk and recovered on the next open.

**One defect found by its own test.** `An_import_refuses_the_close_outright` failed on first run:
`Ask` returned whatever the `AskGuard` hook handed back, so a handler answering `Proceed` — which
the test does deliberately, as the most permissive thing it could say — closed the window over a
running import. **A refusal is not a question.** It is still shown, because the user has to be told
why nothing happened, but the answer is now discarded. The shipped dialog returns null for a
refusal's acknowledgement, so this changes nothing in the real path; it matters because the hook is
a delegate anybody can set, and the refusal has to *be* the decision rather than a strong suggestion
the caller is trusted to respect.

**9 new tests** (143 → 152). Verified able to fail: treating "Cancel" as permission to close failed
three of them.

#### The bug all of that missed

Every test passed and **Versio could not be closed at all.**

`RequestCloseAsync` returns synchronously when nothing is running — the overwhelmingly common
case — so the `await` never yielded and `Close()` ran inside the `Closing` event WPF was still
raising. WPF forbids exactly that:

```
InvalidOperationException: Cannot set Visibility to Visible or call Show, ShowDialog,
Close, or WindowInteropHelper.EnsureHandle while a Window is closing.
```

It surfaced on the dispatcher, where the crash handler logged it and marked it handled so the
window would survive — so the only symptom was a window that would not close. Found by running
the application, which nothing in phases 1–4 had done. The second close is now **posted** behind
the event rather than called inline.

Two things came out of it, and both are worth more than the fix:

- **`MainWindowClosingTests` is the only test in the suite that shows a window**, and it earns the
  exception: the re-entrancy state that makes the call illegal exists only inside a real `Closing`
  event. It is positioned off-screen and kept out of the taskbar. Verified able to fail — restoring
  the inline `Close()` fails both of its tests.
- **`StaHarness` now sets `ShutdownMode.OnExplicitShutdown`.** WPF ends an `Application` when its
  last window closes, so one test that shows and closes a window took the shared harness with it,
  and every test after it failed inside `InitializeComponent` with *"The Application object is
  being shut down"* — naming the victim rather than the cause.

### The audit before phase 4

A pass over everything phases 1–3 added, for organisation, comments and logging. Eight changes:

| Found | Done |
|---|---|
| `GuardVerdict.cs` held seven types and was named after one of them | Split: `WorkingTreeSnapshot.cs` takes the repository-state types, `GuardVerdict.cs` keeps the intent and the answer |
| A `<see cref="AskGuardAsync"/>` pointing at a method that never existed | Points at `Ask`. Crefs are unvalidated here — no documentation file is generated — so these rot silently |
| A comment claiming held locks were "empty until phase 7" | They were wired in phase 2. Now describes what it actually does, and says why it asks the orchestrator rather than reading the project rows |
| `Ask` was `internal`, overstating its reach | `private` |
| Every guard evaluation logged at Info | `Proceed` at Debug, `Ask`/`Refuse` at Info. Phase 4 puts this on every dropdown selection, and a line per "nothing was in the way" is how the log panel got drowned before |
| `RequestCloseAsync` did not say which way the user answered | It does |
| **`BusyTracker` had no logger at all** | Takes an optional `IVersioLogger`, defaulting to `NullLogger.Instance` as the orchestrator does |
| **A cancelled operation left no record of itself** | Named at Warning — `cancelled 'Exporting Machine…' on request`. Must-finish work stopped by a shutdown is named at **Error**, since that is the line somebody reads after a shutdown left a project that will not open |

That last pair is the one that mattered. The app could kill a CODESYS process tree and the only
trace was a count. "Why did my export not produce a diff?" now has an answer in the log, for the
same reason discarding a file and dropping a stash are both logged by name. **3 new tests** assert
the log lines, because logging that nothing checks is logging that quietly stops happening.

### Phase 4, as built

The dropdowns were the one way round every guard in the application. `Ready` gates every git button
on `Busy.IsBusy`; neither `ComboBox` consulted anything at all, and a selection went straight to a
checkout or a repository open underneath whatever was running.

**`CanChangeContext`** (`!Busy.HasUninterruptible`) is bound to `IsEnabled` on both. While an import
is writing into a project there is no safe answer to "switch now", so the question is made
**unreachable** rather than asked and refused — a control that accepts a click, opens a dialog and
snaps back is worse than one that was visibly unavailable. An export leaves them live: 17–31 s with
a real choice worth offering, and the guard offers it.

**Both dropdowns route through `ClearForContextChangeAsync`**, so they cannot come to differ about
what "something is running" means. Waiting and cancelling both end by waiting — cancelling is a
request, and the operation is over when its own `await` unwinds, not when the token trips.

**The dropdown snaps back whenever the switch does not happen** — refused, cancelled, or a checkout
git turned down. This was a real bug: a failed checkout left the control showing a branch the user
was not on, and every other panel disagreed with it silently. `CheckoutAsync`'s comment already
claimed a revert that nothing did; it now does it explicitly rather than relying on the refresh path
that happens to reload the branch list on some of its routes.

**`WithoutSideEffects`** replaced three copies of the suppression flag's try/finally. A revert *is*
a change, so without it the correction reads as a fresh request to switch.

**The dirty tree is deliberately not asked about yet.** `AskAboutWork` evaluates against
`WorkingTreeSnapshot.Clean` — a documented seam, not a shortcut. The guard's verdicts about a dirty
tree and a held lock both resolve to the auto-stash, which is phase 7; passing the real tree state
now would produce a *"stash my changes"* button that does nothing, and a button that lies about what
it will do is how somebody loses work believing it was saved. Until then a dirty checkout stays
git's to allow or refuse, which it does correctly. Phase 7 deletes `AskAboutWork` and its two
callers switch to `Ask`.

**"`OnRunFinished` must tolerate a detached view model"** turned out to need no defensive code. Both
paths through `ClearForContextChangeAsync` drain to idle before the switch, and a refusal blocks it
outright — so a context switch can no longer happen with a run in flight at all. `BuildOrchestratorAsync`
already detaches the old orchestrator's signals before attaching new ones. The hole is closed by
construction rather than by null checks that could never fire.

**16 new tests** (152 → 168). Verified able to fail: dropping the revert failed two, and pinning
`CanChangeContext` to `true` failed one.

### Phase 5, as built

`ResetForRepositoryChange(leaving, arrivingAt)` is the one method a repo-scoped field now has an
obvious home in. It runs **only once the switch is certain** — past the locator's refusal and past
git's own — so picking the wrong folder still costs nothing that was on screen.

`Sync.Detach()` finally has a caller. It was missing five things of its own, found while wiring it:

| Missed | Consequence |
|---|---|
| `DetachRunSignals()` | The old orchestrator kept handlers into this view model, so a run finishing late reported itself into the new repository's panel. `BuildOrchestratorAsync` detaches before attaching, which hid it — the leak only shows on a `Detach` with no rebuild behind it |
| `_lastStatus` | The next tree rebuild was handed the previous repository's file list to mark changes against |
| `TreeSearch` | A search typed about one repository's objects, applied to another's |
| `SelectedNode` | A selection pointing into a tree that no longer exists |
| `FailedProject` / `FailureDetail` | *Try again* offered for a project the new repository does not have, against a binding id that no longer resolves |

**The commit message is kept, per repository, in memory.** Silently binning somebody's typing is
the thing they actually notice. Deliberately **not** `repo.json` as the plan suggested — that file
is committed and shared, so a half-written message would be pushed to everyone else on the project.

#### The mutation that was not caught

Nine tests, all green, and deleting the call to `ResetForRepositoryChange` from
`OpenRepositoryAsync` **left every one of them passing.** They called the method directly, so they
proved it works and said nothing about whether opening a repository runs it — the identical shape
to the guard intents that were defined and never asked, found an hour earlier.

Closed by opening something real: `Versio.App.Tests` now takes `Xunit.SkippableFact` and one test
builds two throwaway git repositories, opens each in turn and asserts the reset happened. It skips
when git is absent, so the project still needs nothing but Windows. A second test does the same
**through a live `MainWindow`**, because the two halves were otherwise covered separately — real
git with no window, and bound collections with no git — and neither would notice a failure that
needs both.

**12 new tests** (168 → 180). Verified able to fail: removing the call site fails the real-repository
test and nothing else, which is the point.

#### Three intents that reached nothing

`GuardIntent` shipped with six members and three call sites. `Clone`, `Pull` and `Discard` were
defined, given labels, given verdicts, covered by theory tests — and called by nothing.

**The theory tests could not have caught it.** `Every_intent_has_a_verb_phrase` and its siblings
iterate `Enum.GetValues<GuardIntent>()` and assert each member has a label and a verdict. That
proves the guard *handles* every intent. It says nothing about whether anything *asks*, which was
the actual question.

- **`Clone` and `Discard` are removed.** `RewritesWorkingTree` is false for both, so the guard's
  verdict is exactly what the commands' own `Ready` check already enforces — dead at every phase,
  not just this one. Wiring them would have added a dialog in front of a button that is already
  disabled.
- **`Pull` is kept and documented as not-yet-wired**, the same treatment the stash client
  operations already get in the README: the behaviour is designed and tested, and what is missing
  is a caller. All three of its verdicts resolve to the auto-stash (phase 7). Meanwhile `PullAsync`
  is gated on `Ready` like every other git command, so it cannot run while anything else is —
  this was dead API, not an open door.

The enum's own doc comment now records the rule: every member has a caller, or says why it does
not.
