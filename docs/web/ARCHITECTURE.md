# Versio Web: architecture

**What this is.** A map of the web viewer (read-only until slice 2 of PLAN.md; it reviews pull
requests on GitHub's behalf now, writes on, as the tables below say) as built on branch `WebViewer`
(2026-10-11, merged; the page restructured into the desktop's workbench shell 2026-10-14):
what each part is, where it lives, how a request travels through it, and which
code it shares with the desktop app. Why it is built this way, the security rules, and what is
still open are in [`PLAN.md`](PLAN.md); the threats and what stops each are in
[`THREAT-MODEL.md`](THREAT-MODEL.md). Where the project stands is in
[`../STATUS.md`](../STATUS.md) (item 10).

**In one sentence:** the page draws nothing; a .NET server reads a repository from GitHub and
draws each PLC object with the same Versio.Core code the desktop's panes use, and a React page
places that HTML.

---

## 1. The layers

```
 Browser ──────────────────────────────────────────────────────────────────────────────────
   web/  React + TypeScript page
     the workbench: rail, panel card, pane         ← lays out the page as the desktop shell does
     FragmentHost: DOMPurify, then innerHTML        ← places the server's HTML, draws no code
     /assets/code.css, /assets/ladder.css, ladder.js← Core's own stylesheets and script
         │  JSON over /api, sign-in over /auth, same origin only; session = HttpOnly cookie
         ▼
 Server ───────────────────────────────────────────────────────────────────────────────────
   src/Versio.Web  ASP.NET Core (net9.0)
     Http/       security headers, request log, rate limit
     Auth/       who is asking, and with which GitHub credential  (AccessResolver)
     Api/        routes: credential → commit → model → view, in that order
     Repo/       a commit as the desktop's tree     (RepoExplorerBuilder)
     Rendering/  an object as HTML, as Core chose it     (ViewService)
     Sources/    GitHub REST + Git LFS, no clone    (IRepoSource)
         │                                │
         ▼                                ▼
   Versio.Core  (shared with desktop)   Versio.Plc.Codesys / .TiaPortal / .TwinCat  (pure packs)
     RenderChoice: what a row draws        parse a vendor's files into Core's neutral models:
     the HTML renderers                    TryParseMemberTable, TryParseLadder, CodeBody,
     the diff, layout, tree, tables,       TryParseLibraryCatalog, TryParseTask,
     catalogs, cards; SafeXml              TryParseDeviceCatalog, HoldsNoSettings, SectionsFor
   Versio.Infrastructure (vendor-neutral)
     RollingFileSink, SystemClock, RepoConfigStore.Parse
         │
         ▼
 GitHub ───────────────────────────────────────────────────────────────────────────────────
   REST API (repos, branches, commits, git/trees, git/blobs), OAuth, Git LFS batch + download
```

**What the server may not reference** is as important as what it does: no `*.Infrastructure`
vendor pack (those drive CODESYS, Portal and XAE) and no `.Host` executable. It parses vendors'
files; it never drives a vendor's tool, starts a process or clones. `Versio.Arch.Tests` checks
the project file, the IL and every vendor reference (section 7).

---

## 2. Where everything is

### The server: `src/Versio.Web/` (about 12,600 lines on 2026-10-24; it was 2,700 when this map was first drawn)

| File | What it does |
|---|---|
| `Program.cs` | Sets the process-wide regex timeout on its first line, then builds and runs the host |
| `WebHost.cs` | **The composition root.** Registers services, the three vendor formats (`RegisterFormats`, the only place a vendor is named), the middleware order, the routes and the page's fallbacks. Logs one startup line saying what is configured |
| `WebOptions.cs` | All configuration (`Web` section or `Web__…` environment variables): kill switches, limits, the GitHub App and its hosts |
| `appsettings.json` | Defaults with no secrets; framework logging turned down to warnings |
| `Dockerfile` | Multi-stage container: Release publish (builds the page), then a non-root ASP.NET runtime. **Written, not built** (no Docker where it was written) |
| **`Sources/`** | |
| `IRepoSource.cs` | The seam for "where repositories come from": `RepoAccess` (whose credential), `RepoResult<T>` (a value or why not), the tree, blob, commit, pull-request and release records. GitLab or Azure DevOps would be new implementations of this |
| `GitHubRepoSource.cs` | The GitHub implementation: repo, branches (every page, up to 1,000, and says when it was cut), downloads checked against the LFS pointer's hash, commit SHA, recursive tree, raw blobs, commits, a pull request's merge-base and changed files (paged to GitHub's 3,000; a compare's stop at 300), and over GraphQL its live summary, review threads (both levels paged, bounded) and timeline; checks are polled with ETags, so an unchanged poll costs no rate limit; follows Git LFS pointers. Maps GitHub's refusals to what the page can say (a bare 403 is "not found") |
| `IRepoWriter.cs`, `GitHubWriter.cs` | **The only sender of mutations** (an Arch test holds it): REST for new comments, GraphQL for replies, edits, deletions and resolutions; three outcomes — done, refused, unknown (a timeout, a lost connection or a 5xx after sending) — and the reads reconciliation needs |
| `LfsGuard.cs` | Parses LFS pointers; allows LFS downloads only over https to allowlisted hosts; a connect callback that refuses internal addresses at the moment of connecting; no redirects |
| **`Auth/`** | |
| `AccessResolver.cs` | **Decides which credential reads a repository for a request.** First the server's scope (`Web:Repositories`): off the list is "not found" to everyone. Then, signed in: the person's own token. Anonymous: the App's token, only after GitHub says the repository is public |
| `SessionStore.cs` | Server-side sessions on `IDistributedCache` (in memory, or Redis when `Web:Redis` is set, so instances share them). The browser holds a random id; tokens are encrypted with ASP.NET Data Protection; idle and absolute limits enforced by the cache's expiry (a read refreshes the sliding expiry and never writes the entry, so after sign-in only a token renewal writes a session); an epoch kill switch; one token renewal per session at a time |
| `GitHubOAuth.cs` | `GitHubOAuth`: authorize URL with PKCE, code exchange, refresh, user, revoke. `AppTokens` (behind `IInstallationTokens`): a JWT signed through `IAppSigner`, then a read-only installation token per repository |
| `AppSigner.cs` | `IAppSigner`, the seam a key vault plugs into (rule 15); `PemAppSigner` signs with the configured key until the host is chosen |
| `AuthEndpoints.cs` | `/auth/login`, `/auth/callback`, `/auth/logout` (needs the `X-Versio-Request` header), `/api/me`. `SafeReturn` keeps the return URL on this site |
| **`Api/`** | |
| `ApiEndpoints.cs` | `/api/repos` (the scoped server's list, filtered to what the caller can read), `/api/repos/{owner}/{repo}` plus `/branches`, `/tags`, `/tree` (with `base`, the tree marked with what changed since that commit — `RepoExplorerBuilder.Build(…, changes)`, Step 8 — and with `changed=1` only what changed), `/commit` (one commit's message, people, parents, stats and files; the drawings are `/compare/view`'s from the first parent), `/commit/comments` (GitHub's commit comments, Step 3b, with whether the viewer may add one), `/view`, `/usages` ("used by", Step 3c: the objects of the same project whose typed source names this one, cut at a bound and said so), `/compare`, `/compare/view`, `/history`, `/pulls` (GitHub's list, Step 7: `?q=` in GitHub's grammar — `Review/PullQuery.cs` checks every token and re-emits only known ones, pinned to this repository and to pull requests — answered by GitHub's search for a signed-in person, with the open and closed counts and a cursor, and by REST's list by state for an anonymous reader, which says so), `/pulls/view`, `/releases`, `/releases/view` (lists carry meta; one Markdown body is rendered when picked), `/raw` (a document's bytes for the Docs tab, under `Documents.cs`'s rules), and the three `/assets/…` routes serving Core's CSS and script. Validates every input, writes an audit line per view, turns failures into status codes. `Loader` does credential → commit → model |
| `PullEndpoints.cs` | **Pull request review, read side** (PLAN.md, *Pull request review*). `/pulls/{n}/snapshot` resolves and seals one revision; `/files`, `/files/view`, `/status` (live head and base, polled: "stale" when either moved; checks of the snapshot's head), `/threads`, `/timeline`, `/commits`, `/checks/{runId}/annotations` (what one run left on the code, Step 3b: a hundred at most, the CI's words passed as text), `/reviews/pending` (the viewer's own, read with their credential), `/viewed`, `/merge-state` (what the merge box draws, read only with the merge tier on) and `/coverage` (per file, complete, partial or unchecked, for the review dialog; at most 200 drawings) take it in the `X-Versio-Snapshot` header and answer from its SHAs. Repository-level, under `MapRepoLevel`: `/labels`, `/milestones` (what the editors offer) and `/pulls/template` (the repository's pull request template for the form). `/files/view` answers with the drawing's coverage (complete, partial, raw) and every gap. Every request re-authorises first, then opens the snapshot, which must name this repository, pull request and viewer |
| `PullWriteEndpoints.cs` | **Pull request review, write side** (slices 2–4): `/comments`, `/line-comments`, `/anchor-comments` (a drawn element by its anchor id: re-rendered, resolved and tagged by the server, posted at the element's own lines inside GitHub's hunks or on the file), `/threads/{id}/replies`, `/threads/{id}/resolve`, `PATCH /comments/{id}`, `/comments/{id}/delete`, `/markdown` (preview); reviews (C2): `/reviews` (one call with `commit_id` = the snapshot's head), `/reviews/pending/threads` (a draft, into the viewer's pending review on the head, created with `commitOID` the first time), `/reviews/{id}/submit`, `/reviews/{id}/submit-historical` (an older pending review, for the commit the request names), `/reviews/{id}/discard`, `/reviewers`, `/viewed`; metadata (slice 5, `PullMetaEndpoints.cs`, the same partial class): `/edit` (title, description, base — each naming what it replaces), `/state`, `/draft`, `/labels`, `/assignees`, and `POST /api/repos/{owner}/{repo}/pulls` (open one, through `RunWithoutSnapshotAsync`: no pull request, so no snapshot), with `POST /api/repos/{owner}/{repo}/markdown` previewing the form's description the same way the review's `/markdown` does, and `POST /api/repos/{owner}/{repo}/commits/{sha}/comments` (Step 3b, the same way: a commit is its own revision, named by its full sha and resolved first; settled by the viewer's tagged comment on that commit); the merge tier (slice 6, `PullMergeEndpoints.cs`, each marked `MergeEndpoint`, which `WriteGate` answers 404 for unless `Web:Writes:Merge`): `/merge` (`sha` = the snapshot's head, only in a `mergeStateStatus` anyone may merge in), `/update-branch` (`expected_head_sha`; or `method: REBASE` through `updatePullRequestBranch`, whose lost answer stays unknown since a rebase leaves nothing to recognise), `/auto-merge` (`expectedHeadOid`), and from Step 6 `/delete-branch` (after a merge or a close, this repository's branch only, refused unless its tip is still the pull request's head — GitHub's ref delete has no expected commit, so the moment between that read and the delete stays open, said on the button), `/restore-branch` (at that head), `/merge-queue` (in with `expectedHeadOid`, or out; only where the base has a queue); the rest of GitHub's page (FINISH.md Step 5): `/reviews/{id}/dismiss` (an approval or request for changes, with its reason), `PATCH /reviews/{id}` (the viewer's own submitted summary), teams by slug on `/reviewers` (`addTeams` / `removeTeams`, which is also "re-request"); `PullConversationWriteEndpoints.cs`: `/reactions` (a comment, a review or the pull request's own node — S7 checked for all four kinds), `/comments/{id}/minimize` (a reason, or null to show again), `/lock`, `/milestone` (one the repository has, as labels), `/subscription`; `PullSuggestionEndpoints.cs` (merge tier): `/suggestions/apply` — the ```suggestion blocks of one or several review comments (`Review/Suggestions.cs`: the first fence; applied bottom-up with the file's own line endings kept) committed as ONE `createCommitOnBranch` whose `expectedHeadOid` is the reviewed head, refused for an outdated thread, an old-side one, overlapping lines or a fork's branch, and settled after a lost answer by the live head's parent and the operation tag in its message. `MapWrite` marks each; `WriteGate` (middleware, before any body is read) applies the kill switch, the forgery checks, the body cap and person-only access; `Writes.RunAsync` takes each through snapshot, operation key, reservation, preconditions (node scope, GitHub's hunks, staleness), the call, and the record — reconciling, never retrying, an unknown outcome |
| `Documents.cs` | What the Documentation tab serves raw, and how: the extension allowlist, the content type chosen by us, inline for PDF alone. The self-XSS reasoning is on the class; the threat row is in THREAT-MODEL (Tampering) |
| **`Repo/`** | |
| `RepoModel.cs` | `RepoModelLoader` turns one commit into a `RepoModel`: GitHub's file list, `.versio/repo.json`'s bindings, each export's `_manifest.json`, each project's library, task and device catalogs, any File-Based Storage source project, then `RepoExplorerBuilder`. Cached per repository and commit; concurrent loads of one commit share one; each blob fetched once. `NodeFor(path)` is an index; `Objects` is Core's `ObjectIndex`, for type links |
| `SingleFlight.cs` | Concurrent requests for the same key share one piece of work (model loads, visibility checks, branch resolution) |
| **`Review/`** | |
| `ReviewSnapshot.cs` | `ReviewSnapshot` (base tip, merge-base, head, issued, viewer) and `ReviewSnapshots`, which seals it with Data Protection and opens it only for the same repository, pull request and viewer, within 8 hours. Integrity, never access |
| `ReviewTags.cs` | The operation tag a posted body carries and how drawing strips every Versio tag; operation keys, whose own timestamp makes expiry need no storage |
| `OperationLedger.cs` | `IOperationLedger`: atomic reservation of operation keys. `MemoryOperationLedger` (one instance), `RedisOperationLedger` (`SET NX`, shared and durable) |
| `GitHub/HunkLines.cs` | The lines GitHub will take a comment on, from its own patch: context on both sides, a range inside one hunk; `Placement` turns an element's lines into GitHub's line and start line |
| `GitHub/ThreadPlacement.cs` | Where each review thread goes on a drawing: by its anchor tag (Core's `AnchorMatcher`), else by the element holding GitHub's current line, else a hint for a drawn source line, else listed with the reason. A tag that disagrees with the current line places nothing |
| `PullReview.cs` | Resolves a pull request to a snapshot, re-reading the head and base until three reads agree; builds the file inventory from Versio's own tree diff, with GitHub's patches (for this revision only) deciding where a line comment may go, and renames only as GitHub reports them |
| **`Rendering/`** | |
| `ViewService.cs` | **Turns an object into the fragment the page shows**, for one commit or two. Reads every layer of the object (and each child's first file, for a configuration saved as one file per task), hands Core's `RenderChoice.Decide` the reader and draws what it chose — card, catalog, table with its code, ladder, note, Markdown, web page, source — applies the size limits and the time budget, adds notices (a native file no model draws says so), caches by content, commit, row and file. Every drawn ST identifier carries the file of the object it names in the same project (`ObjectLinks`, Step 3c). `ViewService.Usages.cs`: the "used by" search, each object's typed source read once per commit, cut at 1,500 files or the render budget and saying so |
| **`Http/`** | |
| `Middleware.cs` | `SecurityHeaders` (CSP, nosniff, no-referrer, noindex, no-store on the API; the diagram frame's own narrower policy), `RequestLog` (one line per API request under an operation id, path only), `ConsoleLogSink` |

### The page: `web/` (about 1,450 lines)

| File | What it does |
|---|---|
| `node.props` | **The pinned Node**: version 24.21.0 and its SHA-256 per platform. The server's build fetches it into `web/.node/` and refuses a mismatch |
| `package.json`, `package-lock.json` | Exact versions, installed with `npm ci` only |
| `vite.config.ts` | Builds into `src/Versio.Web/wwwroot/`; in development proxies `/api`, `/auth` and `/assets/*.css|js` to a server on port 5099 |
| `index.html` | Links Core's stylesheets (`/assets/code.css`, `/assets/ladder.css`) |
| `src/main.tsx` | The routes, in GitHub's shapes (FINISH.md, F): `/`, `/:owner/:repo`, `…/tree/:ref/*`, `…/blob/:ref/*`, `…/compare/:base...:head`, `…/commits/:ref/*`, `…/commit/:sha`, `…/branches`, `…/tags`, `…/docs/:ref`, `…/pulls`, `…/pulls/new`, `…/pull/:number[/commits|checks|files]` (the review), `…/releases[/tag/:tag]`, and a 404 page; the older shapes (`/history`, `?tab=`, `?n=`, `?tag=`) redirect. Applies the person's colour mode before the first draw |
| `src/api.ts` | The JSON client and its types; sign-out sends the header the server requires; `api.review.*` carries the sealed snapshot in `X-Versio-Snapshot`, never in a URL |
| `src/routes.ts` | Builds and parses the page's URLs (every view has its own link, in GitHub's shape, so a github.com link with the host swapped opens the same thing) |
| `src/theme.ts`, `src/title.ts` | The colour mode (system, light or dark; kept per browser, guarded against storage being refused) and the per-page browser title ("Title by author · Pull Request #3 · owner/repo · Versio") |
| `src/fragment.ts` | `sanitize` (DOMPurify: HTML and SVG, data-* markers kept, script and handlers removed), `linkTarget` (what a Markdown link may do: another Markdown document of the repository in the viewer — the desktop's rule, aligned in Step 9 — http(s) in a new tab, anything else nowhere) |
| `src/useAsync.ts` | Loads with cancellation, so a slow earlier answer never replaces a newer one |
| `src/components/FragmentHost.tsx` | Places a sanitised fragment, re-runs the ladder script, scrolls to `#L12`, turns gutter clicks into line links, Markdown and type-link clicks into navigation, and each Mermaid block into a sandboxed diagram frame. **Annotations**: content the page draws between rows (a review thread under its line), placed through portals into slots inserted after the rows a selector names — the renderers' markup is untouched — and reported back, so whatever finds no row is listed rather than lost |
| `public/mermaid-frame.html`, `public/mermaid-frame.js` | The diagram frame: draws one diagram with the desktop's vendored Mermaid (strict mode), sandboxed without same-origin; text in, a height out |
| `src/components/AppHeader.tsx`, `RepoLayout.tsx` | **GitHub's shell** (FINISH.md, replacing the desktop-shell workbench of 2026-10-14): the global header (mark, breadcrumb, the person's menu with the theme and sign-out, or Sign in) and the repository header — icon, owner / name, a Private badge, the underline nav Code · Pull requests (with the open count) · Commits · Releases · Docs — over a centred page, or a full-width one for the code view |
| `src/components/ui/` | The base components in Primer's grammar — `Icon` (Octicons, inline), `Button` / `LinkButton`, `Counter`, `IssueLabel` (filled, contrast-lettered), `StateBadge` (`pullState` picks it), `Avatar` (a letter when GitHub gave no picture), `UnderlineNav`, `Blankslate`, `Flash`, `Spinner` / `Loading`, `RelativeTime` ("3 days ago", the full stamp as the tooltip), `Layout` (main + sidebar), `SidebarSection`, `Box`. `app.css` styles them under the same names; `styles/tokens.css` holds the desktop palette's colours under Primer's names (pinned to `Palette.Light/Dark.xaml` by `tokens.test.ts`), light and dark, and re-points Core's `--cd-*` / `--ld-*` at them so the drawings follow the person's theme; `VersioMark` is the header's mark, held to `assets/versio.svg` by its test |
| `src/components/Conversation.tsx` | The conversation's pieces as github.com draws them (FINISH.md B3): `CommentCard` (the picture beside a card whose grey header names the person, their role, when, "edited", a permalink, with the reactions under the body; folded behind its reason when hidden), `EventRow` (an icon badge on the rail and one sentence per event kind, in GitHub's words), `CommitRow`, `ReviewCard` (the review's state as its badge and colour, its summary — or the page's editor in its place — its tools, its own threads under it with GitHub's hunk excerpt), `Reactions` (pills; with `onToggle` each a toggle and GitHub's picker), `Minimized`, `RoleBadge`. Nothing here writes: the boxes and the toggles' posts come from the page |
| `src/components/FilesTree.tsx` | Files changed's tree (FINISH.md Step 3): the inventory's paths as folders, an object's own files (`X.fb.st^/…`) nested under it, chains of single folders drawn as one row; per file the A/M/D/R mark, `+N −M`, the thread count, the viewed box; the filter; "N / M files viewed" |
| `src/components/TreePanel.tsx` | The object tree with a filter, the code view's sidebar and, marked, the compare and commit pages' (Step 8). Each row is the desktop's `TreeRow`: the vendor's pill on a project, the type or detail beside the name, the one-letter change glyph, a struck deleted name, and on a folder the count of changes below it in the strongest state's colour; a comparison opens every folder. Reveals the selection whenever it changes (expands the ancestors, scrolls the row into view), not only on the first draw — a type link into a closed folder used to leave its target hidden (FINISH.md, C2) |
| `src/pages/CommitPage.tsx` | `/owner/repo/commit/sha` (Step 8): the desktop's commit card — the message and its body, who authored and who committed, the signature, the stats, the parents, Browse files — over the marked tree of what the commit touched and every file drawn from the first parent as the compare view draws it, each loaded as it nears the viewport |
| `src/pages/RefsPage.tsx` | `/owner/repo/branches` and `/owner/repo/tags` (Step 8): GitHub's lists, each row the way into the tree, the commit, the commits and the comparison with the default branch; a tag with a release links to it |
| `src/components/Pane.tsx` | The pane card: the desktop's `PaneHeading` grammar (FILE or DIFF, the name, the folder from the spelled label, the full label as tooltip), Rendered / Raw, Unified / Side by side, layer chips, notices, errors with sign-in when it would help; for an HTML object the desktop's third tab, Web page (Step 9): the raw route framed with every sandbox flag off, the route's own `sandbox` policy standing behind the attribute |
| `src/components/ReviewWrite.tsx` | `CommentBox` (draft kept in sessionStorage with its operation key until the server says posted; preview through the server's Markdown; the outcome said — posted, pending, may-or-may-not, refused; an optional second target, "Add to review", and a held key retries only the target it was sent to; "Suggest a change" in its toolbar when the line's text is known; `submitRef` lets the page submit it for "Close with comment"), `ActionButton` (resolve, delete, unhide, subscribe, apply a suggestion) and `ReasonAction` (a write that needs a reason first — Dismiss review, Hide, Lock — as a small form under its button, with ActionButton's key rules). **How a held key behaves**, here and in the review dialog, the merge box (one key per action) and the new merge request form: it is kept with the target it was sent to and the snapshot it went under (`sentUnder`), so a retry is the same operation on the same revision. `releasesKey` lets it go on done; never on unknown or pending; on a refusal of a first attempt; and on a refusal of a RETRY only when the server marks it `X-Versio-Settled` (the operation's recorded outcome) — a 429 or a stale snapshot proves only that the retry did nothing. Every held key is saved with its snapshot in the same update, so there is no key without one to guard against. A box is keyed by its draft's identity, so one draft is never saved under another's |
| `src/components/MergeBox.tsx` | The merge box (slice 6; GitHub's shape since Step 6), only with the merge tier on: the split "Merge pull request ▾" button with the method menu, the commit title and message asked for in the confirm step, "Update branch ▾" with "Update with rebase" when behind, auto-merge as what it is — a later merge of whatever the branch then holds, confirmed only with an explicit yes to later commits being merged; after a merge or a close, Delete branch (the race said in the confirmation) or Restore branch, or the note that the repository deletes branches itself; "Add to merge queue" / "Remove from queue" where the base has one; every action confirmed in words naming the pull request, its branches, the commit and the method. Offers nothing when GitHub cannot say whether a merge is allowed, or when the head moved past the reviewed commit |
| `src/components/PullMeta.tsx` | The review page's metadata editors (slice 5): `TitleEditor` and the description edit fix the text they replace when editing starts — never the polled live text, which may be a colleague's newer edit — `BaseEditor`, `StateActions` (close, reopen, draft, ready), `LabelsEditor` (the repository's labels only), `AssigneesEditor`; and from Step 5 `MilestoneEditor` (the repository's open milestones only), `LockAction` (GitHub's reasons), `SubscriptionAction` |
| `src/pages/PullsPage.tsx` | `/owner/repo/pulls?q=`: GitHub's list (Step 7) — the search box in GitHub's grammar, the Open / Closed tabs with their counts, the menus (author, label, reviews, assignee, sort) each editing one token of the query, a row per pull request with its state icon, labels, review and check marks, assignees and comment count, a link straight to its page; Next by the server's cursor |
| `src/pages/NewPullPage.tsx` | `/owner/repo/pulls/new`: open a pull request between two branches of this repository — or from a fork's branch as `owner:branch`, which GitHub checks — from the template, as a draft or not, with the changed files shown under the form and a Markdown preview (Step 7). Reviewers, labels, assignees and the milestone are applied after it is opened, each its own write under the new pull request's snapshot; a refused one is said beside the link, never lost. The form and its operation key survive a reload; while an outcome is unknown the form is locked and the only way on is the same request again |
| `src/components/ReviewBar.tsx` | The viewer's review (C2): the pending review and its comments, Discard, and the dialog — Comment, Approve or Request changes (Comment only for the author), to `/reviews`, the pending review's submit, or the historical route naming its commit. Before submitting it lists files never opened here and files whose drawing may not show every change (`/coverage`, read only when the dialog opens). Step 5: GitHub's green "Review changes" button with the pending count; the drafts listed with their words and lines, each edited (`PATCH /comments/{id}` on a pending comment) or deleted in place. `ReviewersEditor` asks for and withdraws reviewers — people by login, teams by slug ("org/team" in the box) — draws GitHub's icon per standing, and re-requests a review from someone who already gave one |
| `src/pages/*.tsx` | Home (a scoped server's repository list, the open-a-repository form, or the sign-in gate), RepoPage (the code view: branch picker, the latest commit, the project tree in a sidebar beside the pane), ComparePage (two refs over the changed files, a Create pull request button where one can be opened, each file drawn as a comparison), CommitsPage (GitHub's commits page, by day; each subject opens what it changed, each row the repository at that commit), PullsPage (GitHub's list: the open count at the head, one row per pull request linking straight to its page), NewPullPage, PullPage (**the review**: the title with its number, the state badge and the "wants to merge … into … from …" line, the tabs across — Conversation / Commits / Checks / Files changed, with counters — against one sealed snapshot, the live state polled every 30 s with a reload offered when the pull request moves; the right sidebar carries reviewers, labels, a milestone, assignees and the checks count; the conversation is the description card, the timeline with every kind GitHub shows, the merge summary (reviews, checks, conflicts, for every reader) and the merge box, then the comment box, in GitHub's order; Commits carry the picture, the signature, the committer, the body on demand and a link to the commit; Checks are grouped by app with the required mark, the duration and the run's output on demand; Files changed is the changed-file tree beside every file stacked (`FileSection`: a sticky header with the chevron, the object and where it lives, `+N −M`, Viewed, the view switches; the drawing asked for as the section nears the viewport; a viewed file folded), threads under their lines on Raw and on the drawn rows and networks of rendered views, a shift-click making a line draft a range, resolved threads hidden on request, a folded stretch opening the whole file on click, a comment permalink scrolled to once placed, a link to another changed file going to its section; threads under their lines on Raw and on the drawn rows and networks of rendered views, each drawing saying whether it shows every change and listing the gaps when not; the review bar, viewed marks, and changes since the viewer's last review), ReleasesPage (one box per release, the opened one with its notes), DocsPage (every document under its path: text through /view, PDF embedded from /raw, Office formats as downloads), NotFound |
| `src/styles/app.css` | The page around the fragments, in github.com's grammar over `tokens.css`: the headers and nav, the two-column layout, the code view's tree sidebar, the file-tree-beside-diffs review view with sticky file headers, the components above, GitHub's markdown body. The fragments scroll inside their box rather than clipping at the pane's edge. Narrow screens stack the sidebars and make the tree a drawer |
| `src/fragment.test.ts` | 13 vitest tests: the sanitiser (the ladder's checkbox kept, every other input removed), link rules, URL building |
| `src/ladderScript.test.ts` | 5: Core's ladder script, read from `LadderPageAssets.cs` itself (Vite `?raw`), run fragment after fragment into one document, counting the listeners it leaves |
| `src/pages/CommitsPage.test.tsx` | 4: the first page-component tests; render Commits over a stubbed fetch: the Compare link, the "latest 50" note only when a 51st commit came, the cut-branch-list note |

### Added to Core for the web, and usable by the desktop

| File | What it does |
|---|---|
| `src/Versio.Core/Diff/CodeHtml.cs` | One line of code as HTML: syntax classes, changed words, one escaping rule, hidden characters shown as `U+XXXX` markers; `A()` for attributes; the notice; 256-row blocks |
| `src/Versio.Core/Diff/CodePageAssets.cs` | The stylesheet for all code renderers (`cd-*`, `mt-*`, `ct-*`, `pc-*`); default colours are the desktop palette's |
| `src/Versio.Core/Iec/SourceHtml.cs` | The Rendered tab: `StSourceLayout` rows as HTML, unified or side by side, **the rows the desktop's `DiffView` draws**: a comparison folds with `StSourceLayout.Fold` unless `fullFile` (the pane's *Show full file*, `full=1`), and unified draws `StSourceLayout.Unified` (no removed lines; a pure deletion marked on the line after it). Decide what is drawn in Core, never here (FINDINGS 2026-10-23); every row names its file line on each side it exists on (`data-file-line`, `data-old-file-line`) and the block names its file (`data-src-path`), for review comments |
| `src/Versio.Core/Review/Anchors.cs` | Review anchors (PLAN.md, *Pull request review*, C3): `RenderedAnchor` (what a renderer drew that a comment can sit on, with its source layer, container, key, fingerprint and lines), `SemanticAnchor` (what a comment was written on), and `AnchorMatcher` — Exact, Moved, Ambiguous or Gone, refusing whenever it could be wrong. Host-neutral: GitHub's lines and tags are in `Versio.Web` |
| `src/Versio.Core/Review/Coverage.cs` | Review coverage (C4): `Coverage.Check` takes every changed line of every file of the object and asks the renderers' evidence — `Coverage.Source` (the lines a source layout draws added or struck) and `Coverage.Table` (a line inside one row, drawn as changed, every token a whole token of a shown field). A line nothing vouches for is a `CoverageGap` with its reason; `ReviewCoverage.Complete` only with none |
| `src/Versio.Core/Text/LineSpan.cs` | The file lines something drawn came from; `SafeXml.LinesOf` reads them from XML parsed with line info |
| `src/Versio.Core/Diff/UnifiedDiffWriter.cs` | Two texts to unified-diff text via `LineDiff`, so the existing diff pipeline runs unchanged |
| `src/Versio.Core/Diff/TextDiffHtml.cs` | The Raw tab: a parsed diff as HTML, unified or side by side |
| `src/Versio.Core/Projects/MemberTableHtml.cs` | A member table as HTML, old then new in changed cells |
| `src/Versio.Core/Projects/ObjectLinks.cs` (2026-10-24) | Links between structures by name (Step 3c): `Resolver` turns an identifier in one object's source into the object of that name in the same project (never itself, never another project; a library name stays text), for `CodeHtml.AppendLine`'s `data-link`; `NamesReferenced` lists the names a source refers to outside comments and strings, for "used by". The desktop's `DiffView` does not draw the links yet |
| `src/Versio.Core/Projects/RenderChoice.cs` (2026-10-23) | **The one decision of what a tree row's Rendered view draws**, for both hosts: `RenderChoice.Decide(node, reader, primary, formats)` returns the kind (project card, library / task / device catalog, task or device card, member table with what sits under it, ladder, empty-object note, Markdown, web page, source, nothing), the layer it came from, the parsed model, a `Reparse` for the committed or older copy, and the notice over a native file no model draws. The desktop's order, with the husk rule once. Pure: the host reads files. `ShellViewModel.BuildRenderedContent` and `ViewService` both call it; `RenderChoiceParityTests` (desktop suite) runs both hosts over every fixture row |
| `src/Versio.Core/Projects/Cards.cs` (2026-10-23) | The rows of the task card, the device card (slot / value / connector) and the project card (vendor, exported by, mirror, objects, libraries, tasks, coverage, warnings; a source project's own rows), decided once; `CardHtml.Fragment` draws a card as `pc-*` markup with `data-fact` rows |
| `src/Versio.Core/Projects/CatalogHtml.cs` (2026-10-23) | The library, task and device catalogs as HTML (`ct-*`): the desktop's columns, A / M / D marks, old then new in moved cells, empty slots under the connectors, `data-key` and `data-anchors` per row, `data-src-path` on the page; `Anchors` for review comments (no line spans: the catalog readers record none) |
| `src/Versio.Core/Text/SafeXml.cs` | The one XML parser every pure pack uses: no DTD, no resolver, a size cap |
| `src/Versio.Core/Iec/Ladder/LadderHtml.cs` (changed) | Takes an optional `LadderWording`; the desktop's text by default |
| `src/Versio.Core/Projects/ProjectTreeBuilder.cs` (changed) | `ProjectTreeNode.PrimaryFile`, moved from the desktop's `TreeRow`; which layer is code comes from `PlcFormats.LanguageFor`, never a list of extensions (the list held Siemens' `.scl` and `.udt` until a review found them) |
| `src/Versio.Core/Plc/PlcFormats.cs` (changed) | `LanguageFor`: a vendor's `SyntaxFor`, else the neutral catalog. The desktop's highlighter, its layer labels, the tree and the web viewer all ask it |
| `src/Versio.Infrastructure/Bindings/RepoConfigStore.cs` (changed) | `Parse(string)`, the store's own reader of `repo.json` |
| `src/Versio.Core/Logging/LogEvent.cs` (changed) | `LogCategory.Web` and `LogCategory.Auth` |

### Tests

| Where | What |
|---|---|
| `tests/Versio.Web.Tests/` (448 on 2026-10-24; the write, merge, suggestion, search and comments-everywhere classes joined the ones named here) | `ViewServiceTests` (tables, ladders, code under an interface, comparisons over the real fixtures), `RenderedKindsTests` (the project card, catalogs and cards from YAML and from the vendor's own files, the note, the web page and its sandboxed `/raw`, the native-file notice, the tree's grown node), `SecurityTests` (the rules against the real host on a free port, sign-in end to end, two instances sharing sessions, the real executable's startup), `GitHubRepoSourceTests` (the real client against a local server shaped like GitHub), `PerformanceTests` (each thing fetched once, compression, cache headers), `LfsGuardTests`. `Fakes.cs` is a GitHub in memory serving this repository's own fixtures; `TestHost.cs` runs the real host; `FixturePreview.cs` serves the fixtures on port 5199 to look at (opt-in) |
| `tests/Versio.Core.Tests/` | `SourceHtmlTests`, `TextDiffHtmlTests`, `UnifiedDiffWriterTests`, `MemberTableHtmlTests`, `CatalogHtmlTests`, `CardsTests`, `RenderChoiceTests` (every arm with a stand-in format), `SafeXmlTests`, `SyntaxTokenizerCoverageTests`, `RendererAllowlistTests` (the catalogs and cards included), `PrimaryFileTests`, and the wording test in `LadderHtmlTests` |
| `tests/Versio.App.Tests/RenderChoiceParityTests.cs` | **Both hosts over every fixture row.** The real shell on a temp repository holding the fixtures, the web's `ViewService` on a fake GitHub holding the same files; every row selected on both, what each drew mapped to a kind, the two compared, and the desktop's drawing checked against the choice it recorded. References `Versio.Web.Tests` for its fake GitHub |
| `tests/Versio.Arch.Tests/` | `ProjectReferenceTests.Versio_Web_references_only_the_pure_packs`; `PurityRuleTests`: no process or file access in the server, vendors named only in `RegisterFormats`, XML only through `SafeXml` in Core and the packs |
| `web/src/` vitest (195 on 2026-10-24) | The page: its pure modules, the ladder script as Core writes it, and every page rendered against a stubbed server — the pull request page's file is the largest, and waits for the page to be at rest rather than for ticks |

---

## 3. One request, end to end

Opening `/acme/plant/blob/main/Device/Plc%20Logic/Application/Signals.xml`:

1. **The page loads.** The server serves `wwwroot/index.html` through the page fallback
   (`WebHost`: static files first, then routing, then `/{owner}/{repo}/{**rest}` → index.html).
   React reads the route and asks `/api/repos/acme/plant`, `/branches`, `/tree?ref=main` and
   `/view?ref=main&path=…Signals.xml`.
2. **Middleware.** `SecurityHeaders` adds the CSP and the rest; `RequestLog` opens an operation
   id; the rate limiter counts the client: the connection, or behind a proxy named in
   `Web:TrustedProxies` the address it forwarded (nobody else's `X-Forwarded-For` counts).
3. **Credential.** `ApiEndpoints.Loader` validates owner, repo and ref, then
   `AccessResolver.ResolveAsync`: the session cookie → `SessionStore` → the person's token
   (refreshed if close to expiry), or, anonymously, an installation token after GitHub says the
   repository is public.
4. **Commit.** `IRepoSource.ResolveCommitAsync` turns `main` into a SHA **with that
   credential**. Only now is any cache consulted.
5. **Model.** `RepoModelLoader.LoadAsync` (cached per repository and commit): tree →
   `repo.json` → manifests → `RepoExplorerBuilder.Build`. The same call builds the tree the
   page shows.
6. **View.** `ViewService.ViewAsync`: `NodeFor(path)` finds the `Signals` object and its
   layers; every layer's blob is read (cached by hash); Core's `RenderChoice.Decide` is handed
   the reader and asks each registered format `TryParseMemberTable` over each layer. Signals
   is an interface, so the choice says its ladder (parsed with `TryParseLadder`) sits
   underneath. The HTML comes from `MemberTableHtml.Fragment` and `LadderHtml.Fragment`; the
   result is cached by the renderer version, the row, the file shown and every blob hash.
7. **Answer.** JSON with `kind`, `title`, `html`, `notices` and `layers`; an audit line naming
   who viewed what at which commit; `Cache-Control: private, no-store`.
8. **Placing.** `FragmentHost` runs DOMPurify over the HTML, sets it, re-runs
   `/assets/ladder.js` for the ladder's navigation, and scrolls to `#L…` if there is one.

A comparison (`/compare/view?base=…&head=…`) is the same with two models: the choice is made
from the newer copy of each layer (the older where the newer is gone), then member tables
through `MemberTableDiff`, catalogs through `CatalogDiff` (both sides read by the choice's
`Reparse`), ladders through `LadderDiff`, source through `StSourceLayout.Mark`; a card, a note
or a document draws one version and a notice says when its file changed. Raw goes through
`UnifiedDiffWriter` → `UnifiedDiffParser` → `TextDiffHtml`.

---

## 4. What it reuses from the desktop app

The point of the design is that the browser and the window draw the same thing because they
run the same code. Everything below is **called, not copied**.

### Shared as is (existed before the web viewer)

| From | Used for |
|---|---|
| `Versio.Core.Iec.Ladder`: `LadderDiff`, `LadderHtml`, `LadderPageAssets`, `LadderMerge`, the layout | Ladder and FBD, drawn and compared. `LadderHtml` already named "the React page" as its second caller |
| `Versio.Core.Text`: `Markdown`, `MarkdownHtml` | READMEs and other Markdown |
| `Versio.Core.Diff`: `LineDiff`, `UnifiedDiffParser`, `SideBySideDiff`, `IntralineEmphasis`, `SyntaxTokenizer`, `LanguageCatalog` | Every text diff and every highlighted line |
| `Versio.Core.Iec.StSourceLayout` | The Rendered layout: Declaration / Implementation, IDE numbering, `Mark`, `SideBySide`, `Body` |
| `Versio.Core.Projects`: `RepoExplorerBuilder`, `ProjectManifest`, `MemberTable`, `MemberTableDiff`, `WorkspaceProject` | The object tree, member tables and their comparison |
| `Versio.Core.Plc.PlcFormats` and the three pure formats (`CodesysProjectFormat`, `TiaPortalProjectFormat`, `TwinCatProjectFormat`) | Every vendor-specific parse: member tables, ladders, code bodies, sections, syntax |
| `Versio.Core.Bindings`: `RepoConfig`, `BindingConventions` | Reading `repo.json` and finding manifests |
| `Versio.Core.Projects`: `LibraryCatalog`, `TaskCatalog`, `DeviceCatalog`, `ExportCoverage` forms | The libraries, tasks and devices grafted into the tree, read as the desktop's `ReadCatalogs` reads them |
| `Versio.Core.Projects.ObjectIndex` | Member types linked to the objects they name, as the desktop's `LinkTypes` does |
| `IPlcProjectFormat.DescribeSourceProject` | File-Based Storage projects described from GitHub's file list, as the desktop's explorer does from disk |
| `src/Versio.App/Assets/mermaid/mermaid.min.js` | Mermaid diagrams: the desktop's vendored file, linked into Versio.Web as an embedded resource, not copied |
| `Versio.Core.Logging`: `VersioLogger`, `LoggerExtensions`; `Versio.Infrastructure`: `RollingFileSink`, `SystemClock` | The same log format, operation ids and daily file as the desktop (in a `web/` subfolder) |

### Moved so both share one answer (were desktop-only)

| What | From → to |
|---|---|
| Which layer a pane opens | `TreeRow.PrimaryFile` (WPF view model) → `ProjectTreeNode.PrimaryFile` (Core); `TreeRow` delegates |
| Reading `repo.json` | `RepoConfigStore.LoadAsync` internals → `RepoConfigStore.Parse(string)` |

### New in Core, ready for the desktop to adopt

`CodeHtml`, `SourceHtml`, `TextDiffHtml`, `MemberTableHtml` and `CodePageAssets` are the HTML
versions of what the desktop draws in WPF (`DiffView`, the member-table grid). The desktop still
draws in WPF; moving it onto these is "later" in both plans, and would leave one renderer for
every product, as `LadderHtml` already is. `SafeXml` is already used by the desktop, because it
sits under the packs' parsers.

### Shared since 2026-10-23: the choice of what a row draws

`RenderChoice.Decide` in Core is the one switch. `ShellViewModel.BuildRenderedContent` hands it
a disk reader and draws the pane's records from its answer; `ViewService` hands it the fetched
layers and draws HTML. Until then each host had its own copy and they drifted — the web had no
project card, no catalogs, no husk rule and no web page, and drew the File-Based Storage
objects that hold nothing as raw metadata in a pull request (FINDINGS 2026-10-23).
`RenderChoiceParityTests` runs both hosts over every fixture row; it found four more
web-only differences on its first run (device objects and grafted rows that 404'd, a task
configuration whose children were never read, a cache key that confused a task row with its
configuration), each fixed under it.

### Mirrored, not shared (and why)

| What | Desktop | Web | Why not shared yet |
|---|---|---|---|
| Wording of comparisons | "since the last commit" | "since the older version" | Different questions; `LadderWording`, `MemberTable.Note`, `ViewService.Reword` and `ViewService.RewordCatalog` carry the web's words, and a test fails if Core's phrases change |
| The project card's host rows | file size and save time, last export, toolchain installed | — | Only a disk and a machine can answer them; the shared rows are `Cards.Project` |

### Not used by the web, on purpose

The sync engine, locks, watchers, git client, the toolchains and runners, the `.Host`
executables, and the whole WPF app. The viewer reads, and writes only what THREAT-MODEL.md's
table lists, each write through `MapWrite` and the ledger.

---

## 5. Where each security rule lives

| Rule (PLAN.md) | Code |
|---|---|
| 1 Who reads | `AccessResolver.ResolveAsync`; `AppTokens` mints per-repository, read-only tokens |
| 2 No cross-user cache | `ApiEndpoints.Loader` (credential, then commit, then caches); cache keys in `RepoModelLoader` and `ViewService.Key`; `no-store` in `SecurityHeaders` |
| 3 Bounded parsing | `SafeXml`; `WebOptions.MaxDrawBytes`, `MaxBlobBytes`, `RenderBudget` in `ViewService`; the regex timeout in `Program.cs` / `WebHost.ConfigureProcess`; `LineDiff`'s cell cap |
| 4 No SSRF | `LfsGuard` (allowlist, connect callback, no redirects); GitHub URLs from configuration only |
| 5 Markup walls | Escaping in `CodeHtml` and every renderer; `RendererAllowlistTests`; `web/src/fragment.ts` (DOMPurify); the CSP |
| 6 Headers | `Http/Middleware.cs` |
| 7 Tokens at rest | `SessionStore` (Data Protection, shared through Redis); key ring persistence in `WebHost` (`Web:KeysDirectory`) |
| 15 App key | `IAppSigner` (`AppSigner.cs`); a vault signer replaces `PemAppSigner` in `WebHost` |
| 8 Rate limits | `WebHost` (`AddRateLimiter`, and `ForwardedFrom` for `Web:TrustedProxies`), `RequireRateLimiting` on every API and auth route |
| 9 Hidden and look-alike characters | `CodeHtml.IsHidden` (markers) and `CodeHtml.MixedScripts` (underlined names), with the notice |
| 10 Nothing drawn ≠ nothing changed | `ViewService.NothingDrawnChanged` |
| 11 Visibility re-check | `AccessResolver` (five-minute verdict cache) |
| 13 Audit, noindex | `ApiEndpoints.Audit`; `X-Robots-Tag` and the page's robots meta |
| 14 Session hygiene | `SessionStore.Create` (new id per sign-in), `__Host-` cookies in `AuthEndpoints` |
| 16 Least privilege | `Dockerfile` (non-root, volumes); Arch rules (no process, no file access) |
| 17 Kill switches | `WebOptions.AnonymousAccess`, `WebOptions.SessionEpoch` |

---

## 6. Building, running, looking at it

| To | Do |
|---|---|
| Build and test the server | `dotnet build Versio.sln`, `dotnet test tests/Versio.Web.Tests` (no Node, no network) |
| Build the page | `dotnet build src/Versio.Web -p:BuildWeb=true` (fetches the pinned Node, `npm ci`, `vite build`). Release builds always do |
| Test the page | `npm test` in `web/` with `web/.node/node-v24.21.0-win-x64` on PATH |
| Run it against GitHub | `dotnet run --project src/Versio.Web` (anonymous, public repositories, until the App is configured) |
| Look at one real pull request, no App | `python tools/web/dump_pull.py <owner> <repo> <number> <clone> <dump>` (read-only, through `gh`), then `VERSIO_WEB_PREVIEW_PULL=<dump> dotnet test tests/Versio.Web.Tests --filter PullPreview`, then http://127.0.0.1:5199/&lt;owner&gt;/&lt;repo&gt;/pull/&lt;number&gt;. Sessions for the author and a reviewer are in `%TEMP%/versio-web-pull-preview-sessions.txt`; writes stay in memory and show on the page. Keep the dump out of git |
| Look at it on the fixtures, no GitHub | `VERSIO_WEB_PREVIEW=1 dotnet test tests/Versio.Web.Tests --filter FixturePreview`, then http://127.0.0.1:5199/acme/plant |
| Develop the page with hot reload | run the server on port 5099, then `npm run dev` in `web/` |
| Configure sign-in | `Web__GitHub__ClientId`, `ClientSecret`, `AppId`, `PrivateKeyPem`, `AppSlug`, `PublicBaseUrl` from the host's secret store |
| Ship | `dotnet publish src/Versio.Web -c Release`, or the `Dockerfile` |
| Run behind a load balancer or CDN | `Web__TrustedProxies__0=10.0.0.0/8` (addresses or CIDR, one per index), or the rate limit counts the balancer as one client; a malformed entry stops startup |
| Scope the server to chosen repositories | `Web__Repositories__0=owner/repo` (one per index): everything else is "not found", and the home page lists these, filtered per caller; a malformed entry stops startup. With `Web__AnonymousAccess=false` the page is sign-in only |
| Look at the scoped home page on the fixtures | `VERSIO_WEB_PREVIEW_SCOPED=1` beside `VERSIO_WEB_PREVIEW=1` |

Logs: one daily file in `%LOCALAPPDATA%\VersioUser\logs\web\` (or `Web:LogDirectory`), and the
same lines on standard output.

---

## 7. How it stays this shape

| If someone… | What fails |
|---|---|
| references an `*.Infrastructure` pack or a `.Host` from the server | `ProjectReferenceTests.Versio_Web_references_only_the_pure_packs` |
| starts a process or reads a file in the server | `PurityRuleTests.Versio_Web_starts_no_process_and_reads_no_file` |
| uses a vendor type anywhere but `RegisterFormats` | `PurityRuleTests.Versio_Web_names_a_vendor_only_where_it_registers_the_formats` |
| parses XML in Core or a pack without `SafeXml` | `PurityRuleTests.Xml_is_parsed_only_through_SafeXml` |
| emits markup a file could inject | `RendererAllowlistTests`, each renderer's own escaping test |
| renames a CSS class on one side only | the "every class is in the stylesheet" tests |
| lets the desktop's "last commit" wording reach the page | `ViewServiceTests.No_view_speaks_of_the_last_commit_or_uncommitted_changes` |

**Adding a vendor** to the viewer is one line in `WebHost.RegisterFormats`, once its pure pack
exists. **Adding a host** other than GitHub is a new `IRepoSource`; nothing above it changes.
