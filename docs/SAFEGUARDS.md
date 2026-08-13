# Versio — safeguards

**Date:** 2026-08-12

| | |
|---|---|
| Document | **The interruption and destructive-action plan.** What Versio must refuse, ask about, or wait for — and why each answer is the one it is |
| Status | **Plan. Nothing here is built yet.** Every "today" below is a reading of the code at the commit this was written against |
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

1. **`Ready` guards buttons, not the dropdowns.** `Ready => _git is not null && !Busy.IsBusy`
   (`ShellViewModel.cs:938`) is consulted by every git command. Neither the repository nor the
   branch `ComboBox` (`MainWindow.xaml:86`, `:120`) binds `IsEnabled` to anything at all.

2. **There is no `Closing` handler anywhere in `Versio.App`.** `Exit += shell.Dispose()` releases
   watcher handles and returns. A CODESYS child launched with `CREATE_NO_WINDOW` is orphaned,
   invisible, and — if it was an import — halfway through writing a binary `.project`.

3. **Every UI call site passes `CancellationToken.None`.** The plumbing beneath is complete:
   `GitCliClient.RunAsync` links the caller's token (`GitCliClient.cs:767`), and the CODESYS runner
   links it and kills the process tree. Nothing at the top ever creates a token to pass down. The
   cancellation feature is fully built and entirely unreachable.

---

## 2. Shared machinery

Four pieces. Everything in §3–§5 is a consumer of these.

### 2.1 `Versio.Core/Guarding/` — a pure interruption gate

```
InterruptionGuard.Evaluate(GuardRequest, WorkSnapshot, RepoSnapshot) → GuardVerdict
```

| Type | Carries |
|---|---|
| `GuardRequest` | `Intent`: `SwitchBranch`, `SwitchRepository`, `CloseApp`, `Clone`, `Pull`, `Discard` |
| `WorkSnapshot` | Every in-flight operation, each with a `WorkKind` and an `Interruption` |
| `RepoSnapshot` | Dirty paths, conflicted paths, held direction locks per binding |
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
| Nothing to stash (clean tree) | The guard never asks in the first place — `RepoSnapshot` says the tree is clean and the verdict is `Proceed`. |
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

---

## 5. Other scenarios that can break

Ordered by how badly they end.

### A. A pull or checkout rewrites the project *and* the mirror under the watcher — *highest risk*

`git pull` changes both sides of a binding at once. The watcher fires, the hash gate correctly
agrees both changed, and the orchestrator can trigger an export or an import from **git's** write
rather than the user's. `SelfWriteSuppressor` covers only `ChangeSource.ExportedText` from our own
exports (`SyncOrchestrator.cs:404`) — it has no concept of a git-authored write.

→ A suppression window around every ref-moving git operation — `NoteExternalStart` already exists for
`StatusRefreshPolicy`, and the sync watcher needs its equivalent — and afterwards a re-baseline of
both hashes from the new HEAD rather than treating the difference as a user edit.

**This is the only item here that can corrupt a project through no user error at all.**

### B. Merge conflicts inside the mirror get imported into the project

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
| **1** | `InterruptionGuard`, `GuardNarrative`, `WorkKind`/`Interruption` on `BusyTracker`. Unit tests only, nothing wired | Pure and fast. Everything below depends on it. |
| **2** | A real `CancellationTokenSource` per operation; `GuardPromptWindow`; the `AskGuard` hook | The two mechanisms every scenario needs. |
| **3** | **Close guard** (§3.4) | Highest damage per line: an orphaned CODESYS mid-import is the worst outcome on this list. |
| **4** | **Context-switch guard** (§3.3) — dropdowns disabled for uninterruptible work, prompt for the rest | Second highest, and reuses phase 3 wholesale. |
| **5** | **`ResetForRepositoryChange`** (§3.2) | Self-contained, no new machinery, immediately visible. |
| **6** | Git-write suppression window (§5.A) and the conflict gate (§5.B) | Correctness, and both are **prerequisites** for phase 7 — see §4.8 and §4.6. |
| **7** | **Branch-switch guard with the full auto-stash** (§3.1, §4), plus the branch recorded in `BindingLock` | Needs the prompt from phase 2 and both defences from phase 6. |
| **8** | Swap via trash-rename (§5.D), staging in the ignore plan (§5.F), leftover sweep at open | Small, independent, and §5.D closes a real window. |
| **9** | Import confirmation made real (§5.C), backup restore UI (§5.E), read-only second instance (§5.G) | Features rather than guards. Each is its own conversation. |

Phases 6 and 7 are ordered deliberately: **the auto-stash cannot ship before the suppression window
and the conflict gate**, or restoring a stash becomes a way to trigger an unwanted import, and a
conflicted `pop` becomes a way to import conflict markers into a project.

Tests follow the existing split: guard and stash policy in `Versio.Core.Tests` (pure, ~0.3 s), the
prompt window in `Versio.App.Tests` on the STA harness, and the stash round trip, conflict gate and
suppression window in `Versio.Integration.Tests` against real git.
