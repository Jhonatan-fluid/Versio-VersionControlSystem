# Versio — a UX pass over the running shell

**Date opened:** 2026-08-17 · **Status: a plan. Nothing here is built.**

This is a review of the app *as somebody uses it*, not as a set of features. Everything below
was read out of the markup and the view models — `MainWindow.xaml`, the three panels, the
stylesheet, `IGitClient` — and every claim carries the file and line it came from, because a UX
finding that cannot be pointed at is an opinion.

**What it is not.** It is not the human eyeball pass that [`PHASE2.md`](PHASE2.md) item 6 still
owes. Nothing here can see the screen. These are findings about *reachability, verbs and
counts* — what the app can and cannot be asked to do, and where it puts the answer — which is
the half a reader of the markup can settle. Colour, spacing and whether a card looks right
remain owed to a person.

**Two rules this list tries to keep.** Versio's git panel exists so that an engineer does not
open a terminal; every finding below is measured against that, and *"you can do it in git bash"*
is the failure, not the mitigation. And Versio is not a generic git client — where a gap is one
every git GUI has, it is ranked low; where a gap is one **only Versio could close**, because it
knows the mirror and the project are the same edit, it is ranked high.

---

## The queue, ranked

| | Finding | Cost | Why it ranks there |
|---|---|---|---|
| **UX-1** | **Merging a branch into the current one is not implemented at all** | medium | The one missing *verb*. And the one place Versio can beat a terminal outright |
| **UX-2** | The rail carries no counts, so the app is blind from two of its three panels | small | Cheapest large win; the user asked for it by name |
| **UX-3** | Branch actions are split across two surfaces, and half of them are panel-local | small–medium | `Delete branches…` is in the wrong place, and the fix is a group rather than a move |
| **UX-4** | `STAGED` and `NOT STAGED` do not say how many | trivial | A one-line lie of omission on the panel's two busiest headers |
| **UX-5** | The git panel's lists have no context menu and no double-click | small | The project tree got one this session; the panel about those same files did not |
| **UX-6** | Nothing in the git panel or the history can be searched | small | The project panel has a find box. The list with four hundred rows does not |
| **UX-7** | The keyboard reaches three things | small | Panel switching is the most frequent act in the app and has no key |
| **UX-8** | A commit in the history cannot be acted on, or even copied | small | Read-only by design is a decision; *un-copyable* is not |
| **UX-9** | `+2 more` is a dead end | trivial | A count with nothing behind it |
| **UX-10** | Detached HEAD says the sentence and does not offer the exit | trivial | §9e is built except for the half that was its point |
| **UX-11** | The change badge renders `0` on a clean tree | trivial | Furniture |
| **UX-12** | The last commit cannot be amended | small | Declined-or-built decision, not a gap to leave unstated |

---

## UX-1 · Merging a branch into the current one is not implemented at all

**Today.** `IGitClient` has `MergeUpstreamAsync` and `RebaseOntoUpstreamAsync`
([`IGitClient.cs:212`](../src/Versio.Core/Abstractions/IGitClient.cs), `:220`), and both are
reachable only through `ReconcileCommand`, which the git panel shows **only while the branch is
diverged from its own upstream** ([`GitPanelView.xaml:292`](../src/Versio.App/Views/Panels/GitPanelView.xaml)).
There is no `MergeBranchAsync`. So: finish `feature/alarm-rework`, switch to `main`, and there
is no way from inside Versio to bring the work in. The answer today is a terminal, which is the
thing the panel exists to remove.

**Why this one ranks first, and why it is not a generic git feature.** `GitAttributesPlan`
writes `binary -diff -merge` beside every `.project`
([`GitAttributesPlan.cs:101`](../src/Versio.Core/Git/GitAttributesPlan.cs)) — deliberately, so
git never line-merges a compound document into something that opens in neither CODESYS nor an
editor. The consequence is that **any merge touching a tracked project conflicts on the
`.project` by construction, while the mirror beside it merges cleanly.** That is not a problem
to be solved once; it is the normal shape of every merge in this repository.

And Versio is the only tool that can resolve it correctly, because it is the only one that knows
those two things are one edit. The resolution is not "pick ours or theirs" — it is:

1. take **either** side of the binary `.project`, since both are about to be overwritten;
2. let the mirror merge, and review it, which is the whole reason the mirror exists;
3. **import the merged mirror**, regenerating the `.project` from text a person has read.

A terminal cannot offer step 3. Neither can any other git client. That makes this the single
highest-value item in this document, on the same argument GP-J was ranked highest in the git
panel plan — *the reason to use Versio instead of a generic git client*.

**Proposal.**

- `Task<GitResult> MergeBranchAsync(string branch, bool noFastForward, IProgress<GitProgress>?, CancellationToken)`
  on `IGitClient`, implemented in `GitCliClient.Branches.cs` beside the merge and rebase that
  already exist there.
- A **`Merge a branch into <current>…`** action, in the branch group UX-3 proposes, opening a
  picker with the shape `BranchDeleteWindow` already has: local and remote branches, a
  `WhereText` column, and — free, because `MergedBranchesAsync` already exists and the delete
  dialog already calls it ([`IGitClient.cs:253`](../src/Versio.Core/Abstractions/IGitClient.cs)) —
  an *already merged* marker, so nobody merges a branch that is a no-op.
- Through **the existing guard**, not around it. `GuardIntent.Pull` already covers uncommitted
  work, the direction lock and IDE occupancy; a merge is the same class of event arriving from
  the same direction, and giving it its own path is how two answers to one question appear.
- The conflict section is **already built** and will pick the result up unchanged: `MergeState`,
  the conflicted list, `Keep mine` / `Keep theirs` / `Abandon`
  ([`GitPanelView.xaml:145–209`](../src/Versio.App/Views/Panels/GitPanelView.xaml)). Nothing new
  is needed there.
- **The new piece is step 3**, and it is where the care goes: after a merge that resolved a
  `.project` conflict, the project is stale against a mirror nobody imported. The existing
  pending-import banner is the right surface — it already says *"review the diff and confirm
  before importing"* — so the work is making a merge that touched a project raise it, rather
  than inventing a second confirmation.

**Cost:** medium. One git call, one dialog reusing an existing one, one guard wiring, and one
real decision about the post-merge import. **Value:** highest here.

**Tests.** Core: the merge picker's rows mark an already-merged branch and refuse the current
branch. Integration, against real git: a merge that conflicts leaves `MergeState` mid-merge and
the panel's conflicted list non-empty; a clean merge does not. App: the post-merge pending
import is raised for a merge that touched a project path and not for one that did not.
**Mutation check:** stop raising the pending import — the App test must go red, or the step that
justifies the whole feature is unwatched.

---

## UX-2 · The rail carries no counts, so the app is blind from two of its three panels

**Today.** The only count on screen is a pill in the **panel title**
([`MainWindow.xaml:465–478`](../src/Versio.App/Views/MainWindow.xaml)), and code-behind shows it
**only on the Project panel** ([`MainWindow.xaml.cs:348`](../src/Versio.App/Views/MainWindow.xaml.cs)).
The three rail buttons carry an icon and a tooltip and nothing else
([`MainWindow.xaml:405–430`](../src/Versio.App/Views/MainWindow.xaml)).

**Why it is wrong.** Sit on the History panel — reading what happened, which is exactly when
somebody is about to act — and the app tells you nothing about the working tree. Twelve changed
files, or none, look identical. The rail is app-level navigation and the one thing permanently
on screen; a navigation control that cannot say *there is something over here* is doing half its
job. The badge that exists is on the panel you are already looking at, which is the one place it
is least needed.

**Proposal.** An attached property on the rail button — `Rail.Badge` (int) and
`Rail.BadgeKind` — rendered as a small pill in the top-right of the existing 40×40 template
grid ([`Buttons.xaml:150`](../src/Versio.App/Theme/controls/Buttons.xaml)), which already has the
room and needs no relayout.

- **Git** — `Changes.Count`. Red rather than accent while `HasConflicts`
  ([`ShellViewModel.cs:2136`](../src/Versio.App/ViewModels/ShellViewModel.cs)), because a
  conflicted tree is a different fact from a busy one and the count alone hides it.
- **Project** — the existing panel-title pill, kept, and **suppressed at zero** (UX-11).
- **History** — nothing. The behind-count belongs to the branch card, which already has two
  buttons explaining it, and a third meaning of a badge would make all three unreadable.

**Suppressed at zero everywhere.** A rail permanently wearing three `0`s is the coverage line
this codebase has already argued out once: a caption that says the same thing every time is read
once and never again.

**Cost:** small. **Tests.** App: the badge is present at one change and absent at zero; the
brush switches on a conflicted tree. **Mutation check:** hard-code the count — the
zero-suppression test must fail.

---

## UX-3 · Branch actions are split across two surfaces, and half of them are panel-local

**Today.** *Create a branch* is a `+` in the top bar beside the branch dropdown
([`MainWindow.xaml:295–300`](../src/Versio.App/Views/MainWindow.xaml)). *Delete branches…* is a
right-aligned ghost button inside the git panel's branch card
([`GitPanelView.xaml:461`](../src/Versio.App/Views/Panels/GitPanelView.xaml)).

**Why it is wrong — and what the existing comment does and does not settle.** The comment above
that button argues, correctly, that a fourth button in the fetch/pull/push row overflows the
380 px panel and that a destructive act should not sit where `Fetch` sits. Both true. Neither is
an argument for **that** home. Two things are actually wrong with it:

- **It is split from its own family.** Create and delete are the same question — *which branches
  exist* — and they are on two different surfaces, one of which is reached through a rail
  button. The branch card is about *where this branch stands against its remote*, which is a
  different question and is exactly the reasoning the comment itself uses to keep the button out
  of the fetch row.
- **It is panel-local.** Deleting branches has nothing to do with staging, so it is unreachable
  from two panels for no reason a user could state.

**Proposal.** One branch group in the top bar, where the dropdown already is: the dropdown, `+`,
and an overflow `⋯` carrying **New branch…**, **Merge a branch into `<current>`…** (UX-1) and
**Delete branches…**. Destructive last and separated. This also gives UX-1 a home that does not
cost the 380 px panel a row, and it makes every branch verb reachable from every panel.

**One trap, already learned once this session.** A menu is a popup with its own HWND and does
not inherit the window's theme — the project tree's context menu needed its own style for
exactly this, and Aero's white slab is what appears otherwise. Reuse that style; do not
rediscover it.

**Cost:** small–medium. **Tests.** App: every branch verb is reachable with each of the three
panels active — the point of the move, and a test that fails today. **Mutation check:** re-hide
the menu behind the git panel; that test must go red.

---

## UX-4 · `STAGED` and `NOT STAGED` do not say how many

**Today.** `StagingSelection.Caption` returns the empty string unless something is ticked
([`StagingSelection.cs:90`](../src/Versio.Core/Git/StagingSelection.cs)), and it is the only
number beside either header ([`GitPanelView.xaml:846`](../src/Versio.App/Views/Panels/GitPanelView.xaml),
`:903`). So a forty-file stage reads `STAGED`, and the way to learn it is forty is to count it.

**Why it is wrong.** The count is the first thing anybody wants from either list and the one
thing a scrollable list cannot show. The commit button's caption carries a number — *"Stage &
commit (3)"* — so the panel already knows it and states it in the one place that is about the
*selection* rather than about the list.

**Proposal.** `STAGED · 12` standing, replaced by `3 of 12 selected` while anything is ticked —
the caption already renders that sentence, so this is a fallback rather than a new string.
Suppressed at zero, with the empty-state line already covering that case.

**Cost:** trivial, in Core, with the rest of `StagingSelection`. **Tests.** Core: the caption at
zero ticked, at some, and at all. **Mutation check:** return the empty string again at zero
ticked; the new Core case must fail.

---

## UX-5 · The git panel's lists have no context menu and no double-click

**Today.** The project tree grew Stage / Discard / Reveal in Explorer / Copy path this session
([`ProjectPanelView.xaml:237–249`](../src/Versio.App/Views/Panels/ProjectPanelView.xaml)). The
git panel's two lists have no `ContextMenu` and no `MouseDoubleClick` anywhere in the file.

**Why it is wrong.** Two lists of the same files, in the same app, answering right-click
differently — and the one that *cannot* is the panel whose entire subject is those files.
Double-click-to-stage is also the single most common interaction in every git GUI there is;
Versio asks for a tick and then a button press, every time.

**Proposal.** The tree's menu, reused rather than re-authored — plus **Open containing folder**,
which is the one entry a change list wants and a tree does not. Double-click on a row stages or
unstages it depending on which list it is in. Nothing about the tick/selection split changes:
`RowTemplates.xaml` already records why the checkbox is not the selection, and both new routes
act on the clicked row, not on the ticks.

**Cost:** small. **Tests.** App: double-click on an unstaged row stages exactly that row and
does not disturb the ticks — that last clause is the one worth writing, because it is the
regression this change can cause.

---

## UX-6 · Nothing in the git panel or the history can be searched

**Today.** The project panel has *Find an object or a file…*
([`ProjectPanelView.xaml:144`](../src/Versio.App/Views/Panels/ProjectPanelView.xaml)). The git
panel's only text boxes are the two identity fields and the commit message. The history panel
has none at all.

**Why it is wrong.** The lists that get long are precisely the ones with no filter. An import or
a branch switch across a real project produces hundreds of mirror rows, and the only way to find
one is to scroll. The history is worse: `Load older commits` will happily walk a branch back a
year, and there is no way to find anything in what it loaded.

**Proposal.** One find box over the staged/unstaged pair, matching the project panel's exactly —
same style, same 200 ms delay, same placeholder grammar. For the history, a subject/author
filter over what is loaded. **Not** a path filter on the history: per-file history is
deliberately out of scope and stated as such in item 1's close, and a box that filtered
*loaded* commits by path would look like the feature that was declined.

**Cost:** small each. **Tests.** App: filtering hides rows without changing what a stage-all
would take — the trap, and the reason the filter must never touch the ticks.

---

## UX-7 · The keyboard reaches three things

**Today.** `F5` refresh ([`MainWindow.xaml.cs:71`](../src/Versio.App/Views/MainWindow.xaml.cs)),
`Ctrl+Enter` commit, `Ctrl+Shift+P` push
([`GitPanelView.xaml:38`](../src/Versio.App/Views/Panels/GitPanelView.xaml)). That is the
complete list.

**Why it is wrong.** Switching panels is the most frequent thing anybody does in this window and
costs a mouse trip every time. §10.6 already established the pattern *and* the rule that makes
it worth doing — the shortcut goes in the tooltip, because the discoverability is most of the
value.

**Proposal.** `Ctrl+1/2/3` for the rail, on the window; `Ctrl+F` to whichever find box the
active panel has (UX-6); `Escape` to close the settings popup and the ahead/behind preview
strip. Each advertised in the tooltip of the control it drives, and — following the rule the
chrome test already caught once — **only where the control is live**, never on a dead one.

**Cost:** small. **Tests.** App: each key raises its command and does nothing while the command
is disabled.

---

## UX-8 · A commit in the history cannot be acted on, or even copied

**Today.** The history panel renders commits and their files and offers `Load older commits`
([`HistoryPanelView.xaml:29`](../src/Versio.App/Views/Panels/HistoryPanelView.xaml)). There is no
context menu, no command of any kind. The commit id is in a tooltip
([`HistoryPanelView.xaml:131`](../src/Versio.App/Views/Panels/HistoryPanelView.xaml)) and cannot
be copied out of it.

**Why it is wrong, and where the line is.** Read-only history is a defensible decision and
mostly the right one — this application's failure mode is destroying work, and `revert`,
`reset` and `cherry-pick` all belong on the far side of that line until somebody argues them
in. **Copying an id is not on that side.** It is the thing people want from a history panel
several times a day, the value is already on screen, and the only reason it cannot be had is
that a tooltip is not selectable.

**Proposal.** A context menu with **Copy commit id**, **Copy subject** and **Copy the whole
message** — three read-only entries, no state changed. `Branch from here…`, which is the only
*write* that fits the app's own safety posture (it creates, it destroys nothing), is worth
raising with the owner rather than assuming; `revert` and `reset` are deliberately not
proposed. Note that the existing comment on `TrackingState.Detached` says detached HEAD is
*"reached by accident, usually from clicking a commit in the history"* — no such route exists,
so that sentence describes an app this one is not. Worth correcting whichever way this lands.

**Cost:** small. **Tests.** App: the menu's entries put the expected text on the clipboard.

---

## UX-9 · `+2 more` is a dead end

**Today.** When more than three notifications are live the rest become a pill reading
`+2 more` ([`MainWindow.xaml:1128–1137`](../src/Versio.App/Views/MainWindow.xaml)). It is a
`Border` with a `TextBlock`. There is nothing behind it.

**Why it is wrong.** It is a count of things that happened, offered at the moment several
things went wrong at once — a network outage, a failing push retried — which is exactly when
somebody wants to read them. Every one of them *is* in the log, mirrored there by
`NotificationCenter.Post` deliberately, and nothing on screen says so.

**Proposal.** Make the pill a button: open the log panel, filtered to the notification
categories. The log filter, the category popup and the counts all exist already
([`LogPanelView.xaml:115`](../src/Versio.App/Views/Panels/LogPanelView.xaml)); this is a
`Command` and a call into the existing filter.

**Cost:** trivial. **Tests.** App: pressing it opens the log and sets the filter.

---

## UX-10 · Detached HEAD says the sentence and does not offer the exit

**Today.** `RemoteStanding` produces the amber sentence, and the gating works — with no upstream
recorded, `CanPull`, `CanPush` and `CanPublish` are all false, so the remote row is dead and
`Fetch` stays live, exactly as §9e specified
([`TrackingState.cs:171–183`](../src/Versio.Core/Git/TrackingState.cs)).

**What is missing** is the half §9e called the point: *"the create-branch action sits in the
badge, pre-filled, because that is the only route out and making the user find it in a menu is
the whole problem."* It is not in the badge. The only route out is the `+` in the top bar —
which is the menu §9e was arguing against.

**Proposal.** A `Put this on a branch…` button inside the standing sentence's own block, visible
only in `Detached`, raising `CreateBranchCommand`. Pre-filling the name is worth a moment's
thought and is not required for the fix.

**Cost:** trivial. **Tests.** App: the button is present in `Detached` and absent in every other
state.

---

## UX-11 · The change badge renders `0` on a clean tree

**Today.** The pill has no zero-suppression of any kind
([`MainWindow.xaml:465–478`](../src/Versio.App/Views/MainWindow.xaml)) — on the Project panel
with a clean tree it draws a pill containing `0`, beside a panel whose empty state already says
so in words.

**Proposal.** Collapse at zero, with UX-2, which is where the rule belongs anyway.

---

## UX-12 · The last commit cannot be amended

**Today.** `CommitAsync(message, ct)` takes no amend flag
([`IGitClient.cs:87`](../src/Versio.Core/Abstractions/IGitClient.cs)) and nothing offers one.

**Why it is here at all.** It is the most-reached-for git verb after `commit`, and the case is
narrow and common: a typo in the subject, thirty seconds after pressing the button. It is also
**history rewriting**, which is a genuine hazard once a commit has been pushed — and Versio
already knows which commits those are, because `UnpushedCommitIdsAsync` exists and the history
panel already marks them ([`IGitClient.cs:453`](../src/Versio.Core/Abstractions/IGitClient.cs),
[`HistoryPanelView.xaml:123`](../src/Versio.App/Views/Panels/HistoryPanelView.xaml)).

So the safe version is available for free: **offer amend only while HEAD is unpushed.** That is
a rule this app can enforce and a terminal will not.

Listed last, and listed rather than assumed, because it is a **decision for the owner** — the
same way §9f (undo a branch delete) was decided rather than backlogged. Either answer should be
written down, so a future session finds the decision instead of re-deriving it.

---

## Suggested order

**UX-2, UX-4 and UX-11 together** — one change to the counts, cheap, and the whole app reads
better afterwards. Then **UX-3**, because it is the home UX-1 needs. Then **UX-1**, which is the
real work and the reason this document is ranked the way it is. **UX-5, UX-6, UX-7** are an
interaction pass and can go in any order after that. **UX-8, UX-9, UX-10** are small and
independent. **UX-12** is a question before it is a task.

Nothing here touches `Sync/`, `Git/`, `Locking/`, `Watching/`, `Projects/` or `Diff/` in a way
that carries vendor knowledge, and nothing here needs CODESYS — **except UX-1's step 3**, which
raises the existing pending-import banner and therefore ends up in the half-hour suite before it
can be believed.
