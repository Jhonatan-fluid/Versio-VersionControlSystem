# Versio Web: a read-only repository viewer

**Date:** 2026-10-11, after a review of the stack, the sign-in and two security passes with the
owner. **This is a design record, not the live queue.** What Versio is today lives in
[`../STATUS.md`](../STATUS.md) (item 10). **Built on `WebViewer`, 2026-10-11, not merged:**
Steps 0 to 5 done and tested, Step 6 written but its image not built (no Docker on that machine).
Each step below says what was built and what changed from the plan. Still to do, in order: the
owner registers the GitHub App and sign-in is run against real GitHub; the image is built; who
hosts it is decided. **The map of what was built** (layers, files, one request end to end, what
is reused from the desktop) is [`ARCHITECTURE.md`](ARCHITECTURE.md).

| | |
|---|---|
| Document | A browser page that opens a Versio repository on GitHub and shows it the way the desktop app does. View only |
| Sibling of | The desktop app, and [`../outpost/PLAN.md`](../outpost/PLAN.md). Its Step 1 **is** Outpost's Step 2b: the HTML renderers are built once, for both |
| Stack | ASP.NET Core server that renders with `Versio.Core` and the pure vendor packs; a React + TypeScript page that only places the HTML (owner, 2026-10-10) |
| Sign-in | A read-only GitHub App; the token stays on the server |

## Context

The owner wants Versio in a browser: open a repository that Versio manages and look at it the way
the desktop app shows it. The first version only views. It does not commit, sync or touch a PLC.
The page has to use **the same rendering code** as the desktop app, not a copy of it.

What already exists:

- **Ladder and FBD already render to HTML in Core.** `LadderHtml.Fragment()`
  (`src/Versio.Core/Iec/Ladder/LadderHtml.cs`) plus `LadderPageAssets.Css/Script`. The desktop
  shows that HTML in WebView2 (`src/Versio.App/Views/LadderPage.cs`). Its remarks already name a
  React page as the second caller, and say a TypeScript re-implementation was considered and rejected.
- **Markdown renders to HTML in Core.** `src/Versio.Core/Text/MarkdownHtml.cs`.
- **Highlighted text, text diffs and member tables are still drawn in WPF.**
  `src/Versio.App/Views/DiffView.cs` is a virtualised ListBox over `UnifiedDiffParser`,
  `SideBySideDiff`, `IntralineEmphasis`, `SyntaxTokenizer` and `StSourceLayout`. The parsing and
  logic are in Core. Only the drawing is WPF.
- **The repository tree is built from a list of files.** `RepoExplorerBuilder.Build(files, projects)`
  needs no disk and no git, so the file list GitHub returns is enough.
- **Parsing a vendor's file is vendor code.** `IPlcToolchain.TryParseLadder` and
  `TryParseMemberTable` live in the *pure* vendor packs (`Versio.Plc.Codesys`, `.TiaPortal`,
  `.TwinCat`). Core and those packs target plain `net9.0`, so they run on Linux too. None of them
  needs CODESYS, Portal or XAE.
- **docs/outpost/PLAN.md §8 and Step 2b already plan the missing piece.** Outpost plans HTML
  renderers in Core for text diffs and member tables, served by an API and placed by React. This
  viewer needs exactly the same piece, so it is shared work and should be built once.

## Recommendation, in short

**An ASP.NET Core server (C#) that renders, and a React + TypeScript page that only places the
HTML the server sends.** The same rendering code runs because it is the same compiled C#. Neither
React nor TypeScript ever draws a rung or highlights a line.

- **Why React.** The owner confirmed it on 2026-10-10, after a review. It keeps Outpost's
  existing decision, and Outpost's agent needs a JSON API anyway. React only lays out the page:
  tree, tabs, pickers. Considered and rejected: ASP.NET pages built on the server, with htmx
  (one toolchain, but a second stack once Outpost's interactive screens arrive), and Blazor
  WebAssembly (below).
- **Why render on the server and not in the browser (Blazor WASM).** I looked at WASM seriously:
  Core does no I/O, so it would compile. But logging in to GitHub needs a server anyway, because
  the token exchange needs a client secret and GitHub's token endpoint does not allow browser
  (CORS) requests. Once a server exists, letting it render keeps the token and the parsing in one
  place, makes the page about 5 MB lighter, and lets results be cached by content.
- **Why not GitHub's own diff (the compare API).** It cuts off large files. The fixture's
  33,644-line visualisation is that kind of file. The server fetches both versions of the file
  and diffs them with `LineDiff` (`src/Versio.Core/Diff/LineDiff.cs`), the comparison the
  Rendered tab has used since 2026-10-10. No new library. Everything after
  `UnifiedDiffParser` is then shared code.

## Access to GitHub: a GitHub App, with the token kept on the server

1. **Register a GitHub App ("Versio Viewer"), not an OAuth App.** Permissions: `Contents: read`,
   `Metadata: read`, nothing else. The organisation installs it on only the repositories it chooses.
   An OAuth App's `repo` scope would give write access to every repository the user has, and a
   viewer has no reason to hold that.
2. **Sign-in uses the app's user flow (OAuth with PKCE).** The server swaps the code for a
   short-lived user token (8 h, plus a refresh token). What the user can see is then the
   intersection of their own access and the app's installation, so the viewer never shows
   someone a repository they could not open on GitHub.
3. **The token never reaches the browser (the backend-for-frontend pattern).** The server keeps it,
   encrypted with ASP.NET Data Protection, behind an `HttpOnly; Secure; SameSite=Lax` session
   cookie. Not in localStorage, not in a URL. The page calls only its own server. The server
   calls `api.github.com`.
4. **Public repositories work without signing in**, through the app's installation token, so they
   do not hit the 60-requests-an-hour anonymous limit.
5. **Never logged:** tokens, the client secret, the app's private key. The secret and the key live
   in the host's secret store (Azure Key Vault or AWS Secrets Manager), never in `appsettings.json`.
6. **Behind one interface, `IRepoSource`** (list refs, list a tree, read a blob, list commits).
   GitHub is the first implementation. GitHub Enterprise Server is the same code with a different
   base URL. GitLab and Azure DevOps come later, as new implementations, without changing the page.

### The sign-in flow, end to end

1. `/auth/login?returnUrl=…` creates `state` and a PKCE verifier, keeps them in a short-lived
   encrypted cookie, and redirects to `github.com/login/oauth/authorize`.
2. `/auth/callback?code&state` checks `state`. The **server** exchanges code, client secret and
   verifier for an access token (8 h) and a refresh token (about 6 months).
3. `GET /user` for name and avatar. The tokens go, encrypted, into a **server-side session store**
   (in memory for the proof of concept, then the database or Redis). The cookie holds only a
   random session id, which means a session can be revoked. Redirect to `returnUrl` only if it
   is one of our own pages (no open redirect).
4. Each API call finds the token by session id. Near expiry it refreshes quietly. If the refresh
   fails, the API returns 401, and the page goes to `/auth/login?returnUrl=<current page>`.
5. The repositories shown are those where the app is installed and the user has access
   (`/user/installations/…/repositories`). Not installed: the page links to the App's install
   page. An organisation that enforces SAML: GitHub's 403 and `X-GitHub-SSO` header become an
   "authorize SSO" link, not an error.
6. Logout is a POST with an antiforgery token. It deletes the session, clears the cookie and
   revokes the token at GitHub.
7. **Who you are and repository access are kept apart.** The user record holds linked accounts
   from day one. In v1, GitHub is both. When Outpost arrives, the company's login (§16 there) is
   who you are, and GitHub is a linked "Connect GitHub" account.
8. A separate development GitHub App, with a `localhost` callback, so development never shares
   production's secret.
9. Logged: each sign-in, refresh, refusal and logout, with the GitHub user id and an operation
   id. Never a token, code, verifier or secret.

## What the page looks like

GitHub's layout, with Versio's panels inside it:

```
┌──────────────────────────────────────────────────────────────────────────┐
│ Versio  ▸ acme/line3-plc   [branch: main ▾]   [History]  [Compare]  (me) │
├───────────────────────┬──────────────────────────────────────────────────┤
│ Project ▾  Files      │ PLC_PRG  (Program, ST)       [Rendered|Raw|Ladder]│
│ ▾ Line3.project       │ ┌──────────────────────────────────────────────┐ │
│   ▾ Application       │ │  1 PROGRAM PLC_PRG                           │ │
│     ▸ POUs            │ │  2 VAR                                       │ │
│       • PLC_PRG   ◀── │ │  3   bStart : BOOL;                          │ │
│       • FB_Valve      │ │ ──────── implementation ───────────          │ │
│     ▸ DUTs            │ │  4 IF bStart THEN ...                        │ │
│     ▸ GVLs            │ └──────────────────────────────────────────────┘ │
│ ▸ CleaningStation.ap20│  last changed 3 days ago by J. — "fix interlock" │
└───────────────────────┴──────────────────────────────────────────────────┘
```

- **The tree** is `RepoExplorerBuilder`'s output: the PLC object tree (POUs, DUTs, GVLs), not just
  folders. The desktop's "Project" and "Files" views become a toggle.
- **The file pane** has the same modes as the desktop: Rendered (ST with its declaration and
  implementation divider), Raw, Ladder/FBD as networks, data types as member tables, Markdown
  for READMEs.
- **History**: the commits that touched this object.
- **Compare**: pick two commits or two branches. You get the changed files in the same tree, and
  each one diffed side by side or unified, ladder merged or side by side, as the desktop does it.
- **Every view has its own URL**: `/acme/line3-plc/blob/main/<path>?view=ladder`. A link can then
  be pasted into a review, which is the main thing a web viewer gives you over the desktop app.
- **Theme**: the shared CSS variables (`--ld-*`) with light and dark defaults, the way
  `LadderPage.Build` maps the WPF theme onto them today.

## Steps

**Step 0 — Branch. Done 2026-10-11.** `WebViewer`, from `main` at 78de833, which already
contained everything from FbdRendering. This record was committed on its own, so the reasoning
is in the repository before the code is. The baseline, re-derived on that commit before anything
changed: Core 1,693, Integration 171, Arch 27, CODESYS Pure 365, TIA Pure 93, TwinCAT Pure 23,
all green. **CODESYS Parity was 8 failed, 89 skipped** in a shell where `python` is only the
Microsoft Store placeholder. The 89 skip through `CPythonHarness.PythonIsAvailable()`; the 8 are
`PythonFileNameParityTests`, which do not. That is an existing test-guard defect, not caused by
this branch, and is tracked as its own task. `Versio.App.Tests` (several minutes) was not run for
the baseline, because no step here touches the app before Step 1 has its own tests.

**Step 1 — Text, source and member-table renderers to HTML, in Core (= Outpost Step 2b).**

*Done, 2026-10-11.* `CodeHtml` (one line of code, shared), `CodePageAssets`, `SourceHtml`,
`UnifiedDiffWriter` + `TextDiffHtml`, `MemberTableHtml`. Mutation-checked throughout; two
defensive branches (gap filling between tokens, a line-kind check before a line number) were
removed when a mutation showed nothing could reach them, and the tokenizer's coverage became a
test of its own instead. `CodeHtml.A` spells hidden characters in attributes, where a marker
element cannot go. Later the same day, the browser showed desktop wording leaking ("since the
last commit"); `LadderHtml` took an optional `LadderWording`, the desktop's text by default.
**Not done:** the large-file measurement on the 33,644-line visualisation; the blocks and
`content-visibility` are in place, unmeasured.

- `Versio.Core/Diff/TextDiffHtml.cs`: unified and side-by-side, from `UnifiedDiffParser` output,
  with `SyntaxTokenizer` spans and `IntralineEmphasis`. Plus `SourceHtml.cs` for a single file,
  laid out by `StSourceLayout`. Plus `MemberTableHtml.cs` from `MemberTableDiff`. Assets as
  constants, the `LadderPageAssets` pattern.
- **Large files**: the WPF view virtualises 33k lines. Try CSS `content-visibility: auto` on
  each hunk block first, measured on the fixture's 33,644-line visualisation. Only if the browser
  still struggles, the server sends hunks in pages and the page loads more on scroll. Measured in
  this step, not found out in Step 4.
- **Stable markers in every fragment, as a contract**: `data-line` (old and new numbers),
  `data-hunk`, `data-network`, `data-element`. The viewer uses them for links to a line. Later,
  merge reviews attach selection, comment threads and per-hunk merge choices to them, without
  React taking the drawing apart. Tested like `LadderPageAssets`' class names: a rename that
  misses one side must fail a test.
- **The line diff already exists: `LineDiff`**, in Core since 2026-10-10 (`StSourceLayout` calls
  it). A small writer turns its `LineStep`s into unified-diff text, so `UnifiedDiffParser`,
  `SideBySideDiff` and `IntralineEmphasis` read it unchanged. DiffPlex was the first idea, and was
  dropped once this was found. `LineDiff`'s 4,000,000-cell cap is also the memory budget security
  rule 3 asks for: a middle bigger than that is reported as all removed and all added, which is
  honest, and costs at most about 16 MB.
- Tests in `Versio.Core.Tests`, the way `LadderHtml` is tested: a known pair gives a known
  fragment, everything from the file is escaped, and unified and side by side show the same
  changed lines.

**Step 2 — `src/Versio.Web` (ASP.NET Core, net9.0).**

*Done, 2026-10-11.* Checked against real GitHub anonymously (octocat/Hello-World renders; this
repository, private, is a 404). Departures from the plan below: `CodesysPack.Register()` and its
siblings live in the Infrastructure packs, which also register IDE-driving toolchains, so the
server calls `PlcFormats.Register` with each pure format in `WebHost.RegisterFormats` instead.
`PrimaryFile` moved from the desktop's `TreeRow` into Core and `repo.json` got a pure
`RepoConfigStore.Parse`, so the server reuses both answers rather than copying them. The
endpoints are `/tree`, `/view`, `/compare`, `/compare/view`, `/history`, `/branches`. The
desktop's choice of renderer (table, ladder, Markdown, source) is mirrored in `ViewService`,
not shared, and says so; moving it into Core is later.

- References Core and the three **pure** vendor packs, never `*.Infrastructure`. ~~It is a
  composition root like `Versio.App`, so it calls the same `CodesysPack.Register()`,
  `TiaPortalPack.Register()` and `TwinCatPack.Register()`.~~ (See above.) Registration is its
  only knowledge of vendors.
- `IRepoSource` plus `GitHubRepoSource`, built on the Git Trees and Blobs APIs, so a whole
  repository's tree costs one call.
- Endpoints: `GET /api/repos/{o}/{r}/refs`, `/tree?ref=`, `/file?ref=&path=&view=` (returns a
  fragment), `/compare?base=&head=&path=&view=`, `/history?path=`.
- **The cache is keyed by blob SHA plus renderer version.** A blob never changes, so a rendered
  fragment never goes stale. A renderer upgrade starts a new key. It starts in memory, and moves
  to Redis or disk only when there is more than one instance.
- **Data Protection keys are stored where every instance can read them** (blob storage or Redis).
  Otherwise, with two instances, a session cookie one encrypted cannot be read by the other, and
  people are logged out at random.
- Recognises a Versio repository by `.versio/repo.json`. A repository without it is shown as
  plain files, with a line saying it is not set up for Versio.
- Logging through `VersioLogger` (or a bridge to it): each request gets an operation id, a line
  for what it fetched and rendered, and the outcome. A GitHub rate limit or 403 is logged and
  shown on the page, never swallowed.

**Step 3 — Sign-in.** The GitHub App and the server-held session described above, plus a 401
handler that sends the user back to sign in when the token has expired and cannot be refreshed.

*Built and tested, 2026-10-11, against a local server that answers like GitHub's OAuth
endpoints*: PKCE checked end to end (the verifier GitHub receives hashes to the challenge the
browser was sent), a forged state refused, logout revoking. Installation tokens are minted per
repository and read-only. **Not yet run against real GitHub**: that needs the App, which only
the owner can register.

**Step 4 — `web/` (React + TypeScript + Vite).**
- Shell, tree, tabs, branch picker, compare picker, history, and routing (React Router).
- **A fragment from the server is placed, never built.** It goes in through
  `dangerouslySetInnerHTML` inside a host component, under a strict CSP: no inline script, script
  from our own origin only (the same rule `LadderPage` enforces). `LadderPageAssets.Script` is
  served as a static file, never inline.
- The server escapes everything from a file. CSP is the second wall, as on the desktop.

*Done, 2026-10-11, and looked at in the built-in browser.* Node 24.21.0 is pinned by version and
SHA-256 in `web/node.props` (the owner asked for the repository to carry what the page needs);
the server's Release build fetches it into `web/.node`, refuses a hash mismatch (tested with a
false hash), and runs `npm ci` and `vite build` into `wwwroot`. Dependencies pinned exactly, at
least two weeks old, `npm audit` at zero; the versions first written from memory were stale and
had advisories (DOMPurify, React Router 6, Vite 5 and Vitest 2), so the page uses react-router 7.
Two hosting bugs only the browser showed were fixed with tests: links to files with an extension
were 404s on reload, and the first fix then 404'd the page's own assets.

**Step 5 — Guardrails in `Versio.Arch.Tests`.**

*Done, 2026-10-11:* exact project references; no `Process`, `File` or `Directory` in the
server's IL; every use of a vendor member only in `WebHost.RegisterFormats`. That last rule read
calls only at first and its positive control came back empty (a static field load is not a
call); it reads field loads and `newobj` too now.

- `Versio.Web` may reference only the pure packs, never `*.Infrastructure` and never a `.Host`.
- Vendor names in `Versio.Web` appear only in the `Register()` calls. One row in the existing
  vendor-seam tests.
- No `Process`, no `git.exe`. The server never clones.

**Step 6 — Deploy.** One container (the API serves the built React files), on Linux. For the proof
of concept, one Azure App Service or AWS App Runner instance. Who runs it, and for which
customers, is the same open question as Outpost §16 and is decided once for both.

*Written, not built, 2026-10-11:* `src/Versio.Web/Dockerfile` (multi-stage, Release publish
fetching the Linux Node by its pinned hash, non-root, read-only root file system, logs and key
ring on volumes) and `.dockerignore`. The machine that wrote it had no Docker.

**Later, on purpose:** the desktop moving onto the HTML text and member-table renderers (one
renderer for every product, as Outpost §17 already lists); GitLab, Azure DevOps, Bitbucket;
comments on a line; anything that writes.

## Security (each rule has a test)

The desktop parses the owner's own files. The server parses anyone's repository. The same code now
faces the internet, and that change is the source of most of what follows.

**Where each rule stands, 2026-10-11.** Built and tested (each test named in `Versio.Web.Tests`,
`Versio.Core.Tests` or `Versio.Arch.Tests`, most also mutation-checked): 1, 2, 3, 4, 5, 6, 8,
10, 11, 13, 14, 17. Built as code, with the production half being configuration: 7 (sessions are
encrypted with Data Protection; protecting the key ring with a vault is deployment), 16 (the
Dockerfile runs non-root on a read-only root; the outbound allowlist is the network's). Partly:
9 (bidirectional, zero-width and control characters are marked; homoglyphs are not), 12 (the
cache is in memory with a 30-minute sliding expiry, so nothing is at rest yet; a shared cache
must bring encryption and a retention). Open: 15 (the App key is configuration, not signed in
a vault), 18 (no webhooks or account linking exist yet), 19 (threat model, penetration test,
cost alarm), and the comparison-size cap in 8 (one file is bounded; the changed-file list of
two huge commits is not).

**Must have, before anything goes live**
1. **The installation token never reads for a signed-in user, and never reads a private repository.**
   A signed-in request always uses the user's token. The installation token is used only
   anonymously, and only after GitHub says `private: false`. Test: a private repository with no
   session returns 404.
2. **The cache cannot cross users.** Path → SHA is always resolved with the requester's token
   before the cache is looked at. No endpoint takes a raw SHA. Trees and refs are never shared
   between users. `Cache-Control: private, no-store` on every response.
3. **The parsers are hardened.**
   - A shared safe XML loader in Core: DTDs prohibited, `MaxCharactersInDocument` capped. It
     replaces the six `XDocument.Parse` calls in the pure packs (`CodesysLadderParser.cs:149`,
     `TiaPortalLadderParser.cs:104`, `TiaPortalMemberTableParser.cs:61`, `CodesysCatalogs.cs:301`,
     `TiaPortalNativeScrubber.cs:34,82`). This is vendor code, so the CODESYS gate runs.
   - A process-wide regex timeout (`REGEX_DEFAULT_MATCH_TIMEOUT`) set at server startup. Today
     none of the 23 regular expressions has one.
   - Size limits for each file and each response. A CPU and time budget with cancellation for each
     render. Over budget, the file falls back to escaped plain text with a notice.
   - Tests: a billion-laughs file, a ReDoS line, an oversized file and a huge network each end in
     the fallback within the budget.
4. **No SSRF.** Git LFS download addresses are followed only to allowlisted GitHub LFS hosts. Never
   private, loopback or link-local addresses (`169.254.169.254`). Download size capped. The GitHub
   Enterprise base URL is server configuration only.

**Defence in depth**
5. Three walls against injected markup: escaping in Core; a test that feeds random input through
   every renderer and checks only allowed tags and attributes come out; DOMPurify (with SVG
   allowed) on the client. Fallback, if ever needed: a sandboxed iframe on a separate domain.
6. Headers: a strict CSP (no inline script, `img-src 'self'`, `frame-ancestors 'none'`), HSTS,
   `nosniff`, and `Referrer-Policy: no-referrer`, because the URLs name private repositories and
   paths. `MarkdownHtml` already puts links in `data-href` and draws no images. Keep it that way.
7. Tokens at rest: the session store and Data Protection keys are encrypted with Key Vault or KMS.
   Sessions have an inactivity timeout and an absolute lifetime. Logout revokes the token at GitHub.
8. Rate limits per IP address and per user. A cap on the size of a comparison. Anonymous use cannot
   use up the installation's whole GitHub quota.

**Particular to a code viewer**
9. **Hidden Unicode (Trojan Source).** Characters that reverse text direction, zero-width
   characters and raw control characters are replaced by visible `U+XXXX` markers, with a notice
   above the code (`CodeHtml`, done 2026-10-11). Test: a file containing U+202E shows a marker and
   a notice, and the character itself never reaches the page. **Not done yet: homoglyphs** (a
   Cyrillic letter inside a Latin identifier). That needs a mixed-script check per identifier and
   is still open. Desktop: not yet, until it draws with these renderers.
10. **Nothing drawn is not nothing changed.** When the hashes differ but the drawing shows no
    difference, the page says so and offers the Raw diff. A parse that skips something says what
    it skipped. Raw is always one click away. "Changes only" and ignoring whitespace never hide a
    change by default. Test: an edit to an XML element the ladder does not draw is still announced.

**Data, access, operations**
11. Anonymous cache entries re-check `private: false` on a short TTL, so a repository made private
    stops being served.
12. The rendered-content cache is customer code held outside GitHub: encrypted at rest, a stated
    retention, wiped when the app is uninstalled, a stated data region.
13. An audit trail of who viewed what (with a privacy note), and `noindex` on every page.
14. A new session id at login (against session fixation). The cookie is named `__Host-`.
15. The App's private key signs through Key Vault or KMS and is never exported. Rotation planned
    for it and the client secret.
16. The container runs as non-root with a read-only file system. Outbound traffic only to
    GitHub's hosts (the second wall behind rule 4).
17. Kill switches: rotate the session key (logs everyone out), switch off anonymous access.
18. Later features: webhooks verify GitHub's HMAC signature. Account linking checks `state` and
    requires a fresh login on both sides.
19. Before going public: a written threat model, one external penetration test, a cost alarm.

**Hygiene:** never log file contents; repository and path names are customer data (retention and
access to logs). A repository the user cannot see returns 404, not 403. No CORS, no GET that changes
anything, and logout is a POST with an antiforgery token. Lockfiles and exact versions for npm and
NuGet, with Dependabot.

## Files

- New, done: `src/Versio.Core/Diff/CodeHtml.cs` (one line of code: tokens, emphasis, escaping,
  hidden characters, shared by every code renderer), `src/Versio.Core/Diff/CodePageAssets.cs`
  (the stylesheet, the desktop palette's colours), and `src/Versio.Core/Iec/SourceHtml.cs`
  (beside the `StSourceLayout` it draws).
- New, to come: `src/Versio.Core/Diff/TextDiffHtml.cs`, `src/Versio.Core/Projects/MemberTableHtml.cs`; `src/Versio.Web/` (server); `web/` (React page);
  tests in `tests/Versio.Core.Tests` and `tests/Versio.Arch.Tests`.
- Reused, unchanged: `LadderHtml`, `LadderPageAssets`, `MarkdownHtml`, `UnifiedDiffParser`,
  `SideBySideDiff`, `IntralineEmphasis`, `SyntaxTokenizer`, `StSourceLayout`, `MemberTableDiff`,
  `RepoExplorerBuilder`, `PlcFormats`, the vendor packs' `Register()`.
- Docs: `docs/web/PLAN.md` (this, as a design record), a line in `docs/STATUS.md`, a pointer in
  `docs/outpost/PLAN.md` Step 2b that the renderers are shared, and the README layout table.

## Verification

- The Core renderers' tests, plus the full fast loop from AGENTS.md (Core, Integration, Arch,
  three Pure suites, Parity), all green, with the counts re-derived.
- Mutation checks: remove the escaping and a test must fail. Point `Versio.Web` at a
  `*.Infrastructure` project and an Arch test must fail.
- End to end: run `Versio.Web` locally, sign in with a test GitHub App installed on a test
  repository that holds the three fixtures (`CleaningStation_V20_V21` through LFS,
  `TC3_PlcSample_reStrText`, a CODESYS mirror). Open a POU, a ladder block, a data type and the
  33k-line visualisation. Compare two commits. Look at it in the built-in browser, in light and dark
  and at phone width.
- **Check that it matches**: the same file and the same commit pair, desktop and web side by side.
  The ladder should be identical (it is the same fragment). Text diffs should mark the same changed
  lines.
- Confirm no token appears in the browser (devtools: storage, network) or in the server log.

## Open, for the owner

1. **Repositories behind LFS**: TIA projects store content in Git LFS. The Blobs API returns the
   pointer, not the file, so the server must follow the LFS batch API. It is cheap to add, but it
   has to be in Step 2.
2. **Who hosts it, and whether private repositories are in scope from day one.** If day one is
   public repositories only, Step 3 shrinks to the installation token.
3. **One web app with Outpost or two?** The recommendation is one React codebase with two areas
   (Repositories, Devices) later. For now, ship the viewer alone. It is the smaller, safer half and
   it proves the renderers Outpost needs.
4. **Merge reviews (later): comments in Versio or on GitHub?** Posting comments back to GitHub
   needs `Pull requests: write` on the App, and each organisation has to approve that again.
   Keeping them in Versio needs our own store. Not decided now. Step 1's markers serve both.

Not open, but easy to forget: **npm hygiene.** A committed lockfile, exact versions, as few
dependencies as possible. Node is needed in the web build only, never in the desktop build.
