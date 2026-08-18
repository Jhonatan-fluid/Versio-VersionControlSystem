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
| **UX-3** | The rare branch actions have no container, so each one lands as furniture | small | The fix is a shape, not a move — and it is where UX-1 lives |
| **UX-4** | The two list headers say no count, and run into each other once there is one to say | small | A live layout defect, **and a test named for it that cannot reach the failing state** |
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
- A **`Merge a branch into <current>…`** entry in the branch overflow UX-3 proposes, opening a
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

## UX-3 · The rare branch actions have no container, so each one lands as furniture

**Today.** *Create a branch* is a `+` in the top bar beside the branch dropdown
([`MainWindow.xaml:295–300`](../src/Versio.App/Views/MainWindow.xaml)). *Delete branches…* is a
right-aligned ghost button in its own row inside the git panel's branch card
([`GitPanelView.xaml:461`](../src/Versio.App/Views/Panels/GitPanelView.xaml)).

**What this finding used to say, and why it changed.** The first version proposed hoisting both
into one branch group in the top bar, on the argument that create and delete are the same
family. **Rejected by the owner, 2026-08-18**, and the counter-argument is better than the
proposal was:

> They are the same family and they are **not the same kind of act**. Creating a branch is
> frequent, cheap and reversible; deleting one is rare and destroys work. Grouping by family
> puts a rare destructive action at the same prominence as a frequent safe one — which is
> precisely the mistake the existing comment already rejects when it refuses to let `Delete
> branches…` sit where `Fetch` sits. Hoisting it to the top bar does not fix that mistake, it
> relocates it to a more prominent row. **Frequency decides prominence; family decides
> grouping**, and only one of those is a claim about where a control goes.

So `+` stays in the top bar, untouched. The rare actions stay in the git panel.

**What is actually wrong, then.** Not the panel — the *shape*. `Delete branches…` is a
permanent, top-level control in a 380 px column for something reached a few times a month, and
it is the only one of its kind so far. UX-1 adds a second. Every future infrequent branch verb
adds a third. There is no container for them, so each one arrives as another permanent button
and the card degrades one row at a time. The stashes section one border up already made this
argument and won it — *"the first version was six permanent controls in the panel's narrowest
column for something reached a few times a month"* — and the answer there was zero permanent
controls.

**Accepted trade, recorded so it is not re-derived.** Branch deletion stays reachable only with
the Git rail selected. That is a real cost — it is not a staging action and it is unreachable
from Project and History — and it is accepted, because an action taken a few times a month can
afford one rail click, and the alternative costs the top bar a permanent control.

**Proposal.** One overflow, in the slot `Delete branches…` occupies today — same row, same
right alignment, same quiet ghost styling, so the card's geometry does not move at all.

- **Label it for what it holds, not `More…`.** `Branch ⌄` — or `Branch actions ⌄` if the width
  allows. This codebase's own rule is that a caption says what will happen; `More…` says
  nothing and makes the contents unguessable, which is the one thing an overflow must not do.
  The tooltip names the entries, so the menu can be judged without being opened.
- **Contents when built:** *Merge a branch into `<current>`…* (UX-1) first, then a separator,
  then *Delete branches…* last. Destructive last and separated, the same order the branch
  delete dialog already uses for its own force checkbox.
- **The container is most of the value**, more than either entry. It is the standing answer to
  *"where does the next infrequent branch verb go"* — rename a branch, set upstream, copy the
  branch name, `fetch --prune`. None of those is proposed here. Naming the home means none of
  them needs its own placement argument later, and none of them lands as another permanent
  button.

**One risk worth stating rather than discovering.** UX-1 is a verb users currently believe does
not exist, and putting a brand-new verb behind an overflow on day one is how it stays
undiscovered. Three cheap defences, in order: the label names the subject; the tooltip names the
entries; and **promotion is one line if it turns out to be wanted often** — the merge-request
button directly below is already a full-width row in this same card, at exactly the same
once-per-branch frequency, so the precedent and the shape both exist. Ship it in the menu, and
move it out if the owner finds themselves hunting for it.

**One trap, already learned once this session.** A menu is a popup with its own HWND and does
not inherit the window's theme — the project tree's context menu needed its own style for
exactly this, and Aero's white slab is what appears otherwise. Reuse that style; do not
rediscover it.

**Cost:** small. One control replacing one control, plus the menu's entries.

**Tests.** App, in `GitPanelChromeTests`: the overflow exists, opens, and each entry raises the
command it names. In `GitPanelGeometryTests`: the branch card's realised height is unchanged
against today's — the whole point of reusing the slot, and the assertion that catches a menu
that quietly grew a second row. **Mutation check:** put `Delete branches…` back as a bare
top-level button; the chrome test asserting that no command which destroys work sits at the
card's top level must go red. Write that test as the rule rather than as a name-check, so the
third and fourth rare verb are covered by it too.

---

## UX-4 · The two list headers say no count, and run into each other once there is one to say

Two halves of one row, and they must be fixed together — because **fixing the first alone makes
the second worse.** Adding a count to a header that already overflows widens the thing that
overflows.

### 4a · The headers do not say how many

**Today.** `StagingSelection.Caption` returns the empty string unless something is ticked
([`StagingSelection.cs:90`](../src/Versio.Core/Git/StagingSelection.cs)), and it is the only
number beside either header ([`GitPanelView.xaml:846`](../src/Versio.App/Views/Panels/GitPanelView.xaml),
`:903`). So a forty-file stage reads `STAGED`, and the way to learn it is forty is to count it.

**Why it is wrong.** The count is the first thing anybody wants from either list and the one
thing a scrollable list cannot show. The commit button's caption carries a number — *"Stage &
commit (3)"* — so the panel already knows it, and states it only in the place that is about the
*selection* rather than about the list.

### 4b · The header controls collide once the labels grow — reported from the running app

**Today**, the header of each list is a four-column grid — `Auto | Auto | * | Auto`
([`GitPanelView.xaml:830`](../src/Versio.App/Views/Panels/GitPanelView.xaml), and the same shape
at `:889`): checkbox, section label, caption, action buttons.

**The mechanism.** A WPF `Grid` measures its `Auto` columns against infinity and hands each one
its full desired width; the `*` column gets whatever is left, floored at zero and never
negative. So when the `Auto` columns together want more than the row has, **the star column
collapses to nothing and the Autos keep their width anyway** — the grid's desired width exceeds
its slot, and the rightmost children are laid out past the panel's right edge, over the
splitter and the diff pane. Nothing here clips, so nothing stops it.

**And every input to that sum grows with the number of changes**, which is why it appears when
the lists fill and not before:

- The button labels are counts, not fixed strings.
  `Label` renders `"{verb} {selected}"` ([`StagingSelection.cs:73`](../src/Versio.Core/Git/StagingSelection.cs)),
  so `Discard` becomes `Discard 127` and `Stage` becomes `Stage 127`. The unstaged header
  carries **two** of those side by side.
- The section label cannot give way. `NOT STAGED` sits in an `Auto` column in the letter-spaced
  `SectionLabel` face, with no trimming and no ceiling — so it is rigid while everything around
  it is growing, and it is by some distance the least informative thing in the row.
- The column is narrower than the test believes. `PanelColumn` is `Width="380" MinWidth="260"`
  ([`MainWindow.xaml:436`](../src/Versio.App/Views/MainWindow.xaml)) and the width is
  **persisted** ([`MainWindow.xaml.cs:245`](../src/Versio.App/Views/MainWindow.xaml.cs)), so a
  panel dragged narrow once stays narrow across every later launch. 120 px of the budget can be
  gone before a single row is drawn.

**This is a defect with a test that is named for it and cannot see it.**
`GitPanelGeometryTests.Nothing_overflows_the_panel_sideways` walks every visible child and
asserts none crosses the panel's right edge — precisely this failure. It passes, for three
independent reasons, and each one is worth writing down because the same trap is available to
the next fixture:

1. **It measures one width.** `PanelWidth = 380`
   ([`GitPanelGeometryTests.cs:38`](../tests/Versio.App.Tests/GitPanelGeometryTests.cs)), and
   the class comment calls it *"the panel's real column width"*. It is the **default** width, not
   the only one — the control is user-resizable to 260 and remembers it.
2. **Nothing in the fixture is ticked.** It adds 4 staged and 12 unstaged rows and never sets
   `IsSelected`, so every button renders its `"{verb} all"` form and **both captions are the
   empty string**. The widest state the header can reach is not merely untested — it cannot be
   constructed in this fixture at all.
3. **The empty caption is then skipped by the assertion itself.** The sweep filters on
   `.Where(e => e.Visibility == Visibility.Visible && e.ActualWidth > 0)`. An empty `TextBlock`
   measures zero wide, so the one control in the row *designed to absorb the slack* is excluded
   from the only test watching the row. And twelve rows caps every count at two digits, where a
   real import produces three.

**This one gets a Finding when it is fixed**, at the top of `FINDINGS.md` with the index line in
`STATUS.md` — not for the overlap, which is ordinary, but for the shape of the miss: a test
written for exactly this failure, passing because its fixture cannot reach the failing state.
That is the same class as the pinned wording that went on asserting the old sentence while the
script grew `remove()`.

### The proposal, for both halves

**One column instead of two, and let it trim.** The section label and the caption become a
single run in the star column — `STAGED · 12`, and `STAGED · 3 of 12 selected` while anything
is ticked — with `TextTrimming="CharacterEllipsis"` and a `MinWidth` so it can shrink without
vanishing. That delivers 4a's count as a side effect of 4b's fix rather than as an addition to
it, and it removes an entire rigid `Auto` column from the row. Suppressed at zero, where the
clean-tree line already speaks.

**A ceiling on the buttons, and a fallback that is a rule rather than a layout trick.** Past
that ceiling the labels drop to the bare verb — `Stage`, `Discard` — because by then the count
is in the header, one control away. The fallback belongs in `StagingSelection` in Core, beside
`Label`, so it is asserted without a window.

**But `Stage all` and `Discard all` never abbreviate.** `Label`'s own doc comment carries the
safety argument — *"a button reading 'Stage' over a list of forty with three ticked is
ambiguous in the one direction that costs something"* — and that argument is about the
**nothing-ticked** case, where the button silently acts on everything. That is the dangerous
label and it stays verbatim. The one that abbreviates is `Discard 127`, which is both the widest
and the *safe* case: the count is explicit in the header and the ticks are on screen.

**`ClipToBounds` on both header grids, as the backstop.** Not the fix — a fix that relies on
clipping is a fix that hides its own failure — but the guarantee that a label nobody predicted
produces a clipped button instead of text painted across the diff pane. **The status bar already
took exactly this lesson**, has `ClipToBounds="True"` on `StatusState` and a comment recording
why ([`MainWindow.xaml:1181`](../src/Versio.App/Views/MainWindow.xaml)); the git panel's headers
never got the same treatment. Carry the reasoning across, do not re-derive it.

**Cost:** small — one Core rule, one column merge, two attributes. The test work is the larger
half and is worth more.

**Tests — the fixture first, before any markup moves.**

- Fix `GitPanelGeometryTests` before fixing the panel, and **watch it go red**: parameterise the
  width over `380` **and** `260`; tick a subset so both captions render and the labels carry
  counts; use enough rows for a three-digit count. A layout fix landed against a fixture that
  could never fail is a fix nobody can show working.
- Drop the `ActualWidth > 0` filter, or narrow it to genuinely collapsed elements. It is there
  to skip controls that are not laid out, and it is silently skipping one that is.
- Core: the caption at zero ticked, at some and at all; the label's abbreviation threshold.

**Mutation check:** put the rigid `Auto` label column back. The 260 px case must go red — and if
it does not, the fixture is still not reaching the state the user is reporting, and nothing
below it is worth believing.

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

**UX-4 first, on its own**, and it has been promoted out of the cheap-counts group since the
first draft: it is the only item here reported from the running app rather than read out of the
markup, it is a live layout fault a user is hitting today, and its fixture work has to happen
before any of it can be shown working. Start by making
`Nothing_overflows_the_panel_sideways` fail.

**Then UX-2 and UX-11 together** — one change to the counts, cheap, and the whole app reads
better afterwards. UX-4's header merge has already delivered the third count by then.

Then **UX-3 and UX-1 in that order but in one sitting**, not one release apart. UX-3 is the
home UX-1 needs, and on its own it is a menu holding a single entry — which is strictly worse
than the button it replaced, since it costs a click to reach the same thing. The container only
pays for itself once there are two things in it, so land the overflow and the merge verb
together, as two commits and one change of shape.

**UX-5, UX-6, UX-7** are an interaction pass and can go in any order after that. **UX-8, UX-9,
UX-10** are small and independent. **UX-12** is a question before it is a task.

Nothing here touches `Sync/`, `Git/`, `Locking/`, `Watching/`, `Projects/` or `Diff/` in a way
that carries vendor knowledge, and nothing here needs CODESYS — **except UX-1's step 3**, which
raises the existing pending-import banner and therefore ends up in the half-hour suite before it
can be believed.
