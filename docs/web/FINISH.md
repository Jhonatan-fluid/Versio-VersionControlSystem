# Versio Web: finishing it — the gap list and the order

**Date:** 2026-10-23 · **This is the live queue for Versio Web.** Where each gap stands lives
here; the design records stay in [`PLAN.md`](PLAN.md) (the read-only viewer, the product shape,
the six review slices) and the map of what is built in [`ARCHITECTURE.md`](ARCHITECTURE.md).
Strike a line through when it lands, with the commit; never delete it.

**Why this file exists.** The six pull-request-review slices were "built" (STATUS item 10), and
the owner then looked at the page on a real pull request — `fluidstackio/CodesysStandardLibrary#3`,
an FBS library, 85 files, served by `tests/Versio.Web.Tests/PullPreview.cs` from a
`tools/web/dump_pull.py` dump — and found it far from a product. The list below came from that
look, three code audits (the page, the server, the desktop's rendering against the web's) and the
docs. Everything marked *(seen)* was seen on that preview on 2026-10-23.

**The decision this records.** The page is to **look and feel like github.com** — the
repository page, the pull request page, the list pages — with the desktop's *rendering* inside it.
That **reverses the 2026-10-14 workbench restructure** in [`PLAN.md`](PLAN.md) (the desktop's
rail and panel card on the web); the rendering stays, the shell goes. The page says **"Pull
request"**, GitHub's word, not "Merge request". **Head-branch deletion is in scope**, with its
race stated (no atomic precondition on GitHub: re-read the tip, refuse unless it is the merged
commit, say the window remains, honour the repository's auto-delete setting).

---

## A. What was seen on the real pull request (2026-10-23)

1. (seen) **Files changed is a flat list of 85 names.** No folders, no project, no object grouping.
   Eight rows are `.folder_meta.json`; `StateMachine`, `UpdateState`, `Reset`, `WriteLog` each
   appear twice; nothing says which FB they belong to without hovering.
2. (seen) **One file at a time.** The pane says "Pick a changed file." GitHub stacks every diff on
   one scrolling page with sticky per-file headers.
3. ~~(seen) **The coverage banner is noise on real code.** FB_Alarm lists 12 "changed, but not shown as
   changed" lines; Symbol Set (a *new* file, every row drawn with an A mark) says "lines 1–780:
   changed, but not shown as changed"; the review dialog says "55 files with changes the drawing may
   not show" out of 85. The rule (C4: XML tables never vouch for a line) is working as written and
   reads as "the drawing is wrong". A whole-file add/delete, a rename, and a row drawn A/D/M whose
   whole source line is that row must count as shown.~~ **Step 4, 2026-10-23:** the six rules in
   Core's `Coverage`; FINDINGS 2026-10-23 has the measurement on the real pull request.
4. ~~(seen) A method whose header changed (`METHOD` → `METHOD PUBLIC`) reports the same gap twice ("as
   it was", "as it is"), and the empty member table above the code carries "1 declaration change is
   not shown in this table" — the header line is *the* declaration, and nothing draws it.~~ **Step 4:**
   the code block vouches for the header it draws, and one line is one gap.
5. (seen) **Long lines are clipped at the pane edge** on Raw and Rendered source (no horizontal
   scroll). Member tables get a scrollbar but DEFAULT VALUE / COMMENT run off the pane; the TYPE
   column overflows into the next on wide types (`E_AlarmState := E_AlarmState.Off`).
6. (seen) **Member tables have large empty gaps between rows** (every row reserves an attribute /
   comment line even when empty) — see `MemberTableHtml`.
7. ~~(seen) **`Communication Manager`, `OPC UA Server`** (device/application objects) draw as raw
   `__V3_META__` JSON + XML, under the note "Every change in this object is shown in the drawing".~~
   **Step 9:** `RenderChoice` gives them the "stores no settings" note on both hosts.
8. (seen) The project tree says **"This repository is not set up for Versio, so it is shown as plain
   folders"** while drawing an FBS library with Devices / POUs panes. The notice is keyed on
   `.versio/repo.json`; an FBS project is a Versio project without one.
9. (seen) **Commits tab**: rows with no links, no avatars, no status, no count.
10. (seen) **Checks tab**: "No checks ran on the version you are reviewing." and nothing else.
11. (seen) **Conversation**: review threads are absent from it; approvals have no avatar; absolute
    `toLocaleString` times; no reactions, no "edited", no role badges.
12. (seen) **Header**: the Open/Draft/Merged badge is in the side panel, not beside the title; no
    `+N −M`, no author/avatar, no "wants to merge N commits into main from branch"; branch names are
    not links; a `main` branch picker sits in the top bar on a PR page.
13. (seen) **Merge box**: a `<select>` of methods and a blue "Merge…" under the comment box; no list of
    reviews / checks / requirements; a closed PR shows nothing.
14. (seen) **Pulls list**: no open/closed toggle, no filters, no search, no counts, no check or
    comment icons; a row opens a side pane with a "Review" button instead of the PR.
15. (seen) Everything is called "Merge request" (GitLab's word). GitHub says "Pull request".
16. (seen) **History** panel reads "Tester · 12/31/1969" — a preview-dump artefact (the dump carries
    no commit metadata), but it also shows History reads only headline / author / when.
17. (seen) Type links in member tables work (FB_Alarm → ST_AlarmConfiguration) but leave the review
    for `/blob/<headSha>/…`.
18. (seen) Mobile: the gap list's bullets sit centred under their text; tabs and metadata live in
    a drawer.

---

## B. The pull request page — every GitHub feature, where it stands

Legend: **missing** = nothing; **partial** = exists but short of GitHub; **ok** = at parity.
File refs: `P` = `web/src/pages/PullPage.tsx`, `RB` = `components/ReviewBar.tsx`,
`MB` = `components/MergeBox.tsx`, `PM` = `components/PullMeta.tsx`, `Src` =
`src/Versio.Web/Sources/GitHubRepoSource.cs`, `PE/PW/PM/PMg` = `src/Versio.Web/Api/Pull*Endpoints.cs`.

### B1. Header and state
- ~~**missing** State badge (Open / Draft / Merged / Closed, with icon) beside the title; it is a pill in
  the side panel (`P:443`).~~ `StateBadge` under the title (Step 1).
- ~~**missing** "`author` wants to merge N commits into `base` from `head`" line, with avatar, branch
  names as links to the tree and a copy-branch button (`P:356` is plain text).~~ Step 2: the line with the picture and the commit count; the branch names are not links yet.
- ~~**missing** `+N −M` additions/deletions, changed-files and commits counts (server never reads
  `additions`, `deletions`, `changed_files`, `commits`, `comments`, `review_comments` — Src:1070-1078).~~ Step 2: read from the summary; the diffstat beside the tabs, the counts on the tabs and in the meta line.
- **partial** ~~Lock conversation / locked state~~ (the locked state and reason are shown, Step 2; locking and unlocking, Step 5); pin; ~~subscribe~~ (Step 5) / notification reason.
- **partial** Linked issues ("Closes #12", Development sidebar); ~~milestone~~ (shown in the sidebar, Step 2; set from it, Step 5); projects; participants.
- **partial** Title edit exists (`PM:47-66`) but is a link + input, not GitHub's inline editor.
- ~~**missing** Per-page `document.title` ("Title by author · Pull Request #3 · owner/repo").~~ `usePageTitle` on every page (Step 1).
- ~~**missing** Relative times ("2 days ago") with an absolute tooltip; everywhere `toLocaleString`.~~ `RelativeTime` on the pull request page, the lists and the commits (Step 1–2).
- ~~**missing** Avatars anywhere except the signed-in user.~~ Step 2: the author, every comment, review and commit, the signed-in person beside their box; a letter where GitHub gave no picture.
- ~~**wrong** The top bar's branch `<select>` on a PR page (`P:288-292`).~~ Gone with the shell (Step 1, 2026-10-23); the picker lives in the code and commits views.
- ~~**wrong** "Merge request" wording throughout (`Workbench.tsx:102`, `P:294,342,365`,
  `PullsPage.tsx:50,54`, `NewPullPage.tsx:132,149,193`, `PM:105`).~~ "Pull request" everywhere (Step 1).

### B2. Tabs
- ~~**partial** Four tabs exist but as vertical side-panel buttons (`P:297-304`, `.review-tabs`), not
  GitHub's horizontal tab bar under the title with counters.~~ `UnderlineNav` under the title with the Checks and Files counts (Step 1); Conversation and Commits counts wait on the server's `comments` / `commits` fields (B12).
- ~~**missing** URLs `/pull/N/commits`, `/pull/N/checks`, `/pull/N/files` (today `?tab=`; a pasted
  GitHub-shaped URL lands on Home — `main.tsx:33` falls to Home, there is no 404 page).~~ Step 1: the three routes, `?tab=` redirects, a 404 page.

### B3. Conversation
- ~~**missing** Review-comment threads inside the timeline under their review, with the diff hunk
  excerpt (GitHub shows them; here only on Files changed).~~ Step 2: a review's threads (those whose first comment names it) under its card, hunk first.
- **partial** Timeline event kinds: the server asks only ISSUE_COMMENT, PULL_REQUEST_REVIEW,
  PULL_REQUEST_COMMIT, HEAD_REF_FORCE_PUSHED, MERGED, CLOSED, REOPENED, READY_FOR_REVIEW,
  CONVERT_TO_DRAFT (`Src:705-706`). Missing: LABELED/UNLABELED, ASSIGNED/UNASSIGNED,
  REVIEW_REQUESTED/REMOVED, REVIEW_DISMISSED, RENAMED_TITLE, BASE_REF_CHANGED/FORCE_PUSHED,
  HEAD_REF_DELETED/RESTORED, CROSS_REFERENCED/REFERENCED/MENTIONED, MILESTONED, LOCKED/UNLOCKED,
  AUTO_MERGE_ENABLED/DISABLED, merge-queue, DEPLOYED, CONNECTED, COMMENT_DELETED.
- ~~**wrong** Unknown kinds print their raw word ("jo merged …" from `: item.kind`, `P:557`); no
  icons; force-push shows no "from X to Y" compare link (`Src` reads `afterCommit` only).~~ Step 2: every kind the server reads has its sentence and badge; force-push names both commits. Still: commits are not grouped ("X added 3 commits"), and the force-push's "from X to Y" is text, not a compare link.
- **partial** ~~Comment card chrome: avatar beside the card, grey header "author commented 2 days
  ago", role badge (Author / Member / Owner / Contributor — `authorAssociation` not read), "edited" marker~~ (Step 2: `CommentCard`). Still missing: the "…" menu (copy link, quote reply, hide with reason, report) and the edit history.
- ~~**partial** Reactions on the PR body, on comments, on reviews: ~~read~~ (Step 2, drawn as pills); writing them is Step 5.~~ Step 5: each pill a toggle, GitHub's picker to add one.
- **partial** Permalinks: ~~`#issuecomment-…`, `#pullrequestreview-…`, `#discussion_r…`~~ each card and comment carries GitHub's own fragment as its id, with a link to it (Step 2); the page does not yet scroll to the hash on load, and `#discussion_r…` on Files changed does not pick the file.
- **missing** Edit / delete of a *review* summary; reviews have no id (`Src:765-767`).
- **missing** Hide / minimise a comment (`isMinimized`, `minimizedReason` not read; no mutation).
- **missing** Mention autocomplete (@user), issue autocomplete (#123), emoji (:smile:), Markdown
  toolbar (bold, italic, code, link, list, task list, quote, mention, reference, saved reply),
  drag-and-drop / paste attachments, Ctrl+Enter to submit. The box is a bare textarea with a Preview
  toggle (`ReviewWrite.tsx:171-193`).
- ~~**wrong** The merge box sits under the new-comment box (`P:393-396`); GitHub puts the merge box
  *above* the comment box at the end of the timeline~~ (Step 2, with a merge summary — reviews, checks, conflicts — for every reader). Still: "Close pull request" / "Close with comment" beside "Comment".

### B4. Commits
- **missing** Links: to the commit page (`/commit/<sha>` does not exist), to "review this commit
  only" (`/pull/N/commits/<sha>`), to a range (`/pull/N/files/<a>..<b>`).
- **partial** ~~Avatar, author/committer distinction, verified-signature badge, message body on
  expand~~ (Step 2), co-authors, copy SHA, per-commit check status, grouping by day. The sha links to the commit on GitHub until `/commit/<sha>` exists (Step 8).

### B5. Checks
- **partial** Rows carry name, status/conclusion word, glyph and an external "Details" link
  (`P:610-638`). Check data is name, status, conclusion, url, source (`Src:846-913`).
- **partial** ~~Grouping by app, app name, duration and timestamps, `output.title/summary`, the annotation count, "Required" badge (from the live head's `statusCheckRollup`), distinct icons per conclusion, the "N successful, M failing, K in progress" summary~~ (Step 2). Still missing: the annotations themselves (and inline in the diff), the app's avatar, re-run / re-run failed, a log page inside Versio, deployments / environments, merge-queue state.
- **partial** Classic commit statuses are read and merged into the same list (`source` unused on the
  page); `description`, `creator`, times not read.
- **wrong** Checks are read for the snapshot head only; GitHub shows the live head's checks and says
  when the branch moved.

### B6. Files changed
- ~~**missing** All files on one page, each under a sticky header with: chevron (collapse), `+N −M`,
  path with copy button, Viewed checkbox~~ (Step 3, 2026-10-23: `FileSection`, every file stacked and loaded as it comes into view; a viewed file folds). Still missing: a copy-path button and the "…" menu (View file, Edit file, Delete file, Open on GitHub).
- ~~**missing** A **file tree** sidebar grouped folder → object with per-node A/M/D/R marks, a filter box, "jump to file", viewed progress
  ("12 / 85 files viewed")~~ (Step 3: `FilesTree`, an object's own files nested under it; `+N −M` and thread counts per file). Still missing: grouping by *project* with the vendor's pill (Step 8/9, from the grown tree node), filtering by extension / owner / viewed.
- ~~**missing** Per-file additions / deletions (not in the inventory, `PE:131-148`).~~ Step 3: counted from GitHub's patch; null without one.
- **partial** Expand-hunk arrows (↑ ↓ per hunk, "expand all"): a click on a folded stretch asks for the whole file (Step 3); per-hunk expansion needs Core's fold rows to carry a range.
- ~~**missing** Multi-line comments (click-drag or shift-click a range); drafts are one line
  (`P:683`), though the server accepts `start_line` on the same side (`PW:111`).~~ Step 3: shift-click a second line of the same side and file; posted with `startLine`.
- **missing** Comments on expanded / unchanged lines outside GitHub's hunks (refused
  `line-not-in-diff`, `PW:1034`); GitHub allows them within ±N lines.
- **missing** Suggested changes: insert a suggestion block from the comment toolbar, render a
  suggestion as a mini-diff with "Apply suggestion" / "Add to batch" / "Commit suggestions"
  (PLAN "later, on purpose"; needs Contents write, same tier as merge).
- **missing** Diff settings menu: hide whitespace (the server has no `w=1`), show/hide comments,
  show/hide annotations, persisted unified/split preference. Rendered/Raw and Unified/Split exist.
- **missing** Commit range selection ("changes from all commits" / "since your last review" /
  one commit) inside the tab; today only an external compare link.
- ~~**missing** "Show resolved" toggle; resolved threads stay expanded and dimmed (`app.css:268`).~~ Step 3: per file, "Show N resolved threads", on by default.
- **missing** Outdated-thread view with the original hunk; outdated threads are only listed as
  "unplaced" (`P:817-826`).
- **missing** Resolve-by / resolved-at; thread "collapsed" state.
- **missing** Find in diff; keyboard shortcuts (`j/k`, `n/p`, `t`, `c`, `?`).
- ~~**partial** Viewed checkbox exists (`P:321,751`) but collapses nothing and shows no progress.~~ Step 3: a viewed file folds, the tree counts "N / M files viewed".
- **partial** In split view a thread slot is inserted after the row, not into its side's column
  (`FragmentHost.tsx:74-95`).
- ~~**wrong** Rename notice text "The drawing does not yet compare against the old path" (`P:798`) is
  stale: the server does compare (`PE:153`, `VS:145-185`).~~ Step 3: the header says "renamed from …" and nothing claims otherwise.
- **partial** ~~"where it lives" for each file~~ (Step 3: the header reads object, then folder › folder › block); still no link to the object's node in the repository tree.
- ~~**missing** Type links that stay inside the review when the target is itself a changed file.~~ Step 3: a link to a changed file goes to its section; to anything else, to the object at the reviewed commit.

### B6b. Comments — where one can go today, where it cannot, and how it lands on a GitHub line

How a comment reaches GitHub today (keep this, it is right): a click names a drawn element
(`data-anchors` id) or a file line; the server re-renders the file, finds the element's **source
layer and its file lines** (`AnchorPreconditionAsync`, `PW:969-999`), intersects those lines with
**GitHub's own hunks** for that layer (`HunkLines.Placement`, picks the hunk holding most of the
element's lines, as a range ending on its last line), posts there with the `versio:anchor` tag, or
on the **whole file** when the diff shows none of the element's lines. Placing it back uses the tag
(Exact / Moved / Ambiguous / Gone, `AnchorMatcher`) and never lets a tag beat a disagreeing current
line. Raw line comments are checked with `HunkLines.Accepts` and refused `line-not-in-diff`.

| Place | Today | Lands on GitHub as | Gap |
|---|---|---|---|
| Conversation | yes (`/comments`) | issue comment | ~~reactions, hide~~ (Step 5); quote; ~~permalink~~ (Step 2) |
| PR description | edit only | — | ~~reactions~~ (Step 5, on the pull request's node) |
| Whole file | yes (`subject_type=file`) | file-level review comment | — |
| Raw diff line inside a hunk, either side | yes | line + side | multi-line (shift-click / drag) not on the page (server takes `start_line`); no comment on a **folded** line until "Show full file" |
| Raw line outside GitHub's hunks (expanded context) | refused, on purpose (Step 3b) | — | GitHub's REST takes a line only inside a hunk of its own diff (`422 pull_request_review_thread.line must be part of the diff`); github.com's own expanded-context comments go through a private endpoint. So the page keeps refusing `line-not-in-diff` and says so on the line; **the real-GitHub gate (Step 10) confirms the 422 on a throwaway repository** before this row is closed |
| Member-table row (name / mark cell), nested member, section heading | row yes; heading no | the row's lines ∩ hunk of its **layer file** (`.gvl.xml`, `.st` …) | ~~the old side of a *modified* row (comment "as it was") is not offered~~ Step 3b: a modified row's draft offers "comment on it as it was instead", posting the `r-old-N` anchor; section headings and the table caption (the object itself) still have no anchor — a comment on the object goes on the whole file |
| Attribute chip / pragma line | no | — | the chip's line is a changed line the table now draws; it should take a comment and vouch for coverage |
| Ladder / FBD network heading | yes (`net` anchor) | the network's lines ∩ hunk | — |
| Ladder / FBD **element** (contact, coil, box, operand, jump label) | no (deferred) | lands on the network | parsers must record per-element spans (CODESYS `.xml`/`.v3`, TIA SimaticML both have line info); anchors kind `el` with container = network; drawn marks in the SVG; thread drawn beside the network with the element named |
| Drawn ST source line under a table | yes (`data-file-line` + `data-src-path`) | that file's line | — |
| ST declaration drawn **as a table row** when the person wants the line | n/a | row anchor | fine |
| Rendered Markdown (`.md` in the PR) | no gutter | — | comment per paragraph/line (Markdown has source lines; add `data-file-line` to blocks) or at least Raw |
| XML / YAML / JSON drawn as source (catalogs, device objects, raw fallbacks) | gutter only if `.cd-num` present (source layout: yes) | file line | once catalogs become tables (Step 9) they need row anchors like member tables |
| Project card, task / device card, library catalog row | not drawn | — | row anchors on the catalog's YAML lines |
| Pending (unsubmitted) review drafts | ~~listed by path only~~ drawn in place as Pending (the threads carry `state`, Step 2); listed with their words and lines, each editable and deletable, in the review panel (Step 5) | — | ~~drafts must draw in place, editable and deletable~~ done: the same `PATCH /comments/{id}` and delete routes, on a pending comment |
| Outdated thread | listed, not placed; shown with the hunk it was written on and "View the file at `<commit>`" (Step 3b) | — | ~~show with its original hunk (`diffHunk`) and a "view at that commit" link~~ done |
| Split view thread | its own split row, under its side's column, the other column empty (Step 3b) | — | ~~put it under its side's column~~ done; a second thread on a pair of lines stacks under its own side too (the first version compared against the slot row and put it under the wrong one — caught by the test) |
| Commit (commit comment on `/commit/sha`) | yes (Step 3b): listed under the files, a line comment naming its file and line; added on the commit as a whole (`POST /api/repos/{o}/{r}/commits/{sha}/comments`, no snapshot — the sha is the revision; settled by the viewer's tagged comment) | commit comment | a comment on a line of a commit's file is read, not written: GitHub's REST wants a `position` in the commit's own diff, which the drawing does not have |
| Check annotation | yes (Step 3b): `GET /pulls/{n}/checks/{runId}/annotations` (a hundred per run), listed under the run on Checks with a link to the file's line, drawn under the line on Raw and on a drawn source line of any file the drawing shows | — | ~~read and draw inline~~ done; a range is drawn under its first line |
| Suggestion | yes (Step 5): the toolbar puts the line's text in a ```suggestion block; the comment draws it as a diff | a review comment; applied as one commit on the reviewed head (`/suggestions/apply`, merge tier) | a range starts from no text; GitHub's "applied" mark is not written (no API for it) |
| Reply / edit / delete / resolve / unresolve | yes | GraphQL | ~~reactions, minimise~~ (Step 5), ~~"edited"~~ (Step 2) |

Correctness guards to keep: a comment is refused (never guessed) when the element is not in this
revision's drawing (`anchor-not-found`), when the pull request moved (`snapshot-stale`), and when two
candidates match on placement (Ambiguous). Add tests on the real-PR dump: FB_Alarm's moved `Scada`
member (INOUT ← STATIC) comments on the new row and lands inside the hunk at its new lines; the
Symbol Set (new file, 780 lines) row comment lands on the row's lines; a `.gvl` row whose table was
built from the `.gvl.xml` layer lands in the `.gvl.st` or `.gvl.xml` file GitHub actually diffs.

### B7. Review (approve / request changes)
- ~~**partial** Review dialog is inline (`RB:187-301`), not GitHub's dropdown from a "Review changes ▾"
  button with a pending-count badge.~~ Step 5: the green button with its count and caret; the dialog opens under it.
- ~~**partial** Pending review comments listed by path only (`RB:100-108`); GitHub shows each draft
  with its body and lets you edit / delete each (pending comment `line`, `side`, `body` not read —
  `Src:432`).~~ Step 5: "Show your drafts" lists each with its words and line, Edit and Delete.
- ~~**missing** Dismiss a review (with reason); re-request review button beside a reviewer.~~ Step 5.
- ~~**missing** Review-request of **teams** (`PW:462` regex refuses slugs; `Wr:163` sends `reviewers`
  only); teams read into the same list as users with no marker (`Src:301-311`).~~ Step 5: `addTeams` / `removeTeams` by slug, the slug read from GitHub; "org/team" in the box.
- **missing** Reviewer suggestions / autocomplete; CODEOWNERS badges.
- ~~**partial** Reviewer states shown as lowercase words (`RB:330`, `P:457`), not icons.~~ Step 5: GitHub's icons with the words on hover.
- **missing** The "move pending review to current head" path (deferred; GitHub itself discards).

### B8. Merge box
- **partial** Merge, squash, rebase exist with confirmation (`MB:127-143`); update branch when
  BEHIND; auto-merge on/off. Good safety rules (no admin bypass) — keep them.
- **partial** GitHub's merge box content: ~~the list of required reviews ("1 approval required",
  "Changes requested by X"), the checks summary with each check row~~ (Steps 2 and 6: the summary
  rows, each check under "Show all checks"), "This branch has conflicts that
  must be resolved" ~~with the file list~~ (said; GitHub's API names no conflicting files), ~~"Merging is blocked" with the rule that blocks~~ (the
  words from `mergeStateStatus`; the rules themselves are not readable without `Administration: read`), ~~the split
  "Merge pull request ▾" button, "Delete branch" / "Restore branch" after merge~~ (Step 6: the owner's decision — offered
  with the read-then-delete race *stated* on the button and in THREAT-MODEL, and
  `deleteBranchOnMerge` shown when the repository does it itself), ~~"Update branch ▾" with "Update with rebase"~~ (Step 6:
  `updatePullRequestBranch(updateMethod: REBASE)`; its lost answer stays unknown, since a rebase leaves nothing to recognise), ~~merge-queue enqueue~~ (Step 6: read, enqueue, dequeue, with `expectedHeadOid`).
- ~~**missing** For a closed PR: "Reopen" / "Delete branch" in the box (box is empty, `MB:74-76`).~~ Step 6: Delete / Restore branch in the box; Reopen beside Comment (Step 5).
- ~~**missing** "Close pull request" beside Comment; "Close with comment".~~ Step 5: the comment is posted first, then the state, each its own operation; a refused comment closes nothing; Reopen likewise.
- **missing** Conflict resolution in the browser (PLAN later, on purpose) — list the conflicting files
  at least.
- **wrong** Merge-tier gate hides the *state* ("can be merged") when the tier is off; GitHub shows the
  state to everyone and hides only the button.

### B9. Sidebar (GitHub's right column)
- **missing** The column itself; metadata is stacked in the left panel (`P:431-496`), hidden on
  Files changed (`P:330`).
- **partial** Reviewers / Labels / Assignees editors exist (`PM`, `RB:307-345`) as inline inputs and
  native `<select>`; GitHub uses gear-button popovers with search and a check list.
- **partial** ~~Milestone~~, Projects, Development (linked issues), ~~Notifications (subscribe)~~,
  Participants, ~~"Lock conversation"~~ (the three struck: Step 5), "Pin", "Transfer".
- **wrong** Label pills are border-only (`app.css:252`); GitHub fills them with the label colour and
  computes contrast text.

### B10. Pull request list and creation
- ~~**missing** Open / Closed tabs with counts (server reads `state=open` only, `Src:176`), filters
  (author, label, milestone, reviewer, assignee, sort), a search box with GitHub's `is:pr is:open`
  grammar, pagination.~~ Step 7: `?q=` in GitHub's grammar (the subset `PullQuery` takes, every
  token checked), GitHub's search for a signed-in person with the counts, REST by state for an
  anonymous reader (said on the page), the menus editing one token each, cursor paging. The
  milestone filter is in the grammar, not yet a menu.
- ~~**missing** Row content: state icon, labels, check-status icon, review-decision icon, comment count,
  assignee avatars, "#3 opened 2 days ago by author" (API `Pull` has none: `api.ts:112-124`).~~ Step 7.
- ~~**wrong** Master-detail pane; a row should go straight to `/pull/N`.~~ Step 1.
- **partial** On "New pull request": ~~the comparison's diff~~ (the changed files under the form, with
  the way to the full comparison; Step 7) and commit list inline; ~~Markdown preview~~ (Step 7);
  ~~reviewers / labels / assignees / milestone at creation~~ (Step 7: each applied after the pull
  request is opened, as its own write under its snapshot; a refused one is said with the link);
  ~~fork heads (refused `PM:202`)~~ (Step 7: `owner:branch`, checked by GitHub); "Create
  draft" split button (a checkbox today).

### B11. Writes the server cannot do (one bullet each)
~~reactions add/remove~~ · ~~suggestion apply (single and batch commit)~~ · ~~multi-line comment from the
page~~ (Step 3) · comment on unchanged lines · ~~minimise / unminimise a comment~~ · ~~lock / unlock~~ · ~~milestone~~ ·
project · ~~team review request~~ · ~~dismiss review~~ · ~~re-request review~~ · ~~edit a submitted review's
summary~~ · ~~edit / delete one pending draft comment~~ (every strike here: Step 5, 2026-10-23) · re-run checks · ~~delete / restore head branch~~ (Step 6) ·
revert PR · rename branch · ~~merge-queue enqueue / dequeue~~ (Step 6) · ~~update branch by rebase~~ (Step 6) · ~~subscribe~~ ·
open a PR from a fork · reviewers / labels / assignees at creation · create a label.

### B12. Data the server does not read although GitHub has it
PR: additions, deletions, changed_files, commits, comments, review_comments, created_at, closed_at,
merged_at, merged_by, milestone, requested_teams, head.repo / isCrossRepository, locked,
author_association, avatars, html_url, participants, viewerSubscription, closingIssuesReferences,
suggestedReviewers, viewerCanDeleteHeadRef / MergeAsAdmin / UpdateBranch / React / Subscribe /
ApplySuggestion, merge-queue, deleteBranchOnMerge, statusCheckRollup.isRequired, label description.
Threads/comments: start_side, original_start_line, in_reply_to_id, diffHunk, isMinimized,
minimizedReason, authorAssociation, avatar, url, reactions, lastEditedAt, pullRequestReview,
resolvedBy. Reviews: id, commit, authorAssociation, onBehalfOf, dismissal reason, lastEditedAt,
comments grouped under the review, viewerCanUpdate / Dismiss. Commits: committer, avatar,
verification, body, parents, stats, files, co-authors, per-commit status. Checks: id, details_url,
started/completed, output (title, summary, text, annotations), app, suite / workflow / job,
external_id; status description, creator, times; deployments; required checks; CODEOWNERS.

---

## C. Repository browsing (the "Code" view) — desktop parity and GitHub shape

- **partial** ~~GitHub's repository header and nav (Code · Pull requests · Releases · Docs …)~~ (Step 1), the
  branch/tag switcher ~~with search~~ (a select; search is not built), ~~the latest-commit bar ("author · message · time · N commits")~~ (Step 1; the commit links to its page since Step 8),
  ~~README below the tree~~ (opened by default in the pane), ~~"Go to file" search~~ (the tree's filter), ~~breadcrumb path~~ (Step 1); "N Branches · Tags" beside the picker (Step 8).
- ~~**wrong** "This repository is not set up for Versio" shown on an FBS project (seen).~~ Step 8: a source project is a Versio project; the notice is for a repository with nothing to draw.
- ~~**missing** Change marks in the tree (`RepoExplorerBuilder.Build` is called with **no changes**,
  `RepoModel.cs:158-257`); the desktop draws A/M/D glyphs, struck deleted names, changed-descendant
  roll-ups (`MainWindow.xaml:106-183`, `ShellViewModel.cs:8156`). `/compare` returns a flat list
  (`ApiEndpoints.cs:169-202`), and PLAN's restructure phase 3 ("compare as marks on the tree") was
  never built.~~ Step 8: `RepoExplorerBuilder.Build(…, changes)` marks the listing; `/tree?base=&changed=1`
  is the marked tree, which the compare and commit pages draw beside the files.
- ~~**missing** `TreeNodeDto` drops `Toolchain`, `Detail`, `Type`, `Guid`, `Status`, `RollUp`,
  `ChangedDescendants` (`ApiEndpoints.cs:19`), so the page cannot show the vendor pill, the object
  type, or a status.~~ Sent since the RenderChoice merge; drawn since Step 8 (`TreePanel.tsx`).
- **missing** The desktop's **Project summary** card (project file, vendor, toolchain, sync state,
  mirror, last export, object counts, libraries, tasks, coverage, warnings —
  `ShellViewModel.cs:7852-7948`), the **task** detail and task-catalog table (`:7527-7555`), the
  **device** detail with slot / value / connector rows and the device catalog (`:7534-7563`,
  `:7792-7809`), the **library** catalog table (`:7520-7547`). `ViewService` has no branch for
  Project / Library / Task / Device rows; it draws their YAML as source or "missing".
  `TryParseLibraryCatalog`, `TryParseTask`, `TryParseDeviceCatalog` exist in the CODESYS pack and are
  never called from the web.
- **partial** The desktop's **Devices pane** for FBS projects (Step 9) and ~~the commit card in History
  (byline, stats, body, objects touched, per-file diffs — PLAN restructure phase 2, never built;
  `/history` returns sha, message, author, when only)~~ (Step 8: `/commit/<sha>` with the card, the
  marked tree of what it touched, every file drawn from the first parent).
- **missing** Explorer (files-as-on-disk) view (PLAN phase 5, deferred).
- **missing** Shared theme tokens generated from the desktop palette (PLAN phase 4, never built);
  success green, merged purple, modified amber are hard-coded in `app.css:107,254,255,264`.
- ~~**wrong** The "husk" rule differs: desktop sends an empty non-interface table to source
  (`ShellViewModel.cs:7605`), the web takes any valid table (`ViewService.cs:284,428`). Nothing pins
  the two switches to each other (no "mirror" test exists in `ViewServiceTests.cs`; `VS:66-73`
  calls moving the switch into Core "later"). FINDINGS 2026-10-23 already states the rule: *the
  decision of what is drawn belongs in Core*.~~ **Step 9:** `RenderChoice` in Core, pinned by
  `RenderChoiceParityTests` over every fixture row on both hosts.
- ~~**missing** `/commit/<sha>` page, `/commits/<ref>` (today `/history`), `/branches`, `/tags`,
  `/tree/<ref>/<path>`, `/releases/tag/<tag>`, a 404 page.~~ Steps 1 and 8.
- **missing** "Where it lives": a breadcrumb from a drawn element back to project → folder → object
  → layer file, and a "Find usages" / cross-reference (neither side has it; the desktop resolves
  type links only, `OpenObjectAsync`). The web has `data-src-path` on every block already.

### C2. Links between structures and "reveal" — desktop behaviour the web must match

Desktop (`ShellViewModel.LinkTypes :6805-6837`, `OpenObjectAsync :6852-6904`, `TryOpenObject :6906`):
a member's type is resolved through the object index by name; the link is marked when the target
has uncommitted changes; a click **selects and expands the target in the Project tree** (Explorer
tree if absent), scrolls it into view, **and opens it in the pane**, Rendered first.

Web today: `ViewService.LinkTypes :633-666` resolves to the target's file path (marked when its
files changed between the compared commits; handles a deleted type); `MemberTableHtml :291` writes
`data-link`; `FragmentHost :203-210` navigates to `/blob/<ref>/<path>`. The pane opens. **The tree
does not reveal it**: `TreePanel.tsx` computes `startOpen` once (`useState(startOpen)`), so a node
collapsed when the page rendered stays collapsed when the selection changes; there is no
`scrollIntoView`; a type link from Compare leaves the comparison; from the PR page it leaves the
review (`P:393,415`).

To build (Step 3c, 2026-10-24: struck where it landed):
- ~~**Reveal in tree**: an effect that expands every ancestor of the selected path and scrolls the row
  into view, on every selection change; highlight the row; the same in the Files-changed tree (a link
  to a file in the PR scrolls both the tree and the stacked diff to it).~~ `TreePanel` since 2026-10-23; the Files-changed tree scrolls its section.
- ~~**Stay in context**: in Compare, a link to a changed file opens *that comparison* of the target; in
  a review, a link to a changed file opens it inside the review; a link to an unchanged file opens the
  object at the head commit in a **peek panel** (or new tab), never dropping the review.~~ `PeekPane`, Step 3c.
- **Links everywhere a structure is named**: member-table types (exist); ~~**ST source identifiers**~~ (Step 3c, in Core, drawn on the web)
  (tokenizer already classes identifiers; resolve against the object index in Core: types, FB
  instances' types, called POUs, GVL members as `GVL.Member`); **ladder boxes and operands** (FB
  type on a box, instance variable, operands to their declaration row); catalog rows (a library
  version to its library, a task's program to its POU, a device to its catalog entry). Each
  resolved in Core by name against `ObjectIndex` so both hosts get them; the desktop gets ST and
  ladder links for free.
- **Origin of a structure** ("where that actually lives"): every drawn row / network / line carries
  `data-src-path` and lines (anchors). Show it: ~~a hover/“…” on a row with `layer file : line`~~ (the
  gutter's tooltip, Step 3c), "Open in Raw at this line" (`/blob/<ref>/<layer>#L<n>`), and a
  breadcrumb project → folder → object → layer above the pane. Also "declared in" for a member of a
  nested struct.
- ~~**Reverse links**: "Used by" for a type / FB / GVL member (a cross-reference index built once per
  commit in Core from the declarations and ST bodies; ladder operands when element parsing lands).
  Neither host has it; the web gets it first, the desktop panel second.~~ Step 3c: `ObjectLinks`
  in Core, `/usages` and the pane's list on the web; the desktop panel is still to come.

---

## D. Rendering — object kinds per vendor (desktop vs web)

The desktop decides in `ShellViewModel.BuildRenderedContent` (`:7460-7703`): project card → library /
task / device row → catalogs → member table (+ ladder or code under an interface) → ladder → empty
note → Markdown → source. The web decides in `ViewService.Rendered` (`:268-325`) without the first
three. The packs answer: CODESYS everything incl. catalogs (`CodesysProjectFormat.cs:224-269`); TIA
table, ladder (LAD/FBD), SCL body (`TiaPortalProjectFormat.cs:242-288`); TwinCAT table and body only
(`TwinCatProjectFormat.cs:192-198`).

| Vendor | Object kind | Desktop | Web | Gap |
|---|---|---|---|---|
| All | Project row (`.project`, `.apXX`, `.tsproj`, FBS folder) | Project card + coverage, Raw explanation (`:7845-7968`, `:8113-8172`) | Row only expands; nothing opens (`TreePanel.tsx:72`) | ~~**Card missing**~~ Step 9, `Cards.Project` |
| All | `.html` | Rendered / Raw / **Web page** tab (`:3100-3111`) | ~~Highlighted source~~ Rendered / Raw / Web page (Step 9: the raw route framed with every sandbox flag off, the route's own sandbox policy behind it) | ~~Web-page tab missing~~ |
| All | `.md` | Markdown, follows `.md` links only | ~~Markdown, follows any repo path~~ follows `.md` links only (Step 9) | ~~Link rule differs~~ |
| CODESYS | POU / method / interface (ST) | Table + code | Same | — |
| CODESYS | POU in LD/FBD (`.xml`, FBS `.pou.xml.v3`) | Table + networks | Same | — |
| CODESYS | CFC, SFC, IL | Raw XML as source | Same | Neither draws (future renderer) |
| CODESYS | DUT / enum / GVL / persistent list | Table (empty non-interface → source) | Table always | ~~**Husk rule differs**~~ Step 9, `RenderChoice` |
| CODESYS | Property + Get/Set, action, transition | Table + source / ladder | Same | — |
| CODESYS | Global text list; project settings; symbol config; OPC UA symbol set | Tables (`CodesysCatalogs.cs:306,346,398,478`) | Same | — |
| CODESYS | Plain text list, image pool, EVC, project info, visualisation, external file | Raw XML | Same | Neither draws (visualisation: future) |
| CODESYS | Plc Logic / Application / Communication Manager | "stores no settings" note | Same (seen: raw `__V3_META__` for the FBS form) | ~~**FBS form falls through to raw**~~ Step 9, the note |
| CODESYS | Library manager (`.libraries.yaml`, FBS `Libraries.json`) | Library catalog table with marks | YAML/JSON source | ~~**Missing**~~ Step 9, `CatalogHtml` |
| CODESYS | Task configuration (`.tasks.yaml`, `.task.xml.v3`, `.taskconfig`), task row | Task table; task facts card | Source / raw | ~~**Missing**~~ Step 9, `CatalogHtml` and `Cards.Task` |
| CODESYS | Devices (`.devices.yaml`, `.device.xml.v3`), slot / connector / parameter rows | Device table + detail card | Source / raw | ~~**Missing**~~ Step 9, `CatalogHtml` and `Cards.Device` |
| CODESYS | FBS POUs / Devices panes | Shared builder | Same | — (catalog readers not called on the web) |
| TIA | FB/FC SCL (`.scl` + `.xml`) | Table + SCL body | Same | — |
| TIA | FB/FC LAD/FBD | Table + networks | Same | — |
| TIA | FB/FC STL / GRAPH (`.xml` only) | Table + the whole XML as source under it | Table only (`AppendInterfaceCode:577-580`) | ~~**Code missing under table**~~ Step 9, `CodeUnder.WholeFile` |
| TIA | OB LAD/FBD · OB SCL/STL | Networks · source | Same | — |
| TIA | Global / instance DB, UDT | Table | Same | — |
| TIA | Tag table | Raw XML (no parser) | Same | Future parser |
| TIA | Hardware (`*.devices.yaml`) | Device table + detail | YAML source | ~~**Missing**~~ Step 9, `CatalogHtml` |
| TIA | HMI, safety, technology | Not exported | Same | Out of scope (export) |
| TwinCAT | POU / method / property / action / interface | Table + body | Same | — |
| TwinCAT | Graphical POU (declaration-only `.st`) | Table + whole file | Table only | ~~**Code missing**~~ Step 9, `CodeUnder.WholeFile` |
| TwinCAT | DUT, GVL | Table (empty → source) | Table always | ~~**Husk rule differs**~~ Step 9, `RenderChoice` |
| TwinCAT | I/O tree (`*.devices.yaml`) | Device table | YAML source | ~~**Missing**~~ Step 9, `CatalogHtml` |
| TwinCAT | NC / CAM, safety, C++, visualisation | Not exported | Same | Out of scope (export) |
| TwinCAT | Native `.TcPOU`, `.xti`, `.plcproj` beside the export | Plain text | Same | Future parser |
| All | Deleted ladder block | Committed copy drawn removed | Compare draws removed | — |

Also from the map:
- **Tree rows** (desktop `TreeRow.cs`): ~~the web lacks the A/M/D/R/?/! letter (`:283`), roll-up colour
  and changed-below count (`:301-306`), struck deleted names (`:179`), the annotation (type "FB" /
  "Method", library version, task schedule, export state, `:231`), the toolchain label (`:216`)~~ (Step 8),
  ~~kind icons (`:191`; the web draws a letter, `TreePanel.tsx:10`)~~ (Step 9), and the panel summaries (tree,
  coverage, findings, staleness, in-sync list).
- **History** (`HistoryPage.tsx:45-60` vs `CommitHeading.cs`, `HistoryViewModel.cs`): the web lacks
  ~~the body, "N files · +x −y", the objects sentence~~ (Step 8, on the commit's page), branch/tag labels, ~~expanding a commit into its
  files with marks and rename origin~~ (Step 8: the commit page), the filter, and "load older" (stops at 50).
- **Links**: the desktop's type link selects and expands the target in the tree (`OpenObjectAsync`
  `:6852-6904`); the web navigates to the path and never selects it in the tree. `data-src-path` is
  used only for comment placement, never for navigation. Neither side has find-usages,
  cross-references, call hierarchy or go-to-declaration from ST identifiers or ladder boxes.
- **Theme**: no generator, no pinning test (the palette header names a `ThemeTokenTests` that does
  not exist); `CodePageAssets.cs` base colours differ from the desktop palette (added `#1a7f37` vs
  `#1A7F45`, accent `#0969da` vs `#2563EB`…); ladder CSS defaults are GitHub-ish and the desktop
  overrides them at runtime; dark follows the OS only. **Decision for this plan:** the web adopts
  GitHub's palette (the owner's ask) and the *code/ladder/table* CSS stays the shared Core one with
  GitHub-matching light/dark values; the desktop keeps mapping its own palette onto the same tokens.
- **Vendor-name leaks** in the web: none in executable code; four comments name CODESYS / TIA
  (`RepoModel.cs:335`, `ViewService.cs:159,311,823`, `GitHubRepoSource.cs:134`) — reword.

### D2. Shared-renderer parity checklist (same Core code on both hosts — what still differs)

| Part | Desktop | Web | Status |
|---|---|---|---|
| Member table: Name / Type / Start value / Access / Comment columns, sections, nested members, indent | WPF template (`MainWindow.xaml:1403+`) | `MemberTableHtml` (`mt-name/type/value/access/comment`, `mt-section`, `mt-indent`) | same content; **CSS**: empty attribute rows reserve height, column overflow (seen) |
| Table captions: instance-of, member count, memory layout, read-only note, change summary | `InstanceCaption`, `NumberCaption`, `MemoryLayoutCaption`, `ReadOnlyNote`, `ChangeSummary` | `MemberTableHtml:62-72` emits all four | same (verified) |
| Attribute chips, unshown-change note with "Show Raw" link | yes (2026-10-22) | `mt-attrs`, `mt-unshown` | same |
| Type links, marked when target changed | yes | `data-link`, `mt-ref-changed` | same; reveal differs (C2) |
| Change marks A/M/D per row, old→new in a cell (`mt-was`/`mt-now`), type-changed notice, nothing-drawn-changed notice | yes | yes | same |
| Husk rule (empty non-interface table → source) | yes (`:7605`) | no (`VS:284`) | **differs** → `RenderChoice` in Core |
| Ladder / FBD: networks, titles, comments, jump links (`data-jump`), merged vs side-by-side, stacked replaced networks, moved / stub rows, counts | `LadderHtml` in WebView2, palette mapped at runtime (`LadderPage.cs:51-72`) | same fragment, default CSS | same; colours differ (theme) |
| Ladder pending (working-tree) state | yes | n/a (no working tree) | by design |
| ST source: declaration / implementation divider, IDE numbering, fold to context, unified removal rule, side by side, intraline emphasis, hidden characters, look-alike letters | `DiffView` over `StSourceLayout` | `SourceHtml` over the same | same since 2026-10-23 (FINDINGS) |
| ST find box (Ctrl+F, UX-6/7), copy | yes | browser only; folded rows not searchable | add find-in-file that unfolds |
| Raw diff | `DiffView` unified with `@@` | `TextDiffHtml` unified / split | same; web lacks expand-hunk |
| Markdown | `MarkdownHtml`, `.md` links only | same, any repo path; Mermaid frame | link rule differs (align to desktop's) |
| `.html` Web page tab | sandboxed WebView2 | missing | add a sandboxed frame like Mermaid's |
| Project card; library / task / device catalogs; task / device cards | yes | missing | Step 9 |
| "stores no settings" note; "nothing in its own file" note; binary / too large / missing | yes | yes (+ over-budget fallback) | same |
| Pane heading (KIND / name / folder / tooltip), layer chips | `PaneHeading`, `DiffLayerChoice` | `Pane.tsx`, chips | same grammar; GitHub restyle keeps the content |

- **wrong** (seen) Member table row spacing and column overflow; code lines clipped; the empty
  table's "declaration change not shown" note on a method header change.
- ~~**wrong** (seen) Coverage: XML tables and ladders never vouch; whole-file adds/deletes are gaps;
  a row drawn as changed whose line *is* the row is a gap when the table hides a pragma. Needs
  `Coverage.Table` to vouch for A/D rows, for a file that is entirely new/deleted, and for
  attribute-only lines when the table draws the attribute chip (it does since 2026-10-22).~~ Step 4.
- ~~**missing** Ladder/FBD field evidence (`no-evidence`), so every ladder stays "partial".~~ `Coverage.Ladder`, 2026-10-23: every network vouches for its span.
- **missing** Element-level anchors in ladders (deferred; a thread lands on the network).
- ~~**missing** The same renderer choice for both hosts (`RenderChoice` in Core).~~ Step 9.

---

## E. Look and feel versus github.com (shell, components, states)

- ~~**Shell**: replace the desktop workbench (rail + panel card + pane card on a sunken background,
  `Workbench.tsx`, `app.css:76-92`) with GitHub's: dark global header (logo, repo breadcrumb, search,
  user menu), repository header + horizontal nav with counters, page content on a plain canvas,
  footer.~~ Step 1, 2026-10-23: `AppHeader`, `RepoLayout`, `tokens.css`, the `ui/` components, Octicons; no search box and no footer yet.
- **Typography**: GitHub's stacks and sizes (`-apple-system, "Segoe UI", "Noto Sans"…`, 14px/1.5;
  PR title ~32px; `ui-monospace` 12px in diffs). Today `app.css:23-24,47,117`.
- **Tokens**: adopt Primer's full token set (canvas, canvas-subtle, border-muted, fg-muted,
  accent, success, attention, severe, danger, done, diff add/del backgrounds and word emphasis,
  focus ring) in light and dark, with a theme switch (`data-color-mode`) rather than only
  `prefers-color-scheme`. Fix dark-mode state pills (dark text on saturated fills, `app.css:253-256`).
- **Components**: Octicons; buttons (primary green, default, danger, invisible, split with caret,
  button groups); counters (filled); filled labels with contrast text; state badges with icons;
  avatars and stacks; timeline with icon badges; comment cards; select menus / popovers with search;
  tab nav with counters; blankslates (icon + heading + text); skeleton loaders / spinners; flash
  banners and toasts; `:focus-visible` rings; tooltips.
- **Diff**: GitHub's diff table (line numbers in columns, `+`/`−` gutter, word-level emphasis, hunk
  header rows with expand arrows, sticky file header, horizontal scroll inside the file box).
- **Responsive**: GitHub's breakpoints; the file tree collapses to a "Files" dropdown; sidebar stacks
  under the body on narrow screens.
- **Empty / loading / error states**: every "Pick a …", "Loading...", "Nothing yet." replaced.
- **Wording**: "Pull request" everywhere; GitHub's verbs ("approved these changes", "requested
  changes", "left a comment", "merged commit X into main").

---

## F. Routing — every GitHub URL shape the page must answer

`/:o/:r` · `/:o/:r/tree/:ref/*` · `/:o/:r/blob/:ref/*` (+ `#L12`, `#L12-L20`) ·
`/:o/:r/commits/:ref/*` · `/:o/:r/commit/:sha` · `/:o/:r/branches` · `/:o/:r/tags` ·
`/:o/:r/compare/:base...:head` (and `owner:branch` forks) · `/:o/:r/pulls` (+`?q=`) ·
`/:o/:r/pull/:n` · `/pull/:n/commits` · `/pull/:n/commits/:sha` · `/pull/:n/checks` ·
`/pull/:n/files` (+ `#diff-…`, `#discussion_r…`, `#issuecomment-…`, `#pullrequestreview-…`) ·
`/pull/:n/files/:a..:b` · `/:o/:r/compare/:base...:head?expand=1` (new PR) · `/:o/:r/releases` ·
`/:o/:r/releases/tag/:tag` · `/:o/:r/docs` (Versio's own) · a 404 page.
`routes.ts` builds `/history/:ref` and `?tab=` / `?n=` / `?path=` today.

---

## G. Server, data and operations gaps that the page work depends on
- Read everything in B12 (summary, threads, reviews, commits, checks) — extend `IRepoSource` records
  and the GraphQL queries (`Src:269-284, 551-582, 705-706, 790-822, 846-913`); keep the paging bounds
  and the "cut" flags.
- New reads: reviews list (grouped comments), check-run detail (output, annotations), branch
  protection / rulesets required checks (needs `Administration: read` or rulesets read — may be
  refused: degrade to "cannot tell"), closed/merged pulls with filters and paging, commit detail,
  branches / tags lists, compare across forks, deployments.
- New writes (each through `MapWrite`, the ledger, the audit line): reactions, suggestion apply,
  multi-line + unchanged-line comments, minimise, lock, milestone, team reviewers, dismiss review,
  re-request, edit review summary, edit/delete pending draft, re-run check (`Actions: write`),
  delete/restore branch (document the race), update-branch REBASE, subscribe, fork PRs, labels /
  reviewers / assignees at creation, rename branch (admin).
- App permissions this needs beyond today's: `Pull requests: write` (have), `Issues: write` (have),
  `Contents: write` (merge tier; suggestions too), `Actions: write` (re-run), `Administration: read`
  (rulesets / required checks) — list them in `docs/web/PLAN.md` so the App is registered once.
- Staleness: conversation comments, replies, resolve, edit/delete, reviewers, viewed, state, draft,
  labels, assignees skip the head/base check (`PW:1004-1010` covers the rest) — fine by design, say
  so on the page.
- Audit lines missing for ledger hits, expired-snapshot settles, malformed keys (`PW:787-873`);
  WriteGate does not log S9 404 / S8 413.
- Checks for the live head, not only the snapshot head (`Src:846`).
- Stale remark `Src:286-290` ("mergeStateStatus is not asked for yet"); two stacked `<summary>`
  blocks at `PW:948-952`.
- `PerformanceTests` flake (three sightings, STATUS) and the `settle` tick-wait in
  `PullPage.test.tsx` — both on STATUS's list; retire them before the page is rebuilt on top.

---

## H. Delivery plan (the order, each step a coherent set of commits on `PrReview` or a branch from it)

The work is large (weeks). Each step is independently shippable, keeps the safety rules S1–S11,
and leaves the suites green with counts re-derived. Vendor rule: nothing in `web/`, `Versio.Web`
or Core learns a vendor's name; new object-kind renderers are Core HTML over the packs' existing
neutral models, and the packs grow only parsers.

**Step 0 — Record the decision and the list.** Write `docs/web/FINISH.md` (this gap list as the
live queue, sections A–G), mark PLAN.md's *workbench restructure* superseded, STATUS item 10 points
to it. One commit.

**Step 1 — The GitHub shell and design system (`web/`).** Primer-like tokens (light/dark + switch),
Octicons (inline SVG set, no CDN), base components (Button, Counter, Label, StateBadge, Avatar,
Timeline, CommentCard, SelectPanel, TabNav, Blankslate, Flash, Spinner, Tooltip), the global header,
repository header + nav, footer; GitHub's URL shapes in `routes.ts` with redirects from the old
ones; a 404 page; `document.title` per page; relative-time component; "Pull request" wording.
Replace `Workbench.tsx`, `Header.tsx`, `Pane.tsx`'s heading, `app.css`. Keep `FragmentHost.tsx`
(sanitising, annotation slots, Mermaid frame) and `ReviewWrite.tsx`'s operation-key rules untouched —
they are the safety layer, and their tests must stay green through the restyle. Vitest for routes
and the components' contracts.

**Step 2 — The pull request page, read side.** Title header with badge/author/counts; horizontal
tabs with counters and `/pull/N/{commits,checks,files}` URLs; Conversation with full timeline
kinds, review threads under reviews, comment cards, permalinks, reactions (read); Commits with
avatars/links/status; Checks with suites, times, output, annotations, required; the right sidebar;
the merge box drawn GitHub-style above the comment box (state visible to all, buttons tier-gated).
Server: extend `IRepoSource` records + queries (B12), reviews list, check detail, closed PRs,
branch protection "cannot tell" path. Mutation-check the new readers as before.

**Step 3 — Files changed, GitHub-shaped, PLC-drawn.** The file tree (project → folder → object,
with marks and `+N −M`, filter, jump, viewed progress) built from the inventory joined to the
`RepoExplorerBuilder` tree (`PE:108` + `RepoModel`); all files stacked with sticky headers and
chevrons, lazily rendered (the `/files/view` call per file as it scrolls, `content-visibility`),
Rendered/Raw per file plus a page-level default; expand-hunk arrows (server: `context=` on
`/files/view` raw); horizontal scroll inside the file box; multi-line and unchanged-line comments
(server: accept `start_line`, relax `line-not-in-diff` to GitHub's rule); "Show resolved"; outdated
with original hunk (`diffHunk`); split-view threads in their column; type links that stay inside the
review; breadcrumb "where it lives" per file; `#diff-…` / `#discussion_r…` hashes; diff settings
menu (whitespace needs a `w=1` on the server); commit-range selection (`/pull/N/files/a..b` —
compare any two commits of the PR, same snapshot rules).

~~**Step 3b — Comments everywhere (B6b).** Multi-line on Raw and on drawn source (shift-click,
drag; server already takes `start_line`); the old side of a modified row ("as it was"); section
headings and the table caption as anchors; attribute-chip lines; **ladder elements** (parsers record
per-element spans in the CODESYS and TIA packs — vendor code, so the CODESYS gate runs; Core anchor
kind `el` inside its network; the SVG marks the element; the thread draws beside the network naming
the element); Markdown blocks with their source lines; catalog / card rows once drawn; pending
drafts drawn in place as Pending with edit / delete (read `state`, add the two mutations); outdated
threads with `diffHunk`; split-view threads in their column; commit comments; check annotations
inline; expanded-context lines after the real-GitHub gate says what REST accepts. Every placement
has a test that asserts the exact GitHub `path`, `line`, `start_line`, `side` posted, on the fixture
PR and on the real dump, plus mutations on `HunkLines.Placement` and `AnchorMatcher`.~~
**Done 2026-10-24, with the scope cut said here.** Landed: the old side of a modified row
(the draft's "comment on it as it was instead", posting `r-old-N`); outdated threads with their
`diffHunk` and the way to the file at the commit they were written on; side-by-side threads in
their own column (`FragmentHost` makes a split row with one column empty — and reads the column
from the LINE's row, not the slot row before it, which the test caught); commit comments read and
written (`/commit/comments`, `POST …/commits/{sha}/comments` through `RunWithoutSnapshotAsync`,
settled by the viewer's tagged comment, `CommentsEverywhereTests`); check annotations
(`GetCheckAnnotationsAsync`, `/checks/{runId}/annotations`, listed under the run and drawn under
the line on Raw and on drawn source, `CheckNote`); multi-line on Raw and drawn source and pending
drafts in place were already Step 3's and Step 5's. Expanded-context lines stay refused, with the
reason in B6b (GitHub's REST 422). **Not done, on purpose:** ladder elements (vendor parsers and
the CODESYS gate — a session of its own, listed under *Future renderers and anchors* below);
section headings, the table caption, attribute-chip lines, Markdown blocks and catalog rows as
anchors — each is a Core anchor kind with its own placement rule and tests, and a comment on any
of them lands on the whole file today, which is honest rather than wrong; they stay in B6b's table
as open rows.

~~**Step 3c — Links, origin and reveal (C2).** Tree reveal on selection change (expand ancestors,
scroll, highlight) in the project tree and the Files-changed tree; links stay inside Compare and
inside a review, peek for unchanged targets; Core name resolution against `ObjectIndex` for ST
identifiers, ladder boxes / operands and catalog rows (both hosts); the origin affordance (layer
file : line, open Raw at the line, breadcrumb); the "Used by" cross-reference index per commit. Tests:
a link in a collapsed folder reveals and selects the row; a link from the review stays in the review;
the desktop's `OpenObjectAsync` and the web's reveal pinned to the same object for the same click
on the fixtures.~~
**Done 2026-10-24, with the scope cut said here.** Landed: the tree reveal (already in, from
2026-10-23); stay in context — a link to a changed file opens its comparison or its review
section, a link to anything else opens a **peek** at the newer version beside the page
(`PeekPane`, its own trail, "Open" to the object's page), on Compare and on the pull request;
**ST identifier links** resolved in Core (`ObjectLinks.Resolver` over `ObjectIndex`, same project,
never itself, never a keyword, a comment or a string; `CodeHtml.AppendLine` writes `data-link` on
the word), drawn by the web; the **origin** as the gutter's tooltip, `layer file : line`, on every
drawn line (`FragmentHost`); **"Used by"** (`ObjectLinks.NamesReferenced`, `ObjectIndex.ObjectsIn`,
`ViewService.UsagesAsync` reading each object's typed source once per commit, `/usages`, drawn
under the layer chips with its cut-short note). Found on the way: Step 9's Markdown-only link rule
had silenced every member-table type link, which no page test clicked (FINDINGS 2026-10-24). **Not
done, on purpose:** ladder boxes and operands as links (the same per-element parsing the ladder
anchors wait for), catalog rows as links, the breadcrumb above the pane (the pane's title already
says folder and object), "declared in" for nested members; and the desktop draws ST with its own
`DiffView`, so it gets the resolver but not the drawn links until that control reads `ObjectLinks`
— listed under *Future renderers and anchors*.

~~**Step 4 — Coverage that tells the truth.** In Core `Coverage`: a whole-file add/delete is shown by a
drawing that draws every row A/D; a table row drawn A/D/M vouches for every source line the row
spans; an attribute chip vouches for its pragma line; a `METHOD` header line is vouched for by the
code block that draws the header (or draw the header); ladder field evidence (network title, rung
elements' operands) so ladders can be "complete"; de-duplicate "as it was / as it is" for a renamed
or edited same line. Then the banner is rare, and when it appears it means something. Tests on the
real-PR dump (FB_Alarm, Symbol Set, MoveHome) plus mutations.~~ **Done 2026-10-23** (commits
`ca9489a`, `d52c8c7`, `04f91ae`, `0d595e4`): six rules in `Coverage`, the parsers' framing, the
ladder's evidence; FINDINGS 2026-10-23 has the before/after on the real pull request. This
paragraph went unstruck until the review of 2026-10-24, while STATUS said Steps 0–10 had landed.

~~**Step 5 — Review writes to parity.** Review dropdown with pending badge; pending drafts editable
(read line/side/body; `updatePullRequestReviewComment` / delete on a pending comment); dismiss
review; re-request; team reviewers; reactions (write); suggestions (toolbar insert, render,
apply / batch commit under the merge tier); minimise; lock; milestone; edit review summary;
subscribe; "Close with comment". Each a `MapWrite` route with ledger + audit + test + mutation.~~
**Done 2026-10-23.** `/reactions`, `/comments/{id}/minimize`, `/lock`, `/milestone`,
`/subscription` (`PullConversationWriteEndpoints.cs`); `/reviews/{id}/dismiss`, `PATCH
/reviews/{id}`, teams on `/reviewers` (`PullWriteEndpoints.cs`); `/suggestions/apply`
(`PullSuggestionEndpoints.cs`, merge tier: one `createCommitOnBranch` with `expectedHeadOid` = the
reviewed head, the operation tag in the message, a fork refused). The page: reaction pills as
toggles with the picker, a hidden comment folded behind its reason, Dismiss / Edit on a review
card, Close / Close with comment beside Comment, the suggestion widget with Apply and the batch
bar, "Suggest a change" in the comment toolbar, the review dropdown with its count and editable
drafts, reviewer icons with re-request and teams by slug, Milestone / Notifications / Lock in the
sidebar. Left: reviewer autocomplete and CODEOWNERS badges; "move pending review to the current
head" stays deferred (GitHub itself discards); a suggestion on a range starts from no text (only
the last line's is known).

~~**Step 6 — Merge box to parity.** Required reviews and checks listed with their state; conflicts
with file list; blocked reason from `mergeStateStatus` + rulesets; split merge button; update with
rebase; **delete branch** (owner's decision: a `MapWrite` route that reads the tip, refuses unless it
equals the merged head, deletes, and says on the button and in THREAT-MODEL that a push in between
can be lost; `deleteBranchOnMerge` shown when the repository does it itself) and restore branch;
closed-PR actions; merge queue (read + enqueue when the repo uses it).~~ **Done 2026-10-23**
(`PullMergeEndpoints.cs`: `/update-branch` with `method`, `/delete-branch`, `/restore-branch`,
`/merge-queue`; `MergeBox.tsx` as GitHub's box). Not done, and why: the conflicting files (GitHub's
API does not name them) and the blocking rule's name (needs `Administration: read`; the words come
from `mergeStateStatus`).

~~**Step 7 — Pull request list and creation.** `/pulls` with open/closed counts, filters, search
grammar subset (`is:`, `author:`, `label:`, `review:`, `assignee:`, `sort:`), paging; rows with
icons/labels/counts/avatars; `/compare/base...head?expand=1` creation with inline diff + commits,
preview, reviewers / labels / assignees / milestone, fork heads.~~ **Done 2026-10-23**
(`PullQuery.cs`, `SearchPullsAsync` / `GetPullRowsAsync`, `PullsPage.tsx`, `NewPullPage.tsx`).
Left: the commit list under the form (the compare read carries files only) and the
"Create draft" split button.

~~**Step 8 — Repository browsing to desktop parity in GitHub's shape.** `/tree`, `/blob`, `/commits`,
`/commit/<sha>` (the desktop commit card: byline, stats, body, objects, per-file diffs),
`/branches`, `/tags`, `/releases/tag`; the project tree with vendor pill, object type, change marks
(`RepoExplorerBuilder.Build(files, projects, changes)` from `/compare`), struck deleted names,
roll-ups; the "not set up for Versio" notice only when there is no Versio project at all; README
under the tree; "Go to file".~~ **Done 2026-10-24** (`RepoExplorerBuilder.Build(…, changes)` in
Core; `/tree?base=&changed=1`, `/commit`, `/tags`; `TreePanel.tsx`, `CommitPage.tsx`,
`RefsPage.tsx`; the compare page's sidebar is the marked tree). Left: the branch picker's search,
the tree's kind icons, "load older" on the commits page.

**Step 9 — Every object kind, every vendor, one renderer choice (D, D2).** Move the renderer switch
into Core (`RenderChoice`: project card → catalog row → catalogs → table (+ladder / code, incl. the
whole-file-under-table cases for TIA STL/GRAPH and TwinCAT graphical POUs) → ladder → empty note →
Markdown → web page → source; the husk rule once), used by `ShellViewModel.BuildRenderedContent`
and `ViewService.Rendered`, pinned by one test that runs both hosts over every fixture object and
asserts the same choice. Then Core HTML renderers, each with the same marks / notices / anchors the
member table has: project summary card (`ProjectSummaryRender` fields), library catalog, task
catalog + task card, device catalog + device card (slot / value / connector), the FBS `Libraries.json`
/ `.task.xml.v3` / `.device.xml.v3` forms, the FBS Devices pane, the `.html` web-page frame; the
Plc Logic / Application / Communication Manager FBS forms get the "stores no settings" note (seen
falling through to raw). Whatever the D-table marks raw on **both** sides (CFC, SFC, IL,
visualisation, text list, image pool, recipe, TIA tag table, TwinCAT native files) stays source
**with a named reason in the pane** ("Versio draws no CFC yet; this is the file") — never silent
XML — and is listed in `docs/web/FINISH.md` as future renderers. Member-table CSS: no empty
attribute rows, column widths, scroll inside the table. Markdown link rule aligned with the desktop.
Tree and Files changed group by project with the vendor's pill from `ToolchainLabels`, the object
type annotation and kind icons (`TreeNodeDto` grows `Toolchain`, `Type`, `Detail`, `Status`,
`RollUp`, `ChangedDescendants`). Reword the four vendor-naming comments in the web.
**Where it stands, 2026-10-24.** The Core half (`RenderChoice`, the catalogs and cards, the notices
for what stays source, the grown tree node, the reworded comments) landed with the 2026-10-23
merges; the web half landed in Steps 8 and 9: the tree's pills, types, marks and kind icons; the
`.html` Web page tab; the `.md` link rule aligned to the desktop's; the member table's cells
wrapping inside the table. Left: a renderer for what both hosts still draw as source (CFC, SFC,
IL, visualisation, text list, image pool, recipe, TIA tag table, TwinCAT native files) — each
named in the pane as not yet drawn.

~~**Step 10 — Operations and gates.** Audit lines for every write path; live-head checks; the two
flakes retired; the App permission list; `docs/web/ARCHITECTURE.md` + `THREAT-MODEL.md` updated for
the new writes (suggestions, branch delete, reactions, lock); FINDINGS entries for what the real PR
revealed (coverage noise, flat list, raw device objects); STATUS counts re-derived; then the owner's
real-GitHub gates on a throwaway repository, writes on, merge last.~~
**Done 2026-10-24, all but the owner's gates.** Audit lines: every way out of a write is in the
log now — a retry answered from the ledger, one still in flight, an abandoned or unknown one
reconciled, an expired-snapshot settle in each of its outcomes, an expired or malformed key, and
the gate's S9 (writes off), S11 (merge tier off) and S8 (body cap) refusals. Live-head checks:
`/status` reads the latest commit's checks in counts once the head moved, and the Checks tab says
so over the reviewed commit's list. The two flakes: `PerformanceTests` was the resolution cache
written after its single flight let go (reproduced once in ten runs on the old binary, 32 of 32
after the fix — FINDINGS 2026-10-24); the review page's vitest waits for the page to be at rest
(no read in flight for three ticks) instead of a fixed forty ticks, which also took that file from
three minutes to eighteen seconds. The App permission list is in PLAN.md's App section.
ARCHITECTURE and THREAT-MODEL carry every write since Step 5; FINDINGS has the coverage noise
(2026-10-23), the raw device objects (2026-10-23) and the flat list (2026-10-24); the counts are
re-derived. **Left, the owner's:** the real-GitHub gates on a throwaway repository with writes on
— post, lose the network mid-post, approve as a second user, open a pull request, apply a
suggestion, delete a branch, merge last — and the 422 for a comment outside GitHub's hunks (B6b).

### Future renderers and anchors (named, not scheduled)

What both hosts still draw as source, each said in the pane as not yet drawn: CFC, SFC, IL,
visualisation, text list, image pool, recipe, the TIA tag table, TwinCAT's native files. And the
comment anchors Step 3b left open: **ladder elements** (per-element spans recorded by the CODESYS
and TIA parsers — vendor code, so the CODESYS gate runs — then a Core anchor kind inside the
network, the SVG's mark and the thread beside the network naming the element); section headings
and the table caption; attribute-chip lines; Markdown blocks by their source lines; catalog and
card rows. Each is a Core anchor kind with its own placement rule and tests; until then a comment
on one lands on the whole file, which the page says. And the links Step 3c left open: ladder
boxes and operands (the same per-element spans), catalog rows (a task's program, a library's
version, a device's entry), and the desktop's `DiffView` drawing the ST identifier links its
`ObjectLinks` resolver already answers; a "used by" panel on the desktop beside the web's list.
The web's `SourceHtml` carries the tokenizer's block-comment state between lines since
2026-10-24, so the body of a `(* … *)` is coloured as a comment on every line; the desktop's
`DiffView` still tokenizes each row on its own (its rows are virtualised and built out of order,
so the state would have to be precomputed per file), and colours the second line of such a
comment as code — a visible difference between the hosts until that is done.
What the third read of 2026-10-24 left open, each named where it bites: an operand's comment,
symbol comment, address or a contact's flags changed alone inside a ladder network compares as
Same and is vouched for (the model carries operand, kind and instance — the element-anchor
parsing above would carry the rest); avatars never show on GitHub Enterprise (`Avatar()` and the
CSP name github.com's avatar host); a device's slots are ordered as text, so "10" sorts before
"2" past nine slots; the explorer strikes a rename's old path only when the caller passes it,
and the web's compare passes none (`ChangedPaths` is a set difference of paths); `NotFound`
still promises "a branch … or a release" works for shapes it does not route.
One limit of the resolver as it stands (review, 2026-10-24): it sees no declarations, so a local
variable declared with an object's name — a `Start : BOOL` beside a method called `Start` —
links to the object. A word after a dot is never linked (it is a member of what precedes it);
telling a local from an object needs the declaration parser in the loop, which is the same
work as "declared in".

### Verification (every step)
- `dotnet test tests/Versio.Web.Tests`, `tests/Versio.Core.Tests`, `tests/Versio.Arch.Tests`, the
  three Pure suites; `npm test` in `web/` with the pinned Node; `tsc` clean; mutation checks on new
  rules (Core coverage, renderer choice, each new write's precondition).
- Look at it: `VERSIO_WEB_PREVIEW=1 VERSIO_WEB_PREVIEW_WRITES=1 dotnet test tests/Versio.Web.Tests
  --filter FixturePreview` (acme/plant, PR #1 with threads, checks, labels) and the real dump
  `VERSIO_WEB_PREVIEW_PULL=<dump> … --filter PullPreview` (`fluidstackio/CodesysStandardLibrary#3`,
  85 files) — both on 5199, so one at a time; desktop and phone widths, light and dark; side by side
  with github.com's own page for the same PR and with the desktop app on the same repository.
- Arch: `Versio.Web` still references only pure packs; vendor names only in `RegisterFormats`;
  `VendorSeamTests` green after the Core renderers land.
