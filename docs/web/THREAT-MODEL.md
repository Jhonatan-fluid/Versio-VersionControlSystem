# Versio Web: threat model

**2026-10-11, branch `WebViewer`.** The security rules this answers to are in
[`PLAN.md`](PLAN.md) ("Security"); where each lives in the code is in
[`ARCHITECTURE.md`](ARCHITECTURE.md) section 5. This document is the reasoning that connects
them: what is worth protecting, who might try, through which door, what stops them, which test
proves it, and what is still open. It is written to be handed to whoever does the external
penetration test (rule 19), and revised after it.

---

## 1. What is worth protecting

| Asset | Why it matters | Where it lives |
|---|---|---|
| **Private PLC programs** | A customer's machine logic: intellectual property, and a map of a plant's control for anyone planning harm | GitHub; in this server only as cached blobs and rendered fragments, in memory |
| **GitHub user tokens** | Read every repository the person can see that the App is installed on, for 8 hours (refresh tokens: months) | Session store, encrypted with Data Protection |
| **The App's private key** | Mints installation tokens for every installation of the App | Configuration today; a key vault through `IAppSigner` (rule 15) |
| **The client secret** | With a stolen code, completes someone else's sign-in | Configuration |
| **The Data Protection key ring** | Decrypts every stored token and every login cookie | Memory, or `Web:KeysDirectory` |
| **Who viewed what** | The audit trail; also personal data (logins, IPs) | Daily log files |
| **The server's own cloud identity** | A cloud host's metadata endpoint hands out the server's credentials | The host (169.254.169.254) |

## 2. Who might try

| Actor | Can | Wants |
|---|---|---|
| **Anonymous visitor** | Reach every public URL, send any request | Private programs; a foothold |
| **Signed-in user** | Everything a visitor can, with a session for their own GitHub account | Another person's or another company's programs |
| **Repository author** | Push any content to a repository someone views: files, LFS pointers, `.gitattributes`, `repo.json`, manifests | Run script in a viewer's browser; make the server attack itself or its network; hide a malicious PLC change from a reviewer; exhaust the server |
| **Network attacker** | Watch or alter traffic between the browser and the server | Steal a session |
| **Insider with host access** | Read memory, logs, configuration | Tokens, keys, programs |

The repository author is the one this product is unusual about: the desktop parses the owner's
own files, and the server parses anyone's.

## 3. Trust boundaries

```
 Browser  ──(1) HTTPS, cookie──►  Versio.Web  ──(2) HTTPS, token──►  GitHub REST / OAuth
   ▲                                 │   │
   │ (5) sandboxed frame             │   └──(3) HTTPS, guarded──►  LFS download hosts
   └─ Mermaid diagram                │
                                     └──(4) parse──►  repository content (untrusted)
```

1. Browser to server: everything the browser sends is untrusted; responses carry content that
   came from repositories.
2. Server to GitHub: the server acts with a credential chosen per request.
3. Server to LFS hosts: an address chosen by the repository's LFS configuration.
4. Parsing: repository bytes become HTML, through Core's renderers and the vendor parsers.
5. Page to diagram frame: diagram text crosses into a sandbox; a height comes back.

## 4. Threats, mitigations, proof

Each row names the test that fails if the mitigation is removed (most were checked by doing
exactly that).

### Spoofing

| Threat | Mitigation | Proof |
|---|---|---|
| A forged OAuth callback signs a victim into the attacker's account (login CSRF) | `state` in a sealed, short-lived cookie, compared in fixed time; PKCE | `SecurityTests.The_full_sign_in_round_trip_uses_PKCE_and_a_state_that_must_match` |
| A session id planted before sign-in is reused after it (fixation) | New random 256-bit id per sign-in; the browser's earlier session removed | `Rule14_each_sign_in_gets_a_new_unguessable_session_id`, `Signing_in_again_ends_the_browsers_earlier_session` |
| A subdomain sets or overwrites the session cookie | `__Host-` cookie: Secure, Path=/, no Domain | Cookie flags asserted in the round-trip test |
| Sign-in used as an open redirect | `returnUrl` must be a path on this site | `SignIn_returns_only_to_this_sites_own_pages` |

### Tampering

| Threat | Mitigation | Proof |
|---|---|---|
| **A repository file injects markup or script into the page** | Three walls: Core escapes every character; random input through every renderer emits only known tags and attributes; DOMPurify on the page. CSP behind them: script from this origin only, no inline script | Each renderer's escaping test; `RendererAllowlistTests`; `fragment.test.ts`; `Rule6_the_policy_allows_no_inline_or_remote_script` |
| A Markdown link runs `javascript:` | Core writes links to `data-href`, never `href`; the page follows relative paths in the viewer and http(s) with no opener; anything else goes nowhere | `fragment.test.ts` ("Markdown links") |
| **A malicious PLC change hidden from the reviewer** (Trojan Source) | Bidirectional, zero-width and control characters shown as `U+XXXX` markers; names mixing Latin with Cyrillic or Greek underlined; a notice above the code | `SourceHtmlTests` (override, zero-width, mixed scripts, honest Russian not marked) |
| A file or branch named to read as another (a right-to-left override in the name) | Every name, path label, title, notice, commit message and author the API returns is spelled with `CodeHtml.Spelled`, Core's one list; paths used for navigation stay exact beside a spelled label. Covered from 2026-10-12: until then rule 9 applied to file contents only (found in review) | `Rule9_a_hidden_character_in_a_name_is_spelled_wherever_the_page_shows_it` |
| **A change in a part the drawing leaves out reads as no change** | "The file changed, but nothing this view draws did" notice; Raw always available | `ViewServiceTests.A_change_the_drawing_leaves_out_is_still_announced` |
| A changed type behind a member reads as an unchanged block | Member types linked; a type changed between versions highlighted and said | `A_type_that_changed_between_the_versions_is_highlighted_and_said` |
| A Mermaid diagram escapes into the page | Drawn in an iframe sandboxed without same-origin; only text in, only a height out; strict mode; the frame alone allows inline styles | `Only_the_diagram_frame_allows_inline_styles_and_only_this_site_may_frame_it` (page-embedding not observable in the built-in browser; see section 6) |
| A path or name reshapes the GitHub URL | Inputs validated before anything reaches GitHub; every segment escaped again | `Hygiene_a_path_that_climbs_or_is_absolute_is_not_found` (asserts no GitHub call at all), `A_name_cannot_reshape_the_url` |

### Repudiation

| Threat | Mitigation | Proof |
|---|---|---|
| Nobody can say who viewed a private program | One audit line per view: login, repository, commit, path; sign-in, refresh and sign-out logged | `Rule13_a_view_is_audited_and_no_secret_ever_reaches_the_log` (reads the file the host wrote) |

### Information disclosure

| Threat | Mitigation | Proof |
|---|---|---|
| **An anonymous visitor reads a private repository through the App's token** | The App's token is used only anonymously, only after GitHub says the repository is public; visibility re-checked every 5 minutes | `Rule1_the_installation_token_seeing_a_private_repository_does_not_let_an_anonymous_visitor_in` |
| **One person is served what another loaded** | Credential, then commit, then caches, on every route; caches keyed by repository and content; resolution cache keyed by a hash of the credential | `Rule2_what_one_person_loaded_is_not_served_to_another_who_cannot_see_it` |
| The App's token reads more than it needs | Minted per repository, contents and metadata read-only | `An_installation_token_is_minted_for_one_repository_read_only_with_a_valid_app_jwt` |
| A proxy or CDN keeps a private response | `Cache-Control: private, no-store` on the API | `Rule2_api_responses_are_never_stored_by_a_proxy` |
| A private repository's existence leaks | Every refusal a 404, including GitHub's 403 | `GitHubs_refusals_become_what_the_page_can_say` |
| URLs naming private paths leak through Referer | `Referrer-Policy: no-referrer` | `Rule6_every_response_carries_the_csp_and_the_privacy_headers` |
| A token in a log | Logs carry logins and paths, never tokens, codes, verifiers or secrets; request lines carry the path only, never the query (the callback's code) | The same test: the log holds no token, refresh token, client secret, one-time code, PKCE verifier or state |
| Stored tokens readable from a store dump | Encrypted with Data Protection | `Rule7_tokens_are_encrypted_at_rest_in_the_session_store` |
| **The server fetches its own cloud credentials (SSRF through LFS)** | LFS downloads: https, allowlisted hosts, no redirects, internal addresses refused at connect time | `LfsGuardTests`, `An_LFS_download_pointed_at_the_metadata_service_is_refused_before_any_connection`, `..._that_resolves_internal_is_refused_at_connect` |
| An LFS download host answers with other bytes than the commit's file | The download must match the pointer's SHA-256 and size, or it is refused | `An_LFS_download_is_the_file_only_when_it_matches_its_pointer` |
| Compression leaks a secret (BREACH) | No response body carries a secret | Design argument (WebHost); nothing to test |

### Denial of service

| Threat | Mitigation | Proof |
|---|---|---|
| A crafted XML file (billion laughs) | `SafeXml` refuses any DTD; documents capped | `SafeXmlTests`, the Arch rule that every pack parses through it |
| A crafted line for a slow regular expression | A 2-second process-wide regex timeout, set before anything | `Rule3_the_real_server_starts_with_the_regex_timeout_in_force` |
| A huge file or a slow render | Over 2 MB plain text; over 10 MB never fetched; 5-second render budget with a plain-text fallback; LineDiff's cell cap | `ViewServiceTests` (draw limit, fetch limit, budget) |
| A file that makes a vendor parser throw | The same plain-text fallback, its own notice, uncached; the log names the exception's type, never its message (it can quote the file) | `A_parser_that_throws_gives_the_plain_text_not_an_error` |
| A rate-limited reader hides a change | A side that could not be read is said, never drawn as everything new; an answer built from a failed read is not cached; a failure stays with the credential that received it | `A_compare_whose_older_copy_could_not_be_read_says_so_...`, `A_read_that_failed_is_not_remembered_...`, `A_load_that_failed_for_one_credential_...` |
| A comparison of two enormous commits | 2,000 files listed, the rest counted | `Rule8_a_comparison_lists_at_most_its_cap_and_says_how_many_there_were` |
| Request floods, or spending the App's GitHub quota | Per-client rate limit; behind a load balancer the client is what a proxy in `Web:TrustedProxies` forwarded, and a forwarded header from anyone else is ignored (with none configured the forwarding middleware does not run at all, because ASP.NET's empty lists mean "trust everyone"); anonymous access can be switched off | `Rule8_a_client_over_its_rate_is_refused`, `Rule8_behind_a_trusted_proxy_each_client_has_its_own_rate`, `Rule8_a_forwarded_header_from_anyone_not_trusted_chooses_nothing`, `Rule17_anonymous_access_switched_off_asks_everyone_to_sign_in` |

### Elevation of privilege

| Threat | Mitigation | Proof |
|---|---|---|
| The server drives an IDE, starts a process or touches files | References only the pure packs; no `Process`, `File` or `Directory` in its IL | `Versio_Web_references_only_the_pure_packs`, `Versio_Web_starts_no_process_and_reads_no_file` |
| A forged sign-out | Needs a header no cross-site request can send; no CORS | `Hygiene_logout_without_the_request_header_is_refused_and_ends_nothing` |
| Clickjacking | `frame-ancestors 'none'` and `X-Frame-Options: DENY` everywhere but the diagram frame, which only this site may frame | Header tests above |

## 5. Kill switches

| When | Do |
|---|---|
| A session key or the store may be compromised | Bump `Web:SessionEpoch`: every session ends at once |
| Anonymous use is being abused | `Web:AnonymousAccess=false` |
| The App key may be compromised | Revoke and regenerate it on GitHub; the server picks up the new key from configuration |
| The client secret may be compromised | Regenerate it on GitHub |

## 6. Residual risks and open items

| Risk | Status | Why it is open |
|---|---|---|
| App key in configuration, not a vault | **Seam built** (`IAppSigner`) | Which vault follows from where it is hosted: the owner's decision |
| Data Protection key ring not vault-protected | Configuration | Same; ASP.NET supports vault protection of the ring |
| Sessions shared through Redis, key rings not shared | Configuration, warned | A session sealed under another ring reads as signed out (it was a 500 for every signed-in call, found in review); startup warns when `Web:Redis` is set without `Web:KeysDirectory`. People are still signed out at random until every instance shares one ring |
| Sign-in never run against real GitHub | Tested against a local OAuth server | Needs the owner to register the App |
| The page embedding the Mermaid frame not observed | Frame tested directly; sandbox attributes asserted in code | The built-in browser refuses sub-frames; needs one look in a normal browser |
| Look-alike detection limited to mixed scripts | Built | A whole name in Cyrillic that looks Latin is not flagged; a confusables table would flag much honest text |
| ~~No test asserts the audit line or greps logs for secrets~~ | **Closed 2026-10-11** | `Rule13_a_view_is_audited_and_no_secret_ever_reaches_the_log` |
| No outbound network allowlist | Infrastructure | The container's network policy, rule 16 |
| No external penetration test, no cost alarm | Open | Need a live deployment and a party outside this repository (rule 19) |
| Cross-instance token renewal race | Mitigated | The in-process gate; a refused renewal looks for another instance's for a second, then ends the session. The first version looked once and deleted, and deleted a winner's write landing in between; the next deleted nothing, and the dead session cost every later page load seconds for up to thirty days (both found in review). A winner's write slower than a second is still deleted, and that person signs in again. A session nothing can renew (no refresh token, or a spent one) ends at once, with no race; one GitHub could not answer about (a 5xx, a timeout, a rate limit) is kept, keeps using its current token until that expires, and is not asked again for a minute |
| Sign-out while a renewal is talking to GitHub | Mitigated | The renewal looks before writing; if the session is gone it revokes the new token and writes nothing. No compare-and-swap on `IDistributedCache`, so a sign-out landing between that look and the write still leaves an unrevoked token in the store until the entry expires (`A_sign_out_during_a_renewal_...`) |
| ~~A request that read the session before a renewal writes the old token back~~ | **Closed 2026-10-11** | Reads never write the session (`SessionStoreTests`); found by an external review |
