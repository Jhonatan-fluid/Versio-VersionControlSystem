# Versio — findings

Every defect worth understanding, with the evidence that found it. **Newest first.**

This is an engineering log, not a status report. It is meant to be *searched* rather than read
end to end — when something here looks familiar, the entry usually says what was tried, what it
cost, and what was rejected.

**Split out of `STATUS.md` on 2026-08-17.** It had grown to 3,681 lines of that document's 4,782
— 77% of a file whose job is to answer "where is the project now", and which `CLAUDE.md` opens by
telling every reader to go through before starting. That instruction had quietly stopped being
followable, which is the failure it was written to prevent. The live page is `STATUS.md`; this
file is the search log. The index that used to live in STATUS was dropped when that file was
slimmed on 2026-08-19 — search here, do not look for a table of contents there.

Nothing here was edited in the move. The same argument, one file along, as the `Controls.xaml`
split already recorded in [`archive/STATUS-2026-08.md`](archive/STATUS-2026-08.md): concentration is what makes a change to any one thing
produce a diff touching everything else.

---

### A TwinCAT import could delete or overwrite the wrong same-named object

**Found 2026-08-20**, by a code review of commit `73741e4` — before any user ran a TwinCAT
import with deletions. The host's delete and ApplyText both fell back to a **display-name
index** when the identity lookup missed: `index.ByGuid.TryGetValue(guid, out found) ||
index.ByName.TryGetValue(n, out found)`. The "guid" is an Automation Interface `PathName`
(`TIPC^PLC^PLC Project^Motor`), which goes stale the moment the user moves or renames
anything in XAE — precisely when the fallback fired. The name index was
first-encountered-wins over *every* tree node, folders included, and POU member names
recur (`Init`, `Execute`), so a planned deletion of a departed POU could `Delete()` a
folder or a newer same-named object, and an edit could land on the wrong FB's method. The
ApplyText read-back could not catch it: it verifies the text landed on the item written
to, not that the item was the right one. TIA's host is deliberately guid-only on delete;
the fallback was TwinCAT's addition.

The same review found the host never honoured the contract `DeletedFile.Hash` documents
("the executing side compares the *live* object against it before removing anything") —
CODESYS does, via `why_not_delete`; TwinCAT deleted without comparing, so an object
edited in XAE after the last export went down with its unexported edits. And planned
work could drop silently: a missing source file warned and continued, an unmatched
rename or a null guid skipped with no event, an unrecognised action (`ApplyNative`
included) fell through both branches — then the run saved and emitted `done ok`.
"Import job has no plan" was worse: `EmitFatal` then *return*, after which `Run`
emitted `done` and exited 0.

**Fix (same day):** delete is identity-only and compares the live packed-text hash to
the recorded one before `Delete()` — miss or mismatch keeps the object with a sentence.
ApplyText resolves guid, then a session rename map (a rename changes the live `PathName`
mid-import, which is the one legitimate reason the name fallback existed), then a name
only when it is unique in the tree; ambiguity throws. Every silent drop now throws
before anything is saved — `Solution.Close(false)` discards the half-applied session,
so disk stays untouched. Both directory-vendor runners refuse a plan-less import
instead of fabricating an empty plan that no-ops, saves and reports green.

**What was rejected:** a "unique name" fallback for delete. A unique-named survivor is
still not the object the plan named, and `Delete()` is the call P3 measured to destroy a
referenced object with no exception and no cascade.

The gate's import Fact exercises ApplyText only, so the delete-verify and rename
preconditions are code-reviewed, not XAE-gated — they join create/delete/rename in
[`vendors/twincat/DRIVING.md`](vendors/twincat/DRIVING.md) §9 as Facts still owed.

---

### The TwinCAT conversion refusal failed open exactly when it knew the least

**Found 2026-08-20**, same review. `WouldConvert` ended in `if (project is null ||
install is null) return false` — proceed. `ProjectTcVersion` swallowed every exception,
and the locator returned `Version = "unknown", InstallFolder = ""` for any TwinCAT
outside its three hardcoded folders. On such a machine a 4024 project on a 4026 shell
opened **on the bound path** with `SuppressUI` set and converted headless, nobody asked —
the OpenWithUpgrade class of mistake the check exists to refuse, disabled by exactly the
missing information that makes the mistake likely. The refusal message even had wording
for an unknown install; that branch was unreachable in the unknown case.

**Fix:** `ConvertRefusal` returns a sentence naming which side could not be read — unknown
refuses. The false-refusal cost is paid in discovery, not by skipping the check:
`TWINCAT3DIR` (set machine-wide by the Beckhoff installer) is a fallback in both the
locator and the host's own walk, `Build_*` is picked numerically rather than by ordinal
string sort (a five-digit build would have sorted below `Build_4026`), and an unreadable
`Version.txt` degrades to unknown instead of faulting `ProbeAsync`. `AllowConvert` — the
`%TEMP%` copy — bypasses as designed. Gate re-ran 5/5 green.

---

### The lazy-TIPC wait was dead code, and an empty export reported success

**Found 2026-08-20**, same review. `WaitForPlcProject`'s generic branch ended its
coalesce in `?? child`, so it returned on the first pass for any named TIPC child — the
90 × 500 ms retry loop could never retry, and the node returned could be the outer child
whose lazy children had not loaded. `Export` would then walk zero children, write a
mirror with no objects, and emit `done ok`: the next diff reads as **every object
deleted**, the one failure this project exists to prevent. The gate never saw any of it
because the fixture's name was compiled into a fast path (`TIPC^LibraryDocumentationSample^…`),
so the gate proved a branch no real project would take.

**Fix:** fast path deleted, the loop actually retries the nested `<name> Project`
lookup, an outer child may stand in only after half the wait *and* only with
demonstrably loaded children — and `Export` now refuses to write a mirror with zero
objects at all, so even a wrongly-accepted node cannot produce the mass-deletion diff
silently. Cost accepted: a genuinely empty PLC project cannot export; it has nothing to
lose by the refusal. Gate re-ran green through the generic branch it now actually
exercises — 3 m 17 s against 3 m 14 s before, so the fast path was buying nothing.

---

### WaitForExit(int) let the TIA and TwinCAT runners read a truncated stream

**Found 2026-08-20**, same review. `WaitForExit(int)` is not a flush barrier — the async
`OutputDataReceived` handlers can still hold the last lines when it returns, and only
the parameterless `WaitForExit()` is documented to wait for them. On that race the final
`@@VERSIO@@` `done` line is missing, `Completed` is false, and a fully successful import
— one that changed the project — returns `Succeeded = false, "host exited 0"`.
`LocalCodesysRunner` and `GitCliClient` already carry the follow-up wait with the same
reasoning in a comment; the two newer runners were copied from a shape predating it.
Both drain now, both log malformed protocol lines (previously dropped without a trace —
the "failure arrives with no message" shape the quiet-verbosity finding already
records), and the hosts' JSON escaping covers control characters, which COM error
messages do carry.

---

### TwinCAT version taken from the Visual Studio shell refused every open

**Found 2026-08-19**, first TwinCAT host open of `TC3_PlcSample_reStrText`
(`TcVersion="3.1.4024.4"`) on a 3.1.4026.25 install. The locator compared the
project to `TcXaeShell.exe` ProductVersion, which is **`15.0.0.0`** — the Visual
Studio shell, not TwinCAT. Every open looked like a conversion and was refused,
including the `%TEMP%` copies the gate is allowed to convert.

**Fix:** read `Components\Plc\Build_4026.25` (and `Version.txt` if that folder is
missing). Probe of a TEMP copy then succeeded in ~19 s. Conversion of the path the
user bound stays refused; only `Path.GetTempPath()` copies set `AllowConvert`. The
host's own convert-check had the same ProductVersion fallback until 2026-08-20 —
a locator-supplied version hid it on the gate path; an `unknown` version would have
compared `3.1.4026.25` to `15.0.0.0` and refused a matching project. Both walks
now use `Build_*`.

**What was rejected:** treating several TwinCAT shells as TIA's "several Portal
versions → refuse" case. 17.0 and 15.0 on this machine are the same TwinCAT
build; 17.0 is the 64-bit host and is preferred, logged, never a silent swap.

Related traps measured the same day, kept in
[`vendors/twincat/DRIVING.md`](vendors/twincat/DRIVING.md) rather than as
separate findings: no `IOleMessageFilter` → `RPC_E_CALL_REJECTED` after a 2.6 s
DTE create; `ITcSysManager.Save` is `DISP_E_UNKNOWNNAME`, `File.SaveAll` works;
`DeclarationText` often missing on IDispatch, `ProduceXml` is the fallback —
still the Automation Interface, not a disk parse of `.TcPOU`.

---

### A deletion-only mirror edit could report success while the object kept the line

**Found 2026-08-19**, by a code audit of the import verification — not by a test, because
the gate only ever *added* lines. `LiveCarriesApplied` checked that every line the applied
file added was present in the regenerated source. An edit that only removes a statement
adds nothing, so a generate that silently no-ops — the same failure mode the overwrite
defect had measured hours earlier — would verify vacuously, list the path in
`ChangedPaths`, and the next export would rewrite the mirror from the unchanged object,
**resurrecting the deleted line**: a silent revert of the user's edit, the exact defect
class this project exists to refuse.

**Fix:** when the applied file differs from the pre-generate source, the post-generate
source must differ from that pre-generate text too — compared whitespace-collapsed on
both sides, so a re-indent by generate is not a false refusal. The gate Fact grew the
inverse phase: add the marker statement, round-trip it, then delete it, import, and
re-export — the marker must be gone. Green against live Portal (5/5, 4 m 20 s).

**What was rejected:** a per-line "removed lines must be absent afterwards" check —
`END_IF;` appears dozens of times in any real block, so the *presence* of a common line
proves nothing without occurrence counting, and counting breaks the moment generate
re-wraps one line. The whole-text inequality is the claim that is actually true.

---

### UDT textual apply verified itself against a source no type can generate

**Found 2026-08-19**, same audit. The import verification regenerated source to a temp
file whose extension was hardcoded `.scl`; the export walk generates types to `.udt`,
and Openness validates the target extension per object kind. A textual `.udt` edit —
the planner marks every one of the fixture's 93 as `ApplyText` — would have the verify's
`GenerateSource` refuse and the import throw "cannot emit source after generate" for an
edit that had in fact applied. Never observed live, because no test had ever imported a
UDT: the gate mutated only `Level.scl`, an FB.

**Fix:** the verify's extension follows the object kind (`.udt` for a type). The gate
Fact now mutates the smallest `.udt` beside the `.scl` — a real member before
`END_STRUCT`, because a comment might not survive generate — imports both in one plan,
and asserts both markers on re-export. Green against live Portal. The staged source
copies the import writes to `%TEMP%` are deleted after generate now, too — three had
already leaked.

**What was rejected:** a separate UDT Fact — another 488 MB fixture copy and three more
Portal sessions for what one import job can carry.

---

### A successful TIA export still read as "not exported"

**Found 2026-08-19**, owner screenshot of CleaningStation after 159 objects were
written. The project row said `not exported · 4 objects not exported`; the banner
named Hardware / HMI / Safety / Technology; PLC data types (93) sat underneath as
if they had not been exported. Two independent causes, stacked:

1. **Coverage.** The host recorded those four categories as `NOT_IMPLEMENTED`. That
   reason is a hole, so `ExportCoverage` paints `HasGaps` on every TIA tree even
   when the PLC-software loop succeeded. They are decisions for this product slice,
   the same bargain `SERVICE_OBJECT` makes on the first vendor. `_coverage.md`
   still names them.
2. **Staleness.** Openness rewrites `System/` while it holds the project. Hashing
   the payload before the occupancy sidecar vanished recorded a mid-write tree;
   the commit gate then reported "1 project has changes that have not been
   exported." Occupancy-release used to retry the export in a loop (separate
   finding); after that wait was gated, the yellow banner remained.

**Fix:** category and `F_*` skips are `SERVICE_OBJECT`. After an exclusive-open
export, the orchestrator suppresses Project echoes for the run, waits until
occupancy is Free, then hashes. Sync still does not spell a vendor —
`ExportHeldViaCopy` is the seam.

---

### Occupancy vanish retried a finished TIA export

**Found 2026-08-19**, app loop on `%TEMP%\versio-tia-p7`. PHASE2 item 3 retries
both directions when the occupancy marker vanishes, which is closing the IDE.
Every host `Projects.Open` writes `<stem>.info` and deletes it on the way out.
Each finished export therefore looked like the engineer shutting Portal:
four cycles of 159 objects / ~32 s, nobody saving.

**Fix:** `SyncViewModel` remembers `BlockedByOpenProject` and ignores a vanish
that was not a wait. Tests:
`IdeReleaseRetryTests.A_vanished_marker_with_nothing_waiting_stays_quiet`.
Mutation-checked: deleting the remember/remove pair leaves the suite green
on the old tests and red on that one.

**What was rejected:** special-casing a vendor's sidecar in `Sync/` (the
marker path still comes from the format); treating every occupancy rewrite
as Held (that is the storm the wait was meant to end).

---

### Openness Export(None) refuses an existing file

**Found 2026-08-19**, second export of CleaningStation into the tree the first
export had just written. 159 `XML_EXPORT_FAILED` ("file already exists"),
**zero objects** in the manifest. The determinism gate used two empty
directories, so it never saw overwrite.

**Fix:** delete the dest before `Export` / `GenerateSource`. The same-tree
Fact (`Two_unchanged_textual_exports_match_byte_for_byte`) is the mutation
check.

**What was rejected:** `ExportOptions` other than `None` (not measured as
overwrite on V21); asserting on two fresh dirs and calling that
determinism.

---

### TIA host warnings were strings, so the panel said the manifest was unreadable

**Found 2026-08-19**, first App export of CleaningStation. The host wrote
`"warnings": [ "Level: SCL export failed: …" ]`. Core `ExportWarning` is
`{code, path, detail}`. The panel logged `manifest not fully readable` and
dropped the objects behind the parse failure.

**Fix:** `HostExport.Warn()` writes the record shape. After the overwrite
fix: 270 objects, 0 string-warning parse failures.

**What was rejected:** loosening the parser to accept a string (that hides
the next host that ships the wrong shape).

---

### Hashing a directory payload took an exclusive lock on the pointer

**Found 2026-08-19**, first App session against `%TEMP%\versio-tia-p7`. Export
finished; `ProjectHashAsync` used `File.ReadAllBytesAsync` on the `.ap21`
while Openness still held it. Logged `failed to open … at startup`. Copy
already retried a share lock; hash did not.

**Fix:** `WindowsFileSystem.ReadAllBytesAsync` opens `FileShare.ReadWrite`
and retries the way copy does. A pointer that is still exclusive is skipped
in the tree hash and logged, not thrown. Tests:
`WindowsFileSystemTests` (share + retry) and
`TreePayloadTests.Hashing_a_tree_skips_a_pointer_the_toolchain_still_holds`.

**What was rejected:** hashing only `System/` and ignoring the pointer (the
pointer moving is a real project change); waiting on a timer instead of
share/retry.

---

### TIA GenerateBlocksFromSource does not put a mutated SCL back on re-export

**Found 2026-08-19**, `TiaPortalGateTests.Import_then_reexport_keeps_textual_bytes` on a
copy of CleaningStation. Locator, probe and export are green. The Fact copies the LFS
fixture, exports, mutates one `.scl`, imports the planner's ApplyText for that file,
re-exports, and looks for the mutation in textual files. Native XML is not compared.

Import reports success (`ChangedPaths` names the file). Re-export does not carry the
edit. Measured in order:

- Header comment, extra `VAR`, `REGION`, then `IF FALSE THEN (* ident *)` in `BEGIN` —
  none survived.
- `GenerateBlockOptions` is `None | KeepOnError` on V21, not `Overwrite`. Falling back
  to enum value 1 called `KeepOnError` and created `BoolToDigVal_1.scl` beside the live
  block; the original was missing from the next export.
- Zero-argument `GenerateBlocksFromSource()` — V21's documented overwrite — applied
  `Level.scl` without throwing, then **Level was absent from the re-export**.
  `BoolToDigVal.scl` was back. The object was not renamed; it simply did not generate
  source.

Header comments and extra interface members were already the wrong mutation: generate
rebuilds the interface from the object. A body statement should have been the right
one. It still did not round-trip.

**Closed 2026-08-19.** The empty `IF FALSE THEN … END_IF` does not compile; Save then
left the block unexportable, so the next host wrote no `.scl`. The mutation that
survives is an assignment on an existing TEMP plus a `//` marker. Group+`None` is
the overwrite; KeepOnError is refused; generate verifies via `GenerateSource`
before Save and throws rather than listing a no-op in `ChangedPaths`. P7 SETTLES.
The Fact is green.

**What was rejected:** asserting the whole textual tree byte-identical (that is item 7.3,
and the file *set* already differed); treating AllowList as the failure (runs finished in
~90 s, not the 9-minute unanswered-dialog timeout).

---

### The App session treated every repository as the first vendor

**Found 2026-08-19**, audit of the second pack. The pack split was already right: Core has
no vendor project reference, `VendorSeamTests` holds the IL, and TIA Portal registers
beside the first vendor. The **session** did not follow it.

`SyncViewModel` pinned `_format = PlcFormats.All[0]` and `_vendor` from that format, and
never reassigned them. One `ProbeAsync`, one `IPlcRunner`, `new RepoSetup(..., _format)`,
`IsTemplate` through the pinned format, `WatchFile(ProjectPath)` only. A `.ap21` in a
mixed repo would be adopted under the first vendor's conventions (`*.~u` instead of
`Logs/` / `.info`), a TIA export would wait on that first toolchain being installed, and
a change inside a directory payload would not reach the orchestrator.

`ProjectBinding.Vendor` and `ProjectManifest.Vendor` still deserialize a missing field as
the first vendor. That is history on the JSON, not this pin.

**Fix:** `SyncOrchestrator` takes an optional `Func<PlcVendor, IPlcRunner>` so a binding
runs on its own vendor's runner. The session probes every registered vendor, builds
`runnerFor` from that map, constructs `RepoSetup` with `format: null` so `ForPath` wins,
asks `format.IsTemplate`, and watches `PayloadPathFor` (file or tree). Orphan scan skips
templates the same way and does not invent a vendor on `ProposeBinding`. App.Tests
register both packs; a temp `.ap21` is adopted with this vendor's gitignore entries.

**What was rejected:** teaching `Sync/` `.apXX` or `Logs/`; an App.Tests IL scan of
ViewModels (the composition root may name vendors — the flow test polices the pin);
changing the JSON default for a missing `vendor` field.

---

### SimaticML UId is a document-local integer, not a project identity

**Found 2026-08-19**, first inspectable export of CleaningStation. Manifest `guid` preferred
the first `UId` in the SimaticML. Every object recorded `"0"` — a wrapper attribute, not an
identity. Skipping `"0"` made every object `"1"`. Counting the XML: 159 files, **26,661**
distinct `UId` values, the most common `"35"`. They number elements inside a document.
They do not survive as a project-wide key, and they collide across objects.

**Fix:** the host records `{device}/{plc}/{type}/{name}#{number}` and never takes an
integer UId. `TryReadUid` only accepts a GUID-shaped value, which this vendor's XML does
not currently carry. Rename pairing stays on content hash / line sketch.

**What was rejected:** using the first non-zero UId (that is `"1"`); treating a small
integer as "probably unique enough".

---

### TIA occupancy is a .info sidecar, measured while Openness held the project

**Found 2026-08-19**, polling the scratch tree while the net48 host held
`Projects.Open`. `OccupancyMarkerFor` returned null because the closed-tree listing had
no `.~u`. With the project actually open:

- `<stem>.info` appeared beside the `.ap21` 16 s after open and vanished when the host
  exited
- `System/~PEData.1` appeared two seconds later and vanished on close

Existence of `.info` is Held. The body is not parsed — guessing Free from an unknown
format is how two writers would share a project. The tilde file is a transient inside
`System/`, excluded from the project hash so an in-flight export is not a program change.

**What was rejected:** leaving the marker null and relying on `Projects.Open` failing
(that reports after a multi-minute wait, and the panel cannot watch it); hashing `.info`
into the project (it is occupancy, not the program).

~~GUI-open occupancy (Portal with a window, no Openness) is still unmeasured.~~ —
**measured 2026-08-19 evening: the GUI writes and deletes the same `.info` marker**
(`vendors/tia/DRIVING.md` §5).

---

### Restoring a backup in the same second overwrote the backup

**Found 2026-08-19**, writing the directory-payload restore test. `BackUpProjectAsync`
names copies `yyyyMMdd-HHmmss-<Name>`. Restore takes a safety copy of the live project
*before* it copies the chosen backup back, and on a frozen test clock — and on any
restore that lands in the same second as the backup it is restoring — that safety copy
was the **same path**. The good tree was replaced with the damaged one, then copied
onto itself. The verdict was `Completed`. The project stayed damaged.

The same collision exists for a file payload (the first vendor) if two imports, or an
import and a restore, share a second. Unlikely with a wall clock, guaranteed with
`FakeClock`, and a real restore done immediately after the import that created the
copy.

**Fix:** if the destination already exists, append `-2`, `-3`, … rather than overwrite.
The stamp stays second-resolution so the pruner's ordinal sort still matches
chronology; uniqueness is a suffix, not a finer clock.

**What was rejected:** putting milliseconds in the stamp (a format change the catalogue
parser would have to learn); advancing the fake clock in the test and leaving the
product able to eat the copy it is restoring.

---

### TIA Portal Openness: the group is empty, so the host cannot open a project yet

**Closed 2026-08-19.** `elli_` is in `Siemens TIA Openness` and this logon token carries
the group. Probe 42 s, export 37 s, after a first unanswered AllowList dialog that timed
out at 8 min 53 s.

**Found 2026-08-19**, first probe of a real V21 install. Local group `Siemens TIA Openness`
exists and had **zero members**. `net localgroup ... /add` returned Access denied (error 5).
Until `elli_` was in that group and the session was logged off and on, `Projects.Open` was the
failure, not a product bug.

The first successful connect also needs Yes-to-all on the Openness firewall AllowList
(path + hash + timestamp). Rebuilding the net48 host changes the hash and drops off that
list — the same class of "it worked yesterday" as a CODESYS profile rename.

Recorded in `docs/vendors/tia/DRIVING.md`. The gate test skips with the host's reason rather
than going red, so a machine without membership is still a green suite.

**What was rejected:** loading Openness in-process in net9 (the assemblies are net48);
shipping `Siemens.Engineering.dll`; calling `OpenWithUpgrade`; treating an open failure as
"the project is free".

### A corrupt `repo.json` opened as a repository that had never been adopted

**Found on 2026-08-18 by an audit of silent paths, not by a failure.** `RepoConfigStore.LoadAsync`
caught `JsonException` and returned `new RepoConfig()` — the same answer as a missing file.
Opening the repository then showed no bindings, which is how a never-adopted clone looks, and
nothing in the log named the file that had just been thrown away.

The recovery is right: refusing to start over a malformed config leaves the user with no way
in, and bindings can be re-adopted. The lock store already logged this shape as Error, because
discarding a direction lock is a safety loss. Bindings were the one committed JSON file that
could go bad and leave no trail.

**A Warning, with the exception, and the path.** Empty bindings after a bad edit now have
something to grep. Mutation: dropping the log line leaves the existing round-trip tests green
and turns only the new one red.

### The geometry tests measured a disposed shell

**Found on 2026-08-18, the same defect `GitPanelChromeTests` already records, one file along.**
`GitPanelGeometryTests.Loaded` used `using var shell = TestShell.Build()` inside a helper that
**returns** the shell. When the helper returned, the shell was disposed, and every case in the
file laid out a window bound to a torn-down instance.

It passed throughout because none of those tests opens a repository, so disposal had almost
nothing to release — until the shell began disposing its log console, which the window is
bound to. Held in a list and released in `IDisposable.Dispose` on the STA thread, matching
the chrome file.

The suite staying green while measuring a dead instance is the inverted form of the
fifty-six undisposed shells: that leak kept watchers alive against deleted directories; this
one pretended to measure a panel that had already been torn down.

---

### The log opened on the first line, fought the wheel, and copied nothing

**Found on 2026-08-18 by using the panel.** Three defects, one list.

Opening the log after startup painted the *first* rows. Follow-the-tail lived only in
`OnRowsAppended`, and `ScrollIntoView` on a collapsed `ListBox` is a no-op, so a console that
already held the startup lines came back at offset 0. `ScrollIntoView` is still a no-op once
the list is visible, on a virtualizing panel that has not placed the last row — the test that
caught it saw extent 80, viewport 7, offset 0. The viewer itself has to be told
`ScrollToBottom`. `ScrollToEnd` is the wrong call: it also jumps horizontally, and a long
message would hide its own start.

Wheel-up was overwritten by the next 100 ms drain. Pause existed, but only as a header button,
and it already means freeze — the tooltip that used to say "pause auto-scroll" was rewritten
because the list changing under the reader is exactly what pause is for. Scrolling away from
the tail is the same request, made with the wheel, so it sets the same `IsPaused`. An unused
`AutoScroll` flag on the view model had survived that rewrite with no readers.

The list was a stock `ListBox`. `SelectionMode` defaulted to Single, the rows were
`TextBlock`s, and no `ApplicationCommands.Copy` binding existed anywhere on it. Click,
shift-click and Ctrl+C produced nothing a colleague could paste. The diff pane had already
solved this shape: Extended selection, document order not pick order, `LogEvent.Format()` so
a paste matches the file, and `ExternalException` swallowed so a busy clipboard cannot take
the window down.

Pinned in `LogPanelInteractionTests`. Deleting the expand-scroll left the list at offset 0
with the chevron test still green. Deleting the Copy binding left `CanExecute` false with a
row selected.

**What was still wrong after that, found by using it.** Extended mode still needed Shift or
Ctrl: a plain drag moved a single row with the mouse, so one line copied and a run of them
did not. And arriving back at the newest line stayed frozen — Play was the only way out,
which is not how a log you scrolled up to read should behave. A drag now paints a range;
scrolling back to the tail unpauses and Drain delivers what was held.

---

### Turning a label into a button changed its typeface, and the setter that carried it left with the element

**Found on 2026-08-18 by re-reading a finished, green change.** UX-9 turned the notification
overflow pill from a `Border` holding a `TextBlock` into a `Button`, so that the "+2 more" count
could actually be pressed. Everything about the conversion was correct except one property
nobody named.

The count was a `TextBlock` wearing the `PillText` style, which sets `FontFamily` to
**`Font.Mono`** — deliberately, so that "+2 more" and "+12 more" do not shift the glyphs around
them. The replacement `Button` was styled `BasedOn` `GhostButton`, which sets `Font.UI`, because
its content is normally words on a button rather than a number.

**So the face changed, and the setter that had been holding it disappeared along with the
element it was set on.** Nothing in the diff of the call site shows a font: the old markup names
`PillText`, the new markup names `PillButton`, and neither line says "mono". Nothing in the suite
could see it either — 589 App tests, all green, before and after.

**What makes this worth an entry is the shape rather than the font.** A style swap is a change
of *every* inherited property at once, and only the ones you name are visible in the diff. The
double-pass question — *what did I change that nothing is watching?* — is the only instrument
that reaches it, and reading `PillText` to see what was being replaced is what surfaced it.

**Fixed and pinned to the thing it came from.** `PillButton` names `Font.Mono` explicitly, with
a comment saying it is inherited from neither side and therefore has to be written out. The test
asserts the pill's resolved `FontFamily` against a throwaway `TextBlock` wearing `PillText` —
not against the literal "mono" — so the two styles cannot drift apart later either. Removing the
setter turns it red.

---

### One guard intent grew three verbs, and the dialog kept saying the first one

**Found on 2026-08-18 while fixing the stale comment on the member itself (UX-13), and recorded
rather than fixed.** `GuardIntent.Pull` shipped with one caller and a matching label:
`GuardNarrative.Action` maps it to the word `"pull"`, which is what reaches the dialog body and
the log line.

It now has **three** callers — `PullAsync`, the divergence reconcile, and the merge verb added
on 2026-08-18 — because all three rewrite the project and its mirror together, which is the
scenario the guard exists for. Reusing the intent was right. Reusing its *noun* was not:

- merging a branch onto a dirty tree asks **"Continue and pull?"**
- and logs **"asking before pull: 3 file(s) have changes"**

**The guard's decision is correct in every case.** `RewritesWorkingTree` answers true, the
dirty-tree branch offers the auto-stash, the refusals refuse. Only the verb in the sentence is
wrong, which is why this is a wording defect and not a safety one.

**Why it was left.** Closing it means a second intent, and `InterruptionGuard.RewritesWorkingTree`
has to learn that intent in the same change or the guard grows a silent hole — the exact failure
this document already records under *the seam test passed for a year because a string is not a
signature*. A reconcile is also a rebase **or** a merge depending on a parameter, so the naming
question is genuinely more than one row. That is more than a comment fix should quietly take on,
and the merge verb it touches already owes a real-CODESYS gate run.

**It is written into the enum's own remarks**, at the declaration, so the next reader meets it
before the next caller is added. The prediction that this would happen was already in
`archive/UX-PLAN.md`'s second pass — *"a new intent would also have to be added to
`InterruptionGuard.RewritesWorkingTree` or the guard has a silent hole"* — and the merge verb
shipped without one.

---

### A cancelled merge ran straight past the thing that protects it

**Found on 2026-08-18, on the fourth read of the same feature.** The three before it had each
asked a different question — is the gate right, does the code match its comment, what happens on
the undo — and all three looked only at paths that <i>return</i>.

`MergeBranchAsync` marks the mirror authoritative after the merge command comes back. But
`IGitClient.MergeBranchAsync` can **throw** rather than return a failed `GitResult`: it is handed
`busy.Token`, and a cancelled merge can leave the tree part-way through with the mirror already
rewritten. On that path the exception propagates to `AsyncCommand`'s error handler and the
marking never runs, so the merged text is left open to the commit gate's export — the exact loss
the whole feature exists to prevent, reached by the one route nobody had looked at.

**The fix is a `finally`, and the trade in it is deliberate.** Marking there means a merge that
threw *before touching anything* — git missing, a process that would not start — takes a lock it
did not need. That is recoverable: abandoning releases it, and so does discarding. **An
unprotected merged mirror is not recoverable.** So the safe default is to take the lock and let
the release paths sort it out, rather than to protect only the path that returned cleanly.

**And the comment above it had gone stale in the same edit that made it wrong.** It still read
*"Nothing tracked is involved — the preflight has just established that"*, written when the
preflight refused such merges. It stopped being true the moment the refusal became a
confirmation, two commits earlier, and neither of the two passes in between noticed because both
were reading the code rather than the comment.

**Four passes, four defects, one feature.** The list is in
[`archive/HANDOFF-UxPlan.md`](archive/HANDOFF-UxPlan.md). The useful conclusion is not that reviewing works — it
is that a lot of code was written quickly in the most dangerous subsystem here, and that the
things still owed (the real-CODESYS gate, a human looking at it) are worth more than another
read by the same reader.

---

### Abandoning a merge left the lock it took, with no way out

**Found on 2026-08-18, on the third read of the same feature, before it reached a user.** Two
earlier passes over the merge work missed it because both were looking at the merge and this is
about the *undo*.

`AbortMergeAsync` puts the tree back through `WhileGitRewritesTheTreeAsync`, which calls
`AdoptExternalStateAsync` — and adopt **deliberately skips a binding that holds a lock**. So a
merge that had just made the mirror authoritative left that lock on after the abandon:

- exports blocked, over a mirror that no longer differed from anything;
- the import banner still up, offering to import a mirror identical to the project;
- and neither documented way out available, because the two releases are **push** and
  **discard**, and an abandoned merge leaves nothing to push.

The next project edit would then be refused by the commit gate, and the user would have no
visible way to clear it.

**A blanket release on abort would have been wrong**, which is the part worth recording. A
`TextAuthoritative` lock can predate the merge — committed mirror edits nobody has imported —
and undoing the merge does not undo those. Releasing there would drop the record of real work
still owed to the project. So the release compares **both hashes against `LastSynced`** and lets
go only when nothing is outstanding: a derived answer rather than remembered state, correct
regardless of which operation created the lock.

**And the same read found a silent no-op beside it.** `NoteMergedTextAsync` called `ApplyAsync`
and ignored the result — but the state machine blocks `TextChanged` from `ProjectAuthoritative`,
a project whose own edits have not been exported. In that case the merged mirror was left
unprotected from the export gate and **nothing said so**: `ApplyAsync` logs a refused trigger at
`Debug`, which is right for the ordinary case and far too quiet for this one. It returns whether
the lock actually moved now, and the panel posts a sticky failure when it did not. Mutation
found it: reporting `true` unconditionally left every other test in the file green.

**Three passes, three defects, in one feature** — the adopt ordering, this, and the silent
no-op — and none of them was catchable by running the suite. Each came from re-reading with a
different question: *what does the comment claim*, *what happens on the undo*, *what does this
return value mean*.

---

### The two predicates that name the lock's whole purpose have no callers

**Found on 2026-08-18**, while building the import-after-merge path, at the moment of relying on
one of them.

`SyncState.cs` ends with three predicates:

```csharp
public static bool ImportAllowed(SyncDirection state) => …
public static bool ExportAllowed(SyncDirection state) => …
public static bool HoldsLock(SyncDirection state)     => …
```

`HoldsLock` has callers. **`ExportAllowed` and `ImportAllowed` have none in `src/` at all** —
their only references anywhere are two lines of `SyncStateMachineTests`, which asserts they
return what they return.

**Why that is worth an entry rather than a delete.** They are not obscure helpers. They are the
sentences that state what the direction lock is *for* — "exports blocked", "imports allowed" —
sitting in the file that defines the lock, next to a predicate that is used. Anyone reading the
state machine to find out what enforces the lock finds them first and stops there. **This work
did exactly that**, and designed the whole merge ordering around calling `ExportAllowed` before
checking whether anything already did.

**What actually enforces it** is `SyncStateMachine.Next` plus `DirectionLockForbids`
([`SyncOrchestrator.cs:1096`](../src/Versio.Core/Sync/SyncOrchestrator.cs)), asked in
`AttemptAsync` **before** the toolchain is opened. That ordering is the real protection and it is
correct — but nothing named it, so it had to be established by reading the call chain rather than
by reading the predicate that appears to say it.

**The near-miss.** Had the enforcement been *after* the export instead of before, relying on
`ExportAllowed` would have shipped a merge path that overwrote the merged mirror and then failed
a transition — the mirror gone, the lock unchanged, and a green test suite. The reason it is not
that is luck of an existing design, not anything this work checked first.

**Deliberately not deleted.** The obvious move is to remove two members with no callers, and this
repository's own rule says every member has a caller or says why it does not. They are kept and
their remarks now name the enforcement instead — because the third option is worse than both: a
future reader asking "what stops an export here" would otherwise find nothing at all in the file
that defines the states, and go looking in the orchestrator on their own.

---

### A new verb was live on a detached HEAD, where every old one is dead

**Found on 2026-08-18**, on the third read of the merge work, before it reached a user.

`MergeBranchCommand` was gated `Ready && Branches.Count > 1`. Neither clause says anything about
a **detached HEAD**, and on one the flow would have:

1. read `BranchName`, which is the literal string `"(detached)"`
   ([`ShellViewModel.cs:5158`](../src/Versio.App/ViewModels/ShellViewModel.cs));
2. handed that to `git branch --merged "(detached)"`, where it resolves to nothing, so **every**
   branch would have been offered as having new work;
3. asked *"Choose a branch to merge into (detached)"*;
4. and made a merge commit reachable from **no branch at all** — work that exists only in the
   reflog.

**What makes it worth an entry is the company it was keeping.** Every other verb that writes to
the remote is already off in that state, and not by accident: `CanPush`, `CanPull` and
`CanPublish` all read `HasUpstream`, and a detached `RemoteStanding` carries no upstream, so the
whole remote row goes dead. `RemoteStanding`'s detached branch even spells out the hazard —
*"New work here will be lost unless you put it on a branch."* The new command was the one live
way to write history in a state the rest of the panel had carefully closed down.

**The near-miss is the lesson, not the fix.** The fix is one clause. What nearly shipped it was
adding a command by copying the shape of the one beside it: `DeleteBranchesCommand` requires
`DefaultBranch.IsKnown`, which happens to keep it sane, and the new one dropped that requirement
for a good reason — merging asks about the *current* branch, not the trunk — without asking what
else that requirement had been doing. **A gate copied from a neighbour inherits its accidents as
well as its intent, and dropping half of one needs its own argument.**

Gated on `Remote.Standing.State != TrackingState.Detached` now, and covered by a test that
detaches a real repository and asserts the command goes dead — verified by removing the clause,
which fails exactly that case.

---

### `merge=lfs` quietly cancelled the `-merge` two tokens to its left

**Found on 2026-08-18** while trying to run probe P-M1 — the question of what a merge does to a
`.project` and its mirror. The probe never got that far, because its first step disproved the
premise everything above it rested on.

**`GitAttributesPlan` writes one line per project pattern:**

```
*.project        binary -diff -merge filter=lfs diff=lfs merge=lfs
```

and its own comment calls `-diff -merge` *"the part that matters"* — without it "git will attempt
a line merge on a binary compound document and produce a file that opens in neither CODESYS nor
a text editor". **Later attributes win.** `merge=lfs`, six tokens along the same line, overrode
it. Asked directly:

```
$ git check-attr merge fixtures/projects/RefrigeratorControl.project
fixtures/projects/RefrigeratorControl.project: merge: lfs
```

`lfs`, not `unset`. On every machine with LFS installed — which is precisely the machines the LFS
tokens were added for.

**And `merge=lfs` names a driver that does not exist.** git-lfs installs `filter.lfs.*` and
`diff.lfs.*`; it does **not** install a `merge.lfs.driver`, and nothing in Versio writes one. Git
falls back to the built-in three-way **text** merge — applied to the LFS pointer file.

**Measured on two throwaway repositories differing only in that line**, each with a base commit
and two branches editing the same binary:

| line | git says | working tree afterwards |
|---|---|---|
| `binary -diff -merge` | `warning: Cannot merge binary files` | the **intact binary**, conflict raised |
| `… -merge filter=lfs diff=lfs merge=lfs` | `Auto-merging App.project` | an **LFS pointer with `<<<<<<< HEAD` inside it** |

git-lfs itself noticed and complained on the second: *"Encountered 1 file that should have been
a pointer, but wasn't"*. A pointer carrying conflict markers no longer resolves to any object —
commit it and the binary is unreachable from that commit.

**How exposed Versio actually was.** Narrower than it looks, and worth stating precisely rather
than dramatising. The conflict is still *raised*, so nothing is silently wrong; and Versio's own
conflict section resolves through `git checkout --ours/--theirs`
(`ResolveWithSideAsync`), which replaces the file wholesale and restores a clean pointer. The
danger is the paths that do not go through it: resolving in a text editor, or staging and
committing the marker-laden pointer because the panel reported a conflict and the file "looked
like text".

**The fix is to delete one token.** `filter=lfs diff=lfs` stay — they name real, installed
behaviours and are what make the binary stored and displayed sensibly. Only `merge` goes, and
`check-attr` then answers `merge: unset`. This repository's own committed `.gitattributes` was
rewritten the same way, on the four project patterns that carry `-merge`; the `third_party/**`
lines keep theirs, because they never had a `-merge` to protect.

**The test that should have caught it pinned the defect instead.** `GitAttributesTests` asserted

```csharp
Assert.Contains("filter=lfs diff=lfs merge=lfs", with.Content);
```

— the literal string the code emitted, which is a test that the code does what the code does. It
asserts the *rule* now: `-merge` present, `merge=lfs` absent, `filter` and `diff` still there.
The general shape is the same one this log recorded twice already this week: a test written
against the implementation rather than against the behaviour cannot fail when the implementation
is the thing that is wrong.

**What it means for the merge verb (UX-1).** The original prediction — that a merge leaves the
project and its mirror disagreeing — is *not* disproved; it is still true that resolving a
`.project` conflict by picking a side gives you one branch's project against a three-way-merged
mirror. What changed is that the first link in that chain was worse than described, and had to be
fixed before the rest could be measured at all. P-M1 proper still has to run.

---

### Two rail buttons lit at once, because grouping was a property of the nesting

**Reported by the product owner on 2026-08-18**, with a screenshot, against a change made
minutes earlier: *"cant even click on the git button, is stuck and highlighting 2 buttons at
once, git panel doesnt open"*. Project and Git both showed selected.

**Cause.** A `RadioButton` with no `GroupName` groups by its **visual parent**. The three rail
buttons were bare siblings in one `StackPanel`, so mutual exclusion worked — by accident, and
only for as long as nothing came between them. Adding the change badge wrapped the git button in
a `Grid` so the badge could be overlaid on its tile, and that made it a group of one: checking
it never cleared Project, and the panel could not switch.

**The nesting is not the defect.** Unwrapping the badge would have restored the accident, and
left the same trap for whoever wraps a rail button next — for a second badge, a tooltip host,
anything. `GroupName="Rail"` on all three makes exclusion a property of the buttons rather than
of how they happen to be arranged. The theme radios in the settings popup have carried an
explicit group since they were written; the rail was the odd one out, and the accident had held
long enough to look like a decision.

**What makes this worth an entry is that the suite was blind to it in a way that generalises.**
Every rail test in `Versio.App.Tests` — `ShellLayoutTests`, `MainWindowTests`,
`PanelCodeBehindTests`, `HistoryRowChromeTests`, and the one added with the badge itself, which
deliberately walks all three buttons — does this:

```csharp
Assert.IsType<RadioButton>(window.FindName(rail)).IsChecked = true;
```

**Setting a bool on three objects one after another does not exercise exclusion.** Nothing is
ever asked to turn anything *off*. Each test then asserts that the panel it just selected is
visible, which is true in both the working and the broken build. They were doing exactly what a
user does and checking exactly the thing the user was about to complain about.

**Measured, by putting the bug back.** With `GroupName` removed, exactly one test fails — the
new `Selecting_one_rail_button_clears_the_others`, on its `RailGit` case — while **28 others,
every pre-existing rail and panel test among them, stay green.**

**The rule this leaves behind.** For any mutually exclusive control, assert the **negative**:
after selecting one, the others are off. A test that only checks "the thing I selected is on"
cannot tell a working group from three independent toggles — and neither can a reviewer reading
it, which is why five separate test classes contained the same blind spot without anyone
noticing.

**And a second-order note on the badge itself.** The plan proposed plumbing it through
`RailButton`'s `ControlTemplate` with attached properties. That was rejected — one consumer does
not justify the mechanism — and the rejection stands, but it is worth recording that the cheaper
route carried a cost the plan did not price: overlaying in a `Grid` changes the visual tree, and
in WPF the visual tree is load-bearing for more than layout.

---

### A test named for the overflow it was watching could not reach the overflowing state

**Reported by the product owner on 2026-08-18**, from the running app: *"the staged and not
staged lists get cluttered, when there are multiple changes the labels start overlapping"*.

**The layout defect is ordinary.** Both list headers were `Auto | Auto | * | Auto` — checkbox,
label, caption, buttons. A WPF `Grid` measures every `Auto` column against infinity and hands
each its full desired width; the star column takes the remainder, floored at zero and never
negative. So once the `Auto` columns together wanted more than the row had, the star collapsed
to nothing, the `Auto`s kept their width regardless, and the row arranged **past** the panel —
over the splitter and the diff pane, because nothing in that chain clips. `NOT STAGED` sat in
one of those `Auto` columns in the letter-spaced `SectionLabel` face, rigid, while
`StagingSelection.Label` grew the buttons beside it into `Discard 127` and `Stage 127`.

**What is worth recording is not that. It is that the test existed.**
`GitPanelGeometryTests.Nothing_overflows_the_panel_sideways` walks every visible child of the
panel and asserts that none crosses its right edge. It is named for this failure, it was written
for this failure, and it passed — **for four independent reasons**, each of which is available
to any fixture:

1. **It measured one width.** `PanelWidth = 380`, which the class comment called *"the panel's
   real column width"*. It is the **default**. `PanelColumn` is `Width="380" MinWidth="260"`,
   and the chosen width is **persisted** — so a panel dragged narrow once is narrow on every
   later launch, and 120 px of the budget can be gone before a single row is drawn.
2. **That constant was never read by anything.** The 380 arrived only from the XAML's own
   `Width="380"`. The constant documented the test's premise and enforced none of it — so a
   reader checking "does this measure the narrow case?" would find a named width and a comment
   saying yes, and both were decoration.
3. **Nothing in the fixture was ever ticked.** It added 4 staged and 12 unstaged rows and never
   set `IsSelected`, so every button rendered its short `"{verb} all"` caption and **both header
   captions were the empty string**. Twelve rows also caps every count at two digits. The widest
   state the header can reach was not merely untested — it could not be constructed.
4. **And the assertion then filtered it out anyway.** It ran on
   `Visibility == Visible && ActualWidth > 0`. An empty `TextBlock` measures zero wide, so the
   one control in each row *designed to absorb the slack* was excluded from the only test
   watching the row.

**Measured, once the fixture could reach it.** At 260 px the `NOT STAGED` row — two buttons
where `STAGED` has one — reached 317 px against a 304 px edge with the short labels, and 330 px
with three-digit counts. **The plan's diagnosis was wrong about the trigger**: it blamed the
growing labels, and the row overflows with the short ones too. The narrow column is the cause;
the labels are an aggravator. At 380 px it never overflowed at all, ticked or not.

**The fix and its own trap.** Label and count became one trimming run in the star column, which
removes the rigid `Auto` outright. The mutation check — restore the rigid column — fails at
**380 px as well** (435 against 424), because the header text is longer now. That is the
evidence for the sequencing rule this was planned under: **adding the count alone, without the
column merge, would have broken the default width too.**

**And one correction made during the work.** The first attempt simply deleted the
`ActualWidth > 0` filter. That is wrong: a child of a **collapsed** parent still reports
`Visibility.Visible` for itself, so the conflict section's buttons — not on screen — immediately
began reporting as the widest thing in the panel. The width clause had been doing
ancestor-visibility filtering *by accident*. It is an explicit ancestor walk now, and
deliberately not `UIElement.IsVisible`, which additionally requires a presentation source these
tests never create and would have made the whole assertion vacuous.

**The lesson, which is the reason this is here.** A test that cannot construct the failing state
is indistinguishable from a test that passes, and this one carried a name, a comment and a
constant all asserting it covered the case it could not reach. Green was never evidence. The
question that found it was not *"is this tested?"* but *"can this test fail?"*.

---

### Properties are readable now: the payload imports, the text is for people

**Asked for by the product owner on 2026-08-18**, looking at the panel: *"properties are still
getting rendered as xml only, all the properties on the project only have a getter — needs to
render the same way"*. Seven properties on one function block, seven opaque XML blobs, and
every getter inside them is ordinary Structured Text.

**The shape is genuinely there.** A probe of the live project:

```
P_Active         PROPERTY         decl 59 chars      impl none
Get              PROPERTY_METHOD  decl 0 chars       impl 40 chars
```

A property is its header, an accessor is its body — exactly `ST_DECL` and `ST_IMPL`, the two
forms a DUT and an ACTION already take. So the table was changed, `PROPERTY_METHOD` was added
beside it, and **the export half worked on the first run**:

```
P_Active.st        {attribute 'monitoring' := 'call'}
                   PROPERTY P_Active : BOOL
P_Active/Get.st    P_Active := State = E_AlarmState.Active;
```

Readable, reviewable, pragma intact, and the accessor sitting inside the property's directory
exactly as schema 9 says it should.

**Then the round trip emptied all seven.** Export, import, re-export with nothing edited:

```
BEFORE >>P_Active := State = E_AlarmState.Active;<<
AFTER  >><<
```

That is not churn, it is **silent destruction of every property body in the project** — the
failure this repository exists to prevent, found by the gate on the same run that proved the
export was right. Reverted the same hour.

**Which half does it is not yet measured, and that is the whole of the remaining work.** Two
candidates, and the fix differs completely between them:

- **Replacing the property's declaration resets its accessors.** `apply_st` calls
  `textual_declaration.replace` on the PROPERTY, and CODESYS may regenerate the Get/Set
  underneath it. Ordering does not explain it away: `P_Active.st` sorts before
  `P_Active/Get.st` (`.` is 46, `/` is 47), so the accessor is applied *after* the property
  and still ends up empty.
- **Applying the accessor's own EMPTY declaration wipes the implementation beside it.** A
  PROPERTY_METHOD reports `has_textual_declaration` true with **zero characters**, which is a
  shape nothing else in the table has — every other `ST_IMPL` object (an ACTION, a TRANSITION)
  has no declaration at all rather than an empty one.

**The recursive payload survives because nothing is reassembled**: the accessors travel inside
the property's own XML, so no write ever touches them individually. That is why `NATIVE_R` is
the form again, and the comment in both form tables now says so rather than implying it was
never questioned.

**Shipped as two halves instead, which is the shape this repository already had for exactly
this problem.** A GVL writes a readable `.st` that imports and an `.xml` that does not; a task
configuration writes a `.tasks.yaml` that imports and an `.xml` that does not. A property is
the same bargain pointing the other way:

```
P_Active.xml       NATIVE_R        importable — byte for byte what it always was
P_Active.st        PROPERTY_TEXT   export-only — the header, with its pragma
P_Active/Get.st    PROPERTY_TEXT   export-only — the accessor's body
```

The text is what the owner asked for — a property that can be read in the panel and reviewed
in a diff — and it is never applied back, which removes the step that emptied them. The round
trip is byte-identical again, proven on the same test that rejected the first attempt.

**What it costs, stated because somebody will meet it:** a property edited in the mirror does
not flow back. The import announces it per file and the panel says to change it in the IDE,
exactly as it already does for a `.gvl.xml` and a task configuration's `.xml`.

**Three homes for "export-only", and the test knew all three.** `versio_types.EXPORT_ONLY_FORMS`,
`ExportCoverage.IsExportOnly`, and `tools/codesys/New-ImportPlan.ps1` — which refuses a form it
does not recognise rather than guessing, and which was left behind once before when
`GVL_NATIVE` was added. `Python_and_csharp_agree_on_which_forms_are_never_applied_back` caught
the omission on the first run.

**Still open, and now the only part that is:** which half empties an accessor when the text
*is* applied. Answering it would let a property become editable rather than merely readable.
It needs one experiment against CODESYS — replace a property's declaration, read the accessor
back — not a guess.

---

### An import destroyed a method, and the summary said seven native, zero warnings

**Found 2026-08-18 by the CODESYS gate**, on a round trip nobody had been able to run before:
the product owner added `M_Test` to `PLC_PRG` that morning, and `PLC_PRG` is a **graphical**
POU. Export, import, re-export — and the mirror came back with a file missing:

```
export     walked 26, wrote 19    …/PLC_PRG.xml, …/PLC_PRG/M_Test.st
import     7 native, 8 unchanged, 0 warning(s)
re-export  walked 25, wrote 18    …/PLC_PRG.xml
```

**`import_native` replaces, and a non-recursive payload is the node alone.** `PLC_PRG` exports
as `NATIVE` — its body is graphical, so there is nothing textual to write — and its method
exports separately as ST. Handing the `.xml` back replaces the POU with a payload that has no
method in it, and the method goes. The textual phase runs *first*, so `M_Test` was written to
the project and then destroyed a few seconds later by the phase after it.

**Nothing said so.** `import: 7 native, 8 unchanged, 4 not applied — 0 warning(s)` is the whole
report of a run that lost an object. The count of files applied was correct; there is no count
of objects that stopped existing.

**Pre-existing, and it needed one specific shape to fire**: an owner whose payload is
non-recursive *and* a member exported to a file of its own. Neither fixture had one until that
morning — `MachineWithMembers` gives its members to a **textual** `FB_Pump`, and
`RefrigeratorControl` had no members at all until the day before. It is not related to the
layout change landing beside it: the same three lines run whichever directory the member's
file sits in.

**Refused rather than worked around**, in the planner, because that is where the manifest can
be read — the script sees one file at a time and cannot know what else belongs to the object.
A native file whose object owns recorded members is now `PlannedSkip.WouldDestroyMembers`, with
a warning naming them. The two alternatives were both worse:

- **Export such a POU recursively.** The round trip becomes whole and the members lose their
  reviewable text, which is the thing Versio exists to produce.
- **Re-create the members afterwards.** Not available: a `METHOD` header is not a declaration
  `create_textual` can identify, so the create path refuses it — which is *why* the loss was
  silent rather than a crash.

What it costs is real and small: a graphical POU's body stops being importable once it owns a
member. Its members' own files still import, and a graphical body was never reviewable text.
The warning says which members, and to change the body in the IDE.

**It is the rule `PlanDeletions` already applies, one operation along** — never remove the
thing that others live inside. That rule was written for deletions after P3 measured
`remove()` taking a class's property with it; this is the same destruction reached through
`import_native` instead, and it went unnoticed because the deletion path had a test corpus and
this one had no fixture that could produce it.

---

### Ownership was spelled three ways in one tree, and the panel could only read one

**Raised 2026-08-18 by the product owner, looking at two screens at once** — Explorer with
seventeen files in one directory, and the panel showing `FB_Alarm` with four loose members
under it. Two observations, one cause: *"having things flat on the export side will get really
messy"*, and *"folders for private and protected are not shown, and P_Faulted is shown in the
middle, while these are usually last in the CODESYS IDE"*.

**The mirror encoded ownership three different ways.** A directory for a container, a
`FB_Alarm.` prefix on a member's file name, and — for a folder inside a POU — nothing at all,
the folder erased and its members flattened up beside the owner. All three had already been
got wrong once each. The prefix was the only record of ownership until manifest schema 4, and
a name containing a dot defeats it. The erasure was the finding below, fixed the previous day.

**So the fix was to delete two of the three.** From schema 9 a container makes a directory, an
object with exported children makes a directory named after itself, and a member goes inside
it: `Device/Plc Logic/Application/FB_Alarm/Private/_M_Reset.st` is what CODESYS shows, segment
for segment. The importer splits the path, the panel reads the path, and neither needs to know
what a member is. `ResolveKnown`'s owner-chain walk and stem-split fallback survive for older
mirrors and are dead code for new ones.

**`parent` stays, because a path cannot say everything.** It answers whether
`FB_Alarm/M_Reset.st` is the POU's method or an object that happens to sit in a folder of that
name — and it is what the panel nests by, so a member still hangs off its owner rather than
off a directory that merely shares its name.

**Not everything with children gets a directory**, and the rule is "children exported as
*files*". A task configuration's tasks are rows inside its `.tasks.yaml` — writing them
separately produced a third copy of every task, which was its own defect — and a GVL's two
files are two layers of one object. So `FB_Alarm/` and `PLC_PRG/` exist and
`Task Configuration/` does not.

**The measurement that stopped the wrong fix being built.** The panel sorted alphabetically
and the IDE does not, so the obvious answer was "record the order CODESYS gives us and sort by
it". A probe of the live project says that would have been wrong:

```
get_children()   M_Reset,  Private,  Protected,  P_Faulted     ← storage order
CODESYS IDE      Private,  Protected,  M_Reset,  P_Faulted     ← what the owner sees
```

At application level it is starker: the probe returns `FB_Alarm` eleventh of twelve where the
IDE shows it second. `get_children()` is **creation order**, and sorting by it would have
shipped an ordering that looks authoritative and matches nothing. What the two screenshots do
agree on is **folders first** — so that is the rule the panel now applies, and it costs no
recorded data at all, because the tree builder already emitted directory groups before
objects. Making the in-POU folder a directory was the whole fix. The rule *within* a group is
still alphabetical and still unmeasured; it is not inferred from two cropped screenshots.

**The one-time cost, stated where somebody will meet it.** Every file in every mirror written
before schema 9 is at a different path, so the first export after upgrading renames the lot.
Content is untouched, so git pairs them as renames and history survives, and the planner
branches on `ExportCoverage.PathIsChainSchema` so an un-re-exported mirror keeps importing
correctly. Both layouts are legal file names and neither is recognisable on sight — the schema
number is the only thing that can tell them apart, which is why it is read rather than the
path sniffed.

Covered on both sides. `ProjectTreeMembersTests` gains three cases — folders as rows inside
the POU with the right paths, roll-up through two levels, and a pre-schema-9 member gaining no
folder — and three mutations each fail exactly one of them: never rebuilding the folders,
counting the owner's own directory as one, and putting objects before folders.
`CodesysImportPlannerTests` now names which layout each test describes, with the flat ones
moved to a `LegacyManifest` helper that is *one below the threshold* rather than a literal 8.
The export half rides on `ProjectTreeFromRealExportTests`, against a fixture that now carries
two folders, a property with accessors, and a member on a natively-exported POU.

---

### A folder inside a POU stripped the owner off every method in it

**Found 2026-08-17 by the product owner reading the panel** — the eighth found that way, in
the same hour as the swap failure above and from the same edit. They had grouped one of
`FB_Alarm`'s methods into a folder called `Private` in the CODESYS tree, and Versio drew
`Private > _M_Reset` as a **sibling of FB_Alarm** under the application: *"private `_M_Reset`
belongs to FB_Alarm"*.

**The manifest said it plainly, one record apart:**

```
METHOD  M_Reset   Device/Plc Logic/Application/FB_Alarm.M_Reset.st        parent 6e8ed404-…
METHOD  _M_Reset  Device/Plc Logic/Application/Private/_M_Reset.st        parent null
```

**The walk dropped the chain at the folder.** `Exporter.handle` passes `parent_name` and
`parent_guid` down when it recurses into a POU's children — that is what writes
`FB_Alarm.M_Reset.st` and records the owner GUID. The `FORM_CONTAINER` branch recursed with
`None` for both, unconditionally, because a container had only ever meant a *project-level*
folder, where that is exactly right.

**The visible half was the cheap half.** A member with no recorded owner is resolved by the
planner through its **directory**, so the import chain for that file came out
`Device/Plc Logic/Application/Private/_M_Reset` — and there is no `Private` under
`Application`; it is inside `FB_Alarm`. Editing that file in the mirror would therefore have
been **refused and dropped**: `PARENT_NOT_FOUND`, one warning, nothing written. The mirror
would show the change as applied and the project would never have received it. Worse if the
name had been an ordinary one — a project-level folder called `Private` would make that owner
chain resolve, and the method would be created in a **different part of the project**. Nobody
had reached either yet, and nothing in the suite was watching.

*(The first draft of this entry said the import would create a duplicate. It would not, and
the difference was found by re-reading `create_textual` rather than by any test: an
unresolvable owner is reported, not approximated. The duplicate is real but belongs to the
shape below.)*

**A folder inside a POU is a view, not a place** — that is the whole fix, and it is why the
mirror shape gets *simpler* rather than gaining a case. The grouping is organisation in the
IDE tree and nothing else: the method is `FB_Alarm._M_Reset` to every caller, its name is
unique across the POU whichever folder holds it, and dragging it between folders changes no
code and no reference. So the ownership carries straight through the folder and the folder
leaves no directory behind. `FB_Alarm._M_Reset.st` now sits beside `FB_Alarm.M_Reset.st`,
foldered and unfoldered members are **indistinguishable on disk**, and re-organising the IDE
tree produces no mirror diff at all. The C# planner needed no change whatsoever, which is the
strongest evidence the shape is right.

**And the new shape needs the importer's half, or it trades one defect for another.** The
chain is now `…/Application/FB_Alarm/_M_Reset` — correct, and *the live project still has the
folder in the middle of it*. A strict walk finds `FB_Alarm` and then fails on its last
segment, which is the dangerous outcome rather than the safe one: the owner resolved, so the
importer is holding a valid parent and a file it was told to apply. The textual path stops at
`CANNOT_IDENTIFY_DECLARATION` (a `METHOD` header is not a creatable kind, which is luck rather
than design), and **the native path does not stop** — `import_native` both replaces and
creates, and it would paste the member into the POU's root beside the folder holding the
original.

So a resolve chain names objects, a folder is not one of the things it names, and
`find_by_path` now looks through them — `versio_tree.find_child_folder_blind`, extracted to a
pure module so it can be driven under CPython. Three rules in it, each one a refusal:

- **only folders are walked past**, never a POU, or a chain naming a program could be
  answered by somebody's method;
- **a direct child outranks one found inside a folder**, because the direct child is what the
  chain literally says and a fallback that can outrank the literal reading is not a fallback;
- **an ambiguous name returns nothing**, which is precisely today's behaviour rather than a
  coin toss — CODESYS will not compile two objects of one name, and "cannot happen" is what
  every silent overwrite was called beforehand.

Covered on both sides, at very different prices. The importer's half is
`PythonTreeWalkTests` — seven cases against a stand-in object answering `get_children`,
`get_name` and `is_folder`, under a second. The exporter's half **cannot** be tested without
CODESYS, because the walk needs `scriptengine`, so it rides on the export
`ProjectTreeFromRealExportTests` already pays for: the fixture carries `FB_Alarm` with
`M_Reset` beside it and `_M_Reset` in a folder, and the test asserts the recorded owner, the
shared directory, the prefixed file name, that no `Private/` survives anywhere in the mirror,
and that the drawn tree hangs both methods off the POU.

---

### A process parked inside the mirror made every export fail at the swap

**Found 2026-08-17 by the product owner running the app** — the seventh found that way —
minutes after adding methods to `FB_Alarm`: the export wrote all 18 files including the new
ones, then died at the swap with `[Errno 13] Access is denied`, twice, leaving a
`.versio_staging` beside the mirror (which the next run correctly cleared and re-created,
and which the orphan scanner correctly reported in the meantime).

**The diagnosis was a rename probe, not a guess.** The mirror directory refused to rename
even with Versio killed — so not the watcher, not a transient git race. Probing each child
one at a time drew the map: `Device` refused, `Device/Plc Logic` refused, **`Application` and
every file renamed freely**. That is the signature of a directory *handle* held on
`Plc Logic` — most likely a process's current working directory, which on Windows blocks
renaming every ancestor while blocking no file. An IDE's file dialog browsed into the
mirror is enough to set one.

**The swap was atomic-or-nothing, and "nothing" was the outcome on offer.** The
three-rename swap exists so a crash never leaves a project without a mirror — but
`os.rename` of the tree's root needs every directory handle in it gone, and a parked
process never lets go. Somebody browsing their own export must not be able to break the
export.

**Fixed in two layers, the trade stated in the script:**

1. `rename_retrying` — five attempts over a second, which outlives the genuinely transient
   holders (git hashing a status, a virus scanner, a directory enumeration) that were
   always a millisecond-window risk on every swap.
2. `replace_contents` — when the rename is still refused, the mirror is replaced file by
   file, directories left standing: old files removed, new ones moved in, old-only
   directories removed only if they empty out — the one that refuses is exactly the held
   one, and it stays, empty, for the next full swap to take. The atomic either-old-or-new
   guarantee narrows to a brief mixed-content window on this path only, and the C# side
   logs it as a warning naming the cause, because until the parked process moves every
   export takes the slower path.

Proven under CPython against a real squatter — a child process parked by cwd inside a
scratch mirror — asserting the refusal, the retry timing, the file-level replacement with
the directory still held, and the ordinary path resuming once the squatter exits. The
CODESYS gate re-run in full for the normal path, python being embedded and this being
exactly the kind of edit that looks like it did nothing without a rebuild.

---

### The project row stuck on "exporting…", and the await that widened a race until it hit

**Found 2026-08-17 by the product owner running the app** — the sixth defect in three
branches found that way and by nothing else. They added `FB_Alarm` in CODESYS; the automatic
export ran and finished cleanly ("exported 14 object(s) in 20.7s", `ExportSucceeded`,
`ProjectAuthoritative`) — and the project row said **"exporting…" forever**.

**The race was two refreshes finishing out of order.** `RefreshTreeAsync` captures its
sources — including each row's busy flag — at entry, then awaits before applying. A refresh
captured *mid-export* (busy, "exporting…") could therefore apply **after** the refresh that
saw the export complete, landing the stale snapshot last. And the signature gate then made it
permanent: the stale refresh had already recorded its signature as "shown", so every later
refresh with unchanged inputs returned early while the tree displayed the superseded build.

**The window was always there; the catalog-baseline work widened it from microseconds to a
git spawn.** Before, the only await between the gate and the apply was the `Task.Run` build —
milliseconds. The per-library HEAD fetch added `ShowAtHeadAsync` — a process spawn, tens of
milliseconds per changed catalog — into exactly that window, on exactly the refreshes that
follow an export (which is when the manifest and catalogs have just changed). The feature
that made the tree say *which library moved* is what let the export's completion lose the
race to its own beginning.

**Fixed with the pattern the codebase already had**: `DiffView.Rebuild`'s generation guard,
carried over with its reasoning — the last call to enter owns the tree, everything older
abandons at the checkpoint after the awaits — plus the second half `DiffView` never needed:
the signature is recorded only when a build is **applied**, because a signature claimed by a
refresh that then abandoned is a claim about something nobody is seeing.

**No deterministic regression test, stated rather than papered over.** The inversion needs
the older refresh's awaits to outlast the newer one's, which on the test dispatcher resolves
FIFO — a test of the interleaving would pass with the fix deleted on almost every run, and a
sometimes-red test is the suite's most expensive kind. The guard is structural, the pattern
is the one `DiffView` has carried untested for the same reason, and the reasoning sits on
the field, the entry and the checkpoint.

---

### The expansion default missed the correction the pruner already carried

**Found 2026-08-17, building the library reference rows** — PHASE2's first queue item, and the
first time a node that is not a folder acquired children whose *changes* mattered more than
their existence.

`TreeExpansion.DefaultFor` answered "open when there is a change under it" with
`node.IsFolder && node.HasChanges`. That rule was written when only folders had children. Then
manifest schema 4 nested members under their owners, and `PruneUnchanged` in the same builder
was corrected for exactly that — its remark says so: *tested on `Children` rather than on
`IsFolder`, because an object owns objects now*. The expansion default was the same rule in a
second place, and it was not corrected. Consequence: **an edited method inside a function
block, and now a bumped library version inside a manager, sat behind a closed chevron** with
only the roll-up badge hinting that anything was there — while a folder in the identical
situation opened itself.

Two things worth keeping from it:

- **When a correction lands, grep for the rule it corrects, not only the site it fixes.** The
  folder-only assumption existed in two methods; the fix in the member-nesting work found one.
  The same phrase — "an object owns objects now" — was already in the codebase naming the
  trap, one file away from a second instance of it.
- **The fallback and the default had quietly diverged.** `TreeRow`'s constructor falls back to
  `ChangedDescendants > 0` when no `TreeExpansion` is supplied — the corrected shape — so the
  behaviour differed depending on whether an expansion tracker happened to be attached, which
  is the kind of difference nothing ever looks at.

Fixed as `node.Children.Count > 0 && node.HasChanges`, with the reasoning moved into the
remark, and proven by mutation: reverting to the folder-only rule turns
`TreeRowLibraryTests.A_manager_with_a_bumped_library_opens_itself` red and nothing else.

---

### `import_native` duplicates an external-file object instead of replacing it

**Found 2026-08-17 by the first E2E run of E4** — the first time an `EXTERNAL_FILE` payload was
ever handed back to CODESYS — and G13 in the import plan's table. The type had existed in
Versio's vocabulary for about two hours.

The run exported the member fixture (which had just gained two external-file objects for
E2/E4), edited an ordinary POU, imported, and re-exported. The re-export's manifest carried
**two records for the long-named external file**: same name, same parent, the same normalized
hash — and different GUIDs, colliding on one mirror path. `import_native` with
`NativeImportResolve.replace`, which replaces every other native type in place (A5 measured
`replaced` on all seven), had **pasted a copy** of this one. Every import would have added
another: G7's shape — an unasked-for mutation per cycle — on a new type.

**The first failure of the run was a decoy, and the order mattered.** The initial assertion
compared bytes and failed; the "obvious" reading was P7's known GUID re-minting, so the
assertion was weakened to normalizer-equivalence — the repository's own definition of "the
same native thing". The second run then failed on `Assert.Single`, which is what actually
looked at the *manifest* rather than the file: the equivalence weakening had been correct in
general and about to paper over a duplication in particular, because two equivalent copies are
each equivalent to the original. **A masked comparison can only say "same content"; it takes a
count to say "same object".**

**Closed the same day, before the type ever shipped importable**: the form is
`EXTERNAL_NATIVE`, export-only — the third use of the shape this project invented for
`TASK_NATIVE` and used again for `GVL_NATIVE`, with one difference worth recording: there is
**no importable sibling**. An external-file object is IDE-managed entirely, its payload being
metadata (a `FileId` and a download flag) that nothing in a mirror could meaningfully edit.
The planner announces the skip with the verbatim recorded name and no sibling pointer; the
single-record assertion in the E2E test is the regression guard.

**The residual door, stated rather than hidden**: an *unrecorded* external-file `.xml` in a
mirror still goes to `import_native` — the planner's deliberate rule for unrecorded natives,
which cannot distinguish this type without reading payloads. That path requires a manifest
that does not know the file, arrives with a warning-free plan only in the teammate's-new-file
scenario, and duplicates at most once per unrecorded file rather than once per import. Closing
it would mean the planner sniffing payload types, which is the filename-walk's cousin; the
honest trade is recorded here instead.

---

### The probe that caught two of its own lies was carrying a third, and three documents believed it

**Found 2026-08-17 by reviewing the P11 probe's own output against its step list** — the run
whose results had gone into four documents the same morning. The finding below this one records two
method errors the probe's author caught and fixed mid-session: a by-index pairing that reported
a pass for a move that never happened, and an after-reopen check that reported a failure that
never happened. The fixed probe had a third error of exactly the same class, and this time the
output said so and nobody read that line.

**The two isolating steps shared one folder.** Step 8 moved `VersioMoveEmpty` into `Plc Logic`
and succeeded; step 9 then looked for the same folder at its *old* path under `Application` —
and reported **`OBJECT_NOT_AT_SOURCE`**, right there in the raw log, no call ever made. A step
whose source depends on an earlier step's *outcome* measures nothing when that outcome goes the
other way. So "nine ordered moves" was eight, and "an empty folder into another folder" was
never measured at all.

**The supersession check then compounded it.** It matched later steps by object name alone, so
step 8's after-reopen check was skipped as `SUPERSEDED` — in deference to a step that had run
nothing. The consequence: **"an empty folder into `Plc Logic` survives a reopen" was claimed in
the commit message, `DRIVING.md` and the plan's What-is-left row, and the probe had never
measured it.** The finding below was quietly more honest: its table omits "survives a reopen"
on exactly the two superseded rows, which is the tell that the writer had the raw output in
front of them and the claims elsewhere were written from memory.

**Fixed and re-run 2026-08-17**: step 9 gets its own folder (`VersioMoveEmpty2`), so neither
isolating step's source depends on the other's outcome, and a step supersedes only if it
actually **applied** — a declined or never-run step left the object where the earlier step put
it, which is the position the reopen check exists to read. The re-run answers everything:

- **Nine moves, nine calls, six performed, three declined** — the same three declined as before.
- **An empty folder into `Plc Logic` survives a save/close/reopen, GUID stable** — the claim is
  true; now it is also measured.
- **An empty folder into an ordinary folder is performed and survives a reopen** — a new row,
  and the corpus's first successful folder-into-folder move: until this run, every successful
  folder move in the record had `Plc Logic` as its destination.

**The lesson the first two errors taught was incomplete.** Both were found by re-reading the
method; this one was sitting in the *results* — one row of JSON saying `OBJECT_NOT_AT_SOURCE` —
and survived because the summary lists were read instead of the rows. A probe's verdict rows
are the measurement; its summary is an index into them. **Read every row whose verdict is not
the one the step was written to produce**, because a step that did not run looks exactly like a
step that was not needed.

---

### `move()` declines three moves of nine, raises for none, and the destination is not what decides

**Measured 2026-08-17 by P11**, the last open task in the import plan. Two results existed on
either side of a line nobody had drawn: P2 moved a POU into a folder and the GUID survived a
reopen; the feature checklist's D6 moved one out of `Application` into `Plc Logic` and CODESYS
accepted the call, raised nothing, and left the object where it was. Both true, and together
they said only that "Versio moves objects" was partly true.

Nine ordered moves on one project, each re-resolved at its intended path afterwards. *(This
table was corrected after the re-run the finding above records: the first run's ninth step
never executed, and the two rows marked with a date gained their reopen evidence then.)*

| What, and where to | Result |
|---|---|
| a POU into a folder under `Application` | **performed**, GUID intact |
| a POU into a folder inside that folder | **performed**, GUID intact, survives a reopen |
| a POU back out of a folder into `Application` | **performed**, GUID intact, survives a reopen |
| a GVL to **the project root** | **performed**, GUID intact, survives a reopen |
| an **empty folder** into `Plc Logic` | **performed**, GUID intact, survives a reopen *(measured on the re-run)* |
| an **empty folder** into an ordinary folder | **performed**, GUID intact, survives a reopen *(measured on the re-run — the first run never reached it)* |
| a visualisation into `Plc Logic` | accepted, **ignored** |
| a POU into `Device` | accepted, **ignored** |
| a folder **holding a POU** into `Plc Logic` | accepted, **ignored** |

**Not one of the calls raised.** `move()` returns `None` whether it moved the object or ignored
the instruction, so there is no return value to inspect and no exception to catch. The read-back
the rename phase does is not defence in depth; it is the only signal that exists.

**THE DESTINATION IS NOT WHAT DECIDES, and this document said it was for about an hour.** The
first version of the probe ran seven steps, and every one of them agreed with a tidy rule:
folders, `Application` and the project root work, `Plc Logic` and `Device` do not. That rule went
into `versio_import.py`'s warning, into this file, into `DRIVING.md` and into the plan.

**What broke it was a question about the probe rather than about CODESYS.** Re-reading the steps
as if somebody else had written them: the folder moved into `Plc Logic` had children, so a
refusal could have been about the destination *or* about what was inside. Two more steps isolated
it — and an **empty** folder goes into `Plc Logic` perfectly well. So the same destination accepts
one folder and refuses another, and what is being moved matters as much as where it is going. It
reads like "the destination must be able to hold the whole subtree", but that is an inference
from eight rows and nothing in the product relies on it.

**The lesson is not about moves.** A measurement with one variable per row is a measurement; one
where two things differ at once is a story that fits. Seven rows all agreed with a rule that was
wrong, and no amount of re-reading the *results* would have shown it — only re-reading the
*method*.

**What changed in the product is one sentence.** The behaviour was already right —
`RENAME_NOT_APPLIED`, named object, named path, nothing claimed. What it could not do was tell
the person what to do next, so the warning now names the destinations that work and says to move
it in the IDE otherwise. `A_POU_moved_to_another_container_is_moved_or_reported_not_claimed`
asserts the sentence in the warning rather than in the script's source, because it has to survive
the trip through the script, the protocol, the runner and `SyncOutcome` — and the mutation was
run: suppressing the explanation turns that test red.

**The first version of this probe reported a pass for a move that never happened.** It paired
movers with destinations *by index*, and the third pair came out as `Glob_Var` into
`Application` — already its parent. The read-back found the object at the destination and the row
read `applied: true`. Nobody chose that pair; the loop did. **A test whose cases are generated by
zipping two lists has cases nobody has read**, and the fix was to write the steps out as an
ordered list and to ask `already_there` *before* the call rather than infer success after it.

**And its second version reported a failure that never happened**, one line further on: the
after-reopen check looked for `Simulation` in the folder step 1 had moved it into, and step 2 had
moved it back out. `found_after_reopen: false` reads as "a move into a folder does not persist",
which contradicts P2 and would have been a good half-hour. An absence has to be attributed before
it is reported — the probe names the later step now and says `SUPERSEDED`.

---

### A managed library reference recorded whichever version this machine had installed

**Found 2026-08-17 by calling the API rather than reading it** — G8, the last gap in the import
plan, and the answer is not the one the plan expected.

`versio_libraries.py` shapes each library reference into the mirror's YAML. Its managed
(fixed-version) half had **never executed**: both fixtures are placeholders end to end, 13 in the
project's manager and 21 in the application's, 34 in total. The branch was written from the API's
description and said so in a comment, which is the honest version of untested — but the plan then
reasoned from a probe that had measured the wrong half, and concluded G8 was half a day of
fixture work.

**What P9 had actually measured.** `add_library("Standard, 3.5.17.0 (System)")` works, takes one
argument, and yields a reference reading `is_managed: true`. That is where the first run stopped.
It never enumerated the managed type's members, and the exporter does not read the reference: it
reads `ref.managed_library` and then `.version`, `.company` and `.title` off *that*. Four
spellings in this API have already been wrong. This one had never been called.

**Two runs, and the second is the one that mattered.**

| Called on a `ScriptManagedLibraryReference` | 3.5.17.0 — **not installed here** | 3.5.22.0 — installed |
|---|---|---|
| `is_managed` | `True` | `True` |
| `managed_library` | the object exists | the object exists |
| `.version` | **`NullReferenceException`** | `"3.5.22.0"` |
| `.company` / `.title` | **`NullReferenceException`** | `"System"` / `"Standard"` |

So the branch had two faults, and the second is worse than the first.

**It failed silently.** The fields were read through `safe()`, the exception-swallowing probe, so
a reference to a library this machine does not have was written with **no version, no company and
no title, and no warning** — indistinguishable in the YAML from a placeholder that never had one.
The record was `{name, namespace, flags}` and the export said nothing.

**And had it worked, it would have recorded the wrong thing.** `managed_library` describes the
library as *installed on the exporting machine*. That is precisely what this file's own header
refuses to record — it lists `effective_resolution` and the resolver as "not recorded, on
purpose: that describes which libraries are installed here, not what the project asks for, so it
would differ between two engineers who share this repository and neither would be wrong". The
managed branch was breaking that rule three fields at a time, and it is **G9's defect through a
third door**: a machine-dependent value written into a committed file churns the mirror for
whoever is configured differently.

**The fix takes the version from the project instead.** A managed reference's own `name` is
`"Title, Version (Company)"` — measured byte-identical to `managed_library.displayname` — and it
is project state, present whether or not anything is installed. `parse_managed_name` splits it,
`is_managed` is the discriminator rather than "did some other property raise", and a name that is
not that shape yields **nothing** plus `LIBRARY_MANAGED_NAME_UNPARSED` rather than a plausible
guess. The asymmetry is the point: an absent version is a gap a reader can see, a wrong one is a
diff somebody acts on.

**Proven by running the shipping module inside CODESYS**, against a live reference, which is the
one thing a fake cannot do:

```
product_record = {"name": "Standard, 3.5.22.0 (System)", "namespace": "Standard",
                  "version": "3.5.22.0", "company": "System", "title": "Standard",
                  "qualifiedOnly": true, "systemLibrary": false, ...}
product_record_errors = []
```

with the placeholder beside it in the same run still producing `placeholder`,
`defaultResolution` and `redirection` and no managed keys.

**The regression half is 20 CPython tests** over the real module — `versio_libraries.py` imports
no `scriptengine` and takes its objects and its `safe` probe as arguments, which is what makes
that possible. The fakes are shaped from the probe's output, including which attributes are
absent and which raise, so they assert a measurement rather than the test's own assumptions.

**Five mutations were run rather than reasoned about, and one of them found a hole in the
corpus.** Restoring the `managed_library` read, silencing the unparsed warning, letting a
placeholder fall into the managed branch, and deleting the unclassified branch each turned
exactly one test red. Loosening the version check — "nothing but digits and dots" — turned
**nothing** red, because every refused name in the list failed the *other* rule too ("at least
one digit"). `3.5.22.0-beta` and `1.0a` are in the corpus now, and they are the only two cases
that watch that line.

**The fifth mutation is worth its own sentence, because it nearly did not happen.** The first
four were written down while the branch was being built; the unclassified path got none, on the
unspoken grounds that nothing produces such a reference — which is exactly the argument that
leaves a rule untested. It was mutated last and turned its test red, so it is watched. A
mutation list assembled from "what I changed" misses the branch nobody expects to run, and that
branch is always the one that will run first in front of a customer.

**One thing this cost, worth keeping**: `hide_when_referenced_as_dependency` is documented here
and in `DRIVING.md` as "raises on all 34 references... simply not readable on this version". It
raises because **CODESYS misspells it**: the member is `hide_when_referenced_as_depencency`, one
`d` short, and it reads `False` under that name. Still not recorded — it is a display hint — but
recorded as a *choice* now, because "we could not read it" invites somebody to retry and "we
chose not to" does not.

---

### Four tool scripts pointed one directory above where they now live

**Found 2026-08-17 on the first attempt to run one**, immediately after the structure audit moved
`tools\*.ps1` into `tools\codesys\`.

Every one of them derived the repository root as `Split-Path -Parent $PSScriptRoot`, which was
true while they lived in `tools\` and became `C:\GIT\Versio\tools` the moment they moved. Six
default parameters across four files, so `Probe-ImportApi.ps1` threw `project not found:
C:\GIT\Versio\tools\fixtures\projects\RefrigeratorControl.project` before doing anything.
Two of them also still read the python out of `src\Versio.Scripts.Codesys`, which the same audit
had renamed to `src\Versio.Plc.Codesys.Infrastructure\Scripts`.

**The comment beside two of the six says "derived from the script's own location so the tools work
from any clone path"** — the right idea, and exactly what broke: the location moved and the count
of levels did not.

**Nothing was watching, and the C# half shows why that is not inevitable.** The same move broke
three test paths, and they were fixed by `TestPaths.Scripts` — one place, whose remarks say "three
tests hardcoded `src/Versio.Scripts.Codesys`, and all three broke together when the scripts moved".
The PowerShell has no `TestPaths`, no compiler and no suite; it is reached only by a person typing
the command, and two of these four are named in the import plan's own Verification list as
acceptance gates — `Measure-ImportStability.ps1` is how `P7: SETTLES` is checked at all.

All six defaults now go up two levels and both script paths name the vendor pack.

**And a second pass found a fifth breakage of a different shape**, which is the part worth
keeping. `Compare-Exports.ps1` is vendor-**neutral** and correctly stayed in `tools\` while the
CODESYS harnesses moved down into `tools\codesys\` — so `& "$tools\Compare-Exports.ps1"` in
both of them now names a file that is not there. Grepping for `$PSScriptRoot` found the six
defaults and could not have found this one: it is a *sibling* reference that stopped being a
sibling. It is also the **last line of both scripts** — the one that prints the verdict — so
each would have run its three CODESYS launches, spent about six minutes, and then failed with
nothing to show for it.

**The repair of that repair is its own small lesson.** Writing `"$repoRoot\tools\..."` through
a python-in-bash heredoc delivered `"$repoRoot<TAB>ools\..."` — `CLAUDE.md` says not to do
this, in those words, and the result is still valid PowerShell holding a path nobody could read
in an error message. Written to a file and run, the way the rule says, it was right the first
time.

**Verified rather than assumed**: all four scripts parse, the derived repository root is
`C:\GIT\Versio`, and every path each one builds from it — the fixture, `phase0\p7`,
`phase0\logs`, `phase0\roundtrip`, the vendor pack's `Scripts`, `fixtures\equivalence` and
`Compare-Exports.ps1` — exists. `Probe-ImportApi.ps1` then ran three times against real
CODESYS, which is what closed G8.

---

### The ignore list hid the projects and kept every one of their mirrors

**Found 2026-08-17 by opening the app**, which is the only thing that could have found it.

Launching Versio against this repository has always adopted the 22 `.project` files under
`phase0/` — gitignored experiment scratch — taking the machine-wide CODESYS lock, queueing
twenty minutes of exports, and rewriting `.gitattributes`, `.gitignore`, `.versio/repo.json` and
any committed fixture mirror it reached. It was logged as a Known loose end for days and cost the
schema-7 backward-compatibility fixture **twice** in one session.

**`repo.json` has had an `ignore` array the whole time**, honoured by `Adoption` and
`OrphanScanner`, and it was empty. Adding `phase0/**` stopped the adoption dead: 22 projects
adopted became 1 watched, and the export queue emptied.

**And the panel still said 43 things needed attention.**

```csharp
var orphanExports = exportsOnDisk.Where(e => !boundExports.Contains(e)).ToList();
```

**The ignore list was applied to orphaned projects and not to orphaned export trees.** That is
half a rule, and half is worse than none here: the projects vanished from the panel and every one
of their mirrors stayed, one finding each — and a directory of round-trip experiments has a
mirror for *every* project in it. Ignoring 21 projects removed 13 findings and left 43.

| | Findings |
|---|---|
| Before | 56 |
| After `phase0/**` | 43 |
| After the scanner fix | **1** |
| After ignoring the frozen fixture's mirror as well as its file | **0** |

**The last one is worth its own note.** `fixtures/projects/MachineWithMembers.project` is
deliberately frozen at manifest schema 7 as the only committed pre-schema-8 artifact, so it is
ignored — but the glob matched the *file* and not the *directory beside it*, and the mirror was
reported as an orphaned export. Both are listed explicitly now rather than covered by a wildcard,
because a reader should be able to see exactly what is frozen.

**Nothing in the suite could have caught the scanner half.** `Ignored_paths_are_not_offered`
existed and passed — it only ever supplied `ProjectFiles`, so the export path was never exercised
with an ignore list at all. A test that covers one half of a rule reads exactly like a test that
covers the rule.

**This is the third time this session that running the app found something no test did**, and the
first two were cosmetic by comparison. `CLAUDE.md` says tests prove things resolve, lay out and
are reachable, and do not prove the app behaves — this is that, with a number attached.

### The guard I wrote for the seam was weaker than the claim I made for it

**Found 2026-08-17, in a second pass over this session's own work, by mutation.**
`DependencyRuleTests` asserted that `Versio.Core` and `Versio.Infrastructure` reference no vendor
pack, using `Assembly.GetReferencedAssemblies()`. Adding
`<ProjectReference Include="Versio.Plc.Codesys">` to `Versio.Infrastructure` and rebuilding left
**every one of those assertions green**.

**The C# compiler drops assembly references the code does not use.** So
`GetReferencedAssemblies()` reports what the compiler *kept*, not what the project *declares* —
which means the test answers "does this assembly actually depend on that one", and the commit
message claimed it answered "is there a project reference". Those are different questions, and
the weaker one is satisfied by a door that is open but not yet walked through.

`ProjectReferenceTests` reads the `.csproj` files and asserts the declarations directly. Both
checks stay, because both questions are worth asking: metadata catches real coupling, the project
files catch the door being opened — which is what somebody does immediately before writing the
code that would use it.

**Reading source is normally the wrong instrument here** and it is worth saying why this is the
exception. The vendor seam scanner deliberately reads IL rather than source, so that a brand in a
comment is not a leak. A project file is different: it *is* the declaration, there is nothing to
interpret, and no compiler step stands between what it says and what it means.

**Two things about how this was found.** It was found by mutation and by nothing else — the suite
was green, the claim was wrong, and only deliberately breaking the thing revealed the gap. And it
was found in a pass over work from the same session, which is the argument for that pass: the
three commits that built and used this guard all read as correct, and one of them was overselling
what it had.

**Also removed in the same pass: `CodesysPack.Unregister()`, which had zero callers.** It was
written with a docstring inventing a user for it — "exists so a test can return to a known state"
— on the same branch whose opening finding was that `PlcFormats.Register` had no callers while
three documents described registration as the architecture. Writing that defect twice in one
week, once while documenting it, is worth recording rather than quietly deleting.

### A `git mv` moves a test; it does not move what the test reads

**Found 2026-08-17 by the Phase D gate, and only by the gate.** Splitting the suite into a fast
project and a CODESYS one left five tests failing — and every one of them failed in the
**32-minute** run rather than the eleven-second one, which is the worst place to learn it.

Two separate breakages, same shape:

- **`versio_types.py` and `versio_text.py` were copied to the output directory by an item in
  `Versio.Core.Tests.csproj`.** `TypeTableParityTests` moved to `Versio.Plc.Codesys.Tests`; the
  `<None Include … CopyToOutputDirectory>` did not. The tests looked for a file beside themselves
  and there wasn't one.
- **`New-ImportPlan.ps1` moved to `tools/codesys/`**, and the sweep that repointed every literal
  reference to the seven moved tools missed this one because the path is *built* —
  `Path.Combine(TestPaths.Root, "tools", "New-ImportPlan.ps1")` — rather than written as a
  string. A textual sweep cannot see a path that does not exist textually.

**The process failure is the interesting half.** After creating the new project I confirmed it
*discovered* 365 tests with `--list-tests` and moved on, because running it meant half an hour.
Discovery proves the project compiles and the classes are found. It proves nothing about whether
they pass. **308 of those 365 need no CODESYS at all** and run in thirteen seconds — that filter
existed the whole time and would have caught both breakages immediately.

So the lesson is not "be careful when moving files". It is: **a new test project needs its fast
subset run the moment it exists**, before anything else is built on top of it. Two more phases
went in on top of an unverified split.

**And the guard for the second one is the same class of thing that has now bitten three times
this session** — F1's markdown anchors, D3's cross-document links, and this. A reference that is
assembled rather than written down is invisible to every check that reads text.

### The architecture tests PLAN promised for months had never been written

**Found 2026-08-16, built 2026-08-17.** `docs/PLAN.md` §12.2 is headed **"Dependency rules —
enforced, not documented"** and says `Versio.Arch.Tests` "fails the build if any of these are
violated". It lists five rules. **The project had never existed.** Its only trace anywhere in the
repository was a comment in `Versio.Core.csproj` citing it by name.

| Rule | Was it true? | Now |
|---|---|---|
| 1. Core references no UI, no Infrastructure | yes, by the csproj | enforced, plus vendor packs |
| 2. No `File`/`Directory`/`Process`/`DateTime.Now`/`Guid.NewGuid` in Core | **four yes, one no** | enforced, with one named exemption |
| 3. `App.Views` must not reference Infrastructure | yes | enforced |
| 4. No literal colour or margin in `Views/` | untested | **not enforced, and now says so** |
| 5. Every `public async` in Core takes a `CancellationToken` | yes | enforced |

**Four rules held by discipline alone**, which is the thing worth noticing: the codebase was
obeying an unenforced rule for months, and the one place it slipped is the one nobody could see.

**`Guid.NewGuid` is exempted rather than fixed, and the exemption is pinned.** It produces the
default operation id that correlates a run's log lines, and every one of those is an `init`
property a caller can set — so identity *is* controllable exactly as the rule intends, without an
`IIdentityProvider` nobody would inject anywhere else. The two permitted call sites are named, and
a third appearing fails the build. The alternative was to leave the document saying one thing and
the code doing another, which is how this section came to claim enforcement that did not exist.

**Rule 4 is recorded as unenforced rather than quietly dropped.** It is a lint over markup rather
than a dependency rule over metadata, and `StockControlStyleTests` and `FocusVisualTests` already
catch the failure it exists to prevent — by rendering the controls and reading the result, which
is stronger than a source scan. Worth building only if that stops being true.

**Read from IL, with a positive control.** Rule 2 walks the assembly's own `call` and `callvirt`
sites, because a forbidden call appears in no signature. Every assertion in the file is "this set
is empty", which a scanner that reads nothing satisfies perfectly — so one test asserts the scan
finds something, and adding a `File.Exists` and a `DateTimeOffset.Now` to `Versio.Core` takes two
of them red.

**The same failure shape as the rolling file sink**, which is why it is written up rather than
just fixed: every part of the mechanism present except the one that makes it visible. A rule
nothing checks has already drifted. A rule nothing checks that a document *claims* is checked
also spends the reader's trust.

### The tag that split the suite was doing a project boundary's job

**Changed 2026-08-17.** One integration project held both a 19-second suite and a half-hour one,
separated by `[Trait("Toolchain", "Codesys")]` and a `--filter "Toolchain!=Codesys"` expression
every reader had to remember. `FastLaneTests` existed solely to assert — in both directions —
that the tag had been applied, because *"a filter that silently stops excluding things is worse
than no filter"*.

That reasoning was right and the mechanism was wrong. A tag cannot stop somebody typing
`dotnet test tests\Versio.Integration.Tests` and waiting half an hour, it puts the slow suite's
dependencies on the fast suite's project, and it needs a test whose entire job is guarding the
tag.

| | Before | After |
|---|---|---|
| Fast, no toolchain | 192 of 243, behind a filter | `Versio.Integration.Tests`, **141 in 10 s**, no filter |
| Needs real CODESYS | 51, same project | `Versio.Plc.Codesys.Tests`, **365**, ~31 min |
| Guard | `FastLaneTests` | the project graph |

**`FastLaneTests` is deleted, and its argument moved into the new project's own file header**
rather than being lost — the rule it protected still matters, it is just enforced by something
that cannot be forgotten.

**The CODESYS unit tests went with it.** Nine fast classes under `Versio.Core.Tests/Plc/Codesys/`
were the last thing keeping the neutral unit suite aware of a vendor; `Versio.Core.Tests` drops
from 1,534 to 1,295. A vendor's tests now arrive and leave with the vendor, which is the same
property C1 gave its production code.

**And the whole suite is faster to work in.** The loop is `Versio.Core.Tests` plus
`Versio.Integration.Tests` — **eleven seconds together**, against eighteen before, with nothing
to remember and nothing to type wrong.

### The engine reached for a global registry, and no seam test could see it

**Fixed 2026-08-17.** `SyncOrchestrator` — the file the whole vendor rule exists to protect —
called `PlcFormats.ForPath` directly, twice. That is a **static, mutable, process-wide**
registry, so the engine's behaviour depended on ambient state no caller could see and no test
could set.

**Invisible to every mechanical check.** A static call puts no vendor type in a signature, so
the type checks pass; it names no brand, so the constant and literal checks pass; and it lives
in a neutral namespace, so nothing about it looks wrong. The only thing that finds it is reading
the file and asking where that answer comes from.

**Not a theoretical hazard — it cost twice this week.** Two tests passed alone and failed in
company because one class registered a stand-in vendor into that registry while another was
reading it. xunit runs test classes in parallel, and a static registry is shared by all of them.
An engine that is *handed* its resolver is independent of whatever else is running.

Injected with a default, the same shape `RepoScanner`, `RepoSetup` and `ProjectOccupancy` already
use, so no existing caller moved. `SyncViewModel` — the composition root's agent — now passes it
explicitly, and the orchestrator harness can override it per test.

**Pinned by the one assertion that could catch it.** The test hands in a resolver that claims
nothing, asserts no plan is produced, *and* asserts that `PlcFormats.ForPath` would have claimed
the same path — so it fails if the engine ever quietly goes back to the registry. Making
`_formats` ignore its argument takes it red.

**What was deliberately left alone.** `RepoSetup`, `ObjectTypeNames` and `SyncViewModel` still
default to the registry. They are called from the composition root or are pure display helpers,
and the registry is populated by exactly one line in `App.xaml.cs` — so the ambient reach that
mattered was the engine's, and that one is closed.

### The vendor seam is a project boundary now, not a namespace and a reflection test

**Done 2026-08-17.** CODESYS lived in the namespace `Versio.Core.Plc.Codesys` *inside*
`Versio.Core`, and in `Versio.Infrastructure.Plc.Codesys` inside `Versio.Infrastructure`, with
its python in a loose `src/Versio.Scripts.Codesys/` folder no project owned. So the central
architectural rule — *nothing outside the vendor's own corner may know about CODESYS* — was
checkable only by a test reading namespace strings.

**A reflection test cannot see what a compiler sees.** Over three commits it had to be taught,
separately, about string constants, about literals compiled into method bodies (including inside
lambdas), and about types hiding in generic arguments — and it still could not have caught B3's
leak, where a neutral namespace declared one vendor's *vocabulary* as though it were everybody's.
Each of those was a real leak sitting under a green suite.

`Versio.Core` and `Versio.Infrastructure` now have **no project reference** to
`Versio.Plc.Codesys` or `Versio.Plc.Codesys.Infrastructure`. A leak is a build failure.

| | |
|---|---|
| Moved | 12 pure files, 5 infrastructure files, 12 python modules |
| Namespaces | `Versio.Core.Plc.Codesys` → `Versio.Plc.Codesys`; `Versio.Infrastructure.Plc.Codesys` → `Versio.Plc.Codesys.Infrastructure` |
| Files touched by the rename | 54 |
| `Versio.Core` references to a pack | **0** |

**The namespaces were renamed rather than kept.** A namespace called `Versio.Core.*` living in a
different assembly misleads every reader and every namespace-based tool — including the seam
scanner, which filters by namespace and would have been quietly asserting about the wrong thing.

**Two namespaces joined the neutral list**, which is the visible payoff: `Versio.Core.Plc` and
`Versio.Infrastructure.Plc` are the namespaces that *define* the seam, and until today each one
crossed it — `PlcFormats` held `[CodesysProjectFormat.Instance]` and `PlcToolchains` a factory
naming CODESYS. `Versio.Core.Plc` is checked with one deliberate exception, asserted rather than
exempted: it names **exactly** the four well-known vendors, so a fifth static appearing there
fails just as loudly as a first brand appearing anywhere else.

**What made it cheap, and it was measured before starting rather than hoped for.** Only two
compile-time references crossed the seam in all of `src` — `PlcFormats.cs:23` and
`PlcToolchains.cs:29` — and both were lines C2 deleted anyway. `ScriptExtractor` resolves through
`typeof(ScriptExtractor).Assembly` and filters on the `.py` suffix rather than a hardcoded
resource prefix, so the code and its resources moved together with **no code change at all**.

**The ordering in the plan was wrong, and the dependency graph said so.** C1 was to move the
files and C2 to remove the hardcoded registrations. That cannot work: while `PlcFormats`
references `CodesysProjectFormat`, moving that type out makes `Versio.Core` depend on a project
that depends on `Versio.Core`. C2 ran first.

**The python folder finally has an owner.** `src/Versio.Scripts.Codesys/` was in no solution, so
nothing built it, nothing showed it in an IDE, and its documented rebuild trap — edit a `.py`,
run the suite without building, and test the copy embedded at the *last* build — followed
directly from having no project identity.

**And nothing fast was watching the embedding.** Every test that touches `ScriptExtractor` is
CODESYS-tagged, so a broken `EmbeddedResource` glob would have left the 19-second lane entirely
green and surfaced only in a 31-minute run, as CODESYS failing to start over a missing script.
`EmbeddedScriptTests` now asserts all twelve are present, in milliseconds, and narrowing the glob
to one file takes it red.

### The registries were constants with a mutator bolted on

**Found 2026-08-16, fixed 2026-08-17.** `PlcFormats` held
`[CodesysProjectFormat.Instance]` in a field initialiser and `PlcToolchains` held
`[PlcVendor.Codesys] = log => new CodesysToolchain(log)` in another. So `Versio.Core` — the
assembly the architectural rule exists to keep free of vendor knowledge — **named a vendor in
its own source**, and "adding a vendor means registering it" described something nothing did.

**This is what was blocking the assembly split, and the discovery reordered the plan.** While
`PlcFormats` referenced `CodesysProjectFormat`, moving that type into its own project would have
made `Versio.Core` depend on a project that depends on `Versio.Core`. The reference had to go
*before* the code could move — so C2 runs before C1, not after it as planned.

Both registries start empty. `CodesysPack.Register()` puts CODESYS in, and **the application's
startup is the one line in the whole build that names a toolchain**:

```
00:34:10.813 INF [App] vendors registered: CODESYS 3.5
```

**Explicit rather than a module initialiser, in production.** A module initialiser runs when the
runtime happens to load the assembly, which is lazy and not guaranteed to happen before
something reads `PlcFormats.All` — so the set of vendors would depend on load order, the least
debuggable kind of dependency. In the *test* assemblies it is exactly right, because the test
host loads them to find the tests, and each of the three declares its own.

**Proven load-bearing rather than assumed.** Disabling the registration in `Versio.Core.Tests`
takes **13 tests red**. Registration is now the only route in.

**And running the app cost the fixture that had been preserved an hour earlier.** Launching
`Versio.exe` reopened this repository from its recent list, adopted the 22 `phase0/` scratch
projects — the hazard already logged under *Known loose ends* — and began exporting them. It
rewrote `.gitattributes`, `.gitignore`, `.versio/repo.json`, and
`fixtures/projects/MachineWithMembers/_manifest.json`, which B2 had **deliberately** left at
schema 7 as the only committed pre-schema-8 artifact in the repository. All four reverted.
The loose end is now sharp enough to be worth closing: the next person to follow `CLAUDE.md`'s
"run the app" step against this repository will destroy that fixture without noticing, and
nothing will say so.

### One table held two vocabularies, and the next vendor would have inherited both

**Found 2026-08-16.** `ObjectTypeNames` was thirty entries in `Versio.Core.Projects` — a neutral
namespace — and read as one domain vocabulary. It is two. About a quarter is IEC 61131-3:
`POU`, `ACTION`, `TRANSITION`, `METHOD`, `PROPERTY`, `INTERFACE`, `TASK`. The rest is one
vendor's model of its own project tree: `PLC_LOGIC`, `APPLICATION`, `CALL_TO_POU`,
`LIBRARY_MANAGER`, `TASK_CONFIGURATION`, and four kinds of visualisation.

**The seam test could never have caught it, and that is the point.** No CODESYS type appears in
a signature; no brand appears in a string literal — `WEB_VISUALISATION` names no vendor. The leak
is that a *neutral* namespace declares one toolchain's words as though they were everybody's, and
the only thing that detects that is somebody reading the table and asking whose words these are.

**Split three ways, asked in order:** the vendor that wrote the manifest, then
`IecObjectTypeNames`, then the raw string. Falling through to IEC rather than straight to raw is
deliberate — the standard's words are the ones a second vendor is most likely to share, so a
vendor implementing `DisplayNameFor` as `return null` renders `POU` and `TRANSITION` correctly
for free.

**Asked of `IPlcProjectFormat` rather than of a second registry.** The format object is already
what a vendor registers and already carries knowledge of this kind — the export-path convention,
the template rule, which files a plan needs text for. One registry, asked a new question.

**Two entries are worth arguing with, and they went to the vendor side on purpose.** `DUT` and
`GVL` read as universal PLC vocabulary and are not: IEC 61131-3 says "derived data type" and
`VAR_GLOBAL`, and both abbreviations are CODESYS's. Anything genuinely ambiguous went the same
way, because a term wrongly called standard is one the next vendor inherits and cannot get out
of — the more expensive of the two mistakes.

**The collision is now survivable and tested.** The stand-in vendor claims `PLC_LOGIC` too, with
a different word, and both render correctly. Before the split the second vendor to arrive had to
accept the first one's word or overwrite it for everybody.

**And a race this same session predicted, then walked into.** `An_indexed_object_is_labelled_in_its_own_manifests_vocabulary`
was written using `PlcVendor.TwinCat`. It **passed alone and failed in company** — the tell this
document already records twice — because `SecondVendorTests` registers a fake TwinCAT format into
the *static* `PlcFormats` and xunit runs test classes in parallel, so for the moment that
registration exists every other class sees it. The label came back as the stand-in's word instead
of the raw string. A3's own commit message had named this hazard a few hours earlier.

- **Fixed by naming a vendor nobody else registers**, with the reason written where the next
  person will hit it. Phase C removes the ambient static and the whole class of problem with it.
- **It is the third sighting of the same tell in this repository.** A test that passes alone and
  fails in company is not being slow; something alongside it is still running — or, here, still
  registered.

**A near-vacuous pass in the same shape as schema 8's.** `ObjectIndex` copies the vendor from the
manifest onto each object, and `PlcVendor.Codesys` is the default — so had that copy been
deleted, every other assertion in `ObjectIndexTests` would have gone on passing. One test fails,
and it is the only proof the wiring exists.

### The manifest could not say who wrote it, and its vocabulary belongs to somebody

**Found 2026-08-16.** `_manifest.json` was schema-versioned seven times over and carried no
vendor. Two vendors' mirrors were indistinguishable on disk — which matters more than it sounds,
because `ProjectObject.Type` holds a *toolchain's own tree vocabulary*. `PLC_LOGIC`,
`CALL_TO_POU`, `GVL_PERSISTENT` and `LIBRARY_MANAGER` are CODESYS's words, not IEC 61131-3's, and
a reader that has to guess whose words it is holding cannot render them without guessing too.

**Schema 8 adds `vendor`, and absent reads as CODESYS** — a statement of fact rather than a
guess, because every mirror written before the field existed was written by CODESYS, since
CODESYS was all there was. The same backward-compatible shape `ProjectBinding.Vendor` already
uses.

**Two spellings of one vendor already existed in shipped bytes.** `versio_libraries.py` and
`versio_tasks.py` have each written `"vendor": "codesys"` — lower case, and the constant is
*duplicated* in both modules — into every exported sidecar since they were added. `repo.json`
writes `"Codesys"`. Those sidecar bytes are committed in every mirror and rewriting them would
churn a file in every repository over a capital letter, so the manifest matches `repo.json` and
the two spellings are allowed to coexist. **`PlcVendor` comparing case-insensitively is what
makes that safe** — a decision taken the same day for a different reason, which turned out to be
load-bearing for an inconsistency nobody had noticed.

**The fixture had to be regenerated, and the test that forced it was doing its job.**
`WorkspaceTreeFromCommittedExportTests` asserts the committed mirror's schema equals
`ExportCoverage.CurrentSchema`, and went red the moment the constant moved. Its own comment
explains why that assertion exists: *"a stale fixture makes a true assertion protect a dead
specification, and nothing about it looks stale from here."* A real export regenerated it in
23.9 s.

**The diff was exactly two lines** — `"schema": 7` → `8`, and one added `"vendor": "Codesys"` —
across a mirror of 18 objects. Every other byte identical, which is independent evidence that
the exporter is deterministic across runs and that the change was surgical.

**`MachineWithMembers` was deliberately left at schema 7.** Nothing asserts its schema, so
regenerating it would have been free and would have quietly destroyed the only committed
pre-schema-8 artifact in the repository — the one file that exercises the backward-compatible
reading against something nobody wrote for the purpose. `CommittedManifestVendorTests` now says
so, so a future regeneration is a decision rather than a side effect.

**A near-vacuous pass, caught by mutation.** With `Parse` changed to ignore the field entirely,
*every* test asserting a CODESYS manifest still passed — because the default is CODESYS. The
only assertion that failed was the one reading a vendor this build has never heard of. Every
"does it read Codesys" test in this area is satisfied by the default, and that one test is the
whole proof the field is read at all.

### `PlcVendor` was a closed enum, so you could not name what you were told to register

**Found 2026-08-16, and it is the one that made the architectural rule literally false.** The
README, `CLAUDE.md` and both registries all say the same thing: adding a vendor means
implementing `IPlcRunner`, `IPlcProjectFormat` and `IPlcToolchain` and registering them, and
**nothing in `Sync/`, `Git/`, `Locking/`, `Watching/`, `Projects/` or `Diff/` may change**.

`PlcVendor` was an `enum` declared in `Versio.Core`. Both registries are keyed by it,
`ProjectBinding` records it, and every format returns one. So a vendor that was not already in
the list could not be *named* — and you cannot register what you cannot name. Adding a fifth
vendor meant editing `Versio.Core`, the one assembly the rule exists to keep untouched.

**It bites the fifth vendor, not the second**, because the enum already declared TwinCAT, TIA
Portal and Studio 5000 for exactly this reason. That is why the audit's prediction that
`SecondVendorTests` would fail to compile was wrong, and why this stayed invisible: the escape
hatch for the next two vendors was already cut, so nothing was going to notice until somebody
brought a toolchain nobody had thought of.

**Now a `readonly struct` wrapping a name**, with the four well-known vendors kept as statics —
`VendorSeamTests` still asserts they are all present, so nobody tidies the list down to the one
that works — and `PlcVendor.Known` as the shipped list rather than a limit.

Three properties had to hold at once for that to be safe, and each is pinned:

- **The bytes do not move.** Every committed `repo.json` in every clone holds
  `"vendor": "Codesys"`, written when this was an enum with `JsonStringEnumConverter`. A struct
  serialises as an object with a `Name` field unless something says otherwise, which would have
  rewritten every binding into a shape older versions cannot read. A `JsonConverter` on the type
  — not bolted onto one serializer's options, so every future reader gets it too.
- **Comparison is case-insensitive.** The name round-trips through a committed, hand-editable
  file, and a binding that stopped matching its toolchain over the case of one letter is a very
  confusing way to lose an export.
- **`default` no longer claims a vendor.** As an enum, `default` was ordinal 0, which was
  `Codesys` — an uninitialised field silently asserted a brand. It is now empty, with
  `IsUnknown` to ask. The *backward-compatibility* promise is carried by `ProjectBinding.Vendor`'s
  initialiser instead, which is the right place for it: a binding written before the field
  existed genuinely was CODESYS.

**A hazard was removed rather than mitigated.** `RepoConfigStore` carried a comment arguing
against ordinal serialisation because "inserting a vendor into `PlcVendor` — alphabetically, say
— would silently repoint every existing binding at a different toolchain, and the first sign
would be an export driven by the wrong tool". There is no ordinal left to shift.

**And the guard for all of this was behind a 42-minute gate.** The only test asserting
`"vendor": "Codesys"` in written bytes lives in `AdoptAndExportTests`, which drives real CODESYS.
The property has nothing to do with CODESYS — it is that a committed file keeps meaning what it
meant — so `RepoConfigVendorTests` now covers it in the fast lane in milliseconds, including a
`repo.json` written before vendors existed and one naming a vendor this build has never heard of.

Mutation-checked by deleting the converter attribute: four tests across two assemblies went red.

### The seam test passed for a year because a string is not a signature

**Found 2026-08-16, in a structural audit of the repository.** `VendorSeamTests` was written
because the central architectural rule — nothing outside `*.Plc.Codesys` may know about
CODESYS — had been stated from the start and was not true. It reads assembly metadata so the
next leak fails a build. It was green. Three leaks were in the tree the whole time.

| Where | What |
|---|---|
| `CommitGate` | "no CODESYS projects are tracked in this repository" |
| `OrphanScanner` | "untracked CODESYS project — track it to start exporting" |
| `LogCategory` | `Codesys = "Codesys"`, in a table serving every vendor |

**They survived because the test read fields, properties, parameters and returns.** By the time
it was written every leak of *that* kind had been fixed, so what remained had settled where it
does not look. And two of the three are worse than a leaked type, not better: the compiler
cannot notice them at all, and the commit gate would tell somebody whose repository holds only
TwinCAT projects that no CODESYS projects are tracked — the application confidently describing a
situation that is not the one they are in.

**The constant is the one worth staring at.** It sat directly beneath a paragraph explaining
that `LogCategory.Plc` must be vendor-neutral, *because* "a log filter that said Codesys would
stop finding the interesting lines the moment a repository holds a TwinCAT project" — and
directly above a second `<summary>` tag left behind when that paragraph was written. Three
things wrong in nine lines, in a file somebody had deliberately thought about.

**What changed.** The scanner now reads compile-time constants by reflection (exact) and string
literals out of IL by walking for `ldstr` — which reaches strings built inside lambdas,
iterators and async methods, because those compile into generated nested types carrying the
enclosing namespace. Reading source was rejected: it needs the repository beside the test binary
and re-implements a comment parser badly. IL carries only what was compiled, so a brand inside a
comment stays invisible, which is right — a comment is often the clearest place to explain the
rule.

**A fourth hole, found while widening the third.** `Referenced` yielded field, property and
parameter types without unwrapping them, so the namespace of `IReadOnlyList<CodesysProjectFormat>`
read as `System.Collections.Generic` and the check waved it through. A vendor type travelling in
a collection is a *more* likely leak than a bare one. Arrays hid a type the same way.

**And `Versio.Infrastructure` was never examined at all.** The test reads
`typeof(PlcJob).Assembly` and stops, so the git client, the watchers, the filesystem and the
toolchain registry sat outside the rule that the README, `CLAUDE.md` and the test's own docstring
all claim covers them. That assembly is clean today. Nothing was keeping it that way, and "it was
clean when somebody last looked" is precisely the state the seam test exists to end.

**Fixed:** the two sentences say "PLC"; the constant moved to `CodesysLog.Category`, where the
vendor declares its own label. The logged word is unchanged, so log files still parse and the
filter still offers the same toggle — the entire change is which assembly writes the word down.
Folding it into `LogCategory.Plc` was rejected: the toolchain's line-by-line chatter is worth
filtering on its own without losing the orchestration decisions above it, the same argument that
already keeps `Watch` apart from `Fs`.

**Only one of the two sentences was watched by anything.** `CommitGateTests` pinned the CODESYS
wording and went red, which is how it should be. `OrphanScanner`'s sentence had no assertion
anywhere and could have drifted in any direction without a single test noticing.

Mutation-checked on all three mechanisms separately, because green was the problem here: a
literal, a literal inside a lambda, and a vendor-named constant were each added to a neutral
namespace, and each failed the build. All reverted.

### `Register` had no callers, and no way back

**Found the same day, and it is the load-bearing one.** `PlcFormats.Register` and
`PlcToolchains.Register` had **zero callers** repository-wide — not production, not a test, not a
sample. The README, `CLAUDE.md` and both registries' own docstrings all say that adding a vendor
means implementing the contracts and registering with them. The mechanism the whole architecture
rests on had never once been executed. The real composition is a hardcoded array holding one
format and a hardcoded dictionary holding one factory.

`SecondVendorTests` and `SecondVendorToolchainTests` run it, and cover the case the docstrings
promise and nothing asserted: **a format registered with no toolchain behind it**, whose projects
must be detected, listed and explained as unsupported rather than silently ignored — "which is
how somebody concludes the tool is broken".

**`Register` had no counterpart, and that is the finding rather than an inconvenience.** A
registration was permanent for the life of the process, so a host swapping in its own
implementation of a vendor — the case `Register` exists for — could only ever do so in one
direction. The first test to call it could not put the registry back. `Unregister` on both
registries is new.

**It matters more than it looks, because these registries are static and xunit runs test classes
in parallel by default.** A stand-in vendor left registered is not untidiness; it is one class
quietly changing what every other class sees. The stand-in claims `.tcproj`, which collides with
no real format, and every test restores in a `finally`. Phase C removes the ambient static
entirely and that dance goes with it.

**A prediction that was wrong, recorded rather than dropped.** The audit predicted this test
would not compile, because `PlcVendor` is a closed enum in `Versio.Core`. It compiles fine — the
enum already declares TwinCAT, TIA Portal and Studio 5000, precisely so the abstraction is judged
against real second vendors rather than a hypothetical one. **The closed-enum limitation is real
but it bites the fifth vendor, not the second**, and nothing in this test demonstrates it. —
**Closed the same day** by the finding above; `PlcVendor` is an identity rather than a menu.

### The cap that said it bounded the path bounded one segment, and was off by seven

**Found 2026-08-16 by measuring G9 before building its guard**, with the pure functions that
actually run rather than by counting characters in a comment.

`_MAX_STEM = 80` carried this justification, in both languages: *"short enough that a deep
CODESYS folder chain plus this still clears Windows' 260-character path limit."* Two things
make it false, and the second is the one that matters:

```
safe_file_stem("A" * 300)                 ->  87 chars   the shortened branch adds "~" + 6 hex
file_name_for(child, TRANSITION, NATIVE, parent)
                                          -> 190 chars   TWO shortened stems in one name
```

`file_name_for` prefixes a member with its owner — method, action, property, transition — so a
single **file name** is two capped stems plus an infix plus a suffix. With
`Device/Plc Logic/Application`, the chain every CODESYS project has, and a **one-character**
project name, that leaves **19 characters for the repository root**. `C:\GIT\Versio` is 13 and
fits by six; `C:\Users\m.lamp\Documents\GitHub\MachineControl` is 47 and does not.

**No deep folder chain is needed**, which the gap row had assumed for its whole life.

**And the number was wrong twice before it was derived.** 189 by hand, in the first write-up
of this. Then **201**, from a test that enumerated `CodesysObjectType` against `ExportForm` and
found `Transition/Libraries` — a combination the exporter cannot produce, because
`.libraries.yaml` belongs to a library manager and a library manager is not parent-prefixed.
Asking `ExportFormRules.Decide` which forms a type can actually take gives **190**. **A worst
case built from impossible inputs is not a worst case**, and this one was inflated by eleven
characters until the rule was asked instead of the enum.

**The test that should have caught it asserted `< 100`.** `A_name_nobody_could_open_is_shortened_rather_than_written`
passed at 87 and would have passed at 99. It pins 87 now — a loose bound on a number whose
exact value is the whole point is a test that measures nothing.

**The design trap, which is why this took a shape the plan did not ask for.** The plan said
"shorten deeper segments or adopt `\\?\`". Both are wrong here:

- **Shortening keyed on the total path makes the export machine-dependent.** The total includes
  the repository root, which differs per developer — so two people cloning one repository would
  produce *different file names for the same object*, and git would see renames that are not
  renames. The mirror has to be a function of the project alone.
- **`\\?\` only helps the writer.** It lifts the limit for Versio's own file APIs and leaves
  git, CODESYS and the engineer's editor unable to open the file.

So it is **two guards on two channels**, and the channel is the load-bearing part.
`too_long_for_any_machine` reads only the mirror-relative path, gives every developer the same
answer, and is recorded in `_manifest.json`. `too_long_for_this_machine` reads the staging path
and goes to `emit()` and the log **only** — routing it through `warn()` would put it in the
manifest, where a machine-dependent sentence makes two developers' manifests differ and the
mirror churns on every export by whoever is checked out deeper. **That is Phase 5's defect
arriving through a door Phase 5 could not have known about**, and the difference between the two
spellings is one word at the call site, which is why a test asserts it rather than a comment.

**What is not closed, deliberately.** The guards report the 190-character case; they do not
prevent it. Preventing it means shrinking `MaxStem` or dropping the parent prefix, and either
**renames every method, action, property and transition in every mirror already committed** —
a git rename with no content change, in everybody's repository, on the next export. That is a
decision rather than a consequence, so it is written up with its cost instead of taken.
`The_longest_legal_file_name_overflows_MAX_PATH_before_any_folder_chain` **derives** the number
instead of asserting it, so whoever takes it on is told when they have succeeded rather than
having to re-measure.

**The second pass over the guard found it measuring the wrong thing, and the shape is the one
this repository keeps meeting.** It checked the object's **primary** file only — but a
`GVL_BOTH` writes two, and its `.gvl.xml` is a character **longer** than the `.gvl.st` beside
it. So the one file a GVL could overflow on first was the file nothing looked at. `TASK_BOTH`
escaped only by luck, its `.tasks.yaml` being longer than its `.xml`, which is the kind of luck
that stops holding when a suffix changes. **That is G7 and G12 for the third time**: a rule
written for objects, applied to something that writes two files. The check is a method taking
one path now, called for each file written, and
`Every_file_the_export_writes_has_its_path_length_checked` counts the call sites against the
second-file writes so the next form cannot quietly skip one.

**The test written for that omission then passed its own mutation.** It counted
`check_path_length(` call sites and required one more than the number of second-file writes —
but the regex also matched the `def check_path_length(` line, so definition-plus-two-calls
cleared a bar that three real calls were meant to clear. Deleting the GVL check left it green.
`self.`-prefixed now. **It looked right, and it was watching nothing at exactly the boundary it
existed for** — which is the argument for running the mutation rather than reasoning about it,
made by the one test in this change whose whole job was to catch a missing call.

**And the commit that fixed `MaxStem`'s wording claimed to fix it "in both languages" and fixed
one.** `versio_text.py` was corrected; `CodesysPathMapper.cs` kept the false sentence in two
places, and `CODESYS.md` §5.3 kept a third copy. **Four copies of one claim, and the commit
message asserting the sweep was itself the least accurate of them** — which is the same defect
as the claim it was fixing, one level up: a statement about evidence that nobody checked.

**`PATH_TOO_LONG` is not a coverage gap and is not treated as one.** The object *is* in the
mirror on the machine that wrote it; what is wrong is every other machine. It is
`recordedAnyway` in the warning invariant — and it is the first code that invariant forced
somebody to classify, which is what it was built for.

### An export that lost an object reported full coverage, and `Warnings` had no reader

**Found 2026-08-16 by tracing G9 rather than by building it.** The gap says the total path is
unguarded; the question asked before writing the guard was what happens today when it is
missed, and the answer was worse than the missing guard.

`versio_export.py` wraps every object's write in one `except`. When it fires, the handler warns
and **returns before `record()`** — so the object is in neither `objects` nor `skipped`. It
exists in `warnings` and nowhere else.

`ExportCoverage.Of` read `manifest.Skipped`, filtered to `NOT_IMPLEMENTED`, and **never looked
at `manifest.Warnings` at all**. So:

```
object throws on write  →  EXPORT_FAILED warning, no record
                        →  ExportCoverage.Of sees an empty skip list
                        →  CoverageVerdict.Complete
                        →  panel: "Everything in this project reaches the mirror."
                        →  commit gate offers a mirror with the object missing from it
                        →  run reports Completed
```

**`ProjectManifest.Warnings` was parsed, typed, and documented — "what the export noticed and
could not act on" — and read by nothing in the codebase.** Every `.Warnings` reference in
`Versio.Core` was `plan.Warnings` or `result.Warnings`. That is the rolling-file-sink shape:
every part of the mechanism present except the one that makes it visible.

**It is the third recurrence of this class's own subject, and the first through a warning.**
`TASK_NATIVE` and `GVL_NATIVE` both arrived as a *form* the coverage rules did not name; this
arrived as a code they did not read. `ExportCoverage`'s remarks already say what the first two
cost — "no line in `_coverage.md`, no lock glyph in the panel, nothing in the manifest".

**The second door, found by re-reading the same function afterwards.** `UNNAMED_OBJECT` has the
identical shape and is one step *earlier*: `get_name()` raises, the walk warns and returns
before `self.total` is incremented, so the object is missing from the walk count as well as
from both lists. **Its docstring three lines above promises the opposite** — "unmapped type
GUIDs are exported natively and warned about, never dropped: a newer CODESYS adding a type must
not silently lose objects" — which is true of the line below it and false of this one. Closing
`EXPORT_FAILED` alone would have been G7-patched-and-G12-missed exactly.

**So the fix is a rule, not two constants.** `ExportCoverage.IsFailure(code)` is the single
authority on "this object is not in the mirror at all", the same shape as `IsExportOnly` and
for the same reason. `Every_exporter_warning_is_classified_as_losing_an_object_or_not` scans
the **embedded** script for every `self.warn("…")` code and fails when one is neither in
`IsFailure` nor in an explicit harmless list — so the next warning added to the exporter is a
decision somebody makes rather than a door nobody notices. `UNKNOWN_TYPE_GUID` is the harmless
one, and it earns its place in the test: it warns and then *falls through* to a form, so the
object is exported and recorded. Not every warning is a loss, and a rule that said so would put
a red line on exports that are fine.

**Why the pure suite could never have caught the spelling, measured rather than assumed.** Every
`ExportCoverageTests` fixture is built from `ExportCoverage`'s own constants, so both sides of
the comparison move together. Setting `FailureCode` to `EXPORT_FAILEDX` left **all 1,492 unit
tests green**. `ScriptContractTests` exists for that: it reads the script through
`ScriptExtractor` — the **embedded** copy, the one that runs — and pins the code, the skip
reason, and the path spelling. This is the C#/python drift `CODESYS.md` §9 counts four instances
of, one level down: not two algorithms disagreeing, but one side reading a literal the other
side stopped writing.

**And the path in that sentence was not a path.** Five sites in the exporter build an object's
path from `rel_dir` and `name`; the three that reach `_manifest.json` normalised it and the two
that reach `warnings` did not — so the same object was `App/Thing` in the skips and
`\App/Thing` in the warnings, mixing separators and keeping a leading slash at the project
root. **The comment beside the normalised one states the rule** — "one spelling of the path,
shared by the manifest and the log line" — and it was stated where only the callers written
beside it would ever see it. It stopped being cosmetic the moment the panel began printing the
`EXPORT_FAILED` path in a sentence: a name with a stray backslash is a name nobody can search
the mirror for. `versio_text.object_path` is the one home now, in the pure module rather than
in `versio_export.py`, which imports `scriptengine` at import time and therefore cannot be
loaded under CPython at all — a rule living in it is a rule nothing can test.

**And the second pass over the fix found the fix incomplete.** `UNNAMED_OBJECT` was added to
`IsFailure` and left passing a **raw `rel_dir`** into `warn` — so the one code this change
taught the panel to print was the one site still spelling its path with `os.path.join`'s
backslashes, while the other four had been converted. Nothing failed: the path-spelling test
forbade the inline `rel_dir + "/" + name` expression and a bare `rel_dir` is not that
expression. **A test written against the shape of the defect you just fixed does not cover the
shape you did not.** `object_path` takes a nameless object now — as its own branch, because a
CODESYS name may legitimately contain `/` and stripping a trailing slash would eat one — and
the test forbids the raw form by name.

**And the invariant claimed more than it checked.** `Every_exporter_warning_is_classified…`
scanned for `self.warn("CODE"` — the exporter's own literals — and there are four more codes
that reach `manifest.warnings` without ever appearing beside a `warn` in `versio_export.py`:
the library and task readers raise theirs through an `on_error` callback the exporter hands
them. **A test named "every" that covers one of two spellings is the door it was written to
close**, so the scan now reads all three modules and both forms. The four are
`recordedAnyway` with their reason stated — the file *is* written and recorded, with content
that may be short of what the project holds, which is a different unanswered question and one
this repository does not measure yet. `PROJECT_CLOSE_FAILED` stays out and correctly: it goes
to `emit` rather than through `warn`, so it never enters the manifest at all.

**One limit found the same way and left alone, deliberately.** A two-file object records its
first half before writing its second, so a `GVL_BOTH` whose `.gvl.st` throws is in `objects`
*and* in `warnings`. Reporting it as not exported is right — the declaration really is not in
the mirror — but "1 object not exported" undercounts what survived. Stated on `FailureCode`
rather than fixed; the alternative was to say nothing, which is the state this exists to end.

**Mutation-checked, six ways**, because most of this change is a rule and rules pass silently:
stop failures reaching the verdict → 3 red; gut the extracted gap clause → 4 red, which is what
says the pure move kept its coverage; misspell `FailureCode` → the pin only, and 0 red in the
pure suite; drop `UnnamedCode` from `IsFailure` → the invariant, naming the code in its message;
reintroduce an inline path spelling in the exporter → the shape test; gut `object_path`'s
normalisation → 2 red in the python behaviour tests.

**What this does not do.** It makes a failed export *visible*; it does not stop one happening.
G9 is still open and is still the next thing — but its fix is now a guard on a path rather than
a guard on a silence, which is a much smaller thing to get right.

### The two ST codecs disagreed about four characters, and a rename is what it would have cost

**Found 2026-08-16 by writing the parity test the plan had been asking for**, on its first run.

`StTextCodec` (C#) and `versio_text` (python) both split a `.st` file into a declaration and an
implementation, and both trimmed the edges of each half with their own language's default —
`string.Trim()` and `str.strip()`. Those are not the same set. Enumerated rather than read off
either documentation:

```
python strips 29 codepoints
C#     trims  25
python only:  U+001C U+001D U+001E U+001F   (file, group, record and unit separators)
C# only:      none
```

**Everything else matched exactly**, across the whole range scanned — the two disagreed about
four control characters and nothing else.

**What it would have cost, which is not "a stray character".** The exporter records `ihash` over
python's `split_st(...)` implementation. `CodesysImportPlanner` re-computes that hash over
`StTextCodec.Split(...)`'s, and **the two hashes are what pair a renamed POU with the file it
became** — Phase 3's second pass, the one that handles the ordinary case where a POU's name
lives inside its own declaration. A body ending in one of those four characters hashes
differently on the two sides, the pass finds no match, and the rename is not detected: the
object is destroyed and rebuilt with a new GUID, taking every reference and task assignment
with it. That is G2's failure mode, reached by a control character.

Both C# callers of the codec are **readers** of what python wrote, which is what makes the
direction of the divergence matter rather than being symmetrical.

**Fixed by naming the set once, on both sides, instead of inheriting it.** `versio_text.WS` and
`Versio.Core.Text.PayloadWhitespace` are the same 29 characters, spelled out. Python's set was
adopted because the exporter is the writer and every hash in every committed manifest came from
it.

**And it was not one surface but three, which is why the definition moved out into a type of its
own.** `LineSketch` had the identical divergence with the identical consequence one pass further
along: a line whose edge is trimmed differently hashes differently, so the two sketches share no
digest for it and the similarity is under-reported — losing the rename that the sketch fallback
exists to catch on a mirror git never saw. `versio_import.apply_st` had it too, in a comparison
rather than a hash: it asks whether the live text differs from what `split_st` produced, and two
different trims would report an unchanged object as changed and rewrite it on every import,
forever. `TextSimilarity` has no python mirror but reads the same array now, because
`line_sketch`'s own docstring promises the two "agree about what a line is" — a shared
definition is what makes that true rather than aspirational.

**And that removes an assumption nobody could have tested.** The python that runs inside CODESYS
is **IronPython 2.7**; the python that tests it is CPython 3. A bare `strip()` makes the rule
whatever the interpreter thinks it is, and no test in this repository can see IronPython's
answer. Naming the characters makes the two agree by construction.

**Byte-neutral, verified rather than asserted, and verified twice.** No text file in either
committed mirror contains any of the four. Both fixtures were re-exported and diffed after the
codec change, and again after the `line_sketch` change: every content file identical, and every
recorded `hash`, `ihash`, `shash` and `nhash` identical, with only the `bindingId` differing
because the check passed a different one.

**Both halves of the test were mutation-checked.** Removing `U+001C` from the C# set fails on the
corpus payload that contains it; removing `U+205F` — which no payload contains — fails **only**
the assertion that compares the two sets directly. That second check is the one that earns its
place: the divergence this test actually found was in the corpus only because somebody guessed
it might be worth including.

### A GVL's declaration is applied and then overwritten by its own XML, and the log says it worked

**Found by running the feature checklist rather than by reading anything**, on 2026-08-16, in
the one import path no test had ever asserted: `ST_DECL`.

Edit `Glob_Var.gvl.st` in the mirror, import, re-export. The import log reads

```
applied Device/Plc Logic/Application/Glob_Var.gvl.st (declaration)
...
imported Device/Plc Logic/Application/Glob_Var.gvl.xml as native XML into Device/Plc Logic/Application
import: 1 updated, 7 native, 2 unchanged, 3 not applied (export-only) — 0 warning(s)
```

and **the edit is not in the project.** Both lines are true; the second undoes the first.

**Why.** A GVL exports as `GVL_BOTH`: one object, one GUID, two files — a `.gvl.st` recorded
`ST_DECL` and a `.gvl.xml` recorded plain **`NATIVE`**. `versio_import.py` runs its phases
**textual → native → delete**, so the declaration is written into the object and then
`import_native` replaces that object from an XML holding the declaration as it was at export
time. The later phase wins, every time, on every GVL.

**This is G7 wearing different clothes, and the fix for G7 was modelled on the thing that is
broken.** `TASK_BOTH` has exactly the same shape and does not have the defect, because Phase 4
gave its XML half its own form — `TASK_NATIVE` — and `ExportCoverage.IsExportOnly` names it, so
the importer skips it and the log says *"edit Task Configuration.tasks.yaml instead"*.
`IsExportOnly` names `LIBRARIES` and `TASK_NATIVE` and nothing else. The GVL's XML half was
never given one.

**The intention was written down and never enforced.** `versio_types.py`, on `FORM_TASK_BOTH`:

> Follows GVL_BOTH exactly: one object, two files, one GUID in the manifest. The difference is
> which half is authoritative — **a GVL imports from its `.st`**, and a task configuration
> imports from its `.tasks.yaml`

A GVL does not import from its `.st`. It imports from its `.st` and then from its `.xml`.

**And `_coverage.md` says nothing about it**, which is the part that makes this data loss
rather than an inefficiency. The report names three files as *"exported for review, not applied
back"* — two `.libraries.yaml` and `Task Configuration.xml` — and in a report whose entire job
is to list what an edit will not reach, absence from that list is a promise. `Glob_Var.gvl.st`
is absent from it. **That is the gap `_coverage.md` was built to close, re-opened for the second
time**; the first was closing G7 and G11, and it has its own finding below.

**How it was proven rather than argued.** Two runs differing in exactly one thing: the second
turns the plan's entry for `Glob_Var.gvl.xml` into a skip before the job is handed over, and
leaves the edit and everything else identical. The declaration survives. So the write lands and
the native phase is what removes it — which the log alone could not have told anybody, because
it says `applied` either way.

**✅ FIXED 2026-08-16, the same day.** The `.gvl.xml` is recorded `GVL_NATIVE` and
`ExportCoverage.IsExportOnly` names it, so the import applies the `.gvl.st` alone and the log
says *"edit `…Glob_Var.gvl.st` instead"*. Verified against real CODESYS: the edit is in the
project on the re-export, where the same test failed an hour earlier.

**And the shape is an invariant now, which is the part that matters more than the fix.** G7 was
patched on `TASK_BOTH` and `GVL_BOTH` had the identical defect for months, because the fix was
applied to the instance rather than to the shape.
`An_object_never_writes_two_files_the_import_would_both_apply` asserts against a **real
export** that no object writes two files the import applies — real, because both defects were
in what the exporter wrote and every unit test passed throughout. Removing `GvlNativeForm` from
`IsExportOnly` turns it red, naming both files; that mutation was run rather than assumed.

**It cost the two-file rule its subject, deliberately.** An export-only record neither votes
nor vetoes in the delete planner, so a GVL now has exactly one importable file and `gone ≠
importable` can no longer happen for anything. A GVL became an ordinary single-file object:
deleting `Glob_Var.gvl.st` removes it, exactly as deleting `Simulation.st` has always removed
the POU, and **the content check is what stands in front of it** — `ST_DECL` is verifiable, so
a GVL edited in the IDE since the export is still kept. The rule is kept rather than deleted:
an older mirror still records the XML as `NATIVE` and still reaches it, and a second vendor is
under no obligation to adopt the one-importable-file shape. The integration test that asserted
the old behaviour now asserts the new one, and says both were right at the time.

**The three candidates, and why this one.** They were not equivalent:

- **Give the XML half its own form**, as `TASK_NATIVE` did — **chosen.** Cleanest, and the
  remedy sentence comes free because the exporter finds the sibling by GUID. Its cost is the
  delete rule above, which on inspection makes a GVL *consistent* with every other object
  rather than less safe.
- **Apply textual after native**, which fixes every `BOTH` pair at once and re-orders a phase
  sequence whose current order is load-bearing for creates (`run()` says why).
- **Rebuild the XML from the edited declaration before importing it**, which is the only option
  that keeps both halves authoritative and is by far the most work.

The measurement and the three options are recorded here so the choice is made deliberately
rather than by whoever touches it next.

### "References and task assignments survive" was an inference, and it is false

> **In one line:** a renamed POU keeps its GUID — genuinely the same object — and **its task
> still lists the old name**, because CODESYS stores a task's POU list by NAME and a scripted
> `rename()` does not rewrite it. The claim was written into the README, the plan, `SAFEGUARDS`
> and the **confirmation dialog** on the strength of GUID equality, never measured.
> **✅ Corrected AND fixed 2026-08-15**: the wording no longer over-promises, and the import
> repoints the task so the claim it does make is true.

**It was found by a question, not by a test.** Asked whether a real project had actually been
renamed and imported, the honest answer was "yes, and the GUID was asserted" — which is evidence
that the object survived, and *not* evidence of what that survival buys. The fixture happens to
have `MainTask` listing `Simulation` among its POUs, so the claim was directly checkable. It
was checked. It failed.

```
before   pous: [PLC_PRG, Simulation]        Simulation.st, guid 980a4bef…
rename   Simulation → Renamed               same guid 980a4bef… — same object
after    pous: [PLC_PRG, Simulation]        ← the task still names a POU that no longer exists
```

**This is the live project talking, not a stale file.** `versio_import.py` never writes `pous`;
it applies priority, interval, core binding and watchdog only. So the re-export is reading what
the task actually holds.

**What it costs a user.** Rename a POU in the mirror, import, and the object is renamed properly
— but any task that ran it now references a name nothing answers to. Nothing warns, because
Versio did exactly what it was asked. A delete-plus-create would have broken the same reference,
so this is not a regression; the defect was **claiming otherwise in the one dialog somebody
reads before agreeing**.

**Corrected rather than quietly dropped.** The dialog now says the same object is kept and that
anything referring to it *by name* still uses the old name and needs checking in the IDE. The
narrative test asserts the false phrase is **absent**, not merely that the new one is present —
the same two-sided discipline the deletion wording needed after it lied in the other direction.
The integration test pins the behaviour as it really is, so it will notice if a future CODESYS,
or a fix here, starts updating the reference.

**✅ And then fixed, the same day.** The import repoints the task itself: after a rename it walks
every object carrying a POU list and rewrites entries matching the old name. Measured — `MainTask`
now reads `[PLC_PRG, Renamed]`, with `PLC_PRG` untouched, so the retarget is per entry rather than
a rewrite of the task.

**The API took two probes and every spelling but one is a trap:**

| Call | Result |
|---|---|
| `pous[i] = name` | raises — the list does not support item assignment |
| `pous.remove(name)` | **reports success and does nothing** — a silent no-op, exactly like `move()` |
| `pous.remove(index)` | raises *"not found in the specified Collection"* |
| `pous.replace(name, name)` | raises *"expected int, got str"* |
| **`pous.replace(index, name)`** | **works, and survives a save and reopen** |

**The first probe nearly recorded the wrong answer**, again: it tried `remove` then `add`, saw
both report success, and would have concluded the list was writable that way — while `remove`
had changed nothing and `add` had left a third entry behind. Reading the *value back* after each
call is what caught it. That is the third silent success measured in this API, after `move()`
and `create_folder`.

**The dialog says the narrower true thing.** It names tasks specifically rather than claiming
"references" survive: a visualisation binding may refer to an object by name too, and that has
not been measured. The vaguer word is exactly how the original false claim got in, so
`ImportNarrativeTests` asserts the word *references* is **absent**.

### A rename that renames nothing was reported as a rename — and the test that found it was written to assert the opposite

> **In one line:** exact-content rename matching cannot see a POU rename, because a POU's name
> lives in its own declaration — and when the file alone is renamed the planner emitted a rename
> whose old and new names were equal, which the script counted as a success.
> **✅ CLOSED 2026-08-15** by withdrawing both sides and warning instead.

**The measurement that "proved" renames worked had bypassed the planner.** A scratchpad harness
hand-wrote the rename entry with `newName: "Renamed"` and drove the script with it. That was a
real and useful measurement — it is what proved `rename()` preserves the GUID — but it proved
the **executing** half only, and it read as proof of the whole thing.

**The integration test written afterwards is what caught the difference**, by failing. It asserted
that renaming `Simulation.st` to `Renamed.st` renames the object, and it does not: the file still
opens `PROGRAM Simulation`, so the plan reads that name, old equals new, and there is nothing to
rename it to.

| What somebody does | What the planner sees | What happens |
|---|---|---|
| rename the file only | declaration still names the object — old = new | nothing renamed, both sides withdrawn, warned |
| rename the file *and* the declaration | content changed, no hash matches | delete + create, **GUID lost** — step 2 |

**So step 1 detects a rename only for a form whose content does not carry the name** — actions and
transitions. The fixture has none, which is why unit tests, a hand-built harness and a green
suite all agreed with each other and none of them touched the real case.

**The false success is the fourth in this path this session**, and the one that got furthest: the
script's own `RENAME_NOT_APPLIED` verification could not catch it, because the object never had
to move for the check to pass. **A verification that asks "is it where I expected" cannot notice
that nothing was asked of it.**

**✅ Closed the same day by manifest schema 6.** A full ST record carries `ihash`, the hash of
its implementation alone, and a second pass pairs a departed POU with an arrived one on it. A
real POU rename — file and declaration — now keeps its GUID, measured end to end against real
CODESYS, with the new declaration applied to the renamed object rather than skipped. **The
arrival must be applied**, and that is the difference from the exact-content pass: skip it and
the object wears its new name over its old declaration.

**The first answer written here was also wrong, and is corrected rather than quietly dropped.**
It said a real POU rename could be matched by comparing the implementation exactly and the
declaration with its name masked, needing nothing but the manifest and the mirror. **It cannot**:
the manifest records one `hash` per file and the old file is gone, so there is no old
implementation to compare against. Every fuzzy match needs the departed content, and only two
things have it — a hash recorded at export time (schema 6, `ihash` + `dhash`, the same shape as
`nhash`), or `git show HEAD:<path>`. The first covers a pure rename cheaply; the second is the
only route to a rename that also edits the body.

### `move()` accepted an instruction, did nothing, and reported success

> **In one line:** moving a POU out of `Application` into `Plc Logic` raises nothing and moves
> nothing; the first Phase 3 run reported `renamed: 1`, `failed: 0`, **zero warnings**, and a
> re-export showed the object at its old path. **✅ CLOSED 2026-08-15** by making the script
> re-resolve the object where it was supposed to land and report `RENAME_NOT_APPLIED` when it
> is not there.

**The third silent success in the delete/rename path this session**, after the dialog that
promised objects would stay while deleting them and the log line that described the previous
version's behaviour. The shape is identical every time: a step that cannot fail loudly, and a
counter that is incremented on the assumption it worked.

**What makes this one worse than the others** is that nothing above it could have noticed. The
import verdict was `Completed`, the summary said one object was renamed, the warning list was
empty, and the only way to find out was to export the project again and read the paths. **A
counter incremented next to an operation is not evidence the operation happened** — and this
codebase now has three examples of exactly that.

**✅ The open half of this is now settled, and `move()` is fine.** A folder was created under
`Application` in a throwaway copy and `Simulation` moved into it: no exception, under the folder
in session, still under it **after a save and reopen**, and the GUID `980a4bef…` unchanged
throughout. So `Application` → `Plc Logic` was CODESYS **correctly refusing an illegal parent**
for a POU — the operation is sound and the defect was only ever the silence.

**The verification stays anyway**, and that is the point rather than a leftover: a refusal that
says nothing is indistinguishable from a success no matter *why* it refused, and Versio cannot
enumerate which parents a toolchain considers legal. Checking the outcome costs a walk of names
already in memory.

**`create_folder` returns `None`** — the folder is created regardless and must be found by name.
That silently skipped the entire probe on its first run: the return value was used as the move
destination, so the move never ran, and the result read as "move does not work". **The first
run of a measurement nearly wrote down the opposite of the truth**, which is the same trap as
the harness that proved the executing half and read as proof of the whole feature.

**Rename in place is unaffected and works**: same GUID before and after, no warnings, verified
against real CODESYS.

### The delete path explained itself to nobody — three channels, three different silences

> **In one line:** the dialog said the wrong thing, the plan's log said nothing at all and then
> said something false, and the adapter threw away the script's explanation — so a refusal to
> destroy something reached a person as `DELETE_UNVERIFIABLE: Diagnosis` at best.
> **✅ ALL THREE CLOSED 2026-08-15.** The dialog has its own Finding below; these are the other
> two, found by auditing the change rather than by any test failing.

**`ImportPlanReader` logged nothing about deletions, and had no tests at all.** It logged the
planner's warnings, the schema explanation and the missing-file count — and not the one decision
in a plan that takes work *out* of a project. The removals were logged, but only by the script,
*after* they happened. What had been decided, and what somebody agreed to ~20 s earlier,
appeared nowhere.

**Worse, its missing-file line was actively false.** It read *"deleting objects from the project
is not applied by this version, so they remain in the project"* — written when it was true, left
in place after the import learned to delete. **The identical stale-sentence defect as the
dialog, in a second channel, surviving for the identical reason: no assertion had ever read
it.** `ImportPlanReaderTests` is new and exists because of this; one of its four cases asserts
that exact sentence is *absent* rather than merely replaced, since a test checking only the new
wording goes green again the moment somebody restores the old line beside it.

**And the adapter dropped the reason.** `LocalCodesysRunner` projected script warnings as
`$"{code}: {path}"`, discarding the `detail` the scripts have always emitted. So every refusal
arrived with its explanation removed:

```
DELETE_UNVERIFIABLE: Diagnosis                     ← what a user saw
DELETE_UNVERIFIABLE: Diagnosis — its NATIVE file cannot be compared against
  the live object, so whether it changed since the export is unknowable   ← what the script said
```

**A warning that is present, looks complete and answers nothing is worse than a missing one.**
It mattered least when a warning meant "an object was skipped" and most now that it means
"Versio declined to delete this, and here is what would change that". Fixed in the adapter,
because `SyncOrchestrator.LogWarnings` documents that nothing above it knows what a CODESYS
warning code looks like — and that stays true.

| Guard | Breaks when |
|---|---|
| `ImportPlanReaderTests` (4 cases) | the plan stops naming what it will remove or keep, or the false sentence returns — verified red by disabling the removal line |
| `AdoptAndExportTests` … `_leaves_the_project_and_an_edited_one_does_not` | the adapter drops `detail` again; asserts the reason text against the real script through the real adapter |

### The confirmation dialog told people their objects would stay, and then deleted them

> **In one line:** `670642c` taught `versio_import.py` to run `obj.remove()` and did not touch
> `ImportNarrative`, so for one commit the last thing anybody read before an import said *"This
> version does not remove objects from the project, so they will stay"* — and then the import
> removed them. **✅ CLOSED 2026-08-15**, by splitting the deletion set into what will really go
> and what will really stay, and saying both.

**This is the exact failure the whole import-confirmation chain exists to prevent, pointed the
other way.** The chain is unusually well built: the plan is decided C#-side before CODESYS starts
(Phase 1), carried to the dialog and back so the plan agreed to is the plan executed (Phase 2's
seam step), and `RequireImportConfirmation` defaults to `true` so no watcher-triggered import can
delete anything without somebody being asked. Every one of those gates worked. The sentence they
delivered to the person was false.

**What it cost a user.** Delete `FB_Motor.st`, commit, press Import. The dialog names `FB_Motor`,
says it will stay, and suggests deleting it in the IDE if that is what you want. Press Import —
the POU is gone from the project. There is no undo inside Versio, and the object's own file was
deleted in the same change, so the mirror does not hold a copy either.

**Why a green suite did not notice.** `ImportNarrativeTests` pinned the wording with
`Contains("they will stay")` and `DoesNotContain("will be removed")`, under a comment reading
*"When the script grows remove(), this test is what says the wording has to change with it."* It
does not, and it cannot. **An assertion on the old sentence cannot fail when the behaviour moves
— it can only fail when somebody fixes the sentence.** The tripwire was a lock. The same
assertion sat in `ImportPlanHandoverTests`, and both stayed green across the commit that shipped
deleting, at 1,443 and 480 tests.

**The stale claims trailed it in four more places**, all of them written while they were true and
none updated by the commit that falsified them: `ImportPlan`'s own remarks (*"no wire carries one
yet and nothing executes one"*), `CodesysImportPlanner`'s (*"a deletion is a proposal and nothing
executes it yet"*), this file's bullet recording the wording as correct-and-pinned — **directly
above the bullet announcing that deletes now work** — and the import plan's Phase 2 section.

**The fix is a split, not a re-word.** Flipping the sentence to *"will be removed"* would have
been a fresh lie for native objects, which the script refuses (`DELETE_UNVERIFIABLE`) and which
are **7 of the fixture's 13 objects**. So `PlannedDeletion.CannotVerify` carries the planner's
prediction of the script's own refusal — null when the object will go, a sentence when it will
stay — and the dialog says both things in two sentences, grouped by reason.

**What is watching it now:**

| Guard | Breaks when |
|---|---|
| `ImportNarrativeTests.Objects_the_import_will_remove_are_named_and_said_to_be_going` | the dialog stops promising removal, **or starts promising it falsely** — both sides asserted, which is what the original pair lacked |
| `ImportNarrativeTests.A_mixed_plan_says_both_things_and_does_not_merge_them` | one sentence starts covering both outcomes again |
| `PythonFileNameParityTests.The_planner_and_the_script_agree_about_which_deletions_can_be_checked` | the planner's prediction and `versio_types.can_verify` drift — verified red by adding `FORM_NATIVE` to the python list |

**The parity test is the structural fix.** The planner and the script answer the same question
~20 s apart and had no shared authority; `CAN_VERIFY_FORMS` now lives in `versio_types.py`, which
is pure and therefore runnable under CPython, and the C# mirrors it. A rule living in
`versio_import.py` is a rule nothing can compare, because that module cannot be imported outside
CODESYS.

**Measured end to end**, one import deleting one textual and one native object against real
CODESYS 3.5.22.20: `Simulation` (`ST_FULL`) removed, `Diagnosis` (`NATIVE`) kept with
`DELETE_UNVERIFIABLE`, and a re-export confirming the project afterwards holds the second and not
the first.

### Closing G7 and G11 re-opened the gap `_coverage.md` was built to close

> **In one line:** making `Task Configuration.tasks.yaml` the importable half turned the `.xml`
> beside it into a file the export writes and the import silently discards — and every channel
> that exists to say so reported it as ordinary, because it was still recorded `NATIVE_R`.
> **✅ CLOSED 2026-08-15**, by recording it `TASK_NATIVE` and reading one-wayness from one list
> per side instead of one constant.

Versio has machinery for exactly this. `_coverage.md` carries a section headed *"Exported for
review, not applied back"*; the manifest records a `form`; the changed-files row draws a lock
glyph with a tooltip. `STATUS.md` describes the discipline in four places, in the row about
library references, and the comment in `versio_export.py` above the section states the reason
outright: *a file that is written for review and silently ignored on the way back is its own kind
of gap — editing it looks exactly like editing a POU.*

**The task XML got none of the four.** One-wayness was decided by comparing against a single
constant on each side — `form == FORM_LIBRARIES` in the report, `form == "LIBRARIES"` in
`ObjectIndex` — so the second one-way form was invisible to both by construction.

**The strongest evidence is that the code already said it.** `CodesysExportForm.cs` documents
`TaskBoth` with *"its native XML is export-only"*, and 114 lines below it in the same file sat:

```csharp
// "The predicate the UI needs for the Tier 3 lock glyph … Kept beside the form table so
//  the next one-way form is declared in the same place as the rule that writes it."
public static bool IsExportOnly(ExportForm form) => form is ExportForm.Libraries;
```

The comment names the rule and the function contradicts it. It had **no caller and no test in its
entire life** — the panel is vendor-neutral and reads a manifest, so the live path never touched
this enum at all. That is why nothing went red: the one place that claimed to enforce the rule was
not connected to anything. It is deleted rather than corrected. **A second home for a rule that
cannot be reached from the code that needs it is not a safeguard, it is a comment that compiles.**

**What it cost a user.** Edit `Task Configuration.xml` in the mirror — a watchdog setting, say,
which the YAML does not carry — commit it, import. The edit is discarded, the log says nothing
useful, `_coverage.md` claims the only one-way files are the two library managers, and the panel
shows an ordinary editable row. The change is in the git history and not in the machine.

**The wrong-remedy half.** Both surfaces that *did* speak hard-coded the library manager's answer,
*"change this in the IDE"* — which for a task configuration walks the reader straight past the
`.tasks.yaml` they were meant to edit. The import log discarded the `sibling` field the script was
already sending it. Both now name the sibling when there is one, and the panel's summary sentence
names no remedy at all: the remedy is per file, so it is stated per file and in one place.

**What is watching it now**, since three of these changes are the kind nothing would have failed
on:

| Guard | Breaks when |
|---|---|
| `TypeTableParityTests.Python_and_csharp_agree_on_which_forms_are_never_applied_back` | the two `EXPORT_ONLY_FORMS` lists drift — verified red by removing `FORM_TASK_NATIVE` from the python one |
| `ObjectIndexTests.The_native_half_of_a_task_configuration_is_flagged_and_its_yaml_half_is_not` | the glyph goes back to a single-constant comparison — verified red by restoring it |
| `ExportCoverageFromRealExportTests.The_task_configurations_native_half_is_recorded_and_named_as_one_way` | the exporter records the wrong form, or the report stops naming the file or its sibling |

The third is against a **real export**, because the defect was in what the exporter wrote and
every unit test in the repository passed with the old form. `report.ExportOnlyFiles` moved
**2 → 3** in that suite: the cheapest possible signal, already asserted, and it could not fire
because it was written before there was a second kind of one-way file to count.

**The general lesson, and it is the one this repository keeps re-learning.** Phase 4 was measured
carefully — four snapshots for `Core2`, three cycles for G11, a mutation run to prove the result
was not vacuous. All of it measured *the thing being fixed*. Nothing measured what the fix changed
about a file it was not about. **A change that makes one file authoritative makes the other file
something else, and the something else needs a name.**

### Two bugs that only running the exporter could show

Phase 4's export half added `TASK_BOTH`: task configuration now writes a `.tasks.yaml` beside
its `.xml`. Unit tests were green, the solution built warning-free, and both defects below
were still there.

**One · the walk descended into the task children.** `descends_into_children` decides the
progress pre-count and a separate guard in `handle()` decides the actual recursion. The new
form was added to the first and not the second, so the exporter walked `MainTask` and
`VISU_TASK` as objects in their own right.

**Nothing was written**, because a `TASK` is `FORM_SKIP`. The only symptom was arithmetic:
`counted 18` against `walked 20`, and `skipped 4 → 6`, on a project that had gained no
objects. That is exactly the divergence `descends_into_children`'s own docstring says cannot
happen — *"the count and the walk cannot disagree"* — and it happened the first time a form
was added after that sentence was written. **A guard that lives in two places has to be
changed in two places, and only one of them is named after what it does.**

**Two · `pous` rendered as python's `repr`.** The task's POU list came out as
`pous: "['PLC_PRG', 'Simulation']"` — a quoted string that merely looks like a list, and
would parse back as one on nobody's reader. `versio_yaml`'s subset had no list-of-scalars, so
a python list fell through to the scalar branch, where `_to_text` did what it is supposed to
do.

**This is the same shape as the `FORM_GVL_BOTH` suffix bug**: a value that did not match any
case in a table quietly took the default, and the default produced a file whose contents lied
about their own type. The emitter gained a scalar-list construct — its first addition since it
was written — and its header now records why, because that file states outright that every
construct added is one both halves have to agree about forever.

**Neither would have been caught by a test**, because neither breaks anything a test asserts:
the file count is right, the bytes are deterministic, and every existing assertion passes. The
first shows up only if you read two numbers in the summary against each other, and the second
only if you read the file.

### Every import after the first fails on task configuration, and reports success

> **In one line:** a stale `<project>_project.precompilecache` beside the project made
> `import_native` throw on `MainTask` and `VISU_TASK` while the run reported `failed: 0`.
> **✅ CLOSED 2026-08-15 by Phase 4**, which stops handing task configuration to `import_native`
> at all — three cycles, zero warnings, where cycles 2 and 3 used to throw twice each. **Versio
> never deletes the cache**: the trigger is simply never reached, so the measured cache-deletion
> mitigation was not needed and was not adopted. `Core2` stopped moving in the same change, which
> closes G7 with it.

Probe P7 of [`phase1/IMPORT-PLAN.md`](phase1/IMPORT-PLAN.md) ran the round trip three times
instead of once, on a scratch copy, with the mirror held still between cycles. Cycle 1 is clean:
`native: 8, failed: 0, warnings: 0`. **Cycles 2 and 3 each throw twice** — on `MainTask` and
`VISU_TASK`, the two children of `Task Configuration`:

```
System.InvalidOperationException: Collection was modified; enumeration operation may not execute.
  at _3S.CoDeSys.ObjectManager.Project.GetUniqueName(...)
  at _3S.CoDeSys.ObjectManager.AbstractPasteOrMergeOperation.DoExecuteObject(...)
```

Nothing is lost — the objects survive and the file is simply not applied.

**The cause is a stale `<project>_project.precompilecache`.** Five runs against the same mirror,
varying one thing each:

| The project directory carries | Result |
|---|---|
| `.project` + `.opt` pair + `_project.precompilecache` | **fails** — `MainTask`, `VISU_TASK` |
| `.project` + `_project.precompilecache` only | **fails** — same two |
| `.project` + `.opt` pair only | passes |
| `.project` alone | passes |
| pristine project, fully round-tripped mirror | passes |

The cache alone is sufficient; the `.opt` files are innocent; the mirror's bytes are irrelevant.
A freshly copied project has no cache and the first import creates one, **so the first import
after a clone works and every one after it fails** — which is why it presents as "the second
cycle", and why it reproduced 4 for 4 across two independent P7 runs.

**This reaches real users and could not reach the suite.** Anyone who has opened their project
in the IDE has a cache beside it. `Versio.Integration.Tests` copied the `.project` and nothing
else, so no test had ever run an import with one present — and both of its real-CODESYS import
tests imported exactly **once**, so they would have stopped one step short even if it did.

> **Both axes closed 2026-08-15** — see the loose end. And the diagnosis in this paragraph is
> half wrong, kept because the correction is the useful part: *copying* sidecars was never the
> missing piece. The **export** writes the `.opt` pair and the **import** writes the cache, both
> now measured from the test itself. So any test that exports, imports, then imports again
> reaches the failing state without help. The gap was only ever the second cycle.

**Deleting the cache immediately before an import fixes it, and the import regenerates it.**
Control with the cache present fails; the same directory with the cache removed passes with zero
warnings. Exporting both afterwards gives `walked 18 / written 12 / skipped 4` either way with
nothing added or removed — the only difference being the two `EventPOUGuid`s, which a *successful*
native import re-mints and the failing one never reached. So it is a working mitigation. Whether
Versio should delete another tool's files on that evidence is a separate question and is not
decided here.

**Related, and fixed on the way past:** `CodesysProjectFormat.TransientPatternsFor` did not name
the cache, so an adopted repository left a 277 KB binary untracked beside the project where
*Stage all* would commit it — see the loose end above.

#### Three smaller defects this exposed — **two and three fixed 2026-08-15, one still open**

**One · the guard written for this exact exception never runs.** The exception is quoted verbatim
in `versio_import.py`'s own `run()` docstring, as the thing the two-phase split with a save
between the phases exists to prevent. The save is conditional on `created > 0 or updated > 0`,
and an import touching only native files has both at zero — every cycle here had
`created: 0, updated: 0`. So **the guard never executed during the measurement that hit the bug
it was written for.** ~~Whether running it would have helped is untested~~ — **one observation
exists now, and it favours the guard.** The Phase-4-reverted run inside
`A_task_priority_edited_…` put the task XML back on the `import_native` path with the cache
present and the exception did not return; that run had `updated > 0`, so the save ran. The
mutation moved two things at once, so this is a lead rather than a result — but it is the first
one. A guard skipped in exactly the case it names is worth fixing regardless, because its
docstring reads as coverage.

**Two · the script's summary contradicts its own warnings.** ~~`ReplaceHandler.progress` catches
the exception and warns but never increments `self.failed`; `handle_file` increments
`self.native` regardless. So the summary reads `native: 8, failed: 0` on a run where two objects
failed.~~ The warnings *do* reach the log at `Warn` through `SyncOrchestrator.LogWarnings`, whose
comment already says the right thing — "the run succeeded but is not complete" — so this was a
wrong one-line answer rather than a silent one. The one-line answer is what gets read.

**✅ Fixed 2026-08-15**, and not as the one-liner it looked like. Counting a partly-failed file
out of `native` also takes it out of `dirty = updated > 0 or native > 0`, which is what decides
whether the run saves — so the naive fix would have made the run that had already written to the
project the one that discarded it. There is a `partial` counter in that test now. **The lesson
generalises past this defect: a counter that decides a side effect is not just a report, and
correcting what it reports can change what it does.**

**Three · nothing in the suite imports twice.** ~~Both real-CODESYS import tests in
`Versio.Integration.Tests` import exactly once, and all of them copy the `.project` without its
sidecars. So the suite could not have caught this on either axis, and a defect that needs a
second cycle has no coverage at all.~~ **Closed 2026-08-15** — and it closed Phase 4's own
missing write coverage with it, because they wanted the same fixture and turned out to be one
test. It imports twice with the compile cache present, asserts the cache is there rather than
trusting that it is, and reads a written priority back out of the project.

> **"and all of them copy the `.project` without its sidecars" was wrong**, which only came out
> when the fix was built. CODESYS writes the `.opt` pair itself on first open, so every test
> that exports before importing already had them; copying them in changed nothing and no
> assertion could see the difference. The cache is the sidecar that mattered, and it is the one
> the five-run table above had already isolated. See the loose end.

> **One thing it measured that contradicts the story above.** Deliberately reverting Phase 4 —
> putting the task XML back on the `import_native` path, with the cache present — did **not**
> bring the exception back. It silently overwrote the typed write instead. The one variable
> that differs from P7 is that this run had `updated > 0`, so the phase-split save in defect
> One above actually ran. Two things moved at once, so this is a lead rather than an answer;
> but it is the first evidence on the question defect One has been carrying, and it points at
> the guard being real rather than decorative.

**The first explanation of this was wrong, and the way it was wrong is worth more than the
finding.** It said this was "the `Core2` mutation seen from the other end" — cycle 1 mutates the
file, cycle 2 chokes on it. That fitted every observation then available, explained the timing,
and named a phase that would fix it. It was written into three documents **before a single
variable had been varied**, and the first experiment that varied one killed it: a fully
round-tripped `Task Configuration.xml` imports cleanly into a project with no cache. A story
that fits one run is a hypothesis, and this one was recorded as a cause.

**Found by running the thing twice.** Ten gaps came out of reading the import path carefully;
this one was invisible to reading, because it does not exist on the first cycle.
`tools\Measure-ImportStability.ps1` is committed so it stays reproducible.

### An import's summary was logged with the exporter's field names, and four of its events with nothing at all

Found while tracing the above. `LocalCodesysRunner.cs:514` handles `case "summary"` with one
message built from `walked`, `written`, `skipped` and `gaps` — which are the **exporter's**
fields. An import summary carries `updated`, `unchanged`, `native`, `created`, `missing`,
`failed` and `notImported`, and `ScriptEvent.Int` returns `int?`, null for an absent key. So
every import logs:

```
walked , wrote , skipped  (of which  not implemented), 2 warning(s)
```

The warning count is the only true number in it, and the six an import actually reports never
reach the log at `Info` at all. ~~**Not observed in a log**, because no import has run through
the app in the retained logs — read from the code rather than from evidence, which is worth
stating rather than blurring.~~ **It was observed on 2026-08-15**, twice, in the logs of the
Phase 4 integration test being built — exactly as written above, blanks and all. Recording that
because the caveat was the right thing to write at the time and the upgrade from "read from the
code" to "watched happening" is the part a reader should be able to see. It is the same class as
the rolling file sink that was never instantiated: a log line that exists, is reached, and says
nothing.

**✅ FIXED 2026-08-15.** The `summary` case switches on the job's operation, and an import reads
`import: 1 updated, 7 native, 2 unchanged, 3 not applied (export-only) — 0 warning(s)`. Keyed on
the operation rather than on which keys are present, so a third script growing a summary is a
compiler error rather than a silently-chosen branch; zero-valued fields are omitted, because
`0 failed` on every line is the noise that let the blank version survive.

**And the same reading found four events with no `case` at all**, falling through to
`_log.Debug("· {type}")`. The worst by a distance was **`task_set`** — Versio setting a task's
priority, interval or **CPU core binding** logged as the literal string `· task_set`, with no
task, field or value. That is the one write the entire G7/G11 argument exists to make deliberate,
and it was the least legible line in the log. `Info` now, naming all three.

**`ImportLoggingTests` is the instrument this finding was missing.** `ExportLoggingTests` had
existed for months; the import had no equivalent, which is the whole reason a line of blanks
survived unnoticed. Two tests — a clean import's numbers, and **an import that loses a file**,
because nothing in the suite had ever run a failing import at all. Both verified by mutation.

### Three moves, three deleted subscriptions, three green suites

The log console was the last of the four panels inside `MainWindow.xaml`, and the one held back
through three earlier passes because it is the only one with real code-behind. `LogPanelView` now
holds it: **`MainWindow.xaml` 1,218 → 936**, its code-behind 412.

**The code-behind turned out to be the interesting part rather than the obstacle.** Two of the
three handlers are the console's own — the list follows its tail on append, the chevron swaps
glyph on expand — and both are view concerns that moved with it. The third could not move at all:
the `GridSplitter` above the console and the `RowDefinition` it resizes belong to the window's
grid, and **a panel cannot own the row it is placed in.** So "the log is open" now moves two
things in two places, and they listen to `IsExpanded` independently rather than one calling the
other, so neither has to know the other exists.

**The geometry test caught something no other check could have.** Moving the card's `Margin`
inside the `UserControl` left the control filling the whole grid cell — flush against the rail —
while the `Border` painted 8 px in. *Nothing looked different*, because the Border still painted
exactly where it always had. But `FindName("LogConsole")` now returns the **container**, so the
named element and the painted element had quietly stopped being the same rectangle, and the
overlap assertion said so. The margin belongs on the host. This is the fourth distinct way a WPF
layout has been correct and wrong in this repository, and the first where the defect was in what
the *test* could see rather than in what the user could.

**And then the number that matters.** Deleting the panel's single wiring line — the
`DataContextChanged` subscription that is the whole reason this panel has code-behind — left
**all 477 tests green**. The chevron would have pointed the wrong way and the log would have
stopped following its own tail, in front of a user, with the suite entirely happy.

That is the **third** time in one session:

| Move | Deleted | Suite |
|---|---|---|
| Auth and identity | `Auth.Changed` subscription | 467 green |
| The remote surface | `Remote.Changed` subscription | 475 green |
| The log console | the panel's `DataContextChanged` wiring | 477 green |

Three for three. **A pure move is the single most likely kind of change to lose its coverage
silently**, because nothing about it looks like new behaviour — there is no new branch to test, no
new failure mode to imagine, and the tests that existed go on passing by construction. The rule
this earns is narrow and worth stating: **after moving a subscription, delete it and run the
suite.** If nothing goes red, the move is not yet finished.

The test that closes this one expands the log **through the window** rather than by constructing
the panel and handing it a context, because `DataContextChanged` is precisely what would not fire
in the second case — a test that built the control directly would have passed against the broken
version.

**Three tests asserted `IsType<Border>` on the console and now get a `UserControl`**, relaxed to
`FrameworkElement` exactly as the history panel's move already required, with the same note:
what those tests want is something to measure, not a particular container.

### The queue said staging; the evidence said the remote surface

`GitRemoteViewModel` — the standing, the background watch and fetch/pull/push/publish, 540
lines. `ShellViewModel` goes **5,251 → 4,958**, under five thousand for the first time.

**It is not what the queue said to do next, and the disagreement was already written down.**
`phase1/GIT-PANEL-PLAN.md`'s GP-E log records that a `GitPanelViewModel` extraction — the staging seam —
was measured and deliberately dropped: *"a move dressed as a decomposition"*, because
`CommitAsync` alone reaches eight collaborators. It named the remote surface as the seam worth
cutting instead. That document also states that where the plan and the phase log disagree, **the
phase log is right**.

Re-measured rather than taken on faith, because a note from an earlier phase can go stale:

| Cluster | Borrows | Calls into the PLC engine |
|---|---|---|
| Staging | 8 | **4** — including `EvaluateGateAsync` and `SatisfyGateAsync`, the export gate |
| Remote | 9 | **2** — `ReleaseAfterPushAsync`, `WhileGitRewritesTheTreeAsync` |
| Repository session | **22** | 4 |

Same order of borrow count for the first two; completely different depth. Staging's seam runs
through the junction where git meets the toolchain — committing *runs a CODESYS export* — which
is the most safety-critical path in the application. The remote surface's two calls are both
narrow, and extracting auth had already made it cleaner, because `Auth` is now a collaborator it
can simply hold rather than a tangle it shares.

**The measuring instrument was wrong twice before it was right, and that is worth recording.** A
first pass scored the clusters against a hand-written list of collaborators, so it could only
find what was already on the list — it missed `BranchName` and `ClearForContextChangeAsync`
entirely and reported 8 where the answer was 9. A second pass brace-counted method bodies from
their signature, which runs away on an expression-bodied member: `WakeThePoller() =>
_poller.Wake();` has no opening brace, so the parser swallowed the rest of the file and invented
four borrows the cluster never had. **A measurement that over-reports argues for the wrong seam**,
which is worse than not measuring.

**`ReconcileAsync` stayed behind.** Including it took the borrow count from nine to eleven for
one method: exiting a divergence reads `Conflicted` and `IsMidMerge` and belongs with GP-G's
conflict surface.

**Nine borrows against auth's four, and the poller is all of the difference.** A background watch
*is* its callbacks — what counts as busy, what to do when the remote moves, what to ask on each
probe — and every one of those is the shell's. They were already delegates before this moved,
which is what makes nine honest rather than alarming. Handing over the object that owns them
instead of the delegates would be passing the shell itself, which is precisely the anti-pattern
GP-E named.

**Deleting the `Changed` subscription left all 475 tests green. That is the second time in one
session** — auth was the first, at 467. Two occurrences stop being bad luck and start being a
property of the work: a pure move looks like nothing to a test suite, so nothing gets written to
watch it. The seam test is therefore a **theory over collaborators** now rather than a class per
extraction, and staging and the repository session should each arrive as one more row — which is
far likelier than each remembering to write its own class. Both existing rows fail with their
subscription removed, verified one at a time.

`CommandNotificationTests` needed the same treatment for the same reason. It named
`ShellViewModel` members, so four commands moving to `Remote` would have silently dropped out of
the theory that exists to catch a command missing from the sweep — the failure it guards against,
one level up. It carries an owner beside each command now.

**The blanket XAML rename introduced a bug, and auditing every site rather than trusting the
regex is what caught it.** The branch dropdown's `AheadBehind` lives inside an `ItemTemplate`, so
its DataContext is a `BranchEntry`, which has its own per-branch `AheadBehind`. It never bound to
the shell's and must not bind to the collaborator's. The other fifteen were then each checked for
the same thing rather than assumed — none is inside a template. Worth knowing for the next
rename: **the binding-path test covers the git panel, and this binding was in `MainWindow`**, in
a template that is only realised once the dropdown has branches.

### The first collaborator out of the shell, and the seam nothing was watching

`GitAuthViewModel` — the credential and the git identity, 545 lines. `ShellViewModel` goes
**5,573 → 5,251**. First out because it had the fewest ties into that class's 68 private fields,
not because it is the biggest cluster; auth and identity travel together because they are one
question asked twice — *can this machine speak to the remote, and does git know who is asking.*

**The four hazards were mapped before a line moved, and all four were real.** `_remote` was read
by three unrelated things; the identity feeds the commit gate; the setters called back into the
shell; the commands gate on the shell's busy flag. None of them turned up mid-flight, which is
the whole argument for mapping a 5,500-line file before cutting it rather than discovering its
shape from compiler errors.

`_remote` went **to** the collaborator rather than staying behind and being passed in, and the
reason is worth keeping: it is the credential's subject. What a failure *means* is decided by the
transport, and who a credential belongs to is decided by the host. Forge detection and the
merge-request button read it too, so it could have gone either way — but splitting a value from
the thing that re-reads it on every open is how the two drift apart.

**Then the part that matters. Deleting the `Changed` subscription left all 467 tests green.**

The extraction replaced two direct calls — `RaiseHealth` and `RaiseGitCommandStates` — with one
event and one subscription. That subscription is now the only thing making the status light move
and the commit button re-ask when a credential fails or an identity arrives. Removing it entirely
broke nothing any test could see. This is the **third** sighting of the same shape in this
repository: three moved panel handlers that no test touched, four commands raised from the wrong
setter, and now a brand-new seam. **A pure move is exactly when coverage goes missing, because
nothing about it looks like new behaviour.**

`AuthSeamTests` is written against the *consequences the shell owns* rather than against the
event — an assertion that `Changed` fired would pass while the shell ignored it. Two of its three
cases fail with the subscription removed; the third, which pins that the shell reads the
collaborator's remote rather than a copy, correctly does not.

**Twelve XAML bindings became `Auth.X`, and nothing in the toolchain can check a binding path.**
Each one is a string that compiles clean, throws nothing, and renders an empty control when it is
wrong. `--trace-bindings` cannot answer it either, and the reason is specific: both banners in
that panel are `Collapsed` on any machine whose git is configured and whose credential is
stored — which is every machine this is developed on. A clean trace over a banner nobody showed
is indistinguishable from success, which is the trap `App.xaml.cs` already documents.

So the git panel now has a test that realises it and asserts **no `BindingExpression` is in
`PathError`**, plus a count assertion so a walk that stops reaching bindings cannot pass in
silence. Mutation-checked by restoring one pre-extraction path: it fails naming the control, the
path and the fact that it does not resolve. It covers every binding in that panel, not only the
twelve this change touched.

**Writing that test found a defect in its own file.** `GitPanelChromeTests.Build` read
`using var shell = TestShell.Build();` and then returned that same instance — disposed at the
closing brace, before any test touched it, so all sixteen tests in the class ran against a
torn-down shell. It had been harmless because none of them opens a repository and disposal had
almost nothing to release. It stopped being harmless in the commit immediately before, when the
shell began disposing the log console the window is bound to.

**Verified in the app, not only in the suite**: identity and credential both resolve through the
collaborator on open (`identity: …`, `credential for https://github.com: stored`), zero `ERR`, and
the binding trace is byte-identical to the run before the extraction.

### The log said the lock was taken and never that it was given back

Found by running the app, which is the fifth or sixth time that has produced something no suite
had. The launch window was clean — zero `ERR`, window constructed, repository opened — and the
log ended, on a deliberate close, at *"change detection stopped (2 watcher(s) disposed)"*. That
is the **first** line of the shell's teardown. Everything after it was silent.

Most of that is explained by the code rather than by a defect. But one piece is not:
`JsonSessionMarkerStore.Release` logs when it **refuses** to release a marker belonging to
another session, and logs when the delete **fails** — and says nothing on the path that runs on
every ordinary exit. `Write` announces the claim. So the log of any given run records the
repository being taken and never handed back, and **a clean shutdown leaves the same trace as a
crash.**

The evidence that separates them does exist, in the wrong place: the *next* launch reports
*"the session that left the marker (pid 34692) is gone"*. That is a different run's log, and no
use at all to somebody reading the log of the session that misbehaved — which is the log they
will have.

This is the house rule about silent success rather than silent failure, and it is the same shape
as the rolling file sink that was written, reviewed and never instantiated: the code was correct
throughout. Nothing was broken. It simply did not say so.

**The second test is the one worth keeping.** Asserting the release line appears is nearly free
and would pass against a version that logged it unconditionally — including on the branch where
the release is *refused*, which would have the log stating that a lock this process never held
had been handed back. That is worse than the silence it replaced. So the pair is pinned: the
refusal says "not releasing" and must not say "released", and the marker is still on disk
afterwards, so the log and the disk agree. Mutation-checked by moving the new line above the
refusal branch — the second test fails, the first still passes.

### The disposal rule could not reach the one thing it most needed to

`ShellViewModel.Dispose` stopped the watcher, the remote poller, the sync and history panels and
the repository lock. It did nothing about `LogViewModel` — which is `IDisposable` precisely
because it owns a running `DispatcherTimer` and a subscription to the log sink.

So a test that obeyed `using var shell = ...` **to the letter still leaked a timer and a live
handler, every time.** That is the sharp part: the rule written up after fifty-six undisposed
shells was being followed, and could not have helped, because `TestShell` builds the log, hands
it to the shell and returns only the shell. Nothing the test can reach owns it. Thirty-six call
sites, all leaking, all of them looking correct.

**The cost is not what the previous write-up assumed.** It said the timer "cannot tick on a
thread with nothing pumping a dispatcher, so the practical cost is the subscription rather than
the work". `StaHarness` disproves it in its own remarks: there is **one** long-lived STA thread
running `Dispatcher.Run()` for the entire suite, and it has to be one, because the theme's
`Freezable`s belong to the thread that created them. Every log built inside a windowed test puts
its 100 ms pump on the one dispatcher that actually pumps. A leaked one keeps firing for the rest
of the run — draining its queue, calling `Filters.UpdateCounts(_sink.Snapshot())` and mutating an
`ObservableCollection` on the same thread every later WPF test is using. That is a mechanism for
a failure that is load-dependent, moves between tests, and never reproduces alone.

**The shell disposes the log now, and it is handed one rather than building it** — so this is a
class taking ownership of an object it did not create. Deliberate, and stated on the property.
Nothing else keeps a reference: `App` hands one over and forgets it, `TestShell` returns only the
shell, so there is no second owner to disagree. The alternative — handing the log back so the
caller disposes it too — makes the rule two things to remember at thirty-six sites, and a rule
with an exception is the rule that gets forgotten. Disposed last, because everything above it in
`Dispose` logs on the way out and those lines should still reach the panel.

**More shells were leaking where no rule could have reached them — one per test, across two
classes, seventeen tests between them.**
`AutomationNameTests` and `ShellLayoutTests` each built their shell *inline as an argument* to
`MainWindow`, so it was never in a variable and there was nothing to put `using` on. The helpers
are instance methods recording what they build now, and the classes dispose them; dropping
`static` was the whole edit, so every call site is unchanged.

**Two things the tests themselves taught, both by failing for the wrong reason first.**

The first version asserted `HeldCount == 0` after disposal. It failed against the broken code —
by two and by three — and it would have failed against the fixed code just as surely, because
`Dispose` stops the pump and releases the subscription without emptying the queue: the entries
the shell logged while it was alive are still sitting in it, and nothing drains them without a
dispatcher. **A baseline that survives the fix is not evidence**, and a test that goes red at the
moment you want it to is not thereby going red for your reason. It asserts on a specific entry now, emitted after disposal, not
arriving. Worth knowing for anything similar: the filter path is unusable here, because `Rebuild`
repopulates from `_sink.Snapshot()` directly and would show that entry whether the subscription
was released or not — a test written through it passes against both versions of the code.

**Measured afterwards, and it does not say what it would be convenient for it to say.** Ten
consecutive full runs of `Versio.App.Tests`: **eight green, two red** — a 2-failure run and a
1-failure run. That is not an improvement on the 1-in-14 the previous fix left behind, and it is
not evidence of a regression either; 1/14 against 2/10 is nothing at this sample size.

**The measurement is contaminated, by me.** A second test host was running filtered classes
concurrently while runs 3 to 5 were in flight, and those runs took 1m38s, 1m57s and 2m01s
against 40-50s for the five that followed. Both red runs are inside that window. Since the whole
phenomenon is load-dependent, a measurement taken under self-inflicted extra load establishes
nothing about the rate. **Run the measurement on an otherwise idle machine, and do not touch it
while it runs** — the same discipline the fifty-six-shell measurement needed.

**What the failures do point at.** Every one of them —
`GitHealthTests.Setting_an_identity_from_the_panel_unblocks_the_commit`, and two in
`AutoStashFlowTests` — is a test that drives **real git subprocesses against a real temp
repository**, and all three dispose their shells correctly. So what survives here looks like
contention for the git binary and the filesystem, not a leaked object. That is a different
problem from the one this finding closes, and it wants a different fix.

**So: the leak is closed, and the flake is not.** Those are separate claims and only the first
one is proven — by a test that fails against the old code and passes against the new, which is
the only instrument here that answers a question rather than a mood.

And the disposal in those two window helpers **is watched by nothing**, by construction:
cross-test resource hygiene is invisible to every test in a suite. So it was broken deliberately,
by disposing on the xunit thread instead of the STA one. That throws
`NotSupportedException` — *"does not support changes to its SourceCollection from a thread
different from the Dispatcher thread"* — from `CollectionView`, and fails all thirteen tests in
the class. It also corrected the comment: the reason given for marshalling to the STA thread had
been the timer's thread affinity, which was a guess about which thing objects first, and wrong.

### A dropdown centred in its own column, half a bar away from its buttons

Reported from the app: *"the 2 drop downs are so separated from their buttons, making it look
weird"*.

The top bar gave each dropdown a `*` column and its buttons an `Auto` column beside it, which
looks right and is not. **A control with `HorizontalAlignment="Stretch"` that runs into its
`MaxWidth` stops growing and is then CENTRED in what is left.** On a 2400 px window each `*`
column is far wider than the 420 px and 360 px caps, so each dropdown floated in the middle of
its column with a strip of empty bar between it and the Open button that acts on it — and the
buttons read as belonging to nothing.

**The pairing is now expressed in the layout instead of implied by adjacency.** One column per
group, with `*` for the dropdown and `Auto` for its buttons inside it. The buttons cannot drift
at any width because there is nothing between them any more.

**The first attempt at that over-corrected, and the correction is the more useful half.** It
bounded each group and left-aligned it, which removed the gap and introduced two more: the
dropdowns shrank, and the two caps differed — 500 against 440 — so the bar came out visibly
weighted to one side with dead space down the middle. Reported immediately, and rightly: *"the
drop downs need to cover most of the top bar, and not be 1 side weighted"*. **A cap on a group
is a cap on the dropdown inside it**, which is not what the cap was for.

Two equal `*` columns and no spacer now. The dropdowns match each other at every window size and
together span everything between the brand and Settings. On a very wide monitor that makes each
wider than its contents need — a repository name is short — and that is the trade taken
deliberately: symmetry across the bar over a snug fit around the text. `ShellLayoutTests` pins
the two properties that were wrong rather than the pixel widths: **the dropdowns match each
other**, and **together they span more than 70% of the bar**.

Worth recording for the same reason the earlier bar fix was: this is a **third** distinct way for
a WPF layout to be correct and wrong. A `DockPanel` whose children all default to `Dock=Left`; a
`Grid` cell holding two children that overdraw; and now `Stretch` silently becoming `Center` at a
`MaxWidth`. None of the three is a bug in WPF and none produces a warning — each is a layout that
did exactly what it was told.

All of it is measured at **2400 px**, because that is the only width at which any of it happens:
at the 1000 px the other layout tests use, the columns are narrow enough that nothing binds and
every version of this looks identical.

### The history did not say which commits were still only on this machine

Reported from the app. Every commit in the panel was drawn identically, so nothing on screen
separated work that exists in one place from work a colleague can already see.

The branch badge says *"2 ahead"* — which does not say **which** two, and stops being an answer
at all on a branch with no upstream, which is exactly the branch whose work is most invisible.

**The query is `HEAD --not --remotes`, and the alternative is the interesting part.** The obvious
`@{upstream}..HEAD` answers *nothing* on a branch with no upstream, and would then need a special
case so that a branch cut from a pushed `main` did not report `main`'s own commits as unpushed.
`--not --remotes` asks the question the reader actually has — *can anybody else see this yet* —
and is correct in all three cases: with an upstream, without one, and with no remote configured
at all.

Read **once per refresh, not once per page**: a later page is older history and cannot contain a
commit the first read would have missed, and re-asking would let two pages disagree about the
same branch if a push landed between them. Capped at 2000, because a repository with no remote
answers with its entire history; beyond the cap a commit reads as pushed, which is the safe
direction — it understates how much is local rather than marking ancient history as unsent.

**An arrow, not a label**, and the first version got that wrong. It was a filled pill reading
"unpushed", which is too loud for a list this dense — a word repeated down every row of a run,
when the run is the thing being read. The arrow is the convention every git client uses, it is
the *same* character the branch badge already spends on ahead-ness, and it needs no legend. The
argument for the pill had been "a coloured dot would need a legend", which is true of a dot and
not of `↑`.

`IsUnpushed` is the one mutable thing on `CommitRow`, and deliberately so. The rest of a commit
is immutable — its hash *is* its content, which is what makes the file cache safe — but whether
a remote has it changes on every push and every fetch.

### A UI audit, and what it says about the ones that got through

Fourteen findings across the shell, the stylesheet and the two dialogs. Twelve are fixed; the
two left open are a mechanical sweep and a feature, and both are in the loose ends above.

**What is worth recording is not the list but its shape.** Almost nothing found was a taste
problem — the token layer, the theme-swap contract and the retemplated controls are better than
most WPF applications ever get. What the audit actually found was **the design system's rules
going unenforced**, in exactly the pattern this document has already named twice:

> a comment describing a rule that nothing enforced

`RepoEntry.Location` was the first instance and `BranchEntry.Tracking` the second. The audit
found the third and by far the largest: `Spacing.xaml` opens with *"Scale: 2 4 8 12 16 24 32.
Nothing off-scale."* and `Space.1`–`Space.7`, `Pad.Row`, `Pad.Panel` and `Pad.Control` have
**zero uses** against **239 hardcoded margin and padding literals** — three of the six most
frequent being 6, 10 and 14, sitting beside 8 and 12 in the same rows. That is why the panels
read as slightly restless while every individual screen looks considered.

The same shape, smaller, four more times: `Motion.Fast` and `Motion.Base` declared with an
argument attached and no `Storyboard` anywhere in the codebase; `Brush.Diff.AddedWord` and
`Brush.Diff.DeletedWord` contrast-tested in both palettes and rendered nowhere; `Radius.Dialog`
and `Font.Size.Title` referenced by nothing.

**A token nobody consumes is a claim about the design that no screen has to honour.** The rule
worth taking: a token file needs the same treatment as a style — declared, used, and something
that fails when it stops being either.

### Keyboard focus was invisible in every control in the application

A grep for `FocusVisualStyle`, `KeyboardNavigation` and `IsKeyboardFocused` across
`Versio.App` returned **one hit** — a background change on `FlatTextBox`. Every other
retemplated control had no focused state at all, so WPF fell back to its own adorner: a 1 px
dotted **black** rectangle. On `Surface.Base` at `#1D2026` that is invisible, and on light it is
the exact piece of 2009 chrome the stylesheet's header spends four paragraphs removing.

Practically: tabbing through the git panel gave no way to tell whether Enter would fetch, pull,
push or discard. **Discard is in that row.**

This is the stylesheet's own stated failure mode, one level up. Its header says *"nothing fails
when a control is simply never styled"* about four controls that kept their Aero chrome; the
same sentence is true of a **property** never set on any of them. A control with no focus visual
builds, lays out and renders perfectly.

Fixed with one shared `FocusRing` applied through the styles, an inset variant for the rail —
where a 40 px button in a 44 px column has no room to ring outward — and per-template outlines
for list and tree rows, because an adorner that expands outward is clipped by the viewport and
the first and last visible row would lose the edge being arrowed towards. `FocusVisualTests`,
13 tests, with the rows focused inside a real shown window: **keyboard focus belongs to a
`PresentationSource`, not to the visual tree**, and an element merely measured and arranged in
isolation refuses `Focus()` and reports `IsKeyboardFocused` false forever — which is how the
first version of those two tests failed while the feature under them worked.

### A horizontal scrollbar was arranged ten pixels wide, and three other things on the same axis

The `ScrollBar` style set `Width="10"` as a plain setter and tried to correct for orientation
inside `ControlTemplate.Triggers`, which only ever set `Height`. Nothing released the width.
Measured, through the app's own theme dictionary by pack URI, in a real 400 × 300 `ScrollViewer`:

```
appStyleFound = True
HORIZ:  ActualW = 10      ActualH = 17.33   usesAppStyle = True
VERT :  ActualW = 17.33   ActualH = 282.67  usesAppStyle = True
```

**A 10 px stub in the corner of a 400 px viewport.** Horizontal bars appear in exactly two
places and both matter — the diff view and the log console — so long ST lines and long log
messages could not be scrolled sideways at all.

Three more defects rode along in the same template, and they share one cause: **an axis assumed
rather than stated.** The thumb hardcoded `Width="6"`, which is thickness on one axis and length
on the other. `Track.IsDirectionReversed="True"` is right for vertical and inverts a horizontal
drag. And the trough's page buttons issued `PageUp`/`PageDown` — they are `Opacity="0"` and
therefore **still hit-testable**, so clicking the trough of a horizontal bar scrolled vertically.
That fourth one the audit did not find; it turned up while splitting the template.

It survived because the scrollbar *was* styled. `StockControlStyleTests` exists to catch
controls that kept stock chrome, so a control with a custom template was never on the list — and
nothing in the suite had ever arranged a horizontal one. Now split into a template per axis, the
way WPF's own theme does it, which is what makes each of the four impossible to write again.
`ScrollBarChromeTests`, 7 tests, including a drag through `Track.ValueFromDistance`.

**What this cannot do, stated rather than quietly not happening:** the 10 px is the bar's own
preferred thickness and applies where a `ScrollBar` stands alone. Inside a `ScrollViewer` it does
not win — that template assigns its bars a dimension from `SystemParameters` as a **local
value**, which outranks any style setter — so the gutter stays the system's 17 px with a 6 px
thumb centred in it. Reaching the last 7 px means retemplating `ScrollViewer`.

### Every settings write reset every other setting

`AppSettings`' own remarks nominated the theme as *"the next preference to need persisting"*, and
persisting it turned up something larger. Both write paths built:

```csharp
await _settings.WriteAsync(new AppSettings { Recent = _recent }, CancellationToken.None);
```

Correct **exactly while the record had one field**. The moment it had two, opening any repository
— which promotes a path and rewrites the whole file — would silently reset the other one.

**The failure would have been invisible until the launch after next.** Pick Dark, open a
repository, close, reopen: still Dark, because the write happened after the read. It is the
launch after that which comes back on Follow system, with nothing in the log and nothing on
screen connecting the two. Found only because the field was added and a test asked whether the
recent list survived writing a theme, and vice versa.

The shell holds the whole record now and replaces by field. `PreferencePersistenceTests`, 12
tests, both clobbering directions among them.

Two smaller things travelled with it. **"Follow system" was a snapshot, not a subscription** —
the registry was read once at startup and nothing listened afterwards, so flipping Windows into
dark mode with Versio open left it in the light palette until restart, on the setting that is
the default and that says by name that it follows. And a restored pane size is now **clamped
against the window it is being restored onto**: somebody who dragged the log to 900 px on a 4K
monitor and then opened Versio on a laptop would get a log taller than the window, the panels
squeezed to nothing, and no splitter left on screen to drag it back with.

### A StaticResource that compiled clean and threw on the first render

The motion work needs `StaticResource` and cannot use `DynamicResource`: a `Storyboard` is a
`Freezable`, and a `DynamicResource` inside one resolves once and is never re-resolved — the
trap `ThemeManager.cs` documents at length.

`Theme.xaml` merges `Spacing.xaml` before `Controls.xaml`, which is enough for the BAML compiler
and **not enough at load**. A merged dictionary resolves static references against itself and
its own merges, not against whatever the parent merged alongside it. Zero warnings, then:

```
XamlParseException: Cannot find resource named 'Motion.Fast'
```

the moment anything rendered a rail button.

**This is the stylesheet's own header happening to the stylesheet** — *"a key can hold a template
that resolves fine and throws the first time something renders it"* — and it is the fourth time
in this repository that a XAML reference has compiled and failed at first paint. It cost nothing
this time because `FocusVisualTests` and `StockControlStyleTests` apply and measure rather than
look up, and they went red immediately. Without them it would have shipped and been found by
somebody clicking the rail.

`Controls.xaml` merges `Spacing.xaml` itself now — fifteen immutable primitives, no colours,
nothing theme-dependent, so the duplicate cannot disagree with the original about anything.

### A status light that could only be right by accident

The leftmost pixel in the status bar, shaped exactly like a health indicator:

```xml
<Ellipse Width="7" Height="7" Fill="{DynamicResource Brush.Status.Added}" />
```

Hardcoded green. Green while git was missing, while the tree was mid-merge, while a sync had
failed and while the branch had diverged — and the shell computes every one of those states,
`GitIsMissing`, `IsMidMerge`, `Sync.HasFailure` and `Standing`, and the status bar read none of
them.

**An indicator that cannot be wrong is not an indicator.** The precedence between the seven
conditions lives in `ShellHealth` in Core now, where it is asserted without a repository, a
process or a clock — because the interesting question is never "does it turn amber" but *which
of the three amber reasons does it name*, and several of them are routinely true at once. No
repository open is neutral rather than green: a green light on an empty shell reports the health
of nothing.

The same block held a second defect of the same kind. The status bar's `Grid` had **no
`ColumnDefinitions`**, so the left stack and the right-aligned repository path occupied one cell
and simply drew on top of each other. Invisible on a maximized 2560 px window, which is exactly
why it survived — `MainWindow` opens `WindowState="Maximized"` and the collision only appears
once somebody restores it, a size no test had ever measured at. `ShellLayoutTests` measures at
1000 px now, with a long path and a long message.

### The four dialogs that were still the operating system's

Every window, tooltip and popup follows the palette. `WindowCaption` goes as far as P/Invoking
`DwmSetWindowAttribute` so the title bar is the *exact* `Surface.Base` rather than merely dark,
on the argument that a seam one shade off reads worse than an honest contrast.

And then four flows called `MessageBox.Show`, which is drawn by the operating system, follows
nothing, and lands as a bright system dialog over a dark application. The four were not
marginal: a rejected folder, a repository open elsewhere, **confirming a discard**, and
**confirming an import into a `.project`** — the two most consequential prompts in the product.

`GuardPromptWindow` already did themed prompts with an arbitrary list of options and a
designated cancel. Routing them through it exposed the smaller half of the problem:
`ConfirmDestructive` was `Func<string, string, bool>`, so there was **nowhere to put the button's
words** — every one of these ended in OK and Cancel while the captions were all phrased as
questions. *"Discard changes to this file?"* — OK is not an answer to that. That is the exact
argument `GuardPromptWindow`'s own remarks make about three-way prompts, and it applies no less
to a two-way one.

The delegates take a `DestructivePrompt` carrying its own affirmative label now, so the prompts
say Discard, Abandon, Delete and Import. **Cancel leads**, which is not a style choice: the
window gives the first option the accent and `IsDefault`, so putting Cancel first is what
preserves the property the `MessageBox` got from its `defaultResult` argument — a stray Enter
must not destroy anything.

### Selecting diff lines did nothing, and copying them did nothing

`DiffView` has set `SelectionMode.Extended` since it was written, so click, shift-click and
Ctrl-click all built a real selection. The container style supplied in the shell reduced the row
to a bare `<ContentPresenter />` with no `IsSelected` trigger, so nothing was drawn — and no
`Copy` binding existed anywhere in the application, so Ctrl+C did nothing either. A user dragged
across twelve lines, saw no response at all, pressed Ctrl+C, and got nothing.

**The bare template was half right, and that is the part worth recording.** A selected fill of
the kind `RowItem` uses would paint over `Diff.AddedBg` and `Diff.DeletedBg`, and the row tint
*is* the information. But "must not overwrite the tint" was solved by drawing nothing, which is a
different thing from solving it. Selection is a wash **on top** now — the accent at low opacity
darkens a green or red row without flattening it, so a selected added line still reads as added.

The copy orders by document position, not by `SelectedItems`, which is in the order things were
picked: a Ctrl-click walk upwards through a hunk would otherwise paste the change running
backwards. The marker travels with the line, because what is pasted is an excerpt of a *diff* —
stripping the `+` and `-` turns "here is what changed" into "here is some code", which is the
half worth sending.

### Icon-only controls announced Private Use Area codepoints

`AutomationProperties` appeared **zero times** in the application. `IconButton` puts the glyph in
`Content`, and WPF derives a control's automation name from its content — so Open, Clone, New
branch, Refresh, Settings, the log's Pause and its Clear all announced `U+E8B7`, `U+E896`,
`U+E710` and friends. That is the whole top bar and the whole of primary navigation. Every one of
them had a `ToolTip` written for a sighted user hovering, and **WPF does not use `ToolTip` as an
automation name**.

An icon-only button with no name is unusable by a screen reader and unreachable by voice control:
there is nothing to say to press it.

`IconButton` binds its name to its own `ToolTip` now, so the names already written reach a reader
with nothing new to author and nothing to keep in step — and it follows a tooltip that changes,
which the log's Pause does: it says Resume while paused. The rail takes its short name from `Tag`
and keeps the sentence in `HelpText`, because a name is what a reader says every time focus
lands and *"Project — the repository as CODESYS structures it, with every change marked"* is not
that. `AutomationNameTests` walks the real shell asserting no control's name contains a PUA
codepoint — **through the automation peer, not through the property**, because the peer is what
applies the fallback to content and the fallback was the entire defect. Reading
`AutomationProperties.Name` directly would have returned empty and looked fine.

### A shared row template learned about objects, and blanked the panel that shared it

GP-J taught `ChangeRowTemplate` to show an object — `Primary`, `TypeLabel`, `IsExportOnly` — and
the **history panel was drawing its commit files with that same template**, deliberately, so
that a deletion would be the same letter and the same colour wherever it was seen.

A `CommitFileRow` has none of those properties. **WPF fails a binding to a missing property
silently**, so every row in the history panel rendered with **no filename at all** — and wearing
a lock glyph, because the `Visibility` binding failed too and a failed `Visibility` binding
leaves the element `Visible`. A panel of blank rows, each claiming its file could not be edited.

**Every test was green.** GP-J added realised-row tests precisely because this codebase has been
bitten three times by templates that only fail when a row is built — and they realise rows in
the git panel's own lists. Nothing came to the history panel. **A shared template means every
list that uses it needs a realised row, not only the list the feature was written for**, and
that is the rule worth taking from this.

Found by the product owner opening the app, which is the third time in this branch that running
it has produced a defect no suite had. The history list has its own template now — the glyph and
its colour still come from `ChangeGlyph`, which is where that decision actually lives — and
`HistoryRowChromeTests` builds a commit file row and reads it. Reverting the template fails both
of its tests.

**A commit's files stay paths, deliberately.** Naming them from the object index was tempting and
is wrong: the index describes the *current* export, so a renamed object would put today's name
on yesterday's file.

### The same repository, twice in the dropdown, because git and Windows spell paths differently

`git rev-parse --show-toplevel` answers with forward slashes; the Windows folder picker hands
back backslashes. `RecentRepositories.PathComparer` was `OrdinalIgnoreCase` — case-insensitive
and **separator-sensitive** — so opening one repository both ways produced two entries, with the
same name and the same location and no way to tell them apart.

The comment above that comparer already had the whole argument in it: *"an ordinal comparison
would let the same repository appear twice in the list, once per way somebody happened to type
it."* It was written about case, and separators are another way it gets typed.

`RecentRepositories.Canonical` now settles on backslashes with no trailing separator, applied on
the way in **and** on the way out — the stored spelling matters because the folder picker is
handed it as a starting directory and because `settings.json` is a file people open. An existing
list tidies itself the next time anything is promoted, which is every launch.

### A rule added after a repository was adopted never reached it

Found while checking the previous finding against the reporter's own repository, which is the
only reason it was found at all. Their `.gitattributes` had the two project rules and nothing
about the mirror; their `git config core.autocrlf` said `true`; and `git check-attr` on an
exported `.st` answered `text: unspecified`. The fix written an hour earlier would never have
reached them.

`AutoAdoptAsync` skips a project that is already bound — correctly, since adopting twice is not
a thing — and adoption is the only caller of the two rule planners. So the rules a repository
gets are the rules that existed on the day it was adopted, forever. Three have been added since
this repository started tracking its own fixture: the mirror's `text eol=lf`, and the staging
and trash directories the swap moves through.

Every scan now tops up the rules for **every** binding, not only the ones just adopted. Both
planners already leave an existing rule alone and report no change when everything is covered,
so it writes once after an upgrade and never again — pinned by a test that opens the same
repository twice and compares the bytes.

This is the third time in this session that a rule was correct and unreachable, and the shape
is always the same: the code that decides is fine, and nothing calls it for the case that
matters. The other two were a guard intent nothing asked about and a filter nothing supplied
globs to.

### The mirror arrives with the wrong line endings, and git says it is fine

The loose end this document has carried since the audit read: *"Adoption writes no line-ending
rules to `.gitattributes`. On a machine with `core.autocrlf=true`, a cloned mirror may read as
entirely modified. Measure it before fixing: the repo's own rule is that a plausible inference
is not a measurement."*

Measured, and **the inference was wrong in a way that matters more than being right would
have**. Against real git, a scratch repository, `core.autocrlf=true`:

```
$ git -c core.autocrlf=true clone origin clone-crlf
warning: in the working copy of 'Main.st', LF will be replaced by CRLF …

$ git status --porcelain          # in the clone
                                  # ← nothing. The tree is clean.
```

Git is perfectly happy: it normalises on the way back in, so nothing reads as modified and no
diff appears. **Versio is not**, because the exporter writes LF and the hash gate hashes the
bytes on disk:

| | |
|---|---|
| `origin/Main.st`, as exported | `6fb69762e1887a3f…` |
| the same file in a `core.autocrlf=true` clone | `c58f937fb0ab1dae…` |

Every file in a teammate's fresh clone hashes differently from what an export of the same
project would produce. The engine reads that as a mirror somebody has edited by hand — the one
state that makes the text side authoritative and points the sync the wrong way.

Adoption writes `<mirror>/** text eol=lf` now, beside the `binary -diff -merge` rules for the
project files. It is the opposite rule for the opposite reason, and they belong in the same
file: a project must never be line-merged, and a mirror exists to be. Proven the same way it
was found — with the rule in place, the same clone with `core.autocrlf=true` still on lands
byte-identical to what was committed, `6fb69762…` on both sides.

An existing rule for the same pattern is still never rewritten, so a repository that has
already decided something about its mirror keeps its decision.

### The auto-stash swallowed its own record, and `.gitignore` could not have saved it

Found by the second test written for the flow, not by reading it. Switching away twice stashed
the work correctly both times — and the second stash took `.versio/local/autostash.json` with
it, because a repository with no adopted project has no `.gitignore` rule for that directory,
so `--include-untracked` counted Versio's own record as the user's work.

The consequence is worse than untidy. That record is the **only** thing that knows which
stashes are Versio's and which branch each belongs to, so stashing it away leaves real work
sitting in `git stash list` with nothing pointing at it — and a later `pop` would restore the
*older* record over the newer one.

**`.gitignore` is the wrong instrument for this**, which is the part worth keeping. It is a
tracked file: a stash carries the rule away at exactly the moment the rule is needed, and
writing it means editing a file the user commits and shares. `.git/info/exclude` is outside the
working tree — it cannot be stashed, committed or conflicted — and it is per clone, which is
precisely what this rule is. It is written when a repository opens, before anything reaches
`.versio/local/`. Adoption still writes the `.gitignore` rule, and that is not redundant: that
one is what a *teammate* gets when they clone.

The same gap applied to the direction locks in any repository where no project had been adopted
yet. Nobody had hit it because a lock only exists once there is a binding, and a binding only
exists after adoption has written the ignore rule — the auto-stash is the first thing Versio
writes into a repository *before* adopting anything.

### The IDE's heartbeat was a `git status` every five seconds, forever — and it took two fixes

Found while checking that the log was quiet after the export-echo fix. It was not: with a
repository open and nothing happening, the app ran a `git status` roughly every 2.5 seconds and
wrote a Debug line for each.

The cause is not Versio's own timers. `_deferredTick` fires every five seconds and does nothing
when nothing is deferred, and no watcher event had settled at all. It is the **CODESYS IDE**:

```
ObjectOrientedProgramming(Extended).~u   14:00:07
… six seconds later …
ObjectOrientedProgramming(Extended).~u   14:00:12
```

CODESYS rewrites its lock marker every few seconds for as long as it holds the project. Each
rewrite is a working-tree event, each event refreshes the status, and each refresh spawns
`git.exe`. **The `.~u` file is gitignored** — adoption writes that rule itself — so it cannot
change what `git status` reports, ever. The work is guaranteed to produce the same answer.

The measurement that matters is that this is not an edge case: **an engineer with the IDE open
is the normal state of this tool**, and in that state the app spawns ~24 git processes a minute
and writes ~1,400 log lines an hour, against a design goal of eleven.

Two things make the fix tractable rather than speculative, and both already exist:

- **Versio knows which paths it told git to ignore.** Adoption writes `.versio/local/`,
  `<name>-AllUsers.opt` and `<name>-<user>-<machine>.opt` into `.gitignore`, so those are
  already a list rather than a guess.
- **The vendor seam already answers where the lock marker is.** `IPlcProjectFormat` reports it
  — that is how open-project detection works — so the rule stays vendor-neutral: *a path the
  format declares as its lock marker is not a working-tree change.*

**Fixed, and the fix was wrong twice before it was right** — which is the part worth keeping.

`RepositoryChangeFilter` learned about paths **Versio itself put in `.gitignore`**, supplied by
the vendor seam: `TransientPatternsFor` is the same call adoption makes when it writes the
rules, so what the watcher ignores and what git ignores cannot drift apart. That is the one
exception to this class's standing rule that working-tree paths are never filtered by name, and
it is safe for a specific reason — these are not names guessed to be ignored, they are the rules
this app wrote.

Then the app was relaunched and **it polled exactly as before.** Watching the repository
directly for 20 seconds said why:

```
Samples\ObjectOrientedProgramming(Extended).~u~   7
Samples                                          4
Samples\ObjectOrientedProgramming(Extended).~u    4
```

CODESYS does not write the marker in place. It writes `.~u~` — trailing tilde — and renames it
over `.~u`, so the glob covered the less frequent of the two, and **`.~u~` was in nobody's
`.gitignore`**: a file appearing and vanishing in the working tree that git had never been told
to ignore. The pattern is `.~u*` now, which fixes the ignore rule as well as the noise.

Relaunched again, and **it polled exactly as before.** The remaining trigger was the third row:
a folder's own timestamp moves when a child appears or vanishes, so filtering the child by name
left the parent's event arriving at the same rate. Dropped by the same argument the bare `.git`
event already used — the folder's event says only that *something* changed, and the child's own
event says what. A creation, deletion or rename of a folder is not a write and still gets
through.

| | |
|---|---|
| Before | ~2 `git status` per 5 s, 493 log lines in one 50-second window |
| After | **1 `git status` in 45 seconds of idle** — the one at startup |
| Still detected | a new file, in 0.5 s; its deletion, in 0.5 s; both verified in the running app |

What remains over a longer watch is the **20-second backstop tick** — 24 calls in eight idle
minutes, one every 20 s to the millisecond — which is `StatusRefreshPolicy` doing exactly what
it was written to do, since `FileSystemWatcher` drops events silently and a panel that is 20 s
stale beats one that is stale forever. It is worth stating because this document elsewhere
claims an idle app writes about eleven lines an hour: with a repository open it writes about
180, all of them the backstop's own `git status`. That is a design decision, not the bug that
was just fixed, and it is the next thing to look at if idle cost ever matters again.

The lesson is the one this document keeps recording in different clothes: **the rule was correct
in unit tests while the app still spawned git every five seconds, twice, for two different
reasons.** What closed it was a real `RepositoryWatcher` over a real directory, replaying what
CODESYS actually does — write, rename, repeat — and asserting nothing comes out, then that a
real edit beside it still does.

### An export woke the change detector once per file it had just written

Noticed in the log while checking something else, on a real project with 56 exported files: for
about forty seconds the app ran a `git status` and hashed the whole mirror roughly twice a
second, with git invocations overlapping each other — `still running` — and each one ending in
a `BlockedByLock` warning.

The mechanism is one line in the log above the storm:

```
watcher error on …\ObjectOrientedProgramming(Extended) (Access is denied.) — events were lost
```

The export stages into `<mirror>.versio_staging` and **swaps the directory into place**, which
invalidates the `FileSystemWatcher`'s handle on the directory it was watching. The error
handler does the right thing — an overflow means events were lost, so rescan everything — and
the rescan then finds all 56 files changed, because they are new files, and reports each one as
a settled change. Every one of those became a sync job: a conflict probe (a `git status`
process) and a full-tree hash, before the direction lock refused it. **n files cost n status
processes and n full-tree hashes.**

`SelfWriteSuppressor` exists for exactly this and could not do it. It registers *one* path with
*one* expected hash, consumed by the first matching event, and `ExportAsync` registers it
**after** the run completes — tens of seconds after the swap those events describe. One late
registration against dozens of echoes. It had been that way since the suppressor was written,
and it never showed up because **the direction lock was quietly doing its job**: an export only
runs while the project side is authoritative, and a mirror edit is refused in that state anyway.
Nothing was ever lost. It was pure cost, and it is quadratic in the size of the project — worst
exactly where projects are large.

The fix is a window rather than a set of hashes, because that is the shape of the problem: the
mirror is mid-write for a known stretch of time and every event in it is ours.
`BeginOwnWrite(bindingId, side)` is the same mechanism as the git-write window (§5.A), with two
deliberate differences:

- **Per binding, and per side.** A checkout rewrites the whole repository; an export rewrites
  one mirror. Holding every binding off for the 20–31 s a toolchain run takes would drop real
  edits elsewhere, and an engineer saving in the IDE *during* an export must still be seen.
- **Opened before the run and closed after the bookkeeping**, because the echoes are raised by
  the swap in the middle and settle whenever the watcher's quiet period lets them — which is
  long after the toolchain has exited.

Nine tests, five of which fail with the gate reverted. The one that matters is the ninth: it
drives a **real export** through the orchestrator and asks, from inside the run, whether the
window is open — every other test opens it by hand and would pass against an export that never
called it. That is the mistake this repository has now shipped three times, and it fails when
the call is removed.

Not fixed, and worth knowing: the watcher's response to a lost-events error is still to report
every file in the tree individually. The window makes that free for our own writes; a genuine
overflow — a network share, antivirus — still produces one job per file.

### The repository dropdown could not do the one thing it exists for

Reported from the app, with a screenshot of the control open and holding exactly one row:
*"the repo drop down is supposed to store recent repos… currently it doesnt store anything."*

`RecentRepositories` was an `ObservableCollection` on the view model, appended to when a
repository opened and dropped when the process exited. Nothing wrote it anywhere and nothing
read it back, so on every start it contained precisely the repository the app had opened by
itself — and a control whose entire purpose is jumping between repositories could not jump
anywhere. It had looked right in every session where somebody opened a second repository by
hand, which is every session it was ever demonstrated in.

Underneath it was a gap rather than a bug: **Versio had no machine-level state at all.** Every
store it owns belongs to a repository and lives in that repository's `.versio/`. Which
repositories this user has opened is not repository state — it names paths that exist on one
disk — so there was nowhere for it to go. `%LOCALAPPDATA%\Versio\settings.json` is that
place now, beside the logs, written through the same atomic temp-and-replace `JsonLockStore`
uses and read with the same rule: unreadable costs a dropdown's contents and nothing else.
The theme is the next thing that should join it — it is currently reapplied as `System` on
every start, which is a preference the app forgets just as thoroughly.

Worth pairing with the finding below about *Open logs folder*: writes under `%LOCALAPPDATA%`
are redirected when the app runs from an MSIX/AppContainer, so a packaged build and a plain one
can end up with two separate recent lists. Nothing to fix today — packaging is unbuilt — but it
is the same environment fact, and it now applies to a second file.

Three decisions in it are worth keeping:

- **The remote is recorded at open time, not when it is needed.** By the time it is wanted —
  offering a vanished repository back — there is no `.git` left to read it out of. Promoting an
  entry inherits the old URL when the new read comes back empty, so opening a repository while
  its remote is unreachable cannot erase the only thing that could restore it.
- **A missing repository is a question, not a refusal.** Every row in that dropdown is a
  repository the app told the user they could come back to, so one that has vanished offers
  *clone it again*, *remove from list* and *cancel* — where the generic path says "not a Git
  repository" and opens a folder picker. Which options exist is a pure function of whether a
  URL was recorded, because a *Clone it again* button on a repository that never had a remote
  is a button that cannot work.
- **Nothing is removed on discovery, and nothing deletes a folder.** A disconnected network
  drive and a deleted clone are indistinguishable from here, and quietly forgetting the second
  also forgets the URL that could bring it back. The label says *Remove from list*, never
  *Delete*, because deleting the folder is what the user is afraid the button does.

One existing test had to change rather than being made to pass: `A_repository_that_cannot_be_
opened_leaves_the_dropdown_where_it_was` asserted that a dropdown row which will not open
raises `RepositoryRejected`. It now asserts the missing-repository question instead — the
guarantee it was written for, that the dropdown never points at a repository that did not
open, is unchanged and still asserted.

Startup gained a third fallback while this was wired: an explicit path argument, then the
working directory *if it is inside a repository*, then the most recent repository still on
disk. Launched from a desktop shortcut the working directory is wherever Windows put it, and
the app opened on an empty window beside a dropdown that had forgotten everywhere the user had
ever been.

Five of the seven new view-model tests fail when persistence or the missing-repository check is
reverted, and they come in through `OpenAsync` and `SwitchRepositoryAsync` rather than through
the store — this repository has been bitten three times by a method that was perfectly tested
and called by nothing.

**And the first version of this shipped a bug of its own, caught by running the suite.** The
view model defaulted to the real store, so every test that opened a throwaway repository
rewrote the developer's own recent list: two `versio-reset-…` temp folders were sitting in
`settings.json` when it was found. The default is a store that remembers nothing, the real one
is wired by the application host and nowhere else, and there is now a test that opens a
repository through the default constructor and asserts the user's settings file is byte-for-byte
unchanged.

### One field's type disagreed, and the panel lost fifty-six objects without saying so

Reported from the app against a real customer project, with a screenshot of the CODESYS Devices
tree beside a screenshot of Versio: *"is not rendering at all in a correct format."* The panel
showed `0 object(s) · 60 changed file(s)`, `coverage unknown`, and sixty rows of raw
`Device/Plc Logic/BuildingApplication/…` paths where a tree belonged.

`versio_export.py` has always written `warnings` as records — `{"code": …, "path": …,
"detail": …}` — and `ProjectManifest.Warnings` was declared `IReadOnlyList<string>`.
`JsonSerializer.Deserialize` threw on the first record, `Parse` caught the `JsonException` and
returned `Empty` exactly as its own documentation promised, and **one field's type disagreeing
discarded all fifty-six objects**. Everything downstream then behaved correctly on an empty
manifest: no object nodes, so every exported file fell through to the unmapped-file list as a
raw path, and `ExportCoverage.Of` reported schema 1 and answered "unknown".

Three things had to line up for this to survive a whole feature:

- **The fixture produces no warnings.** `RefrigeratorControl` has none, so `"warnings": []`
  is what every test used — and an empty array is valid for both shapes. `_manifest.json` is
  written by IronPython inside CODESYS and read by System.Text.Json, and nothing on either
  side of that boundary had ever seen a non-empty one.
- **The property was read by nothing.** `Warnings` was populated, exposed, and consumed
  nowhere in the app, so no test, no view and no log line ever looked at it. It could only
  ever cost something, never earn it.
- **The failure was silent by design.** Returning `Empty` for an unreadable manifest is the
  right instinct — the window must not come down over a convenience file — but `Empty` is
  indistinguishable from a manifest that genuinely describes nothing, so the panel had nothing
  to report and reported nothing.

Fixed in three parts, and the third is the one that matters. `ExportWarning` is now the record
the exporter writes. `Parse` reads **field by field and element by element**, so a field this
build cannot make sense of costs that field and leaves the rest standing — a killed export that
truncates one object record loses one row rather than the tree. And `Parse(json, out problems)`
names what it skipped, which `SyncViewModel` logs at Warning, gated by the tree signature so a
broken export says so once rather than per refresh.

Verified against the reporter's own manifest: `schema=3 objects=56 skipped=4 warnings=2
problems=[]`, coverage `CompleteByDesign` rather than `unknown`. Seven new tests, each checked
against the mutation that should break it — reverting to a whole-array parse fails the
per-element test, removing the per-field `catch` fails the per-field test.

### A function block's methods were drawn as its siblings, and four InitRooms sat in one folder

Same report, one level down. CODESYS draws `clsLight (FB)` with an expander and its methods
inside it; Versio drew `clsLight`, `Light`, `SetLifeTime` and — in the rooms folder — four
separate rows called `InitRoom`, with nothing saying which room each belonged to.

The export writes a member **beside** its owner, as `clsLight.Light.xml` next to `clsLight.st`,
so ownership was expressed in a filename and nowhere else. The manifest recorded where a
method's *file* sat and not what the method *belonged to*, and a tree assembled from paths
therefore could not have got this right.

The fix is manifest **schema 4**: `record()` writes the owner's GUID as `parent`, and the tree
builder nests by it. A GUID rather than the owner's name or its path, for the reason identity is
a GUID everywhere else here — a rename must not re-parent anything, and reading ownership back
out of `clsLight.Light.xml` is a guess that any name containing a dot defeats. It is a chain
rather than one level, because a property owns its accessors.

Three consequences were not obvious and are each pinned by a test:

- **`PruneUnchanged` tested `IsFolder`.** Objects had no children until now, so an unchanged
  function block was treated as a leaf and dropped — taking an edited method with it. It tests
  for children now.
- **The search had to move after the nesting.** Filtering leaves first would match a method and
  drop the block that leads to it, leaving the match unreachable. A node that matches keeps all
  its members; one that does not survives only for the members that do.
- **A cycle in a manifest is possible.** It is a file on disk and can say anything, and an
  object claiming its own descendant as its owner would recurse forever inside a background
  build — which presents as the panel simply never appearing again. The walk carries a visited
  set.

An older manifest carries no parents, and that reads as a flat project rather than as an error:
a mirror starts nesting when its project is next exported. Verified by exporting the reporter's
project with real CODESYS — `schema=4 objects=56 withParent=22`, every parent GUID resolving to
an object in the same manifest, `InitRoom` owned by `clsBetterRoom`, `clsCellarRoom` and
`clsCheapRoom` respectively.

The committed fixture was re-exported rather than hand-edited, and the fresh export was compared
byte for byte against the committed one: **`_manifest.json` alone differs, by the schema line**.
That is also the strongest determinism check the export has had — eleven files, two runs,
months apart, identical.

**The corpus is the finding underneath both of these.** One fixture project, with no members and
no warnings, cannot see either bug. Every field report should become a fixture, and this one is
the first candidate.

### Two of the three panels used half the card they sat in, because Dock defaults to Left

Reported from the app: *"the git still, notice how is not using the full panel."* The git panel's
rows stopped about 310 px into a 557 px card, leaving a column of dead surface down the right of
every row in it.

One default did it. The card held a `DockPanel`; **`DockPanel.Dock` defaults to `Left`**, and only
the last child fills. The three panels are listed git, history, project — so the *project* panel
filled the card and the other two were docked left at whatever width their content happened to
want. The git panel was 310 px wide because that is what a branch card and two button rows add up
to.

Nothing about it looked broken from the inside: nothing was misaligned, nothing overflowed,
nothing threw, and every test passed. It is a panel correctly sized to the wrong thing — and it
had been that way since the panels were split, hidden while the git panel still carried the stash
controls and enough width to look deliberate.

The card is a two-row `Grid` now: the title, then one filling row that all three panels share,
with exactly one visible at a time. The test arranges each panel *through its rail button* and
asserts the width the panel renders at equals the card's, which is the assertion that would have
caught this at any point in its life. Checked by pinning the git panel back to its content width,
where it fails.

### Two things overlapping, and a test suite that could not see either

Both reported from the running app, and neither was reachable by anything in the suite: it
constructed the window and measured it once, at a comfortable width. **An overlap is not a
failure in WPF.** Two controls sitting on top of each other measure and arrange perfectly well —
the layout did exactly what it was told.

**The log console ran underneath the rail.** The rail lived inside the body *row*, so it stopped
at the bottom of the panels, while the log console — a sibling row — spanned the full width of
the window beneath it. Two surfaces of the same `Surface.Base` meeting at a T-junction under the
rail icons, which is precisely what "the logs bar overlapping the left side bar" describes. The
rail is app-level navigation, so it now lives in the outer grid and spans the body and the log;
the log starts where the content starts. The status bar still spans the full width, which is
where a status bar belongs.

**A section label and its buttons shared one Grid cell.** `NOT STAGED` with `Discard` and `Stage
all`, `STAGED` with `Unstage all`, the panel title with its count badge, `COMMIT MESSAGE` with
its length counter, and `DIFF` with a checkbox and a 420 px path. A right-aligned child in a
shared cell grows *leftwards across* the label rather than pushing it, and neither knows the
other is there — so in a 380 px panel the label and the first button touched. Every one of those
headers is two columns now: the label takes what is left and trims, the buttons keep their width.

The tests assert **rendered geometry** — bounds transformed into a common ancestor, at a 1000 px
window — because that is the only form in which "these two things are on top of each other" can
be written down. They also select the git panel through the rail first: a collapsed panel
arranges to nothing, which would have made every assertion vacuously true. All three fixes were
checked by putting the old layout back, where each fails.

Two smaller things went with it. The branch card's *Fetch / Pull / Push* are compact: at the full
32 px they set the height of the whole card and pushed the file lists down for nothing. And
`Size.Rail.Width` is finally what sets the rail's width, instead of a hardcoded 52 sitting beside
an unused token.

### An editor that corrupted every em-dash in a file it was only reformatting

Not a design finding, and worth recording anyway because the damage was invisible in the diff
that mattered and glaring on screen: the commit-box placeholder rendered as
`Summary of the changeâ€¦`.

`MainWindow.xaml` was edited in a few places with PowerShell's `Get-Content -Raw` /
`Set-Content`, which reads with the system's ANSI codepage and writes UTF-8. Every one of the
file's 18 em-dashes and ellipses — all of them in tooltips and comments — round-tripped into
mojibake, and a BOM was added to a file that never had one. **Nothing caught it.** The build was
clean, all 116 tests passed, the binding tracer was empty, and XAML is perfectly happy to render
a corrupted string. It took a screenshot of the app.

Repaired by re-decoding: the file's only non-ASCII characters were the mojibake sequences
themselves, so reading the UTF-8 bytes back through CP1252 is exact rather than a guess. Verified
by counting `…` and `—` against the previous commit — 18 in both — and by checking the first three
bytes are `3C 57 69` rather than a BOM.

The lesson is narrow and worth keeping: **edit source files with an editor that preserves
encoding, not with a shell round-trip.** If a shell must do it, `[IO.File]::ReadAllText` /
`WriteAllText` with an explicit `UTF8Encoding($false)` is the pair that does not corrupt anything.

### The stylesheet's own argument, not applied to four controls

`Controls.xaml` opens by naming the four things that made the first pass look like a Win32 app:
visible borders on every control, the system highlight brush for selection, hard dividers, and
colour emoji. It then fixes all four — for the controls it retemplates. Four it never touched
kept every one of the problems the header describes.

**Checkboxes and radio buttons were WPF's own.** The settings popup's three theme radios, *Show
full file*, *+ untracked*, and every row of the category dropdown were drawing Aero's shaded
square and shaded circle — sized for a 2009 desktop, lit as though from above — on a dark
surface. Sixty lines each: a 16 px box with `Radius.Control` and the icon font's check glyph, a
16 px ring with an accent dot. The label is inside the hit area now too, so a click no longer
has to find a 13 px square.

**Both TreeViews painted selection in Windows' highlight blue.** `ListBoxItem` was retemplated
in the first pass *because* the system highlight is the loudest tell — and the project tree, the
surface somebody looks at all day, was not a ListBox. It was a TreeView, so it kept the stock
template: highlight-blue selection and the Aero expander triangle. When the history panel was
added it inherited exactly the same thing. `TreeRowItem` is what `RowItem` already is for the
lists — a tinted fill plus an accent bar — with a chevron rotated by a trigger rather than two
glyph keys that can drift apart.

Two details in it are deliberate and worth keeping:

- **`IsSelected`, not `IsSelectionActive`.** The stock template greys the selection out when
  focus leaves the tree, which happens the moment somebody clicks a file to read its diff — so
  the file whose diff is on screen looked unselected. The row stays lit here.
- **`IsExpanded` is not set by the style.** Both trees bind it two-way per row, and that binding
  is what drives the project tree's lazy children and the history's demand-driven file lists. The
  retemplate puts a `ToggleButton` on the same property, so the test that matters asserts the
  round trip in both directions: expanding a node in the UI must still reach the row, or the
  panels quietly stop loading anything and nothing else looks wrong.

**And a style can be right and unused.** The tree tests build their own `TreeViewItem`, which
proves the template and nothing about the app. A second test asserts both TreeViews actually
name it — because the project tree needs a repository open before it has a single row, so a
missing `BasedOn` would have shipped with every test still green. Checked by removing it from
one of the two, where it fails. Same for the implicit styles: they are keyed by type, nothing in
any view refers to them, and deleting one changes dozens of controls without a single lookup
failing.

### A top bar crowded into its left third, with two control heights in one row

Reported from use: *"the repo and branch bars should cover more real estate horizontally so it
looks more balanced, currently very heavy to the left."* Three separate causes, all visible at
once and none of them nameable without opening the file.

**One flexible column, in the wrong place.** Six columns, five of them `Auto`, and the single
`*` sat on the *branch buttons* — so on a 2560 px window the slack piled up in the middle while
both dropdowns stayed pinned at their hardcoded 230 px and 200 px. Two `*` columns now carry
the dropdowns, bounded with `MaxWidth` so a wide window widens them without handing one of them
half the screen, and a third `*` before Settings is what holds the right-hand cluster right.

**Two heights in one row.** `Size.Control.Height` was 28 and `IconButton` hardcoded 32, so every
dropdown in the bar sat four pixels shorter than the buttons beside it. That is the ragged
baseline, and it is the reason the bar read as unfinished even where the spacing was right. Both
read the token now, at 32, with `Size.Control.Compact` (24) named once for controls inside a
strip that is itself dense — the log header, the section-label rows — rather than a third value
picked per view.

The test measures both controls after `Arrange` instead of reading the styles, because two
controls can name the same token and still render differently if one of them also sets a padding
or a border. Checked by putting the old values back: it fails.

**The mark existed and was nowhere.** `assets/versio.svg` — three plates and a status badge — was
drawn for the `.ico` and never appeared in the app, which showed the word "Versio" in accent
blue instead. The geometry is now also `Theme/controls/Brand.xaml` at 20 px, and the SVG's header
names all three places that carry it. A `ControlTemplate` rather than a `DrawingImage`, and not
as a matter of taste: a `Freezable` in a resource dictionary resolves its `DynamicResource` once
and is not re-resolved when the palette underneath is replaced — the trap `ThemeManager` already
documents — so a `DrawingImage` mark would have kept the colours of whichever theme started
first.

**And accent was doing six jobs.** Brand, commit hashes, ahead/behind, watch state, the busy
caption and every category in a scrolling log. A colour that marks six things marks none of
them, so it is now spent on selection, the primary action, the count badge and the progress bar,
and the rest moved to `Text.Secondary` or `Text.Disabled`. The one that gained meaning rather
than losing colour is divergence: `BranchEntry.BrushKey` says `Status.Modified` when a branch is
*behind*, because that is the state a pull answers, and secondary when it is merely ahead —
which is normal and should not be painted like a problem.

### The app themed everything except the strip Windows draws, and every tooltip in it

Reported from use, and it is the first thing anybody sees: *"the top windows bar should follow
color theme, so if its dark theme then it goes dark as well."* A dark shell under a white
caption, with the app's own top bar — `Surface.Base`, an exact colour — immediately below it.

Two surfaces were unreachable from the stylesheet, for two different reasons:

**The caption belongs to the window manager.** No style, template or brush reaches the
non-client area; the only way in is `DwmSetWindowAttribute`. So the colours are handed over
directly — `CAPTION_COLOR`, `TEXT_COLOR` and `BORDER_COLOR`, with `USE_IMMERSIVE_DARK_MODE`
behind them for Windows 10, where the three colour attributes fail and dark mode is the whole
effect. `Surface.Base` is passed rather than settling for immersive dark mode alone, because
that gives a generic dark grey that is *close* to the top bar and not equal to it, and a seam
one shade off looks worse than an honest contrast.

**Tooltips are popups, and there are dozens of them.** Every one is a plain string on the
element it explains, and not one named a style — so every one was the stock WPF tooltip: a
near-white slab with a hard border, opening over a dark application. An implicit style dresses
all of them at once, including the ones nobody has written yet. That is also why the style is
pinned by a test: nothing in any view refers to it, so deleting it changes the look of dozens
of controls and no lookup anywhere fails.

**Two halves, and the second is the one that gets forgotten.** Painting on load is correct
until the user switches theme with windows open, at which point every caption keeps the colour
it was born with until the app restarts — the same shape of half-working theme switch already
recorded below, one strip further out. So `ThemeManager.Apply` repaints every open window as
part of the swap, and a test asserts it: two windows built, theme switched, both repainted.

**The reach is what is tested, not the paint.** The DWM call needs a shown window with an HWND,
and this suite deliberately shows nothing. What the tests assert instead is that every window
which loads gets to the painter — a plain `Window` and a real dialog, separately, because "it
works on the main window" is exactly how this fails — and they do it by raising `Loaded` by
hand, since a class handler fires on `RaiseEvent` whether or not anything is on screen. The
byte order gets its own test: COLORREF is `0x00BBGGRR`, and reversed, the caption comes out a
warm brown that reads as somebody's decision rather than a bug. The interop itself was
confirmed separately against the running app: all four attributes return `S_OK` on 10.0.26200.

**A class handler rather than four constructors.** `EventManager.RegisterClassHandler` on
`Window.Loaded`, registered once at startup, covers the shell, all three dialogs, the spike
window, and the fifth window that would otherwise have been the one somebody forgot. Two
surfaces remain honestly out of reach and are listed rather than hidden: `MessageBox` is a
Win32 dialog, and `OpenFolderDialog` is the shell's own.

### Twenty seconds of work with nothing on screen, and four separate reasons for it

Reported from use, and precisely: *"I save, the PLC tree immediately shows the changed file,
and after a few seconds the changes get highlighted. I'm assuming the export is running, but
there is no visual indication of this."*

The reading was exactly right. The `.project` appears the instant git notices it — about 300 ms
— and the diff people are actually waiting for appears when CODESYS finishes, 17–31 seconds
later. In between, the app looked idle while doing the most expensive thing it ever does.

What made it worth a full investigation is that **every mechanism for showing it already
existed, and all four were disconnected.** None of them was missing; each was wired to
something that could not deliver it.

| Mechanism | Why nothing appeared |
|---|---|
| The app-wide progress bar | `SyncViewModel` takes a `BusyTracker` and **defaults to constructing its own**. The bar binds to the shell's. So the bar never moved for *any* sync work — including the catch-up and commit-gate exports, which both carefully open a scope for it |
| `ProjectRow.Busy` and the `"exporting…"` state | Documented in the class — *"it says exporting… for 17–31 seconds"* — and **assigned by nothing**. `RefreshStalenessAsync` even guards with `!p.Busy`, a condition that could never be true |
| The heartbeat | `HeartbeatService.Track` was called from exactly one place, the commit-gate path. The automatic save path started no tracker, so `Report` found nothing to attach to and returned |
| The progress detail itself | `PlcJob.OperationId` defaulted to a **fresh random value**. The runner tags its phases and counts with the job's id; the heartbeat is keyed by the operation's. Two unrelated strings, so every phase the toolchain reported was dropped — on *every* path, including the one that did start a heartbeat, which could therefore only ever show elapsed seconds |

The last one is the most instructive: an end-to-end pipeline, complete at both ends, discarding
its payload at the final hop because two ids that had to be equal were each independently
correct.

**The announcement is a notification, not a wrapper.** It would be simpler for the caller to
put a bar around its call to `HandleChangeAsync`, and it would be wrong: the overwhelming
majority of calls are refused by a gate in milliseconds — a save that changed nothing, a
coalesced burst, a throttled repeat — and a bar flashing on every settle is worse than no bar
at all. `RunStarted` fires after the last gate, `RunFinished` from a `finally` so a failure or
a throw cannot leave the bar up. A bar that never goes away is indistinguishable from a hang,
which is the very confusion being fixed.

**The row is shown and marked, not hidden.** Hiding it until the export finished was the other
option and it is the wrong one: the `.project` really is modified in git the moment you save,
and a panel that disagrees with `git status` is how somebody concludes Versio lost their work.
One further defect fell out of choosing that: re-exporting a project whose file matches HEAD
made its row **vanish for the whole run**, because the "show what has something to review" rule
had nothing to show. The tree said so in its own log — `1 project(s) hidden as in sync` — while
CODESYS was running against it. A run in flight now counts as a reason to be visible.

### Two loop defences that had never actually run

Found while tracing the above. Both are in the coalescer, both were written correctly, and
neither had anything driving it.

**A queued follow-up was dropped, and the coalescer was left believing a job was running.**
`OnJobCompleted()` returns `true` to mean *a change arrived while that run was going and must
be handled now*, and sets the slot as running again on the strength of the caller obeying it.
The caller discarded the result. So the follow-up never ran — and worse, from that moment every
later change on that binding answered `Queue`, then `Coalesced`, for the rest of the session.

It stayed invisible because **the commit gate is synchronous and skips the coalescer entirely**,
so exporting still worked at the one moment anybody checked. The plan's worked example — four
saves during one export producing two exports — produced one.

**A throttled change waited for something that did not exist.** `TryStartThrottled()` is
documented as *"used by the idle tick"*; there was no idle tick and no caller anywhere. The
pending flag that "guarantees a final export once things go quiet" guaranteed nothing precisely
when things went quiet: the change waited for the next save, or for somebody to commit. There
is a five-second tick now, and the deferred job re-enters the whole pipeline rather than being
replayed, so the hash gate can say the reason to run has gone.

### "Cancel and restart" cancelled nothing, and ran two CODESYS instances instead

The most consequential of the three, and it was found by watching, not by reading.

A save arriving in the first five seconds of a run returned `CancelAndRestart` — *"cheap to
restart this early, and it avoids 40 s of work we know is stale"*. Sound reasoning, and the
mechanism was never built: **no cancellation is plumbed through to a running toolchain job**.
The orchestrator's only way to act on the instruction was to start a second export beside the
first.

Observed doing exactly that. One `git checkout` of the project file produced two settled
changes 1.8 s apart, both inside the window, and the log shows two exports running
concurrently against one project:

```
23:11:23.548  {4da031c5} Project change (automatic, Project)
23:11:25.351  {81e9666a} Project change (automatic, Project)
…
23:12:09.269  {4da031c5} exported 12 object(s) in 45.6s
23:12:32.748  {81e9666a} ExportSucceeded accepted
```

**45.6 s for a job that takes 17–31 s alone** — the two instances contending for the profile
and the shared temp state, which is the same contention already documented for the integration
suite.

The branch is gone. A change arriving during a run is always queued, and the `cancelWindow`
option went with it rather than being left as a knob that does nothing. Queueing costs almost
nothing in return: an export reads the file as it opens it, so a save landing mid-run is
usually captured by that very run, and the follow-up then finds nothing at the hash gate and
costs a file read instead of a toolchain launch. **Restarting is still worth having** — it just
has to cancel the process first, and killing CODESYS mid-export leaves a staging folder the
exporter already warns about. That is a separate piece of work, not a comment.

**The unit test passed throughout.** It asserted that the coalescer *returns* `CancelAndRestart`
in the window, which it faithfully did. A component tested alone cannot see what its caller does
with the answer, and "the caller cannot honour this verdict" is not a property any test of that
component can express. The test now asserts the property that matters and could have caught it:
**a second job is never started while one is in flight.**

### The history was re-read on every refresh, into a panel four rows tall

The commit list lived at the bottom of the git panel, under the stash, with `MaxHeight="150"`
in a 380 px column that also carried the branch card, the commit box, two file lists and six
stash controls. Four rows, and every one of them a single line holding a hash, a subject and a
relative age — the date and time the commit was actually made were not on screen anywhere.

What it cost is the part worth writing down. This ran on every manual refresh, every refresh
triggered by git's own metadata moving, and every branch change:

```csharp
await LoadBranchesAsync();
await LoadCommitsAsync();     // git log
await LoadStashesAsync();     // git stash list
```

**No part of that asked whether anybody was looking.** The rail could be on the project panel —
it is the default, and it is where the work happens — and a commit, a checkout or a fetch still
spawned two git processes to refill a list that was not on screen. The one interaction the list
did offer, clicking a commit, fetched the commit's *entire* patch, because whole-commit was the
only granularity there was. On a commit touching an exported CODESYS tree that is hundreds of
files in one string, to read one of them.

So history moved out to its own rail button, and everything about it is now demand-driven at
four separate levels: the log is not walked until the panel is first shown, and then one page
of fifty at a time; a commit's files are read when it is expanded; a patch is read when a file
is clicked. A refresh behind a hidden panel *marks* it and reads nothing — deferring the cost
without deferring the correctness, which is the only version of lazy that is safe here. A
commit that did not appear the moment it was made would look like a commit that had failed.

The file lists are cached and the patches deliberately are not. **A commit's hash is its
content**, so its file list cannot go stale and may be held for the session; its patches are
unbounded in size and are held only by the diff pane, one at a time. The same caching would be
wrong one panel to the left, where the working tree changes under you.

Three things surfaced while building it:

| Found | Why it mattered |
|---|---|
| `--name-status` quotes paths | Without `-z`, git renders `Kühlschrank Regelung.st` as `"K\303\274hlschrank Regelung.st"` — quotes and octal escapes included. This repository's own fixture corpus has both spaces and umlauts in its object names, so the panel would have asked git for the diff of a path that does not exist. The same class of bug as the mangled subprocess output already recorded here: an encoding assumption at a process boundary |
| *Show full file* would have swapped what the pane was answering | Its re-read keys off the shown path, which for a historical diff is a caption — `PLC_PRG.st @ 1a2b3c4` — not a path. Left alone it would have quietly re-read the *working tree* version of that file and rendered it under a header naming a commit. Caught before it shipped; the pane now records which source it is showing, and a refresh leaves a historical diff alone entirely |
| A collapsed panel passes the window test | `MainWindowTests` constructs and measures the whole window, and would still have passed with a broken `DataTemplate` in it: a `StaticResource` inside a template is resolved when a row is *realised*, not when the XAML is parsed. The new test switches to the panel through the rail button, fills it, expands a commit and asserts a real `TreeViewItem` came out with a height. Written the obvious way it would have been one more vacuous pass on the pile below |

The stash controls came out at the same time and are not replaced: six controls for something
reached for a few times a month, in the column that most needed the room. The client-side
operations — push, apply, pop, drop, and the list parser — are kept and still covered, because
what was wrong with the stash was the shelf it sat on, not the operations.

### A window that never opened, and three instruments that called it clean

The log panel gained a category dropdown - a `ToggleButton`, because it has to stay pressed
while its popup is open - wearing `GhostButton`, whose `TargetType` was `Button`. WPF refuses
that at the moment the style is set:

```
'Button' TargetType does not match type of element 'ToggleButton'
MainWindow.xaml line 1005
```

So `MainWindow`'s constructor threw, and **the application started, opened its log file, probed
the toolchain and never showed a window**. The build was clean and 883 tests were green while it
did that.

What makes it worth writing down is not the mistake, which is ordinary, but that **every
instrument in place reported success**, each for a different reason:

| Instrument | Why it said nothing |
|---|---|
| The compiler | XAML becomes BAML without resolving a single `StaticResource`. A style reference is a key until something asks for it |
| `Versio.App.Tests` | Covered the two prompt dialogs and the stylesheet. `MainWindow` was the one window nothing constructed - on the reasoning that it opens on every launch and could not go unnoticed |
| The resource-key tests | Assert `GhostButton` **exists**, which it did. The defect was a key that does not *fit* the element asking for it, which no lookup can see |
| `--trace-bindings` | Reported nothing, twice, and it was believed. **A window that never opens evaluates no bindings**, so the trace is empty for the same reason it is empty when everything is correct |

That last row is the thirteenth vacuous pass in this document, and the most embarrassing one,
because it was read as evidence *while the app was failing to start*. The diagnosis offered
first was also wrong: the empty trace was blamed on the MSIX filesystem-virtualization finding
already recorded here, on the strength of it being a documented gotcha in the same area. It was
not that - the app's own log was landing at the real path the whole time. **A documented
explanation that fits is still a guess until it is checked**, and checking cost one command:

```
MainWindowTitle  :
MainWindowHandle : 0
```

The fix is one word - `GhostButton` targets `ButtonBase`, so it dresses a `ToggleButton` and a
`Button` alike - and everything derived from it still targets `Button`, which stays legal.

The real fix is `MainWindowTests`: one construction of the real window, measured so templates
are applied, verified to fail on the reverted bug. It is the cheapest possible regression test
for the whole file - every `StaticResource` in `MainWindow.xaml` resolved on every run, in
200 ms - and it should have existed from the day the two dialogs got theirs.

### The same blind spot again, in the dialog added after it was written up

Found only because the crash above forced a real launch. `SystemInfoWindow` uses
`{StaticResource BoolToVisibility}` inside its row `DataTemplate`, and the converter was
declared in **`MainWindow.Resources`** - a different window, which cannot see it. Opening
Settings > Help > System information threw:

```
Cannot find resource named 'BoolToVisibility'. Resource names are case sensitive.
  at FrameworkTemplate.LoadTemplateXaml ... at FrameworkElement.MeasureCore
```

**This one was not introduced by the change that found it.** It shipped with the dialog, and
`Versio.App.Tests` covered the two prompt dialogs and not this one - so the lesson "a modal
nobody has clicked has never been parsed" was recorded, and then the next modal was added
without applying it. A blind spot named in a document is still a blind spot.

Two things it teaches beyond the fix:

- **A resource referenced from inside a template is not resolved until the template is
  applied**, which is Measure, not construction. A test that only constructed the window would
  have passed with the bug in place, so the tests measure and arrange.
- The converter now lives in the stylesheet, declared once, and `MainWindow`'s private copy is
  gone. A shared resource kept in one window's `Resources` is the same shape of bug as the
  private `Theme.xaml` merge that broke the theme switch - resolution finds it from one place
  and nowhere else, and the failure is invisible from the file that looks correct.

### An idle log that was mostly its own noise

Reported from use: the panel was drowning in `git status`. Measured from the log rather than
argued about - the status backstop runs every 20 s forever, and each run wrote **three** Debug
lines: the command, its duration, and a verdict of `status unchanged`.

```
17:29:29.287 DBG [Git] git status --porcelain=v2 --branch --untracked-files=all -z
17:29:29.333 DBG [Git] git status --porcelain=v2 --branch --untracked-files=all -z ok in 0.0s
17:29:29.335 DBG [Git] status unchanged (Backstop)
```

That is **about 540 lines an hour from an app with nothing to do**, and the entries that matter
scroll out behind them. The panel keeps 5,000 rows, so roughly nine hours of idling evicts
everything real.

Both halves were already trying to be quiet and both had picked the wrong lever. The verdict
line had been *demoted to Debug* - which is not the same as not writing it - and the command
pair had never been questioned at all.

- **A completed command needs one line, not two.** The pre-command line only ever earned its
  place for a command that hangs, so that is now the only case that gets it: after one second,
  `git fetch ... - still running`. Every routine call in this app finishes in tens of
  milliseconds, so in normal use it writes nothing.
- **A run of nothing is one fact, not N.** `QuietRunTracker` counts the quiet polls and says so
  every five minutes: `no change in 15 checks over 5m (12 Backstop, 3 WorkingTree)`. An idle
  hour goes from ~540 lines to 11.

**The per-trigger counts are the part worth keeping.** A *Backstop* poll finding nothing is the
definition of routine; a *WorkingTree* poll finding nothing means `RepositoryChangeFilter`
passed an event git does not care about, which is the only signal for tuning it - and it is
visible only in aggregate. A generic "collapse repeated lines" rule in the sink would have cut
the same volume and thrown that away.

Fourteen tests, and deleting the interval gate fails four of them, including both that assert
silence - the property most easily made vacuous, since a tracker returning `null` unconditionally
passes any test that only counts what it suppressed.

### The CODESYS lock marker was committed, and the ignore rule had guessed its name

Reported from use as "why is `control ~u` in my changes?". Three separate things, and only the
first is cosmetic.

**The rule matched nothing.** This repository's hand-written `.gitignore` carried
`*.project.~*`, which is the plausible reading. CODESYS **replaces** the extension:
`RefrigeratorControl.project` is held by `RefrigeratorControl.~u`. The product code has always
had this right - `CodesysProjectFormat.OccupancyMarkerFor` uses `ChangeExtension`, with the
measurement in its remarks, and adoption writes the scoped form for repositories it sets up.
Only this repository was wrong, because its file was typed by hand before that code existed.
Exactly the shape already recorded for `.versio/local/`: **a rule that is true only where
somebody arranged it separately.**

**The file was tracked, so the rule was irrelevant anyway.** `git check-ignore` reported no
match even after the pattern was fixed - ignore rules do not apply to tracked files, and
`git rm --cached` is the other half of the fix. Worth remembering: a corrected `.gitignore` that
appears to do nothing is usually this.

**It had been committed and pushed.** The marker's contents are a username, a machine name and a
pid, and it went to `origin/main` in a commit made while testing. That is the same harm already
documented for the `.opt` files, arriving by a route nobody had guarded. Removing it from
history means rewriting a pushed commit, so it is left as the owner's call - but the guard is
now in place for the next one.

### A panel about changed files, opening with a version number and two dead buttons

Reported from use, and correct on every count. The card at the top of the project panel was
four unrelated things wearing one border:

| What it showed | What it actually was | Where it went |
|---|---|---|
| Toolchain name, version, profile, status dot | Environment — identical for every repository, changing only when somebody installs something | Settings → Help → System information |
| "1 project, tracked." | A fact about the repository | The panel's own summary |
| "The project file is authoritative…" | State of **one** project | The project's row |
| *Export now*, *Acknowledge failure* | Two real actions, disabled almost always | Gone; see below |

**A permanently disabled button teaches people to stop looking at that corner of the screen** —
the same failure mode as a coverage line that congratulates itself on every refresh, which this
codebase had already argued about once.

*Export now* is gone because **nothing about a stale mirror needs deciding.** A project edited
while Versio was closed was never seen by the watcher, and the answer is always "export it", so
that now happens when the repository opens. The ordering is the guard, and it is the whole
feature: toolchain probed → orchestrator rebuilt and its **locks restored from the previous
session** → projects scanned → watcher started → staleness evaluated → *then* export. Exporting
a step earlier would run against a direction lock that had not been read yet. It goes through
the same `SatisfyGateAsync` the commit gate uses, so the lock, the hash gate, the breaker and
the open-project check all apply unchanged. Verified from the log of a real launch:

```
watching 1 tracked project(s) for changes          ← attach finished
commit gate: ExportRequired …                      ← staleness evaluated
catch-up: 1 project(s) changed while Versio was not running; exporting
…19.6 s… exported 12 object(s) → every tracked project is exported and up to date
```

*Acknowledge failure* is gone as a **panel-wide** control, not as a concept. The gate behind it
is deliberate and unchanged — a failed binding syncs no further until a person has seen the
failure — but a repository holds several projects and the panel could not say which one had
failed. The failure is now reported on its own row, and selecting it shows the toolchain's own
words with one button that clears the failure and retries. Acknowledging and retrying are one
button because they are one intention: two would mean clearing a failure and then wondering why
nothing happened.

One wording fix fell out of it. `SatisfyGateAsync` logged "exporting stale bindings before the
commit", which was true when only the commit gate called it and false for the two callers added
since — in a log read precisely when somebody is working out what the app did on its own.

### Recovery forgot what had been exported, and it cost a toolchain run every launch

Reported from use: the project sat in the panel reading "project ahead" with nothing changed
under it, launch after launch. The panel was telling the truth; the state behind it was wrong.

Recovery decides what to do with a lock left by the previous session. When it does **not**
re-adopt — the ordinary case, because the previous session ended cleanly — it replaced the lock
with a blank one. That blank threw away `LastSynced` along with the direction, and the commit
gate compares the project's hash against `LastSynced.ProjectHash`. An empty one reads as *"the
project has changed since it was last exported"* for a project nobody had touched.

The sequence, from one launch's log:

```
NothingToRecover — binding was clean; now Clean        ← LastSynced wiped here
tree: … 1 project(s) hidden as in sync                 ← still correct at this point
ExportRequired — the project has changed since it was last exported
catch-up: 1 project(s) changed while Versio was not running; exporting
```

So every open ran CODESYS for 17–31 s to rebuild a mirror that was already byte-correct, and
left the binding holding a fresh `ProjectAuthoritative` lock — which is why the row then read
"project ahead" and stayed until somebody pushed.

**Releasing the direction and forgetting what was exported are different facts**, and only the
first is recovery's business. The release now carries `LastSynced` and `Baseline` forward.

**It was latent for as long as exporting was a button.** Nobody pressed *Export now* on a
project they had not changed, so nobody saw the gate's answer. Making the export automatic on
open turned a bookkeeping error into twenty seconds of CODESYS on every launch — which is how
it got found, and an argument for automation surfacing bugs rather than creating them.

Verified across two consecutive launches after the fix: `catch-up: every mirror is up to date`,
no toolchain run, and `1 project(s) hidden as in sync`.

### The log said one time in the panel and another in the file

Two renderings of one instant. `LogViewModel.Time` called `ToLocalTime()`; `LogEvent.Format()`,
which is what the file sink writes, formatted the raw UTC stamp. So an entry read as 17:14 in
the app and 00:14 in the file — and the file is the copy somebody opens hours later, while
correlating against the CODESYS log and their own memory of when it happened. The file names
were UTC-dated too, so an afternoon's work in Pacific time lived in a file named for tomorrow.

Both now render local, and the file rolls on the **local** day so its name matches the stamps
inside it. `LogEvent.Timestamp` stays UTC: ordering, rolling and the tests all want one
unambiguous instant, and only the rendering was ever the problem.

**The first test written for it was vacuous, and only breaking the code showed it.** It built
the timestamp with a *local* offset — where `ToLocalTime()` is the identity and both renderings
are identical by construction. It passed against the bug it was written to catch. Rebuilt with
a UTC-offset stamp, as the real logger produces, it fails properly. That is the thirteenth
vacuous pass caught this way, and the second in two days from the same root cause: a test whose
input cannot distinguish the two behaviours it is comparing.

### The theme switch changed a colour nothing was looking at

Reported from use: the Light/Dark buttons in Settings did nothing. Two independent causes, and
the second one is the more interesting.

**One: the main window carried its own copy of the theme.** `MainWindow.xaml` merged
`Theme.xaml` into `Window.Resources`, and `App.xaml` merges it into `Application.Resources`.
WPF resolves upwards and stops at the first hit, so the window found its private palette and
never reached the application's — the one `ThemeManager` swaps. Every dialog, having no private
copy, followed the switch correctly, which is why the branch dialog opened dark over a light
window. The merge is gone: one theme, one place.

**Two: replacing the palette repainted nothing.** Measured with a probe rather than reasoned
about, and the numbers are the finding:

```
after Apply(Dark)   Color.Surface.Base = #FF1D2026      the palette did change
                    Brush.Surface.Base = #FFFFFFFF      the brush did not
                    window background  = #FFFFFFFF      nor did anything painted with it
```

`Semantic.xaml` defines its brushes as `SolidColorBrush` with `Color="{DynamicResource
Color.Surface.Base}"`, and the comment above `ThemeManager` said that is what makes the whole
window re-theme without a restart. It is not: a `Freezable` living in a resource dictionary
resolves its `DynamicResource` once, and replacing a *sibling* dictionary underneath it does
not re-resolve it. So the swap updated a colour that no live brush was reading any more.

**It had never worked, and it looked like it did** — because `Apply(System)` runs at startup
*before* any window exists, so the first resolution of every brush already sees the right
palette. Only a switch made while the app is running has anything to re-resolve, and that is
the one thing nobody had automated.

The fix rebuilds the whole theme dictionary and replaces it in `Application.Resources`, so
every brush is a fresh instance and every `DynamicResource` consumer in an open window is
invalidated. Four tests now cover it, and reverting to the palette-only swap fails three of
them.

### A diagnostic that verified an empty window

`Versio.exe --trace-bindings <file> C:
epo` never opened the repository. The startup code
took "the first argument that does not start with `--`" as the repository path, which is the
trace file.

The cost was not the missing repository — it is logged — but what it did to the instrument.
That flag exists to verify the UI, and every clean binding trace taken with it was a clean
trace over an **empty** window: no repository, no project rows, no git panel contents, so most
bindings had never been evaluated at all. A verification that quietly checks nothing is worse
than no verification, because it gets reported as a pass. Both of this session's earlier
"clean trace" runs were exactly that, and were re-run properly afterwards.

Flags that consume a value are listed rather than inferred: "skip whatever follows a flag"
breaks on `--spike C:
epo`, which is how the spike is actually run.

### A project with nothing to review stayed on screen

Also reported from use: a project sitting in the panel with no changes under it and a badge
reading 0.

It was doing what it was told. A project stayed visible when its sync state was anything other
than `Clean`, and the commonest state in the tool's whole life is `ProjectAuthoritative` — the
direction lock taken by a successful export and held until the work is **pushed**. So a project
that had been exported, committed and left alone sat permanently in a panel whose stated
premise is "everything with something to review", with nothing to review.

The rule now asks whether there is something to *do*: never exported, failed, conflicted, text
ahead (an import is outstanding), or stale. A held lock with everything up to date is a fact
about state, not a task, and it is already reported in the panel header in its own sentence.

### The UI had no automated coverage, and the first test written found a bug

`--trace-bindings` is a good instrument and it has one blind spot that matters: **it can only
report on windows that were opened.** A modal nobody has clicked has never been parsed, so its
XAML has never been checked against the stylesheet at all — a renamed style is not a compile
error, not a binding warning, and not anything until a user clicks the button that opens it.

Adding two dialogs made that gap load-bearing, so `Versio.App.Tests` exists now: a WPF test
project that constructs the real windows against the real `Theme.xaml`, loaded by pack URI from
the app assembly so it cannot drift from what ships. Deliberately breaking a style reference
fails **all ten** of the branch dialog's tests, which is the class of failure that would
otherwise have surfaced weeks later in front of somebody.

**The first run found a real defect.** `BranchPromptWindow` validated on `TextChanged`, which
never fires for a box's initial value, so the dialog opened with a **blank message area and a
dead Create button** — no explanation of what it wanted. Invisible by inspection, because a
blank label looks exactly like an empty one, and invisible to the binding tracer, because
nothing was bound. One call to `Revalidate()` in the constructor.

Three things had to be got right before any of it ran, and each fails in a way that names
something other than the cause:

- **an STA thread** — xunit runs tests on MTA threads, where a `Window` constructor throws;
- **an `Application` carrying the theme** — every `StaticResource` resolves by walking up to
  `Application.Resources`, and without one the XAML parse dies on the first style;
- **the *same* STA thread every time** — the theme's brushes are `Freezable`s owned by the
  thread that made them, so a fresh thread per test dies inside the XAML parser with
  `Dispatcher.VerifyAccess` in the stack and nothing pointing at the real problem. That was the
  first version of the harness.

### `async void` handlers and a test host that aborted

Driving the clone dialog by raising `Click` passed eight tests and then **aborted the test
host** — not failed, aborted, with no test named.

The handlers are `async void`, which is what WPF requires of an event handler. On the UI thread
an `await` posts its continuation back to the dispatcher, so raising a click only *starts* the
work: the rest runs whenever the dispatcher next pumps, which is during some later test, against
a window whose test has already finished.

Two changes, and both are improvements to the product rather than concessions to the test:

- the work moved into `VerifyAsync()` and `CloneAsync()`, leaving the `async void` handlers as
  one-line wrappers that start them. An `async void` that anything else calls is un-awaitable
  by everything, including the next feature that wants to reuse it;
- `DialogResult` is set through a guard, because it exists only on a window shown with
  `ShowDialog` and throws on any other. `ClonedPath` is set first regardless — that is the
  result; closing is how it gets delivered.

The harness gained a `Run(Func<Task>)` that waits **from the calling thread**, leaving the
dispatcher free to pump its own continuations. Waiting on it from inside itself deadlocks
instead.

### Validating a branch name is about the message, not the verdict

Git refuses a bad branch name perfectly well. It does it *after* a process spawn, in a sentence
written for a terminal — `fatal: 'feature branch' is not a valid branch name` — which arrives in
a status bar after the dialog has closed, naming neither the offending character nor what to
type instead.

`BranchName.Validate` is therefore in Core, with the rules transcribed from
`git check-ref-format` and a **reason for every refusal**: which character, or which rule. A
check returning a bare `false` would grey out a button and leave the user with nothing to
change — worse than no validation at all, because git would at least have said something.

It does **not** replace git's check, and the tests say so: a name that passes here can still be
refused for existing already, or for colliding with a ref path — `feature` and `feature/x`
cannot both be branches. The failed create is reported rather than assumed away.

The per-segment rules are the ones worth having a test for. `feature/.hidden` and
`feature/x.lock` are both refused by git, and neither is caught by looking at the name as a
whole: refs are paths, and git applies those rules to each component. Deleting the loop that
walks the segments fails five tests.

### The library reference id changes on every open, and the API is not the one it looks like

Two measurements, both cheap, both of which would have been expensive to discover later.

**The obvious API is the wrong one.** `libman.get_libraries()` exists and returns 21 items for
the fixture's application manager, which looks exactly like success. They are **strings** —
`"#Standard"`, `"#Util"` — with no version, no company, no flags and no parameters. The one
that carries the reference is `libman.references`, and it must be **iterated**, not indexed:
`references[i]` looks up by *id*, so `references[0]` raises on every project whose first
reference does not happen to have id 0. The first attempt read **zero** references out of a
manager holding 21, and reported no error at all, because the probe swallowed the exception.

**`ref.id` is re-minted on every open.** Two consecutive runs over an untouched project:

```
run 1   #Standard  id 1886582003        run 2   #Standard  id 1665927582
run 1   #Util      id 4015974643        run 2   #Util      id 1820137668
```

68 differing lines across the two dumps, and **every one of them was an id**. Nothing else
moved. So it is not recorded — the same decision, for the same reason, as the project-level
`__VisualizationStyle` GUID that broke export determinism hours after it was added. The
difference is that this one was caught by measuring *before* writing the exporter rather than
by a determinism test afterwards, which is the cheaper end of the same lesson.

**What resolution a reference gets is a fact about the machine, not the project.** The fixture
asks for `Standard, * (System)` and resolves it to `Standard, 3.5.22.0 (System)` — a wildcard
answered by whatever is installed here. Recording the resolved value would make the mirror
differ between two engineers sharing one repository, with neither of them wrong, and would move
the hash gate on a machine difference. The file therefore carries the **request** — placeholder,
default resolution, redirection, parameters, flags — and says in its own header what it leaves
out and why. `resolution_info` and `resolver_guid` go the same way.

Verified end to end afterwards: two full exports of the fixture, 14 files each, **byte-identical
across the whole tree**, and `_coverage.md` reporting **0 gaps** where it reported 2.

### A test that passed on broken code and failed on correct code

The reader for `*.libraries.yaml` is hand-written, and so is the writer, and they never meet in
one process. So the parser was broken deliberately to see which tests noticed — and the answer
was a genuinely useful surprise.

**Break: stop closing the nested `parameters:` mapping.** All 24 tests still passed. The test
that claimed to protect it asserted that parameters did not leak into the *next* reference —
which the list dash prevents on its own, whatever the mapping code does. It could not have
failed. The property that actually needs a parser is a **sibling key after the mapping**, and
the fixture cannot exercise it because the emitter writes `parameters` last.

Rewritten against a hand-written document with a key after the map, the new test **failed on
the correct code**: keeping one pending nested map and reading it at flush time discarded it the
moment a sibling key followed. The parser now stores nested maps into the item as they are
found. So the bug was real, the fixture could never have shown it, and the first test written
for it proved nothing — three separate ways to have shipped it.

A second break was more instructive than the fix: deleting a "close the open mapping" line
changed **nothing anywhere**. Indent already decided where a key belongs, and the line was a
second mechanism agreeing with the first — code no test could ever fail on. It was removed
rather than kept as defence, with the reason written where it stood.

And a third: the rule that drops a nameless half-entry survived being deleted, because
truncating the fixture never produces one — `name` is the first key of every item. It has its
own test now, from a document that does.

**The habit this reinforces:** a green suite over a committed fixture proves the fixture, not
the code. Every rule that only fires on input the fixture cannot contain needs a document
written to reach it.

### A log test that broke because the export got better

`ExportLoggingTests` asserted the swap line contained `"0 existing file(s) removed, 12 written
in their place"`, and the summary line `"walked 18, wrote 10, skipped 6"` with `"2 not
implemented"`. Exporting two more files turned a correct improvement into a failing string
comparison, which tells the reader nothing about what changed or whether it was wanted.

The numbers were there for a real reason — the counts have to *track reality*, and a swap that
printed the same pair twice would prove nothing — so they were not simply deleted. They are now
derived from the things they describe: the file count comes from the export directory on disk,
and the summary is checked against the `_manifest.json` **that same run wrote**. Both describe
one export, so they must agree, which is a stronger claim than any literal and one that does
not need editing the next time the exporter learns to write a file. The two properties worth
pinning by hand survive: the two swap lines must **differ**, and the fixture still walks 18
objects.

**The rule of thumb:** assert against another record of the same event, not against a number
typed into the test. A literal is right until the code improves, and then it is noise.

### The export's own narrative never reached the log

Asked directly whether the logs were sound for copying, exporting and replacing an export
directory, the honest answer turned out to be no — and the gap was invisible from the code,
because every line that existed was correct. The problem was the lines that did not exist.

**The most destructive step in an export had no log line at all.** The staging swap deletes the
entire export directory and renames a new one into its place:

```python
if os.path.isdir(out_root):
    shutil.rmtree(out_root)
os.rename(staging, out_root)
```

Nothing emitted, nothing logged. If it half-failed, the log would say the export succeeded and
the directory on disk would disagree, with no line anywhere connecting the two.

**And the script's protocol events were thrown away after being counted.** They drove progress,
the changed-path list and the warnings, and were then summarised as `events=24` — a number that
says something happened and nothing about what. So every skip, every object written and every
summary the exporter produced was absent from the log in readable form, in a codebase whose
stated rule is that a silent failure is a bug even when the code is correct.

Both are fixed. An export now reads:

```
replacing the export directory …\RefrigeratorControl: 12 existing file(s) removed, 12 written in their place
export directory now holds 12 file(s)
walked 18, wrote 10, skipped 6 (of which 2 not implemented), 0 warning(s)
not exported (NOT_IMPLEMENTED): Device/Plc Logic/Application/Library Manager [LIBRARY_MANAGER]
```

Levels are chosen by volume and by who needs them: one line per exported object is Debug,
because a real project has thousands of them; skips, the summary and the filesystem swaps are
Info, because they are bounded and each answers a question somebody asks afterwards. A leftover
staging folder is a **Warn** — it means a previous export was killed, which is worth knowing at
the moment it is discovered rather than never.

Two smaller ones went with it. Pruned project backups are now named as they are deleted, because
a backup that vanished with no line saying so is indistinguishable from one that was never made
— exactly the question being asked when somebody goes looking for it. And the skip path is
computed once and shared by the manifest and the log line, after the log showed
`Device\Plc Logic\Application/Library Manager` with both separators in one path.

The test asserts against the log a real export produces, and it asserts **both** runs: the first
removes nothing because there was nothing there, the second removes what the first wrote. A
count that did not track reality would print the same number twice. Verified to fail with the
swap emit removed.

### Recording a GUID broke export determinism, hours after it was added

`_coverage.md` shipped with the skip records carrying the object's GUID, which looked like free
information. It is not: **CODESYS re-mints the project-level `__VisualizationStyle`'s GUID on
every open.** Two consecutive exports of the same file:

```
run 2   09cd3618-c2e4-4e49-888a-02e46902a7b0
run 3   63754151-d4ae-45bd-9876-9d0db2d723eb
```

So `_manifest.json` differed on **every single export** — a spurious diff in every commit,
forever, which is precisely the noise the `Timestamp` scrub exists to remove. Reintroduced by
the report written about coverage.

**The determinism test added alongside it did not catch this**, because it compared
`_coverage.md`, and the report carries no GUIDs. A test written in the same hour as the feature,
against the same mental model, checked the half that could not fail.

Phase 0's gate 0.2 measured byte-identical exports across three runs **by hand**. Nothing in the
automated suite enforced it, which is how a determinism regression got committed at all. The
test now fingerprints the **whole export tree** across two exports of one project and compares
every file — verified to fail against the reverted fix before it was believed.

The GUID is gone from skip records. A skipped object has nothing pointing at it, so path and
type are identity enough; for an *exported* object the GUID stays, because there it is what
makes a move a move rather than a delete and an add.

**The general shape:** a value that is stable in every case you look at is not thereby stable.
This one was stable for five of six skipped objects, and the sixth only reveals itself if you
export twice and compare — which is the one thing the narrower test did not do.

### Exporting a copy works, and the lock is on the path

Measured, because it decides whether the tool can work at all while an engineer has CODESYS
open — the normal way of working:

| Question | Answer |
|---|---|
| Can the `.project` be copied while CODESYS holds it? | **Yes** — no sharing violation |
| Does exporting the copy work while the original is locked? | **Yes** — 16.7 s, full export, 10 files |
| Is the result identical to exporting the original? | **Yes**, all 11 object files byte-for-byte |

The lock is a sidecar on the path (`<project>.~u`), so a copy at another path is unencumbered.
The only difference between exporting the original and exporting a copy was the unstable GUID
above — which was not caused by the copy, and is now gone.

Two constraints that fall out of this and are easy to get wrong:

- **The copy must keep the same filename.** The manifest records
  `os.path.basename(job["project"])`, so exporting `P.project` instead of
  `RefrigeratorControl.project` would rewrite that field and churn the mirror.
- **The copy belongs in `.versio/local/`**, which is already gitignored, so it never reaches
  git and never appears in the panel.

This makes export-while-open a solvable problem rather than a refusal. Import is not, and
should not be: the IDE holds its own copy in memory and would silently revert an import on the
next Ctrl+S, so it must fail and say so.

### `.versio/local/` was gitignored in three comments and nowhere else

Found by re-reading the plan against the code after building the ignore rules. The engine says
`.versio/local/` "is gitignored" in three separate places, and §5 of the plan is explicit that
it must be: it holds the direction locks, session state, project backups — megabytes of binary
snapshots — and now the scratch copy taken while the IDE has a project open. A committed lock
pulled onto a colleague's machine is, in the plan's words, a bug factory.

Nothing wrote the rule. **This repository had one because a person typed it by hand**, which is
precisely why nobody noticed: every test and every manual check ran here. A repository somebody
else adopted a project into got the vendor's `.opt` rules and nothing else, so their locks and
backups sat untracked, visible in the panel, and one *stage all* from being committed.

The same shape as the library manager this morning: a comment asserting a property that nothing
enforces, true only where somebody had arranged it separately. Adoption now writes the rule, and
the test asserts through git — including that `.versio/repo.json` is **still** reported, because
it is committed on purpose and an ignore rule that swallowed it would break every clone's
baseline.

### What CODESYS actually leaves beside a project, and why it belongs in `.gitignore`

Written down because the first account of it in this document was **wrong**. The scratch folder
after an export was said to hold a `.~u` — inferred from the lock file measured earlier, not
looked at, because the test had cleaned up before anyone could see. Measured properly:

| | |
|---|---|
| Open and close cleanly | **Nothing left.** `<name>.~u` exists only while the project is held, and is removed on close |
| Killed while open | `.~u` survives — the stale case the pid probe already handles |
| Export or open | `<name>-AllUsers.opt` and `<name>-<user>-<machine>.opt`, and these **persist** |

The `.opt` files are per-user settings rather than locks, which is why they outlive the session
that made them — and why they matter here. Untracked in a repository, they turn up in
`git status`, in the project panel as loose rows, and in anything that stages everything.

**The per-user one is worse than noise: its filename carries the username and the machine
name.** Committing it writes both into history permanently, and produces a different file for
every engineer who opens the project — a file that can only ever conflict.

So adoption now writes `.gitignore` as well as `.gitattributes`, **creating it when absent**,
which is the ordinary starting point for somebody who has kept PLC projects in a folder until
now. The rules come from the vendor through `IPlcProjectFormat.TransientPatternsFor` and are
**scoped to the project** — `plc/Machine-*.opt`, never a repository-wide `*.opt`. A repository
belongs to other people too, and ignoring every file of an extension on the strength of one
adopted project is not Versio's decision to make.

An existing decision is never overridden, negations included: a repository saying
`!plc/Machine-*.opt` has been configured deliberately, and adding the positive rule underneath
would quietly reverse it. Re-adoption adds nothing, so the file does not grow on every scan.

The integration test asserts against **git**, not against the file it just wrote: it creates
all three leftovers and checks `git status` no longer reports them, while the project itself
still is reported — an ignore rule that swallowed the thing being tracked would be a far worse
bug than the noise it removes. Verified to fail with the patterns removed.

**The habit worth keeping:** the wrong version of this was a reasonable inference from a real
measurement taken minutes earlier, and it read as fact. Looking cost one command.

### Working while the IDE has the project open

Built on the measurements below. The behaviour now:

| | |
|---|---|
| **Export** | Runs. The project is copied to `.versio/local/open/<binding>/` keeping its own filename, and the copy is exported — the vendor lock is a sidecar on the path, so a copy elsewhere is unencumbered |
| **Import** | Waits, and says who has it. It cannot be made to work: the IDE holds its own copy in memory and the next save would silently revert whatever was imported |
| **Either** | `BlockedByOpenProject` — **not** a failure. No failed binding, nothing to acknowledge, and the circuit breaker is never fed |

**The check runs before the coalescer and the breaker**, which a test caught: placed after them, six blocked saves tripped a breaker with a threshold of two and halted the project for the session — turning the wait into exactly the sticky failure the verdict was introduced to remove. It runs *after* the hash gate and the direction lock, because "nothing changed" and "the lock forbids this direction" are more specific answers, and reporting the IDE instead would be noise on every settle.

The staleness rule mirrors `LockRecoveryPolicy`, because the problem is the same. A marker naming a dead process **on this machine** is stale and ignored — a crashed IDE must not block sync until somebody deletes a file they have never heard of. A marker from **another machine** is always believed: its pid means nothing here, and a project on a share really can be open on somebody else's desk. Guessing free would put two writers on one project.

**It sits behind the vendor seam.** `IPlcProjectFormat` gained `OccupancyMarkerFor` and `ParseOccupancy`; `Sync/` knows only that something may be held and by whom. A vendor that cannot answer reports free, so registering a format never makes its projects unsyncable.

Two things the integration test caught that nothing else would have:

- **The scratch folder is emptied, not the copy deleted.** Removing only the file we created left the toolchain's own files behind for good. The first write-up of this named the wrong file — `.~u`, inferred rather than looked at, because the test had already cleaned up. Measured properly afterwards: a clean open/close leaves **nothing**, and it is the *export* that leaves `<name>-AllUsers.opt` and `<name>-<user>-<machine>.opt`. See the finding below.
- **The test's own CODESYS launch used `ArgumentList`**, which quotes the whole `--profile=A B C` pair — the trap already documented in the README and covered by `ArgumentBuildingTests`. It exits 1 in under a second with no output, and the symptom was two silently skipped tests. It now uses `LocalCodesysRunner.BuildArguments`, which also means the test launches CODESYS the same way the product does.

Not built, and deliberately: **automatic retry when the IDE closes.** The `.~u` disappearing is a filesystem event the watcher could see, which would make closing CODESYS the trigger and need no button. Today the next save retries, and so does reopening the repository. Worth doing, not worth blocking this on. *(Written when an "Export now" button existed; it has since been deleted in favour of exporting on open.)*

### A project open in the CODESYS IDE — measured, and it is the normal case

The everyday scenario: an engineer saves in CODESYS and leaves the project open. Versio's
watcher sees the `.project` write and starts an export. What happens was unknown, so it was
measured rather than reasoned about — a second CODESYS holding the project open, then a real
export and a real import against it.

**CODESYS refuses, cleanly, in both directions.**

```
StandardError: The selected project is currently in use by 'elli_' on 'ROG-JVM'.
```

| | |
|---|---|
| Export while open | Fails at `projects.open()` in **6.9 s**. No manifest written, no partial tree |
| Import while open | Fails identically. The `.project` is **byte-for-byte unchanged** — measured, not assumed |
| Reported correctly? | **Yes.** `CODESYS.exe` exits **0** in both cases, but `PlcResult.Succeeded` requires the script's own `rc` *and* its `done` event, so neither is mistaken for success |

That last row is the one that could have gone badly, and it did not: the process exit code is
worthless here, and `Succeeded` already ignored it.

**There is an on-disk lock, and it is cheap to read.** CODESYS writes `<project>.~u` beside the
project while it holds it, and removes it on close:

```
elli_                 user
ROG-JVM               machine
45812                 process id
639221606143479369    timestamp
```

Which is very nearly Versio's own `LockRecord` — machine, user, pid — and carries the same
staleness problem: a CODESYS that crashed leaves the file behind, so the pid has to be probed
before the lock is believed. `LockRecoveryPolicy` already does exactly that reasoning for
Versio's locks.

**The defect this exposes is not the refusal, it is what Versio does with it.** A refusal
arrives as a failed run, and a failed binding stops syncing until somebody acknowledges it
— then a button called *Acknowledge failure* on the panel, now *Try again* on the failing
project's own row. So the most common working state in the tool's whole life — the IDE
open — turns into a sticky failure requiring a button press, over and over. "The project is
open" is not a failure; it is a *wait*.

Worth stating plainly: even if CODESYS allowed the write, importing into an open project would
be wrong. The IDE holds its own copy in memory, and the next Ctrl+S would silently revert the
import. The direction lock cannot help — it arbitrates between Versio's two directions, and the
IDE is a third writer it has never heard of. CODESYS's own lock is what saves us, and it is
luck rather than design that it does.

**The fix, designed but not built:**

1. Pre-flight the `.~u` file before launching CODESYS. It costs a file read instead of 17–31 s,
   and it turns the common case into an instant, quiet refusal.
2. Probe the pid before believing the lock, or a crashed IDE blocks sync until somebody deletes
   a file they have never heard of.
3. A distinct verdict — *blocked by an open project* — that does **not** mark the binding failed
   and does **not** trip the circuit breaker, and that retries when the lock clears.
4. A sentence naming who has it: *"RefrigeratorControl.project is open in CODESYS (elli_ on
   ROG-JVM). Close it and Versio will export automatically."*
5. **It goes behind the vendor seam.** `.~u` is a CODESYS fact, and `Sync/` may not learn it —
   so the question this poses is which contract owns it, most likely `IPlcProjectFormat`.

### A comment claimed a file nobody wrote

`LIBRARY_MANAGER` was skipped by the exporter with the comment *"captured in
config/libraries.yaml instead"*. Nothing has ever written that file. So a library reference —
adding one, bumping a version — changed nothing in the mirror, moved nothing through the hash
gate, and reached no diff and no commit. On the fixture there are **two** library managers, and
neither has ever been exported.

The tool's claim is that git can tell you what changed in a PLC project. For library references
it quietly could not, and nothing anywhere said so: an object that is deliberately not exported
and an object nobody has written the code for look identical from the outside, which is
*absent*.

Reported before it is fixed, and deliberately in that order. Every export now writes
`_coverage.md`, the manifest records each skip with a stable reason code, and the reasons
separate the two cases — `SERVICE_OBJECT`, `TEMPLATE_ONLY` and `COVERED_ELSEWHERE` lose
nothing; `NOT_IMPLEMENTED` is a hole. A stated gap is one somebody can work around. An unstated
one is a wrong answer to the only question the tool exists to answer.

The manifest schema went to 2 for this. An empty skip list means two different things — nothing
was skipped, or nobody was counting — and reading a schema-1 manifest as full coverage would
turn every old export into a clean bill of health.

### Adding a file to the export tree broke the importer, and no test noticed

The importer walks the export tree and treats every file it finds as an object, knowing about
`_manifest.json` by name, in two separate places. Adding `_coverage.md` beside it meant the
importer handed a markdown file to CODESYS as native XML:

```
NATIVE_IMPORT_FAILED  _coverage.md  Data at the root level is invalid. Line 1, position 1.
```

Nothing was corrupted, and that is the trap. The cost is that **every** import would carry a
failure warning that is always present and always meaningless — which is precisely how a real
native-import failure gets read past.

**The existing export→import round-trip test passed with the bug in place.** It was re-run with
the fix deliberately reverted to check, and passed again: it asserts the project is reached and
never looks at the warnings. The reserved names now live in one frozen set beside the exporter's
own type table, so the next file added is added for both halves at once, and a new test asserts
no warning names a bookkeeping file — verified to fail without the guard before it was believed.

The general lesson is about the shape of the mistake rather than the file: **the export tree has
two kinds of contents, and only one of them is the project.** Nothing in the code said so until
now.

### The integration suite had been lucky about running two CODESYS instances

xunit runs test *classes* in parallel by default, and four classes already drove a real
headless CODESYS. Adding a fifth that launches five exports tipped it over:
`ProjectTreeFromRealExportTests` began failing in a full run and passing on its own, with the
export exiting in **0.9 s** instead of 17 — two instances contending on the profile and the
shared temp state, and the loser dying with nothing useful to say.

The race was always there. Nothing had changed about it except the odds, which means the suite
had been passing on luck for as long as those four classes have existed.

Found only by running the whole suite **from a fresh clone** rather than from the working tree
it was written in — a habit worth keeping, since it is also the first thing anybody taking this
over will do. The CODESYS-driving classes now share an xunit collection and run one at a time.
The cost is wall clock, and it is the right trade: a slow suite that is believed beats a fast
one that is not.

### `git apply --cached` reads the index, not the working tree

Written down because the first test of per-hunk staging asserted the opposite, and **failed**.
It edited a file after reading its diff, then expected git to refuse the now-stale hunk. Git
applied it happily: `--cached` takes the *index* as the patch's pre-image, so a save landing
between the diff being read and the button being pressed does not invalidate anything — what
gets staged is still exactly the hunk that was on screen, which is the behaviour you want and
not the one the test claimed to protect.

The staleness that does matter is the index moving, and that is refused. Both are now pinned,
and the pair is why `--unidiff-zero` is not passed: git's context check is the only thing
standing between a patch and the wrong offset.

Worth keeping as a finding rather than a fixed test, because a passing test would have
documented a protection that does not exist.

### Lazy tree rows are proportional to what is reachable, not to nothing

`TreeRow` built its whole subtree in its constructor, which was affordable only while the tree
was filtered to what had changed. Rendering the full CODESYS structure that way would build a
row per object — thousands on a real machine — before the window drew anything.

Children are now built on first access. **That is not zero cost, and it is worth being precise
about why:** WPF evaluates `Children` when it realises a container, because it cannot decide
whether to draw an expander without asking. A visible collapsed folder therefore still costs
one row per child; its grandchildren cost nothing. The win is O(objects in the project) →
O(visible rows × branching), which is the whole point, and claiming more than that would be
the same kind of decorative virtualization the diff view already had once.

### A view that rebuilds on every refresh drops the selection

The project tree is rebuilt whenever git's status is re-read — which, now that it is the only
view, is several times a minute while somebody works. Most of those refreshes find nothing:
they fire because a file was saved in an editor.

Rebuilding anyway would clear the TreeView's selection over and over, which is precisely the
regression the changed-files list was reconciled in place to avoid; the fold would have
reintroduced it one panel to the left. The tree is now derived from a signature — the changed
paths, the search text, the bindings and each manifest's write time — and an identical
signature rebuilds nothing. Measured live: **five refreshes, two rebuilds**, and the three
no-op saves in between rebuilt nothing.

Expansion state is kept separately, keyed by path, and records both what was opened and what
was closed. One set cannot express "the user deliberately shut a folder the default rule wants
open", and the default rule changes its mind every time that folder's change state moves.

### A file under an export tree that the manifest does not describe

`_manifest.json` changes on every export and describes the other files rather than appearing
among them, so no object node claims it. Dropping it would have been worse than showing it: a
file git reports as changed, in a directory Versio owns, appearing nowhere in the panel is
indistinguishable from Versio having lost it. Such files hang off their project as plain rows.

The unit test that asserts every changed path is represented exactly once did **not** fail when
the rule was deliberately removed — the loose-file fallback caught the file at the top level
instead, so coverage held by a second mechanism while placement was wrong. Placement is
asserted separately, and the coverage test was re-verified against a different break (removing
the claim on manifest files, which duplicates every object) before it was believed.

### The hash gate could not see a text edit

Change detection for the mirror hashed `_manifest.json` — a file written by an export and by
nothing else. An engineer editing `Simulation.st`, the entire purpose of the mirror, left it
byte-identical, so the gate returned `NoChange` and the import was refused before the direction
lock was ever consulted. **Import could not have worked at all.**

The 24 scenario tests could not catch it: they simulated a text edit by rewriting
`_manifest.json`, the one file no human ever touches, so both sides agreed about a signal that
does not occur in the field. Fixed with `ContentHash.OfTree` over the tree's real contents, and
`ManifestHash` renamed to `MirrorHash` because the old name described the proxy rather than the
thing — which is how the bug got its blast radius.

**The first draft of the test written to prove the fix passed against the broken gate.** A
fresh binding has never recorded its mirror, so it reports a change whatever the gate does. It
now starts from a synced harness. That is the same vacuous pass one level up, found only by
deliberately breaking the code and re-running.

### The vendor seam was documented but not true

`SyncOrchestrator` was built directly on `ICodesysRunner`, `CodesysJob`, `CodesysOperation`,
`CodesysResult` and `CodesysProgress`, and `Abstractions/` — a folder whose entire purpose is
neutrality — held two brand-named interfaces. A second vendor would have meant rewriting the
engine, which is exactly what the rule existed to prevent. `IPlcToolchain` had been declared
from the start and never implemented, so the application assembled a toolchain out of vendor
parts instead: a locator, a profile resolver, an install record and a runner, five brand names
in a view model with no business knowing any of them.

A rule nothing checks is a rule that decays. It is now enforced from assembly metadata.

### The diff view's virtualization was decorative

`DiffView` is a virtualized `ListBox` whose class comment claimed rows were built on a
background thread. Neither was doing anything: `Rebuild` eagerly constructed a `Grid`, two
gutters and a syntax-tokenized `TextBlock` for every line before the list saw them. Selecting
the fixture's 2.5 MB, 33,644-line visualisation locked the window.

Made visible by the new-file fix that came before it in the same session — until untracked
files produced a diff at all, that file rendered nothing and returned instantly.

### `CreateNoWindow` does not hide a GUI child

It sets `CREATE_NO_WINDOW`, which suppresses the console a *console-subsystem* child would
allocate. `CODESYS.exe` is a GUI application and under `--noUI` still created a top-level
window. `WindowStyle.Hidden` passes `STARTF_USESHOWWINDOW` with `SW_HIDE`, which is what a GUI
child honours. Both flags are set: they cover different subsystems and neither replaces the
other.

### A wrong diagnosis, and the vacuous check that confirmed it

"Open logs folder" reported *Location is not available* for a folder that plainly existed. The
cause was **filesystem virtualization**: the app had been launched from inside an
MSIX/AppContainer, where writes under `%LOCALAPPDATA%` are redirected into that package's
`LocalCache`. Versio wrote to a real folder; Explorer, outside the container, was handed a path
that does not exist from where it stands.

The check that "confirmed" the first diagnosis was vacuous: `Start-Process explorer.exe`
reported success because Explorer *launched* — it then failed on its own and put up its own
dialog. **Process-started is not folder-opened**, and that was the entire question. Launch
context is part of the environment, alongside the per-user .NET install already recorded here.

### `objectCount` in the manifest is a file count

`versio_export.py` writes `len(records)`, and there is one record per *file*. It therefore
differs from the number of distinct objects exactly when an object exports to more than one
layer — which the fixture does, via its GVL. Found by an assertion that trusted the field's
name. Nothing depends on it today; the integration test asserts against distinct GUIDs.

### The repository dropdown was blank over a list of one

The control rendered empty while the list behind it held exactly the right entry, selected
nothing, and reported no error anywhere.

`RecentRepositories.Canonical` exists precisely for this — its own remarks record the sibling
defect, *"git answers `rev-parse --show-toplevel` with forward slashes and the Windows folder
picker hands back backslashes, so opening one repository both ways put two entries in the list
for it"*. That fix was applied **on the way in**: `Promote` canonicalises, so the list correctly
refuses to hold one repository twice.

The dropdown then looked the selection up **on the way out** with an ordinal `==`:

```csharp
SelectedRepository = RecentRepositories.FirstOrDefault(r => r.Path == select);
```

The stored spelling is canonical. The opened spelling is whatever opened it. `C:\GIT\Versio`
and `C:/GIT/Versio` are not the same string, `FirstOrDefault` returned null, and a
`ComboBox` with a null `SelectedItem` draws nothing. Half an argument, applied to half a
problem, leaves a list of one with nothing selected.

**Nothing was watching it, in three separate ways.** No binding failed — the binding tracer over
the running app reported thirteen errors and not one of them on this control, because a
successful binding to null is not an error. No test covered it: the recent-list tests assert on
what is *stored*, which was always right. And the app itself logged `1 recent repositor(ies)
restored`, which was also true.

Found by launching the app and looking at it. It needed nobody to type anything unusual — a
forward-slash path is what git itself answers — and it had been reachable since the canonical
fix went in.

All three lookups go through one `RowFor` now, and the test opens a repository by the other
spelling and asserts the row is selected. It fails with the canonicalisation removed.

**The tell worth keeping.** A control that is blank rather than wrong is the shape of a
successful binding to a null, and no instrument in this codebase reports one — not the binding
tracer, not the log, not a test that asserts on the view model's inputs. The only thing that
catches it is opening the app.

### Fifty-six shells nobody disposed, and a suite that failed at random

The UI suite failed roughly one full run in three, on a **different test each time**, and every
one of them passed when its class was run alone. It had been recorded here as flakiness under
load — three named classes, timeouts, real git and real CODESYS running concurrently — and left
undiagnosed on the grounds that a red run should not be read as a regression.

That was the wrong conclusion, and the reason it survived is worth more than the fix: **a
symptom that moves looks environmental.** Three classes failing on three different runs reads
as load; one class failing every time reads as a bug. It was a bug.

**Fifty-six tests opened a repository through a `ShellViewModel` and never disposed it.** A
shell that has opened one owns:

- a `FileSystemWatcher` over the working tree,
- a `RemotePoller` that spawns `git` subprocesses on a loop,
- the sync panel's own watchers over every tracked project and its export tree,
- and the repository lock.

None of that stops when the test method returns, and `Dispose` is the only thing that ends it.
With xunit running twenty-eight collections in parallel — every class outside the `theme`
collection is its own — the suite accumulated dozens of live watchers and polling loops. They
fired against temp directories their own test had already deleted, and competed for the same
`git` binary as whatever test was actually running.

Every symptom follows from that. **The failure moves** because it lands on whichever test is
unlucky enough to be waiting on git when the noise peaks. **It never reproduces alone** because
alone there is nothing else running. **It reproduces on `main`** because the leak predates every
branch that inherited it.

**Measured rather than argued, and it is a reduction rather than a cure.** Before: four failures
across fourteen observed full runs, on `main` as well as the working branch — roughly one run in
three. After `using var shell = ...` at all fifty-six sites: one failure across the next
fourteen, the first ten of them consecutive. So about 29% down to about 7%.

**Something is still leaking, and the candidate is named below.** Do not read a single red run as
a regression yet; do read a second one as this, still.

Two things this leaves behind.

~~**`LogViewModel` is still leaked**, thirty times over, by tests that construct one inline as a
constructor argument.~~ — **fixed, and the sentence that followed it here was wrong.** It read:
*"the timer cannot tick on a thread with nothing pumping a dispatcher, so the practical cost is
the subscription rather than the work"*. `StaHarness` runs **one** long-lived STA thread with a
live `Dispatcher.Run()` for the whole suite — it has to, because the theme's `Freezable`s belong
to the thread that created them — so every log built inside a windowed test puts its 100 ms pump
on the one dispatcher that does pump, and a leaked one goes on doing the work for the rest of the
run. The instinct about ownership was right, though: the fix was changing who owns the log, not
restructuring thirty call sites. See the finding above.

**`CA2000` was tried as a guard and rejected.** It fires fifty-four times on the suite *after*
the fix, almost all of them on that `new LogViewModel(sink)` argument, and it cannot see
ownership through a factory like `TestShell.Build` — so it would have missed the actual defect
while demanding fifty-four suppressions. The rule is stated in `TestShell`'s own remarks
instead, where somebody about to write the fifty-seventh test will read it.

**The general lesson, which this codebase already half-knew.** `Eventually.Press` exists because
of the same class of problem — a press landing while something else is running does nothing, and
the wait after it times out looking like a real defect. Its remarks say it "passed in isolation
and failed under full-suite load, twice, before the cause was clear". That is the second sighting
of the same tell. **A test that passes alone and fails in company is not being slow; something
alongside it is still running.**

### A flaky watcher test, and two fixes that made it worse

`RepositoryWatcherTests.A_deleted_file_is_reported` failed once during a full run and passes
in isolation every time. The root cause is a fixed `Thread.Sleep(250)` waiting for
`FileSystemWatcher` to arm — an assumption that holds on an idle machine and stops holding
under the full suite, where CODESYS exports are hammering the same disk.

Replacing the sleep with a deterministic arming probe — write a file, wait for the watcher to
report *it*, then act — made the test fail **consistently**. Windows raises a single
directory-level notification when several files in one folder change at once, so the probe and
the test's own file coalesced into one event for `src` rather than two for the files, and the
assertion looks for a file by name. Moving the probe to the repository root did not help
either.

**Reverted.** A flake observed once is not worth destabilising a working test for at a
stopping point, and two attempts had already turned "fails rarely" into "fails always". The
diagnosis is recorded here so the next attempt starts from it rather than from the sleep.

It is also a second sighting of the directory-level coalescing already documented for `.git`:
it applies to working-tree folders too, and any test asserting on a per-file event is making
an assumption Windows does not guarantee.

### Committed configuration has to be byte-stable

`repo.json` is committed by design — it is how a teammate gets the same project mapping — and
two things made it churn:

- **CRLF.** `System.Text.Json` indents with `Environment.NewLine`, so it was written CRLF on
  Windows while `.gitattributes` stores it LF. The file showed as modified the moment anyone
  cloned, and again every time Versio touched it.
- **Enums by ordinal.** Without a converter, `vendor` would have serialised as `0`. A reviewer
  reading that in a diff learns nothing, and inserting a vendor into `PlcVendor` —
  alphabetically, say — would have silently repointed every existing binding at a different
  toolchain, first noticed as an export driven by the wrong tool. ~~The second hazard is now
  gone rather than mitigated: `PlcVendor` stopped being an enum on 2026-08-16, so there is no
  ordinal left to shift.~~

Both fixed and pinned by a test that reads the bytes back.

### A vacuous check, caught in the act

While investigating whether the export's line endings would churn through git, the first
measurement used `grep -c $'
'` and reported that all 34 lines of a `.st` file contained a
carriage return. They contain none: the pattern matched every line rather than CR-containing
ones. Measured again with `tr -cd` **and a known-CRLF control file**, the export is LF
throughout and round-trips through git unchanged.

The fix that was almost applied — rewriting the export's line endings — would have churned a
proven pipeline to solve a problem that did not exist. The control file is what separated the
two, and it cost one line.

### The fake filesystem was not a filesystem

`FakeFileSystem.EnumerateFiles` was a bare prefix match, so `plc/A.project` enumerated as part
of its own export tree `plc/A`. A test written against that would have proven the opposite of
what it claimed.
