# Versio Web: architecture

**What this is.** A map of the read-only web viewer as built on branch `WebViewer`
(2026-10-11): what each part is, where it lives, how a request travels through it, and which
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
     routes, tree, pane, compare, history          ← lays out the page
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
     Rendering/  an object as HTML, the desktop's order (ViewService)
     Sources/    GitHub REST + Git LFS, no clone    (IRepoSource)
         │                                │
         ▼                                ▼
   Versio.Core  (shared with desktop)   Versio.Plc.Codesys / .TiaPortal / .TwinCat  (pure packs)
     the HTML renderers                    parse a vendor's files into Core's neutral models:
     the diff, layout, tree, tables        TryParseMemberTable, TryParseLadder, CodeBody,
     SafeXml                                SectionsFor, SyntaxFor
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

### The server: `src/Versio.Web/` (about 2,700 lines)

| File | What it does |
|---|---|
| `Program.cs` | Sets the process-wide regex timeout on its first line, then builds and runs the host |
| `WebHost.cs` | **The composition root.** Registers services, the three vendor formats (`RegisterFormats`, the only place a vendor is named), the middleware order, the routes and the page's fallbacks. Logs one startup line saying what is configured |
| `WebOptions.cs` | All configuration (`Web` section or `Web__…` environment variables): kill switches, limits, the GitHub App and its hosts |
| `appsettings.json` | Defaults with no secrets; framework logging turned down to warnings |
| `Dockerfile` | Multi-stage container: Release publish (builds the page), then a non-root ASP.NET runtime. **Written, not built** (no Docker where it was written) |
| **`Sources/`** | |
| `IRepoSource.cs` | The seam for "where repositories come from": `RepoAccess` (whose credential), `RepoResult<T>` (a value or why not), the tree, blob and commit records. GitLab or Azure DevOps would be new implementations of this |
| `GitHubRepoSource.cs` | The GitHub implementation: repo, branches, commit SHA, recursive tree, raw blobs, commits; follows Git LFS pointers. Maps GitHub's refusals to what the page can say (a bare 403 is "not found") |
| `LfsGuard.cs` | Parses LFS pointers; allows LFS downloads only over https to allowlisted hosts; a connect callback that refuses internal addresses at the moment of connecting; no redirects |
| **`Auth/`** | |
| `AccessResolver.cs` | **Decides which credential reads a repository for a request.** Signed in: the person's own token. Anonymous: the App's token, only after GitHub says the repository is public |
| `SessionStore.cs` | Server-side sessions on `IDistributedCache` (in memory, or Redis when `Web:Redis` is set, so instances share them). The browser holds a random id; tokens are encrypted with ASP.NET Data Protection; idle and absolute limits enforced by the cache's expiry; an epoch kill switch; one token renewal per session at a time |
| `GitHubOAuth.cs` | `GitHubOAuth`: authorize URL with PKCE, code exchange, refresh, user, revoke. `AppTokens` (behind `IInstallationTokens`): a JWT signed through `IAppSigner`, then a read-only installation token per repository |
| `AppSigner.cs` | `IAppSigner`, the seam a key vault plugs into (rule 15); `PemAppSigner` signs with the configured key until the host is chosen |
| `AuthEndpoints.cs` | `/auth/login`, `/auth/callback`, `/auth/logout` (needs the `X-Versio-Request` header), `/api/me`. `SafeReturn` keeps the return URL on this site |
| **`Api/`** | |
| `ApiEndpoints.cs` | `/api/repos/{owner}/{repo}` plus `/branches`, `/tree`, `/view`, `/compare`, `/compare/view`, `/history`, and the three `/assets/…` routes serving Core's CSS and script. Validates every input, writes an audit line per view, turns failures into status codes. `Loader` does credential → commit → model |
| **`Repo/`** | |
| `RepoModel.cs` | `RepoModelLoader` turns one commit into a `RepoModel`: GitHub's file list, `.versio/repo.json`'s bindings, each export's `_manifest.json`, each project's library, task and device catalogs, any File-Based Storage source project, then `RepoExplorerBuilder`. Cached per repository and commit; concurrent loads of one commit share one; each blob fetched once. `NodeFor(path)` is an index; `Objects` is Core's `ObjectIndex`, for type links |
| `SingleFlight.cs` | Concurrent requests for the same key share one piece of work (model loads, visibility checks, branch resolution) |
| **`Rendering/`** | |
| `ViewService.cs` | **Turns an object into the fragment the page shows**, for one commit or two. Reads every layer of the object, picks the renderer in the desktop's order, applies the size limits and the time budget, adds notices, caches by content |
| **`Http/`** | |
| `Middleware.cs` | `SecurityHeaders` (CSP, nosniff, no-referrer, noindex, no-store on the API; the diagram frame's own narrower policy), `RequestLog` (one line per API request under an operation id, path only), `ConsoleLogSink` |

### The page: `web/` (about 1,300 lines)

| File | What it does |
|---|---|
| `node.props` | **The pinned Node**: version 24.21.0 and its SHA-256 per platform. The server's build fetches it into `web/.node/` and refuses a mismatch |
| `package.json`, `package-lock.json` | Exact versions, installed with `npm ci` only |
| `vite.config.ts` | Builds into `src/Versio.Web/wwwroot/`; in development proxies `/api`, `/auth` and `/assets/*.css|js` to a server on port 5099 |
| `index.html` | Links Core's stylesheets (`/assets/code.css`, `/assets/ladder.css`) |
| `src/main.tsx` | The routes: `/`, `/:owner/:repo`, `…/tree/:ref`, `…/blob/:ref/*`, `…/compare/:base...:head`, `…/history/:ref/*` |
| `src/api.ts` | The JSON client and its types; sign-out sends the header the server requires |
| `src/routes.ts` | Builds and parses the page's URLs (every view has its own link) |
| `src/fragment.ts` | `sanitize` (DOMPurify: HTML and SVG, data-* markers kept, script and handlers removed), `linkTarget` (what a Markdown link may do) |
| `src/useAsync.ts` | Loads with cancellation, so a slow earlier answer never replaces a newer one |
| `src/components/FragmentHost.tsx` | Places a sanitised fragment, re-runs the ladder script, scrolls to `#L12`, turns gutter clicks into line links, Markdown and type-link clicks into navigation, and each Mermaid block into a sandboxed diagram frame |
| `public/mermaid-frame.html`, `public/mermaid-frame.js` | The diagram frame: draws one diagram with the desktop's vendored Mermaid (strict mode), sandboxed without same-origin; text in, a height out |
| `src/components/TreePanel.tsx` | The object tree with a filter |
| `src/components/Pane.tsx` | Title, Rendered / Raw, Unified / Side by side, layer chips, notices, errors with sign-in when it would help |
| `src/components/Header.tsx` | Repository, branch picker, Code / History / Compare, signed-in user |
| `src/pages/*.tsx` | Home (open a repository), RepoPage (tree and pane), ComparePage (changed files and their diffs), HistoryPage (commits, each openable and comparable) |
| `src/styles/app.css` | The page around the fragments, light and dark, a drawer under 760 px. Markdown's styles live here because Core emits plain elements for it |
| `src/fragment.test.ts` | 11 vitest tests: the sanitiser, link rules, URL building |

### Added to Core for the web, and usable by the desktop

| File | What it does |
|---|---|
| `src/Versio.Core/Diff/CodeHtml.cs` | One line of code as HTML: syntax classes, changed words, one escaping rule, hidden characters shown as `U+XXXX` markers; `A()` for attributes; the notice; 256-row blocks |
| `src/Versio.Core/Diff/CodePageAssets.cs` | The stylesheet for all code renderers; default colours are the desktop palette's |
| `src/Versio.Core/Iec/SourceHtml.cs` | The Rendered tab: `StSourceLayout` rows as HTML, unified or side by side |
| `src/Versio.Core/Diff/UnifiedDiffWriter.cs` | Two texts to unified-diff text via `LineDiff`, so the existing diff pipeline runs unchanged |
| `src/Versio.Core/Diff/TextDiffHtml.cs` | The Raw tab: a parsed diff as HTML, unified or side by side |
| `src/Versio.Core/Projects/MemberTableHtml.cs` | A member table as HTML, old then new in changed cells |
| `src/Versio.Core/Text/SafeXml.cs` | The one XML parser every pure pack uses: no DTD, no resolver, a size cap |
| `src/Versio.Core/Iec/Ladder/LadderHtml.cs` (changed) | Takes an optional `LadderWording`; the desktop's text by default |
| `src/Versio.Core/Projects/ProjectTreeBuilder.cs` (changed) | `ProjectTreeNode.PrimaryFile`, moved from the desktop's `TreeRow` |
| `src/Versio.Infrastructure/Bindings/RepoConfigStore.cs` (changed) | `Parse(string)`, the store's own reader of `repo.json` |
| `src/Versio.Core/Logging/LogEvent.cs` (changed) | `LogCategory.Web` and `LogCategory.Auth` |

### Tests

| Where | What |
|---|---|
| `tests/Versio.Web.Tests/` (101) | `ViewServiceTests` (the desktop's choices over the real fixtures), `SecurityTests` (the rules against the real host on a free port, sign-in end to end, two instances sharing sessions, the real executable's startup), `GitHubRepoSourceTests` (the real client against a local server shaped like GitHub), `PerformanceTests` (each thing fetched once, compression, cache headers), `LfsGuardTests`. `Fakes.cs` is a GitHub in memory serving this repository's own fixtures; `TestHost.cs` runs the real host; `FixturePreview.cs` serves the fixtures on port 5199 to look at (opt-in) |
| `tests/Versio.Core.Tests/` | `SourceHtmlTests`, `TextDiffHtmlTests`, `UnifiedDiffWriterTests`, `MemberTableHtmlTests`, `SafeXmlTests`, `SyntaxTokenizerCoverageTests`, `RendererAllowlistTests`, `PrimaryFileTests`, and the wording test in `LadderHtmlTests` |
| `tests/Versio.Arch.Tests/` | `ProjectReferenceTests.Versio_Web_references_only_the_pure_packs`; `PurityRuleTests`: no process or file access in the server, vendors named only in `RegisterFormats`, XML only through `SafeXml` in Core and the packs |
| `web/src/fragment.test.ts` (12) | The page's pure modules |

---

## 3. One request, end to end

Opening `/acme/plant/blob/main/Device/Plc%20Logic/Application/Signals.xml`:

1. **The page loads.** The server serves `wwwroot/index.html` through the page fallback
   (`WebHost`: static files first, then routing, then `/{owner}/{repo}/{**rest}` → index.html).
   React reads the route and asks `/api/repos/acme/plant`, `/branches`, `/tree?ref=main` and
   `/view?ref=main&path=…Signals.xml`.
2. **Middleware.** `SecurityHeaders` adds the CSP and the rest; `RequestLog` opens an operation
   id; the rate limiter counts the client.
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
   layers; every layer's blob is read (cached by hash); each registered format is asked
   `TryParseMemberTable` over each layer. Signals is an interface, so its ladder is parsed with
   `TryParseLadder` and drawn underneath. The HTML comes from `MemberTableHtml.Fragment` and
   `LadderHtml.Fragment`; the result is cached by the renderer version and every blob hash.
7. **Answer.** JSON with `kind`, `title`, `html`, `notices` and `layers`; an audit line naming
   who viewed what at which commit; `Cache-Control: private, no-store`.
8. **Placing.** `FragmentHost` runs DOMPurify over the HTML, sets it, re-runs
   `/assets/ladder.js` for the ladder's navigation, and scrolls to `#L…` if there is one.

A comparison (`/compare/view?base=…&head=…`) is the same with two models: member tables
through `MemberTableDiff`, ladders through `LadderDiff`, source through `StSourceLayout.Mark`,
Raw through `UnifiedDiffWriter` → `UnifiedDiffParser` → `TextDiffHtml`.

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

### Mirrored, not shared (and why)

| What | Desktop | Web | Why not shared yet |
|---|---|---|---|
| Choosing a renderer for an object (table, ladder, Markdown, source) | `ShellViewModel`'s rendered-content switch | `ViewService.Rendered` / `RenderedCompare` | The desktop's version is tied to its tree rows, the disk and its pending marks. Same order, pinned by `ViewServiceTests`; moving it into Core is listed as later |
| Wording of comparisons | "since the last commit" | "since the older version" | Different questions; `LadderWording`, `MemberTable.Note` and `ViewService.Reword` carry the web's words, and a test fails if Core's phrases change |

### Not used by the web, on purpose

The sync engine, locks, watchers, git client, the toolchains and runners, the `.Host`
executables, and the whole WPF app. The viewer only reads.

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
| 8 Rate limits | `WebHost` (`AddRateLimiter`), `RequireRateLimiting` on every API and auth route |
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
| Look at it on the fixtures, no GitHub | `VERSIO_WEB_PREVIEW=1 dotnet test tests/Versio.Web.Tests --filter FixturePreview`, then http://127.0.0.1:5199/acme/plant |
| Develop the page with hot reload | run the server on port 5099, then `npm run dev` in `web/` |
| Configure sign-in | `Web__GitHub__ClientId`, `ClientSecret`, `AppId`, `PrivateKeyPem`, `AppSlug`, `PublicBaseUrl` from the host's secret store |
| Ship | `dotnet publish src/Versio.Web -c Release`, or the `Dockerfile` |

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
