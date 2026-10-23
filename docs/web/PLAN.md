# Versio Web: a read-only repository viewer

**Date:** 2026-10-11, after a review of the stack, the sign-in and two security passes with the
owner. **This is a design record, not the live queue.** What Versio is today lives in
[`../STATUS.md`](../STATUS.md) (item 10). **Built on `WebViewer`, 2026-10-11, not merged:**
Steps 0 to 5 done and tested, Step 6 written but its image not built (no Docker on that machine).
Each step below says what was built and what changed from the plan. Still to do, in order: the
owner registers the GitHub App and sign-in is run against real GitHub; the image is built; who
hosts it is decided. **The map of what was built** (layers, files, one request end to end, what
is reused from the desktop) is [`ARCHITECTURE.md`](ARCHITECTURE.md); the threats and what stops
each are [`THREAT-MODEL.md`](THREAT-MODEL.md). The open items listed in the first version of
this page (type links, catalogs and FBS projects in the tree, look-alike letters, Mermaid, the
comparison cap, shared sessions, the App key seam, the large-file measurement) were all closed
the same day; what is left needs the owner or a deployment (STATUS item 10).

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

> **Superseded 2026-10-14.** The owner looked at this layout running and asked for the desktop's
> shell instead — see *The workbench restructure* at the end of this file. The sketch below is
> what was built first and why; the URLs and the pane's modes survived the restructure unchanged.

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
**Measured later the same day:** the 33,644-line visualisation's Raw view is on screen 1.4 s
after navigation in the built-in browser (under 0.1 s of it the 421 KB compressed download, the
rest DOMPurify and layout over 7.7 MB of HTML in 132 blocks). Paging was not needed.

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

**Where each rule stands, 2026-10-11 (end of day).** Built and tested (each test named in
`Versio.Web.Tests`, `Versio.Core.Tests` or `Versio.Arch.Tests`, most also mutation-checked):
1, 2, 3, 4, 5, 6, 8 (including the comparison cap: 2,000 files listed, the rest counted), 9
(hidden characters as markers, and names mixing Latin with Cyrillic or Greek underlined), 10,
11, 13 (the audit line, and no secret in the log, read from the file the host wrote), 14, 17.
Built as code, the production half being configuration: 7 (sessions encrypted with Data
Protection, shareable across instances through Redis; protecting the key ring with a vault is
deployment), 15 (the App's JWT signs through `IAppSigner`; a vault signer is one class once the
host is chosen), 16 (the Dockerfile runs non-root on a read-only root; the outbound allowlist is
the network's). 12: the rendered-content cache stays in memory per instance (30-minute sliding
expiry), so nothing is at rest. 18: no webhooks or account linking exist. 19: the threat model is
written ([`THREAT-MODEL.md`](THREAT-MODEL.md)); the penetration test and the cost alarm need a
live deployment.

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
4. ~~**Merge reviews (later): comments in Versio or on GitHub?** Posting comments back to GitHub
   needs `Pull requests: write` on the App, and each organisation has to approve that again.
   Keeping them in Versio needs our own store. Not decided now. Step 1's markers serve both.~~
   **Decided by the owner 2026-10-17: on GitHub, in the person's own name.** See *Pull request
   review* below; the markers turned out to carry identity but not the file lines GitHub anchors
   comments to, so that section adds them.

Not open, but easy to forget: **npm hygiene.** A committed lockfile, exact versions, as few
dependencies as possible. Node is needed in the web build only, never in the desktop build.

## The workbench restructure (2026-10-14)

The first layout shipped as GitHub's: top tabs (`Code / History / Compare`), History and Compare
as whole pages of their own. The owner looked at it on the fixtures and asked for the desktop
app's shell instead — the product is the desktop, and the page should read as the same tool.
The restructure is five pieces, each a coherent commit; the server API and the page's URLs
survive all of them, because every piece is presentation.

1. **The workbench shell and the pane header — built 2026-10-14.** One layout for every
   repository route (`web/src/components/Workbench.tsx`): the top bar, a rail on the left edge,
   one panel card the rail swaps, the pane card beside it, cards on a sunken background
   (`--surface-sunken` / `--surface-card`, the desktop's Surface.Sunken / Surface.Base). The
   rail is **Project / History / Compare** — the desktop's is Project / Explorer / Git / History,
   but Git has no read-only meaning, so Compare (what changed between two versions) takes its
   slot, and Explorer waits until somebody misses it. History's commits and Compare's pickers
   and changed-file list moved into the panel card; the routes still deep-link every view. The
   pane header is the desktop's `PaneHeading` grammar: a small KIND label (FILE or DIFF), the
   name in the face an ellipsis cannot eat, the folder after it in the quiet mono face, the full
   spelled path as the tooltip (rule 9: the folder comes from the spelled label, not the raw
   path). The renderer kind ("Member table", "Networks") moved from beside the title to the
   label's tooltip.
2. **History as a panel with the desktop's commit card.** Selecting a commit fills the pane:
   byline, stats, the message **body** (this repository's commit bodies carry the reasoning and
   the page never shows them), the objects it touched, then the per-file diffs. Needs
   `/api/history` to return body and stats per commit.
3. **Compare as marks on the tree.** The changed set drawn as A/M/D glyphs and tints on the
   same tree the Project panel shows — the desktop's change marks — with a "changed only"
   filter replacing the flat list. Needs the compare endpoint to return marks shaped like the
   tree.
4. **Shared theme tokens.** Generate the page's CSS custom properties from the desktop's
   palette dictionaries (`Theme/tokens/Palette.*.xaml`), pinned by a test, so the two faces
   cannot drift.
5. **An Explorer panel**, files-as-on-disk beside Project — deferred until somebody misses it.

Phase 1's measurements: 22 vitest green unchanged (the rail's visually-hidden "Compare" label
is what keeps the History page's link test true), `tsc` clean, looked at on the fixtures in the
built-in browser at desktop and phone widths — the drawer closes on navigation from the
workbench's own effect now, since the pages no longer own that state.

## The product shape (2026-10-16)

The owner's requirements for the viewer as a product, recorded as given, with where each stands:

1. **Sign-in required** — by GitHub, later by Okta too (Fluidstack uses Okta). GitHub sign-in
   was already built; `Web:AnonymousAccess=false` was already the switch that refuses the
   signed-out, and the home page is now the gate when it is off. **Okta is the next auth piece**:
   a second, OIDC sign-in. An Okta user may have no GitHub identity, so their reads will go
   through the App's installation token restricted to the server's repository scope (below) —
   for Okta users the admin defines access, not GitHub.
2. **An admin scopes which repositories the server shows — built 2026-10-16.**
   `Web:Repositories` (owner/repo, one per index) is the server's whole scope: anything off the
   list answers "not found" to everyone, enforced in `AccessResolver.ResolveAsync`, the one gate
   every repository read already passed. `/api/repos` lists the scope **filtered to what the
   caller's credential can actually read** — a repository they cannot read is left out, never
   greyed out — and the home page draws it as "Repositories" instead of the open-a-repository
   form. A malformed entry stops startup, as a malformed trusted proxy does. The settings UI
   where the admin edits the list in the app is deliberately later: it needs the server to keep
   writable state for the first time, and the config file proves the model first.
3. **Browse, switch branches, desktop-parity rendering** — this is the viewer as built; the
   known parity gaps are the restructure's phases 2–4 above (commit card, compare as tree marks,
   theme tokens) and the project card.
4. **A Documentation tab — built 2026-10-16.** A Docs rail tile: the commit's tree filtered to
   documents, grouped under their folders in the panel. Text documents (`.md`, `.txt`, `.csv`)
   render through the same `/view` as the Project panel; PDF embeds from a new `/raw` route; the
   Office formats are download rows — converting them server-side was rejected, as planned.
   `/raw` is the one route serving repository bytes from this origin, so its rules live on
   `Documents.cs` and in THREAT-MODEL (Tampering): extension allowlist, our content type with
   `nosniff`, nothing executable, everything but PDF an attachment. **The PDF embed is one more
   thing the built-in browser cannot show** (no PDF viewer, like its refusal of sub-frames): the
   frame, URL and content type were verified, the drawing needs the owner's one look in a normal
   browser, on the same list as Mermaid.
5. **Open MRs and Releases tabs — built 2026-10-16.** Two rail tiles and four read-only routes
   (`/pulls`, `/pulls/view`, `/releases`, `/releases/view`): the lists carry meta only, one body
   is fetched and rendered — with Core's Markdown, exactly as a README is — when picked, because
   fifty rendered bodies per list load would be paid mostly for rows nobody opens. A pull
   request's **What changed** opens the existing compare view at `base...headSha` — the head
   COMMIT, not the head ref, because a fork's branch name means nothing in this repository while
   its commit is reachable — so a PR's PLC changes are reviewed as member-table and ladder
   diffs. Drafts releases are never listed; an untitled release is shown by its tag; titles,
   authors and refs are spelled (rule 9). Remember at registration: the App needs *Pull
   requests: read* for anonymous/Okta readers of PRs.

Still open here: Okta (2's seam is ready for it: the session is the identity, the credential is
chosen per request), and the settings UI.
`VERSIO_WEB_PREVIEW_SCOPED=1` beside `VERSIO_WEB_PREVIEW=1` serves the fixtures scoped, to look
at the list and the refusal.


## Pull request review (2026-10-17)

The owner's next requirement: **review pull requests in Versio Web**, with everything a GitHub
pull request does — create one, see its status and running checks, comment inline, resolve
threads, approve, request changes, merge, close — but with "Files changed" drawn as PLC code.
This turns a read-only viewer into something that writes, so the design was put through two
external reviews before any code; both are folded in below. Built on branch `PrReview`, slice by
slice; each slice's state is in STATUS item 10.

**Owner decisions.** Every review action is **the person's own**: writes use the signed-in user's
GitHub App user token, never an installation token or a bot, so approvals count toward branch
protection and GitHub's history names them (an Okta-only user, when Okta exists, reads and must
link GitHub to write). **Comments live on GitHub**: one placed on a rendered element is posted at
a valid diff line or at file level, with a hidden Versio anchor tag so Versio can put it back.
Versio keeps no comment store.

**What GitHub cannot do, which the design is shaped around.** Compare lists at most 300 files
(paging pages commits); `pulls/{n}/files` reaches 3,000 but follows the live head; patches are
omitted for large or binary files. Submitting a pending review takes no commit — the commit is
fixed at *creation*. One pending review per person per pull request. Deleting a ref has no
expected-SHA precondition. Resolving threads, `isOutdated`, `reviewDecision` and
`mergeStateStatus` are GraphQL only.

### The concepts

- **C1, the review snapshot.** `{owner, repo, number, baseRef, baseSha, mergeBaseSha, headSha,
  issuedAt, userId}`, sealed with a MAC (Data Protection). The MAC proves integrity, **not
  access**: every request is re-authorised in full, the token's repository and number must equal
  the URL's and its user the session's, and it expires with the user token (8 h). The changed-file
  inventory is **Versio's own tree diff** `mergeBase → head`, complete whatever GitHub's patches
  cover; commentable lines come from `pulls/{n}/files` only when the live head equals the
  snapshot's before and after paging, else from compare (≤ 300 files); a file with no patch is
  listed and drawn, with inline positions unavailable. Threads and comments are paged to
  completion. A head or base move shows "reload" and keeps drafts — never a mix of revisions.
  Writes re-check head and base just before mutating; that narrows the race, it cannot close it.
- **C2, what an approval guarantees.** A review is bound to the commit it was **created** on: a
  one-shot review is one REST call with `commit_id`; a pending review is created with
  `commitOID`. Not guaranteed, and not claimed: that the head did not move between our check and
  GitHub's submit (branch protection's "dismiss stale approvals" covers that; the page reports the
  move). A pending review is resumed only if it is the viewer's and on the snapshot's head. One on
  an older commit can be submitted **as historical** (its own route, its commit named), moved to
  the current head by copy, verify, then delete (the original survives every failure before the
  delete; deferred if GitHub refuses a second pending review), or opened on GitHub.
- **C3, placement spans and semantic anchors.** Each drawn element carries the span of **the layer
  file it was parsed from** — not the file on screen, because `ViewService` draws a table or a
  ladder from any layer of the object. An anchor is `{kind, sourcePath, side, revision, container,
  key, fingerprint}`. Matching is Exact (same path, side, container and key, plus same revision or
  same fingerprint), Moved (same fingerprint, **one** candidate, **inside the same container**),
  else Ambiguous or Gone, which go to an outdated/unplaced list. Identical rungs are common, so two
  candidates are never a pick. Only renames GitHub reports are followed. A tag never silently beats
  a disagreeing current line.
- **C4, coverage: claim what the renderer drew, and say the rest once.** A change is
  **represented** when a renderer can name the line: a source row drawn added or struck; a line
  inside a member row drawn A / D / M (its pragma chips included); a line of an unchanged row whose
  text holds the same words (re-aligned, or a comment moved to the end of its line); a changed
  attribute chip's line; a network's span on a side it is
  drawn on; or a line the parser declared **framing** — the declaration's own keywords, blank
  lines, the markup around and between an XML table's rows (`MemberTable.Structural`,
  `LadderDocument.Structural`, `MarkupLines`). A file entirely new or deleted that anything drew is
  complete. One edit is one gap, named as it is now. Everything else is **unknown** and listed as
  "may not show — check Raw": a doc comment no row owns, a value in XML the table has no column for,
  an `EXTENDS` added to a header, a pragma the parser does not read. A renderer with no evidence at
  all labels its view **partial**. *Rewritten 2026-10-23 (FINDINGS, "the coverage banner on real
  code"): the first version vouched for a declaration line only when every token on it was a shown
  field, and for no XML line at all, which on a real 85-file pull request flagged 55 files, nearly
  all of them re-indentation, framing and whole new files.*
- **C5, uncertain writes.** An operation key per write, reserved atomically before calling
  GitHub, with its payload hash and a state (pending, then succeeded, failed or unknown). No blind
  retries: a lost response or a crash is reconciled (Versio's `versio:op` tag in bodies, or a
  re-read of state), never re-sent. An abandoned pending entry is reconciled first; an expired key
  is refused, never re-executed. Writes need durable storage (Redis with persistence) unless the
  server is declared single-instance, whose weaker guarantee is stated.
- **C6, the host seam.** Spans, anchors, matching and coverage are pure and live in Core;
  GitHub's hunks, line translation, tag format, REST/GraphQL and the pending-review lifecycle live
  in `Versio.Web`. No "GitHub" in Core's `Review` namespace — the vendor-seam rule, one level up.

### The safety rules (each has a test)

S1 user tokens only (installation, anonymous and Okta-only callers get 401). S2 installation
tokens stay minted read-only — the `permissions` field is what stops GitHub granting every App
permission. S3 two Apps recommended: a write-capable one for user sign-in **whose private key the
server never holds**, and a read-only Reader for installation tokens, configured whenever a mode
needs them. S4 every write needs `X-Versio-Request`, JSON, and same-origin by `Sec-Fetch-Site` or
`Origin`; one `MapWrite` registers every write route and a test fails on any that bypasses it. S5
no page with controls can be framed. S6 untrusted content (anyone who can comment writes PR
bodies, comments, check output, labels, branch names) cannot run script: Markdown through Core and
DOMPurify, check output as text, the CSP unchanged, the Mermaid frame without `allow-same-origin`.
S7 a GraphQL node id is checked to belong to the URL's repository and pull request before any
mutation — node ids are not scoped by the URL. S8 per-user limits, body caps, refusals by
GitHub's message only. S9 `Web:Writes` and `Web:WritableRepositories`, off by default. S10 an
audit line per write, never bodies or tokens. S11 merge is its own tier (`Web:Writes:Merge`), the
only use of Contents write; **no admin bypass, ever**.

### Delivery: vertical slices, each closed by a real-GitHub gate

1. Correct, snapshot-consistent, read-only review: the merge-base fix (done 2026-10-17, FINDINGS),
   C1, the PR page (Conversation / Commits / Checks / Files changed), threads on the Raw diff,
   rendered views labelled partial. **Built 2026-10-17; its real-GitHub gate waits for the App.**
   Two things moved from this slice: drawing a renamed file against its old path (the tree diff
   sees a delete and an add; GitHub's rename joins them in the list, but `ViewService` reads one
   path) goes to slice 3 with the anchors, which need the same old-to-new path mapping; and a
   rendered view's threads are listed rather than placed, because its row numbers are section
   lines, not the file lines GitHub means — placing by them would put a comment on the wrong line.
2. Conversation and raw inline comments with C5 recovery; the write seam and S1–S4, S7, S9, S10.
   **Built 2026-10-17; its real-GitHub gate waits for the App with Pull requests and Issues write.**
   As built, differences from the plan worth knowing: the switch is `Web:Writes:Enabled` with
   `Web:Writes:Repositories` (opt-in per repository; empty means none), `Web:Writes:SingleInstance`
   and `PerSessionPerMinute`; there is no startup sweep of abandoned operations, because reconciling
   one needs the person's own token — an abandoned one is reconciled when its key comes back, which
   the page's kept draft makes happen; only line comments re-check the snapshot against the live pull
   request (replies, edits and resolutions are not positions in a revision); and the forgery gate is
   middleware rather than an endpoint filter, because filters run after the body is bound (a form post
   got the framework's answer, and with writes off a malformed body answered before the kill switch).
   `RedisOperationLedger` is written and not yet run against a real Redis: the in-memory ledger carries
   every test, and a multi-instance deployment needs that one look first.
3. Rendered anchors with provenance (C3, C6), parser line info in the pure packs, the markers
   contract (the drifted `data-network` / `data-element` names reconciled with the code's
   `data-net` / `data-key`). **Built 2026-10-19; its real-GitHub gate waits for the App.** As
   built: the markers contract is `data-anchors` (a list of ids, one per side an element stands
   for — an unchanged or merged network is drawn once, for both) and `data-src-path` on every
   member table, ladder and source block, pinned by a test that the anchor list and the markup
   name the same things. Kinds are `row` and `net`; **element-level anchors are deferred**: a
   contact lives inside its network's SVG, where no thread can be drawn beside it, so a comment on
   an element would be placed on its network anyway. Comments name only the clicked anchor id; the
   server re-renders and takes the anchor from its own rendering (`/anchor-comments`). Drawn source
   lines take ordinary line comments in their own file (`data-src-path` + file line). Renamed files
   are compared with their old path when GitHub reports the rename. Coverage (C4) is unchanged:
   every drawing still said it may be partial (slice 4 replaced that).
4. Coverage evidence (C4), pending reviews, approval and request changes (C2). **Built
   2026-10-19; its real-GitHub gate waits for the App.** As built: coverage is decided per
   **changed line of every file of the object**, not per element — a drawn source layout vouches
   for the lines it draws added and struck; a compared member table vouches for the lines of a row
   it draws as changed, for an unchanged row's lines when they hold the same words, for a changed
   attribute chip's line and for the declaration's framing (as C4 says since 2026-10-23; until then
   a line was vouched for only when every token on it was a shown field, and XML tables stayed
   partial). Anything else is a gap with its reason (`not-shown`, `no-evidence`, `not-drawn`,
   `too-large`), and a view is "complete" only with no gap. `Coverage.Ladder` gives a ladder
   evidence (every network's span, plus the parser's framing); **the web still reports its ladders
   as `no-evidence`** until `ViewService` calls it — the one wiring left, noted in the hand-over.
   The page lists every gap and links each to Raw or the layer it is in; the review
   dialog totals files never opened here and files with gaps (`/coverage`, rendering at most 200
   files, the rest "unchecked" and counted as unsure), and informs without blocking. Reviews: a
   one-shot review is `POST /reviews` with `commit_id`; a draft comment can go into a pending
   review ("Start a review" / "Add to review"), which is created with `commitOID` the first time;
   one on an older commit takes no more drafts and is submitted only by the historical route, the
   dialog naming its commit. **The move path is deferred**: GitHub keeps one pending review per
   person per pull request, so there is nowhere to copy the drafts first — discard and start again,
   or submit it as historical. The author is offered Comment only (GitHub refuses the rest). Also
   built: viewed marks (GitHub's own), requesting and withdrawing reviewers by login, and "changes
   since your last review" (a compare from the viewer's last reviewed commit to the head).
5. Pull request creation (same-repository branches) and metadata. **Built 2026-10-20; its
   real-GitHub gate waits for the App.** As built: "New merge request" (`/owner/repo/pulls/new`)
   opens one between two branches of this repository — a `owner:branch` head is refused — with the
   repository's `pull_request_template.md` (`.github/`, the root or `docs/`, at the base branch) and
   a draft toggle. Opening has no snapshot, so `RunWithoutSnapshotAsync` keys it under pull request
   0, and a lost answer is reconciled by finding the viewer's own pull request between those
   branches carrying the operation tag: never opened twice. On the review page, the title, the
   description and the base are edited, **each naming what it replaces** — GitHub's PATCH has no
   precondition and would overwrite a colleague's edit, so the server refuses (409) when the
   title, description or base changed since editing began; the page fixes the replaced text when
   editing starts and keeps it with the draft over a reload, because the live summary is polled
   and would otherwise put the colleague's text in as "the one replaced". A base change makes the
   snapshot stale, which the page reports as for a push. Close and reopen (not when merged), draft
   and ready, labels the repository already has (none is created: GitHub would make a missing one
   silently, a repository-wide change), and assignees GitHub says can be assigned (it drops the
   others while answering success). Edit controls follow GitHub's `viewerCanUpdate`.
6. Merge, update-branch and auto-merge (with `expectedHeadOid`; ~~it is a *future* merge of this
   exact commit, described as such~~ — **wrong, corrected 2026-10-20**: `expectedHeadOid` only
   checks the head when auto-merge is turned on; GitHub turns it off for a push by someone
   *without* write access, and merges later pushes by people with it) — a separate release gate. **Branch deletion deferred**: no
   atomic precondition exists, and read-then-delete could lose pushed work; the repository's
   "Automatically delete head branches" setting does it instead. **Built 2026-10-20; its
   real-GitHub gate waits for the App with Contents write.** As built: `Web:Writes:Merge` (off by
   default, on top of `Web:Writes:Enabled`) — without it `/merge`, `/update-branch` and `/auto-merge`
   answer 404 and the snapshot's `canMerge` is false. The merge carries `sha` = the snapshot's head,
   update-branch `expected_head_sha`, auto-merge `expectedHeadOid`; each first checks the snapshot
   against the live head and base. **No admin bypass, enforced rather than assumed**: GitHub's API
   lets an administrator merge past required reviews and checks, so the server merges only when
   `mergeStateStatus` is CLEAN, UNSTABLE or HAS_HOOKS, and refuses BLOCKED, BEHIND, DIRTY, DRAFT and
   UNKNOWN for everyone. `mergeStateStatus` is read in its own query (with the `merge-info-preview`
   media type), so if GitHub refuses that field only the merge box goes — failing closed — and the
   summary's "this changed" signal stays. Methods follow the repository's settings. The merge box
   asks before every action, naming the pull request, its branches, the commit and the method.
   Auto-merge is a different authorization and is asked for as one: a later merge of whatever the
   branch holds when the requirements pass, which can include commits pushed after the review.
   Turning it on needs the person's explicit yes to that (`acceptsLaterCommits`, refused 400
   without it). What pins a later commit to a fresh review is the repository's own branch
   protection — a required approval with "dismiss stale approvals" — and the docs say so.

Release gates: each slice's mutation checks red when broken; writes on one throwaway repository
first, merge last; the external penetration test with writes on, before anyone but the owner has
them. **Later, on purpose:** applying suggestions, re-running checks, atomic branch deletion,
webhooks, reactions, milestones and projects, fork-head creation, conflict resolution in the
browser, single-commit review ranges, threads in the desktop app.
