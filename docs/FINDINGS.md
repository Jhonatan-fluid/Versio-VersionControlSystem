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

### One rung moved, 1,359 files changed: the export stamp, a refused block deleted from the mirror, and a BOM on every committed ladder

**2026-10-29, `main`, the owner's `controls-design-siemens` (eight CPUs, 2,170 objects).**
The owner moved one network in a `Main` OB, closed Portal, and the Project panel read *1359
changed*, with the one block they had been looking at drawn as nine added networks over
"No committed copy to compare with: every network is new". Three defects, found from one
screenshot and the sibling repository's `git status`:

```
 M  … 1,358 files          D  JV_WorkingSpace/LBB101/LBB101/MHC_001/MHC_001/Program blocks/Main.xml
 git diff --numstat: 1,354 files are 1/1, and the line is
-    <Created>2026-10-29T20:44:47.5625993Z</Created>
+    <Created>2026-10-29T20:49:45.6025282Z</Created>
```

**1. Every export rewrote every file, by one line.** Portal stamps `DocumentInfo/Created`
with the export time, per file. `TiaPortalNativeScrubber` and the host's `HashNormalized`
strip a list of churn *attributes* (`DateModified`, `Version`, …); this is an *element*, so
neither dropped it, the hash gate saw a changed file every time, and the committed mirror
(exported 13:44, committed 13:47) differed from the re-export (13:49) in 1,354 of 1,359
files. DRIVING §12 had recorded "native XML still churns (G12)" as the cost of the review-only
XML beside an `.scl`; on a project whose OBs are ladder, the XML is the only file, and the
churn is the whole tree. **Fix:** the host rewrites each exported file without that one line
(`StripExportStamp`, keeping Portal's BOM and CRLF), and both normalisers drop the element,
so an in-session export (which has it) still hashes like the recorded file (which has not) —
the deletion check depends on that. Rejected: a constant value instead of no line (an import
of either is equally unmeasured, and a lie is worse than a gap); stripping the whole
`DocumentInfo` (the product list differs per machine, but churns only when the machine does).
Open: an import of a file without the line has not been run against Portal — STATUS item 21.

**2. The block the owner edited was gone from the mirror, and nothing said so.** Openness
refuses to export a block edited and not compiled since:

```
_manifest.json → skipped: { "path": "Main", "reason": "EXPORT_FAILED",
  "detail": "…Inconsistent blocks and PLC data types (UDT) cannot be exported." }
```

`ExportXml` deleted the previous file before calling `Export` (because `Export(…, None)`
refuses an existing file — the 2026-08-19 fix for 159 failures and zero objects), so when
Export threw there was nothing left. The refusal went into the manifest's `skipped` and
`warnings` lists and nowhere else: the runner reads only the stdout protocol, so the log had
no `WRN`, `_coverage.md` said *Skipped: 4* where it had said 3, the project card said *in
sync*, and a commit would have recorded the owner's only change as a deletion — the failure
mode this project exists to prevent. **Fix:** the previous file is set aside
(`Main.xml.versio-previous`) and put back when Export throws, a partial write discarded; the
refused object's previous manifest rows come forward (by path, or by identity for the
`.scl` beside a block) so the mirror still knows it and `LiveMatchesRecorded` can refuse to
delete a live object that no longer matches; and the refusal is emitted on stdout as a
warning, which the orchestrator logs and the UI shows. The kept file is the *last* export,
not the owner's edit: the sentence says so, and compiling the block brings the edit across.
Pure tests drive the real `ExportXml` against fakes whose `Export` throws, half-writes, or
writes.

**3. "Nothing committed to compare" over a committed block.** The pane's old side is
`git show HEAD:./…`, which hands over the file's bytes; Portal writes a UTF-8 BOM, and
`File.ReadAllText` strips it from the working copy while the XML reader refuses it on the
committed one — on this machine's .NET, `"﻿<?xml …"` fails with *Data at the root level
is invalid. Line 1, position 1.* `TiaPortalLadderParser.TryParse` swallowed the
`XmlException`, returned null, and `LadderDiff` drew every network as added. The log said so
in plain words every time (`9 networks … with nothing committed to compare; 0 changed, 9
added`) and nobody read it as wrong, because a first export's ladder reads exactly the same.
`TiaPortalMemberTableParser` had `TrimStart('﻿')` from its first day, which is why the
member tables beside it marked correctly. **Fix:** the same trim, and a test that parses a
specimen with and without the mark to the same shape and line spans.

**What the three had in common:** each was correct at the point it was written, and each
reported its symptom somewhere a person was not looking — a one-line diff per file, a JSON
list in the mirror, an `INF` line that reads like the ordinary first-export case.

---

### "exporting…" an hour after the export: a mid-run save handed the panel the follow-up's NoChange

**2026-10-29, `main`, the owner's `controls-design-siemens`, the screenshot at 14:36.** The
project row read *exporting…* with every object under it exported and the diff pane open on
one of them. The owner: "the main project name says exporting... it finished exporting a long
time ago". The log, `versio-2026-10-29.log`:

```
13:49:03.516 DBG [Sync] {b253ab9a} fae2130dee24: Project change (automatic, Project)
13:51:40.464 DBG [Sync] {b253ab9a} fae2130dee24: finished writing ExportedText; ignored 2808 echo(es) of it
13:51:40.464 INF [Sync] fae2130dee24: handling the change that arrived while the last run was in flight
13:51:40.464 DBG [Sync] {cf30c6bd} fae2130dee24: Project change (automatic, Project)
13:51:40.465 INF [Perf] {b253ab9a} Exporting LBB101 finished in 136.5s
13:51:41.315 DBG [Sync] {cf30c6bd} fae2130dee24: NoChange — content is identical to the last synced state (hash sha256:a09e4)
13:51:46.419 DBG [Sync] {d15f61e2} tree: 3 root(s), 1365 object(s), 1359 changed file(s), 1 project(s) hidden as in sync, 0 source project(s), 0 unchanged
```

and then nothing for that project but the remote poller's quiet lines. No *scanning for PLC
projects* after the run; the tree rebuild at 13:51:46 is the ordinary status refresh.

**The mechanism, in three parts that were each right alone.** `OnRunStarted` writes
*exporting…* over the row when the run passes its last gate (the fix for *Twenty seconds of work with
nothing on screen*, further down this file). `OnRunFinished` cleared `Busy`
and left the words to "the rescan that follows, which reads it from the lock", so as not to
blank the row between the run ending and the scan finishing. And the rescan follows an
outcome that `DidWork` — which the view model gets from `RunLoopAsync`, and the loop
returned whatever its *last* attempt said. A save that lands while an export runs is
queued by the coalescer; the export already going captures the file as it is; the queued
follow-up reaches the hash gate and correctly stops with `NoChange`. So the view model was
told "nothing happened" over a 136 s export: no summary, no lock sentence, no rescan, and
nothing put the row's words back. `SyncRunSignallingTests` even describes this follow-up as
the ordinary case, in the remark that explains why its own queued-save test has to move the
file a second time to get a second export.

**Two fixes, each enough on its own, checked without the other.** `RunLoopAsync` returns the
last attempt that ran the toolchain or failed, and only when none did, the last attempt of
all — a follow-up that itself ran or failed is the fresher fact and wins, a refused one is
already in the log at the gate that refused it. And `OnRunFinished` tells the row the lock's
label itself (`SyncNarrative.Label(StateOf(id))`, the words the rescan would have written)
and refreshes the tree as `OnRunStarted` does, because the row's words live there. The 2×2
on `RunFinishedRowStateTests`, which makes the mid-run save from inside the fake toolchain's
run: both off, red; either on, green. The Core test
`A_follow_up_that_finds_nothing_does_not_hide_the_export_that_ran` is red on the old loop.

**Same session, a UX change rather than a defect:** the count beside every folder and
project row (changed descendants: *1357* on this project, *91* on Program blocks) is gone
from the three desktop trees and the web tree. After a first export every object is a
change, so the numbers read as counts of items; the roll-up colour on the name still says
that something under a collapsed folder changed, and the facts pane still states the number.

---
### A TIA re-export asked Portal for every object: 202 s for a save that touched one block, and 1,355 XML files "changed" by their `<Created>` header

**2026-10-29, worktree `recursing-lederberg-c0c5ac`, a copy of the owner's `LBB101.ap20`
(nine PLCs, 1,340 blocks and types) under Portal V20.** A full export of the copy took
**202 s** run directly through the host: about 37 s of Portal start and project open, then one
`Export(FileInfo, None)` and one `GenerateSource` per block or type at roughly 88 ms each.
Every save paid all of it, because the export had no way to know what had changed. The same
afternoon the owner moved one rung in a `Main` on the real project; the installed Versio
exported 1,365 objects in 137.6 s and git showed **1,359 modified files** — 1,355 of them
XML, no `.scl` among them, each differing in the SimaticML document header's `<Created>`
timestamp Portal writes on every export (the G12 churn). The one block that had changed had
a four-line diff under that.

**What Portal knows.** `PlcBlock` and `PlcType` carry a read-only `ModifiedDate`, as an
attribute and as a CLR `DateTime` property with one value. Measured with a tools-only
`stampprobe` op in one Portal session — ten dumps of all 1,355 objects, 12 s each, after
scripted steps — the stamp moves exactly when Portal rewrites an object's content: an SCL
edit through `GenerateBlocksFromSource` moved the edited block alone (its four callers went
inconsistent and did not move); a PLC compile moved only the one caller Portal re-wired; a
second compile with nothing edited moved none; a UDT member added moved the UDT alone, and
the following compile moved the five dependents it rewrote; a rename is a new identity with a
new stamp; `Save()` moved none; close and reopen in a new process, none. Tag tables have
`ModifiedTimeStamp` instead and are always exported (15 on this project). The full table is
DRIVING §7.1.

**Fix.** Manifest schema 11: every TIA record carries `stamp` (UTC round-trip text) and the
manifest names its `toolchainVersion` (install release plus Openness assembly version). The
host reads the previous `_manifest.json` from the export tree before writing anything
(`ExportBaseline`) and keeps a block or type's files and records when: the manifest is
readable, schema ≥ 11, this vendor's and this Portal's; the live stamp equals the stamp on
every record under the identity; every recorded path is one the walk would write where the
object now is (a move exports — there is no move API to measure one through, so the path is
the check); and every recorded file is on disk hashing to its record (a hand-edited mirror
file exports, so Portal's text overwrites it). Anything else exports in full, counted by
reason, one `summary` line per PLC, which the runner now logs at Info — every `info` event
the host had ever emitted was parsed and dropped, not even at Debug. Deletions, renames and
new objects are unchanged: the walk records what it visits.

**Measured, same copy, same Portal.** Unchanged re-export **67.5 s**, wrote 26 files (tag
tables and stations), every block and type kept, textual files byte-identical. One block
edited through the import door and saved: **54.5 s**, wrote that block's `.scl` and `.xml`
and nothing else of the kind. Manifest deleted and the same state exported in full: 148 s,
and no textual file differs from the kept set; the kept stamps are the forced export's
stamps. The raw hash of 1,349 native XML records churned between the two, which is the churn
the skip spares the mirror.

**Pinned.** `ExportBaselineTests` (16) take each refusal on its own; `TiaPortalHostExportTests`
(9) drive the real walk against fakes that count their `Export` calls — a second export asks
for nothing, an edit exports one, a deletion drops the record, a rename and a move export, a
hand-edited mirror file exports, another Portal's manifest and a manifest whose stamps were
rewritten export everything. The gate Fact exports the fixture twice, asserts nothing of the
kind was written the second time, rewrites every stamp on disk and asserts the third export
writes them all — the mutation check, made to the file so it costs no host rebuild (each
rebuild is a new hash and a new Openness access dialog).

**Seen beside it, not fixed here.** (1) `<Created>` is an element, and the normalised hash
strips churn *attributes* only, so `nhash` churns on every re-export of a native file; a
delete of a NATIVE-only object (tag table, LAD block) verifies against it and would be refused
as "edited in Portal since". (2) `BlockType` reads the CLR type name and `GlobalDB` contains
`OB`, so a global data block is recorded as type `OB` (LBB101's `DB_PlantDpControl` is
`MHC_001/MHC_001/OB/DB_PlantDpControl#7`); consistent, so identities pair, but the tree
shows it under the wrong type. (3) The Rendered pane said *"No committed copy to compare
with: every network is new"* for a `Main.xml` git had a committed copy of, with a four-line
diff. (4) The two in-process host test classes swapped `Console.Out` under each other when
xUnit ran them in parallel — two of nine export tests red on the first full run, green alone;
they share one collection now. (5) On this machine `GenerateSource` yields no `.scl` for any data block under V20 or V21 — no `SCL_REVIEW` record and no `SCL_EXPORT_FAILED` warning, because the fallback returns false rather than throwing — so the gate's `Export_writes_a_manifest` is red here before and after this change; the installed 0.4.17's own LBB101 mirror shows the same forms. The owner's machine had it green on 2026-08-22.

**Process.** The first probe run measured nothing twice over: a plain background launch hands
the host a closed stdin and it quits COM before Portal starts (`HostLifetime.WatchStdin`), and
the hardware probe's attribute reader renders a `DateTime` as the string `DateTime`, so every
stamp column read "DateTime" and only the property column carried values. A shell under the
MSIX Claude app sees a virtualised `%LOCALAPPDATA%`, so the project copy, the job files and
`TEMP` all went to `C:\GIT\versio-scratch`, where Portal and the host see the same files.

---

### A TIA marker carried in by a copy: the project had never been opened, and the export refused anyway

**2026-10-29, `main`, the owner's `controls-design-siemens`, `LBB101.ap20` appearing in the
working tree at 12:39.** The panel read, in red, *"1 project cannot be exported yet: LBB101.ap20
is open in the IDE (TIA Portal: JhonatanVillarreal on FS-1872526918, since 2026-10-29
00:53:39Z), so it cannot be exported until it is closed. Close it and the export will run. If
nobody has it open, TIA Portal left its marker behind: delete JV_WorkingSpace/LBB101/LBB101.info
and the export will run."* The owner's words: "this keeps being a false positive, the project is
not open, in fact it has never been opened directly".

**What had happened, from the file times.** The `.info` beside the `.ap20` was dated 28 Oct
17:53 local — the minute the `Time` inside it names in UTC; the `.ap20` itself 14:43 that day;
every folder in the project 29 Oct 12:39, the minute Versio saw the project appear. A folder
copy keeps file times and stamps the folders with the copy. So the project had been created
and saved by a Portal session the day before, that session left its marker behind exactly as
the 2026-10-28 session did, and the copy brought the marker into the repository with it. No
Portal window had ever opened *this* copy. `Get-Process` at the time: no
`Siemens.Automation.Portal` on the machine at all — the ObjectFrame, Help and Telemetry
services, which are not Portal, and nothing else.

**The defect.** The 2026-10-28 fix made the refusal name the file, and that still left a person
to delete it — the second time in two days, for a leftover the engine held every fact it needed
to dismiss: the marker names *this* machine, carries no pid, and the one process that can hold
a Portal project on this machine was not running. The previous entry withheld an automatic
verdict, on purpose, until the exclusive-open probe had been measured on the open side. That
measurement is still not made, and this case does not need it: a process that does not exist
holds nothing, which is the same reasoning a dead pid already gets for CODESYS, asked by name
instead of by pid.

**Fix.** `IPlcProjectFormat.HolderProcessNames`, default empty; the TIA pack names
`Siemens.Automation.Portal` — the name the host watches appear in `CreatePortal`, the runner
reaps in `LogReaped`, and the 2026-10-28 entry counted two of. `IProcessProbe.IsAnyProcessRunning`,
default `true` so that a probe which cannot look believes the marker. `ProjectOccupancyProbe.Resolve`
takes the names: a Held marker with no pid is Stale when it names this machine outright, the
vendor names processes, and none of them is running; a marker that names no machine (a shape
Versio did not parse) is still believed, and so is one from another machine, with nothing asked
about processes here. The probe logs the dismissal once per check, naming the vendor, the
holder and the file, because an export that runs over a marker in silence looks like the
two-writers defect the marker exists to prevent. Measured on this machine with the real probe
against a copy of the owner's repository, before the commit: `portal running=False`,
`verdict=Stale`, and the line *"fae2130dee24: TIA Portal left its marker behind
(JhonatanVillarreal on FS-1872526918, since 2026-10-29 00:53:39Z) and no TIA Portal process is
running on this machine, so nothing here can be holding the project; treating it as closed:
…/LBB101.info"*.

**What is still Held, on purpose.** A leftover beside a *running* Portal — the window left open
on its start page after the project was closed, or holding another project. A process name
cannot tell those from a live hold, so the refusal keeps naming the file. And Portal outlived a
project close by minutes on 2026-08-19 (DRIVING §5), so the retry that runs when the program
store saves on close can land while the process is still on its way out; the next retry is the
next change to the tree. What would close that gap: asking each running Portal which project it
holds (Openness `TiaPortalProcess.ProjectPath`) through the host — a spawn per check, with the
access list in the way. STATUS item 19 narrows to that.

**Mutation check:** with the names ignored in `Resolve`, the two new policy tests go red and the
fifteen beside them stay green; restored, 17/17.

**Tests:** `ProjectOccupancyTests` (Stale when the IDE is absent; believed while it runs;
believed from another machine with nothing asked; an unparsed marker is not rescued; the probe
passes the vendor's names through and logs the dismissal), `TiaPortalPureTests` (the name),
`ProcessProbeTests` in Integration (the real lookup, both answers — the interface default would
keep every leftover believed with no unit test noticing).

### The Openness access list: read before launch, never written, and said on the card

**2026-10-29, `main`, the owner's `controls-design-siemens`, the afternoon after the two
findings below.** The installed Versio (0.4.17, with the V20 support and the group fix of the
morning) exported the V20 project for the first time, and Portal put up *Openness access
(0033:000666)* — *"The application 'Versio.Plc.TiaPortal.Host.exe' … is attempting to access
the TIA Portal … Do you want to grant access?"* — over the failure pane. The owner's words:
"am I going to get this message every time?", and then, on the alternative of Versio writing
the entry itself: "whats the downside of trusting its own next build?"

**What the registry said.** Under `HKLM\SOFTWARE\Siemens\Automation\Openness` there were three
entries for the one exe name, in two lists:

| Key | `Path` | `FileHash` | `DateModified` |
|---|---|---|---|
| `20.0\Whitelist\…\Entry` | `…\Packages\Claude_…\LocalCache\Local\Versio\current\tia\…Host.exe` | `uNMGyQ4q…` | `2026/10/28 20:55:24.056` |
| `AllowList\…\Entry` | `…\AppData\Local\Versio\current\tia\…Host.exe` | `yuBvRtGV…` | `2026/10/29 16:04:33.956` |
| `20.0\Whitelist\…\Entry (1)` | `…\AppData\Local\Versio\current\tia\…Host.exe` | `/3sK8enX…` | `2026/10/29 19:31:13.153` |

`uNMGyQ4q…` is the base64 SHA-256 of the 2026-10-28 host, and `20:55:24.056` is that file's
last write in UTC to the millisecond; `/3sK8enX…` is the SHA-256 of the Release host built at
12:25 local and installed at 12:31:13, five seconds before the log's *Versio 0.4.17 starting*.
So the entry is path + file hash + file write time, V21 keeps it at the root `AllowList` and
V20 under `20.0\Whitelist`, each release keeps its own, and "once per update per release" is
what the three rows are. The first row's path is the Claude app's virtualised
`%LOCALAPPDATA%`: a Versio started from that app's shell saw the install through MSIX
redirection, and Portal recorded the path the process reported. The same redirection made the
shell's hash of the installed host the 2026-10-28 one while Portal was hashing the 0.4.17 file
— ten minutes of "three hashes for one file" until the Release build's hash matched the third.

**The decision.** Pre-registering the host from the elevated helper was rejected, with the
owner: it moves the trust decision from a person at the screen to the release channel, it
still needs an administrator because the list is under HKLM (so a UAC prompt per update
instead of Siemens's dialog per update), and it depends on an undocumented format. What had
actually cost the morning was the surprise and the nine-minute silence of an unanswered dialog,
whose timeout — *"Security error. The operation has timed out."* — names neither Openness nor
access.

**What was built.** `OpennessAccessList` in the TIA Infrastructure pack reads the list for a
release and a host (path and hash; the date is not compared, and an entry for the path with
another hash is said to be the post-update state); `PlcToolchainStatus.Notice`, neutral, is a
sentence true of an *available* toolchain for its next run; the TIA probe fills it per release
that will ask, the project card reads *installed. Portal V20 will ask for Openness access…*,
Settings → System information has a *Next run* row, the runner re-checks at launch and warns,
and the security timeout on a run that knew a dialog was coming carries what to press next
time. Ten tests in `OpennessAccessListTests` (the key rename between V20 and V21, the hash and
stamp formats against an independent SHA-256, the match rule, a never-granted file against the
real registry, an unhashable host as Unknown, the hint rule, and the notice and runner warning
against a fake list), one `ToolchainRowsTests` row and `ToolchainNoticeTests` in the App suite.
DRIVING §2.3 has the shape; STATUS item 20 is struck through.

### The Openness group: detected before Portal is launched, and fixed from a button

**2026-10-29, `main`, the owner's `controls-design-siemens`, the hour before the two-releases
finding below.** The first export after the leftover marker was deleted failed with *"export
failed — Security error. Can't access this process to acquire needed information. Security
error. Owner 'AzureAD\JhonatanVillarreal' of this process is not member of the windows group
'Siemens TIA Openness'. Please contact your administrator."* — Portal's own words, three
seconds into a launch, on the failure card with *Try again*. The owner's words: "how do I fix
this? security error". The fix was a command in chat (`net localgroup "Siemens TIA Openness"
"AzureAD\JhonatanVillarreal" /add`, elevated) and a sign-out, after which `whoami /groups` showed
the group and Portal admitted the host. Then the owner asked whether the install could do
that, and asked for the fix on the project card and the failure pane as well as in Settings.

**What was wrong.** Nothing had asked the question before Portal was launched. The toolchain
probe checked the install and the host and said *ready*; the panel's project card said
*Toolchain: installed* for any vendor; the refusal arrived as a failure an export later, and
the sentence it arrived in named a Windows group and nothing to press. Everything true, nothing
actionable, an hour in.

**Why not the installer.** It is per-user by design — `%LOCALAPPDATA%`, no administrator, so
updates apply themselves — and adding a local-group member needs elevation. Membership also
only enters the logon token at the next sign-in, so even a perfect install step ends in "now
sign out", which an installer cannot do for anybody. The group may not exist (Portal without
the Openness feature). And the account form varies — `AzureAD\Name`, `DOMAIN\name`, local,
Microsoft account — so a typed name is the wrong key. Built instead: detect early, explain
exactly, offer the fix as one consented click, and leave the installer alone.

**What was built.**

1. **A neutral remedy on the toolchain status.** `PlcToolchainStatus.Remedy`
   (`PlcToolchainRemedy`: the button's label, what pressing it does, what to do afterwards)
   and `IPlcToolchain.ApplyRemedyAsync` returning a `PlcRemedyOutcome` with a sentence either
   way. The default refuses. Nothing above the seam knows what a fix is; it knows there is one.
2. **`Versio.Infrastructure.Platform.LocalGroups`**, Windows and neutral. `Check(group)` reads
   the process token (`WindowsPrincipal.IsInRole`) and, when the group is not in it, the member
   list through `NetLocalGroupGetMembers` as SIDs — not `net localgroup`, whose output is in the
   display language, closes with a localised line an English-only parser would count as a
   member, and prints an unresolvable account as its SID. Four standings: in the token; a
   member whose sign-in predates the add; not a member; no such group. `JoinElevatedAsync`
   re-launches `Versio.exe --join-local-group <group> <SID> <result file>` with the `runas`
   verb — the UAC prompt is the only honest way to rights the app does not have — and
   `Program.Main` runs `LocalGroups.RunJoin` before Velopack or WPF: `Add-LocalGroupMember
   -Group … -Member <SID>`, one line in the result file, exit 0; "already a member" is a success.
   The SID, never the name, because the process Windows elevates may be a different account (a
   standard user typing an administrator's credentials). The helper validates both arguments
   against the one shape each legitimately has before they reach a PowerShell command line;
   whoever can start the exe elevated is an administrator already, so this is hygiene, not a
   boundary. A declined prompt (`ERROR_CANCELLED`, 1223) is a refusal in those words.
3. **The TIA probe asks first.** After the install and host checks, `TiaPortalToolchain`
   checks `Siemens TIA Openness` (~20 ms in the dev build's log) and reports: not a member →
   unavailable, naming the account and the group, **with the remedy**; member not in the token
   → unavailable, *sign out and back in*, no remedy, because the add already happened and a
   button that succeeds and changes nothing teaches people that buttons do nothing; no group →
   unavailable, *the Openness feature is not installed*, no remedy; a check that failed → Portal
   is asked as before and the log says why the check could not be made. `ApplyRemedyAsync`
   re-checks before acting — the status may be minutes old — and a member by now is told so
   with no helper started.
4. **Three places, one offer.** The project card (`ProjectSummaryRender.Remedy`, under the
   facts; and its *Toolchain* row is per vendor now, the vendor's own sentence instead of
   "installed" for any vendor), the failure pane (`SyncViewModel.FailureRemedy`, under the
   toolchain's refusal, beside *Try again*), and Settings → System information (a *Fix* row
   with a button, the window re-gathering its rows afterwards and showing the sentence under
   the list). All three run `ShellViewModel.FixToolchainAsync` → `ApplyToolchainRemedyAsync`,
   which logs both ends, probes again, and refreshes the failure pane; the shell posts the
   outcome as a notification. The runners are not rebuilt afterwards, on purpose: the first
   remedy cannot take effect in this session, and a remedy that could would be the moment.

**Measurement.** `OpennessGroupTests` (7, fake group reader, fake install tree): the four
standings' sentences and which carries the remedy; the fix joins by SID and ends with what to
do next; a declined prompt passes through; a re-check finds a member and starts no helper.
`LocalGroupsTests` (5, the real reader): *Users* is in every interactive token (an Azure AD
account's SID is `S-1-12-…`, which the first assertion did not expect); a made-up group is
*GroupMissing*, not an exception; *Guests* is *NotMember* from a real member list;
*Administrators* is read from the list when a non-elevated token filters it; the helper refuses
a malformed command line and a SID or group name that is not one. `ToolchainRemedyFlowTests`
(3, the shell with a refusing toolchain): the card's Toolchain row and remedy, the failure
pane's remedy after a gate export, one press → one apply, the notification, the re-probe, every
offer gone; a refused fix leaves the offer up and warns; a vendor with nothing to offer is
refused without a success. `ToolchainRowsTests` and `SystemInfoWindowTests` cover the Fix row
and the window's button, including an action that throws. The dev build on `C:/GIT/Versio`
started with zero `ERR` lines and the probe reporting both Portals ready. **Not measured here:
the UAC path on a machine where the account is not in the group** — this machine's account is,
since the morning. The next machine that is not is the measurement; the log will show
*asking Windows for approval to add …* and the helper's one line.

**Review, the same day.** The owner had the fourteen commits reviewed by three agents of
another tool and brought back six items; all six held:

- **The elevated helper launched a bare `powershell.exe`** (P1). Windows looks in the
  application and current directories before `PATH`, so a planted executable could have run
  as administrator the moment the user approved the fix. The helper names
  `%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe` by absolute path now.
- **Settings' Fix button acted on the repository's cached probe**, not the probe the window
  had just made — absent before a repository is opened, older after a change — and answered
  "offers no fix right now" to a row that plainly offered one. The row's action carries that
  probe's toolchain and status to `ApplyToolchainRemedyAsync(toolchain, status)`; the
  repository's probe is refreshed afterwards when there is one.
- **The helper's timeout killed.** `ChildProcess.WaitForExitOrKillAsync` tries to kill on
  timeout and then waits without one; an unelevated parent cannot kill an elevated child, so
  a hung helper would have thrown and then hung the fix. A plain timed wait now, and the
  helper is left to finish on its own.
- **The four fixture gate tests accepted any Portal**, and the fixture is V21: a V20-only
  machine — supported since the morning — would have read four red. They skip with the
  reason, the same prerequisite as the environment-driven Probe.
- **The group tests named `Users`, `Guests` and `Administrators` in English.** They resolve
  the local names from the well-known SIDs now, so a German *Benutzer* is the same group.
- **"Already a member" was read from the cmdlet's English error text.** The helper reads the
  group itself, before and after the add, with the same `NetLocalGroupGetMembers` the probe
  uses; the cmdlet's words are only ever shown, never interpreted. A test that would have
  proven the pre-check against `Users` was written and then removed: an interactive account
  is in `Users` through a nested group, not directly, so the helper would not have found it
  listed and would have attempted a real add from a test.

**A second review, read-only, of the same commits** found seven; two were the timeout and the
error-text items above, already fixed, and the rest held:

- **The UAC prompt blocked the UI thread.** `Process.Start` with the `runas` verb returns only
  once the consent dialog is answered, and the whole chain ran on the dispatcher — the window
  would have stopped painting under the prompt. The start runs on a worker now.
- **The group lookup could leave the machine.** `NTAccount("Siemens TIA Openness")` is looked
  up in the built-in and local databases and then in the primary and trusted domains, so a
  domain-joined laptop with no local group would have waited on an unreachable domain
  controller at every probe, and an enterprise with a *domain* group of that name would have
  resolved to it and read "could not be read" instead of "missing". The lookup names the
  machine and then `BUILTIN` (the built-in aliases live there, which the first fix missed and
  the group tests caught), and `NetLocalGroupGetMembers`' own not-found codes (2220, 1376) are
  *GroupMissing* too.
- **After a fix that makes a vendor available, Try again still ran the refusing runner** built
  at open, while the card said installed. The first remedy cannot take effect in this session,
  but the shape is neutral: when the re-probe flips a vendor to available the sync engine is
  rebuilt, and the flow test presses Try again afterwards and sees the export go through.
- **Two fixes could run at once**: the main window's command has its own guard, Settings
  bypassed it, and the modal could be closed from the title bar mid-action, unblocking the
  main window and its Fix button under the still-open prompt. One `RemedyInFlight` on the sync
  model, read by the command and by `ApplyToolchainRemedyAsync` itself; the dialog refuses to
  close while its action runs and says so under the list.
- **The exe's join branch had no test**, and it is the dangerous one: deleted, the elevated
  re-launch would open a full Versio window as administrator and every in-process test would
  stay green. `ExeJoinBranchTests` starts the built `Versio.exe` the way the helper does, with
  a SID that is not one, and reads exit code 2 and the result file.
- **Apply-time standings** other than member-not-in-token fell through to the join when
  deleted; a theory covers all three now. And the "no group" sentence said the Openness
  feature was not installed, which has not been a choice since V14 SP1 — a missing group on a
  Portal machine was deleted or imaged away, and the sentence says how to recreate it.

**And one more from the owner, after that.** The helper's timeout leaves it running on
purpose, so the account may yet be added — and the shell said *Nothing was changed*, which it
did not know. `PlcRemedyOutcome` has a third answer, `Uncertain`, the timeout returns it with
*still running; check the group before pressing again*, and the shell posts it as a warning,
*Not finished*, neither success nor refusal.

**Also seen on the way.** The failure pane is reachable only after an export has been
attempted, and an unavailable toolchain is not asked to export on open — so on a fresh machine
the card and Settings are where the button is first seen, and the pane only once a commit or a
retry has asked. The flow test drives a commit for that reason. The flow test's first version
waited thirty seconds for a catch-up export that, correctly, never came.

**Left for a decision, not forgotten.** The Openness access list (DRIVING §2.3) is keyed by
host path and hash, so every Versio update brings the *Openness access* dialog back on the
first connect. The same elevated helper could pre-register the new host. That is a security
control saying "trust this binary", and an auto-updater quietly trusting its own next build is
a reviewer's decision, not a side effect of a group fix. STATUS *What is next* has it.

---

### Two Portal releases on one machine: the locator saw one, and a V20 project went to V21

**2026-10-29, `main`, the owner's `controls-design-siemens`, the first export after the
Openness group was fixed.** The failure card read *"export failed — Error when calling method
'Open' of type 'Siemens.Engineering.ProjectComposition'. Unable to open the project under path
'…\ProjectInitial.ap20'. Compatibility error. Project '…' with version 'V20' can only be opened
with a software product of the same version. The current product version is 'V21'."* The
machine has Portal V20 and Portal V21 installed side by side, and the project is V20.

**What the log said.** `skipped C:\Program Files\Siemens\Automation\Portal V20: no PublicAPI
net48 folder` — at **debug** level — and then `toolchain ready: …\Portal V21 — V21`. The panel
said *Toolchain: installed*. Nothing anywhere said a release had been passed over.

**Three defects, one path.**

1. **The locator knew one Openness layout.** V21 ships Openness as modular assemblies under
   `PublicAPI\V21\net48\` (`Siemens.Engineering.Base.dll`, `…Step7.dll`, …) and the locator
   was written against that install on 2026-08-19. Every release before V21 ships **one
   `Siemens.Engineering.dll` directly under `PublicAPI\VNN\`** — Portal V20 has
   `PublicAPI\V17`, `V18`, `V19` and `V20`, each with that one file and `…Hmi.dll` beside it,
   and no `net48` anywhere. So V20 was invisible, and a machine with two releases was, to
   Versio, a machine with one. The host never cared: `OpennessLoader` loads every
   `Siemens.Engineering*.dll` in the folder it is given and finds `Siemens.Engineering.TiaPortal`
   by name. Fixed: `FindApiFolder` accepts the single-assembly layout for the install's own
   release — and only its own, because the older API folders an install carries for
   compatibility are not the install — and `TiaPortalInstall.ApiShape` says which was found.
   The skip line is Info now.

2. **The runner fell to the newest install.** `LocalTiaPortalRunner` took the install matching
   the pinned profile, *else the first* — the newest. With V21 the only install in view, that
   was V21 for a `.ap20`, and Portal refused. But the fallback was wrong on its own terms: the
   newest install is exactly the accidental-upgrade path the toolchain refuses several installs
   over, and it had been reachable from the runner all along. Fixed:
   `LocalTiaPortalRunner.ChooseInstall` reads the release from the project's extension
   (`TiaPortalProjectFormat.ReleaseOf`, the same fact the explorer pill has shown since
   2026-08) and picks that install; a release that is not installed is refused in a sentence
   that names the project's release, the installed ones, and that upgrading is the user's act
   in Portal. The profile is consulted only for a path with no number in its extension.

3. **The toolchain refused several releases over a choice nobody could make.** *"2 TIA Portal
   versions installed — pick the one this repository targets before exporting"* with a
   `Candidates` list; the list reached Settings → System information as *Also found* rows and
   no dialog ever offered the pick. Had the locator seen both releases, the owner would have
   been stuck one step earlier with no way through. The guard was aimed at the wrong place: the
   upgrade risk is in which Portal *opens a given project*, and the file says. Fixed: the
   toolchain is available with every installed release (`Version` = `V21, V20`, `Candidates`
   informational, `Profile` the newest), and the per-project choice is the runner's. The
   `Candidates` remark on `PlcToolchainStatus` now says both meanings; the first vendor's
   refusal — where nothing on a `.project` names its profile — is unchanged.

**Measurement.** `PortalReleaseChoiceTests` (8, fake install trees laid out like the real ones,
run on every machine): both layouts found with the right shape; an install whose own API
folder is missing is not driven through an older one; `.ap20` → V20 and `.ap21` → V21 with the
profile pointing the other way; `.ap20` with only V21 installed is the refusal sentence; two
releases leave the toolchain available. The real V20 path, through the env-gated Probe in
`TiaPortalGateTests` against the owner's `ProjectInitial.ap20`: the first attempt drove the
host this test project builds beside itself, which Portal's Openness access list does not
name (the list is keyed by path and hash, and its one entry is the installed Versio's host,
added 09:04 that morning when the V21 attempt asked), and ran into the twenty-minute timeout
having measured nothing; the second, with `VERSIO_TIA_PROBE_HOST` pointed at the installed
host, **passed in 2 m 14 s** — *`Probe of …ProjectInitial.ap20 via
…\current\tia\Versio.Plc.TiaPortal.Host.exe [Portal V20, single Siemens.Engineering.dll;
profile V21]`*, `report.json` written. The host binary is unchanged by this work, so the
installed one is as good a subject as the fresh one. The toolchain line now reads
*`toolchain ready: …\Portal V21 — V21; …\Portal V20 — V20. The project file's extension picks
which one opens it.`*

**Review, the same day.** A reviewer given the five commits and not the reasoning found
seven things; three were real and two of those it proved by mutation:

- **The wiring was untested.** Swapping the project path out of the `RunAsync` →
  `ChooseInstall` call — which turns every job back into "profile, else newest", the defect
  itself — left every test green, because nothing in the fast suites called `RunAsync`. The
  same shape as *moved a subscription? delete it and run the suite*. Now
  `RunAsync_refuses_from_the_project_path_before_any_host_is_started` drives the real
  method with a host path that does not exist: the refusal has to come before a process is
  spawned, or the test fails on the missing executable instead.
- **A V20 project was pinned as V21 in the committed `repo.json`.** Adoption wrote the
  probe's profile — now the newest Portal — on every new binding. Nothing read it as the
  release yet, the log said `[V21]` on every export of the V20 project, and the first guard
  to trust `binding.Profile` would have acted on a falsehood in a shared file. Fixed through
  the seam: `IPlcProjectFormat.ProfileFor(path, probeProfile)` defaults to the probe's word
  and the TIA format answers with the extension's release; `TiaBindingProfileTests` adopts a
  `.ap20` under a probe that says something else and reads `"profile": "V20"` back.
- **Settings → System information called the installed releases "Also found"** under a
  comment saying the list was a problem to pick from, and the vendor's sentence on how the
  choice is made never reached the window. The rows say *Installed* and *Choice* when the
  toolchain is available, *Also found* when it refused; the builder is a static method now
  so `ToolchainRowsTests` can check it without probing the machine.

And four hardening items, each with a test: the release is the `V\d+` in the install folder's
name rather than everything after `Portal ` (so `Portal V21 Update 2`, should one ever exist,
is V21 and not a version no project can match); the modular lookup's fallback to *any*
`*\net48` under `PublicAPI` is gone, because it would have reported a `Portal V22` carrying
only `V21\net48` as V22 driven through V21 — the exact thing the single-assembly path refused
and the remark above it claimed for both; installs are ordered by release number and a
release found under both Program Files roots is reported once; and the refusal names the
*nearest* newer Portal to upgrade in, not the newest, since an upgrade is one step nobody
takes back. The locator's *skipped* line is pinned at Information with its folder and reason,
because the commit raised it from Debug for a stated reason and nothing held it there.

**Also seen on the way.** The Openness group error from the hour before this one
(*"Owner 'AzureAD\…' of this process is not member of the windows group 'Siemens TIA
Openness'"*) is DRIVING §2.2 and was fixed by `net localgroup … /add` plus a sign-out; it is
in the panel's failure card verbatim, which was enough to act on. The leftover `.info` marker
from 2026-10-28 was deleted by hand and the export ran on its own, as the previous entry
predicted.

---

### First open of a repository: "BrokenBinding", a refusal that named nothing, and a TIA marker that never left

**2026-10-28, `main`, the owner's `controls-design-siemens`, opened in Versio for the first
time with a V20 project.** The project panel read, in red, *"BrokenBinding:
JV_WorkingSpace/ProjectInitial/ProjectInitial — the bound export directory no longer exists; a
full export will recreate it"* over *"1 project has changes that have not been exported."*, and
the row said *never exported*. The owner's words: "why am I seeing this?", then "messages should
be a lot clearer on the versio ide", then "I also closed my tia and this did not run
automatically". Three defects, one screen.

**What had happened, from the log.** The open found `ProjectInitial.ap20`, adopted it
automatically (binding `0b92565a9b10`, V21 profile; `.versio/repo.json`, `.gitattributes` and
`.gitignore` written, the three changed files the panel listed), and tried the first export,
which the orchestrator refused: *"open in the IDE (another session), so it cannot be exported
until it is closed. Close it and the export will run."* The owner closed the project in TIA
Portal at 16:52:07 — the watcher saw `System/~PEData.3` go and the program store saved — and
the retry ran, and was refused with the same sentence.

**Defect 1 — a never-exported binding was "broken".** `OrphanScanner` reported any binding
whose export directory was absent as `BrokenBinding` with *"no longer exists"*. For a project
adopted seconds earlier the directory never existed, nothing was broken, and the row already
said *never exported*: one fact, stated three times, once falsely and once in red. The scanner's
own class remark still said adoption was "offered rather than performed", which stopped being
true when `AutoAdoptAsync` landed. **Fix:** `BindingHealth.NotExported`, not needing attention,
with *"no export tree at '…' yet; the next export writes it"*; `ScanFinding.Title` gives every
verdict words, so the panel no longer prints enum members. A deleted mirror lands in the same
verdict, deliberately: the scan cannot tell the two apart and the remedy is the same automatic
export.

**Defect 2 — the refusal was in the log and nowhere else.** The panel said "has changes that
have not been exported" and the row said "not exported", both true, neither the answer to the
owner's question. `SyncViewModel` already remembered which bindings had returned
`BlockedByOpenProject` (for the retry when the marker vanishes) but kept only the ids.
**Fix:** it keeps the refusal's sentence, the row reads *waiting for the IDE*, and the banner
reads *"1 project cannot be exported yet: <the orchestrator's sentence>"*, with that project no
longer also counted under "changes not exported". `WaitingOnIdePanelTests` proves the banner
reads it back; `RefreshStalenessAsync` became internal for it, because a top-bar refresh does
not recompute the banner and no test can make a toolchain refuse without a real IDE.

**Defect 3 — the marker TIA Portal left behind, and a sentence that could not be acted on.**
`ProjectInitial.info` beside the `.ap20` was dated **26 Oct 09:21**, 23 s after the second of
two `Siemens.Automation.Portal` processes started (09:21:22; both were still running on the
28th). Today's session neither rewrote it on open nor removed it on close, while
`System/~PEData.3` did go on close; afterwards an exclusive open of the `.ap20`, `PEData.plf`,
`PEData.idx` and the `.info` itself all succeeded — nothing held the project. The marker's
body, never parsed until now, is `<InfoFile>` with `User`, `Computer` and `Time`, and no pid, so
`ProjectOccupancyProbe.Resolve` had nothing to declare it stale by, and "close it and the
export will run" was advice about a project that was already closed, with no mention of the
file that had to be deleted. This contradicts the 2026-08-19 GUI measurement in
`docs/vendors/tia/DRIVING.md` §5 (V21 window, graceful `taskkill`: the marker went on close);
that measurement stands for what it measured, and this one shows a case it did not cover.
**Fix:** `TiaPortalProjectFormat.ParseOccupancy` reads user, machine and time (through
`SafeXml`, as the arch rule requires — the first draft used `XDocument.Parse` and
`PurityRuleTests` caught it); `ProjectOccupancy` gained `Since` and `Marker`, the probe fills
`Marker` with the path it read; the orchestrator's refusal now reads *"open in the IDE (TIA
Portal: <user> on <machine>, since 2026-10-26 16:21:45Z), so it cannot be exported until it
is closed. Close it and the export will run. If nobody has it open, TIA Portal left its marker
behind: delete JV_WorkingSpace/ProjectInitial/ProjectInitial.info and the export will run."*
"Open in the IDE" stays in the wording; three tests and the commit refusal's clipboard text
look for it.

**Not done, on purpose.** Declaring the `.info` stale automatically. The only process-level
signal measured is the exclusive-open probe above, and it was measured on the *closed* side
only; whether Portal holds `PEData.plf` exclusively while the project is open was not
measured, and a stale policy built on one half of a measurement is the two-writers defect
waiting to happen. STATUS carries it as a watch item with the measurement that would settle
it.

**Tests:** `OrphanScannerTests` (two), `ProjectOccupancyTests` (the probe names the file),
`SyncOrchestratorTests` (the sentence names vendor, time and file), `TiaPortalPureTests`
(the real marker shape, and non-XML is still Held), `WaitingOnIdePanelTests`.

### A method header that gained PUBLIC showed nowhere on the Rendered tab, by decision

**2026-10-28, `main`, the owner's `CodesysStandardLibrary`, commit `5780206` in the History
panel.** `FB_Template.Reset`: the tree said M, Raw showed `METHOD Reset` become
`METHOD PUBLIC Reset`, and the Rendered tab drew the title, an empty interface table and the
two unchanged lines of code with nothing marked and no sentence. The owner's words: "just found
another thing that doesnt show under render".

**Cause: it was decided, and the decision's cost came due.** On 2026-10-23 the same change
(`MoveHome`, the owner's real pull request) made its empty table warn "1 declaration change is
not shown in this table" beside a code block that showed every change, and the owner ruled the
header line framing. `HeaderResidue` then left the modifiers out of the residue, and its remark
stated the cost in so many words: *an access modifier changed from one value to another is not
drawn and not warned about*. Both halves of that were true five days later. Dropping the warning
was right — it said "go to Raw" about a line Raw had nothing more to say about — but it
removed the only thing that showed the change, instead of drawing the change.

**Fix: the header's modifiers are chips beside the title.** `MemberTable.Modifiers` carries the
keywords between the kind and the name (`PUBLIC`, `PRIVATE`, `PROTECTED`, `INTERNAL`, `FINAL`,
`ABSTRACT`), in the order written, read by the neutral declaration reader so CODESYS and
TwinCAT get them alike. They are `MemberAttribute` chips with `IsModifier` set, drawn by the
same chip template on the desktop (an `ItemsControl` docked after the name) and the same
`Chips` in `MemberTableHtml` (a `mt-mods` span inside the head), and compared by
`CompareAttributes` by name — so `PUBLIC` gained is one green chip, and `PRIVATE` become
`PUBLIC` is `PRIVATE` struck beside `PUBLIC` green, the header's own words rather than one chip
"changed from". The attribute strip under the title is untouched: a modifier is part of what the
object is, not a pragma on it. Nothing warns, because nothing is hidden.

**Review coverage found a second, older gap while this was tested.** The header line stays
framing when it is only keywords, so a method without a return type was vouched for before and
after. A method *with* a return type has its header as its Return row, whose words differ across
the sides and whose cells do not, so a gained `PUBLIC` there was a coverage gap all along, on
both sides. And a chip cannot close it alone: the chip is written on one side only (a gained
`PUBLIC` has no line on the older side), while the header line is edited on both. So the table
now carries `Header` / `HeaderBefore` (the line the title and modifiers are read from), and
`Coverage.Table` vouches for the header on both sides when any modifier chip is drawn as
changed. Every other edit to that line belongs to another rule: the type and the comment are the
Return row's cells, the name is a row added and one removed, an `EXTENDS` is unshown and refused.

**Checked by breaking it**, each mutation turning at least one Core test red and the restored
file touched before the green baseline: the modifier arm of `Coverage.Table`; the `Modifiers`
comparison in `MemberTableDiff.Compare` (three tests red); the `mt-mods` span; the `TakeWhile`
that reads the keywords; the `Header` assignment; and the `Header` shift to file lines in
`Parse`. Four tests added: `StDeclarationTableTests`, `MemberTableDiffTests`,
`MemberTableHtmlTests`, `CoverageTests`. Core 2,107.

**The XAML is covered this time.** On 2026-10-23 the chip template was left to the owner's eye,
because the App suite drives the view model and not the templates. `ModifierChipWindowTests`
realises the real window over a one-method source project committed as `METHOD DoIt` and edited
to `METHOD PUBLIC DoIt`, waits for the marked table, lays the window out headless, and asserts
the `ItemsControl` after the name is visible, holds a `PUBLIC` text in the added-status brush,
and sits to the right of the name on its line. Pointing its `ItemsSource` at `Attributes`
instead of `Modifiers` — the silent-binding failure that test exists for — turns it red. With
`SNAP_OUT` set it writes the laid-out window to a PNG, which was looked at: the chip reads
`DoIt` `PUBLIC` in green over the interface table. App 875. The dev build could not be pointed
at the owner's library while the installed Versio held it — a second Versio on the same
repository hands over to the first — so the look on the owner's own repository is theirs to
eyeball on the next release.

---

### PortableGit was two-fifths of every update

**2026-10-28, after *The update card sat on 70%*.** A delta update downloads in about a
second, and then Velopack rebuilds the full package from the installed copy plus the delta:
91 s on 0.4.10 → 0.4.11. The installed copy was 686 MB, and `git/` was 410 MB of it
(mingw64 211, usr 197). Most of what that rebuild recompresses is a git nobody changed.

**What Versio actually runs**, read from `GitExecutable` and every `RunAsync` call: `cmd\git.exe`
(the launcher, which puts mingw64\bin and usr\bin on PATH), the builtins, `git-remote-https`
and its curl/OpenSSL stack, `git lfs`, `credential fill` through GCM, and — not from Versio's
own code but from the repositories it opens — **sh**, because every hook runs under it (`git lfs
install` writes four) and so does every `!` alias. Two things in PortableGit's own config also
run: the `filter "lfs"` driver and the `astextplain` diff driver, which `etc\gitattributes`
routes `*.doc`, `*.pdf`, `*.docx` and others through, and which Versio's plain `git diff`
triggers. The one editor path (`rebase --continue`) runs with `GIT_EDITOR=":"`.

**What was cut**, a denylist in `tools/pack/Trim-PortableGit.ps1`, measured on an extracted
2.55.0.5 (MiB, `Get-ChildItem` sums):

| Removed | MB | Why it was safe |
|---|---|---|
| vim (six 3.8 MB copies of one exe) + `usr\share\vim` | 61.3 | no editor is ever launched |
| perl + `usr\lib\perl5`, `usr\share\perl5` | 38.8 | callers are send-email, `c_rehash`, `docx2txt.pl` |
| `mingw64\share\doc\git-doc` | 19.1 | `git help` opens a browser |
| Tcl/Tk, gitk, git gui (both DLL copies) | 12.9 | Versio is the GUI |
| Subversion DLLs | 3.3 | their consumer, git-svn, is not even shipped |
| mintty, tig, nano, `git-bash.exe` | 5.9 | nothing draws a console |
| **PortableGit** | **384.9 → 244.1** | |

Packed 0.4.11 against the published one: full `.nupkg` **287.8 → 231.6 MB**, Setup.exe 292.3
→ 236.1 MB, MSI 162.7 → 134.3 MB, extracted payload 686 → 532 MB. The compressed saving is
smaller than the raw one because vim's runtime and perl's modules are text and zip well; the
rebuild after a delta works on the uncompressed bytes, so it should shrink by about the raw
fifth. **The update time itself is not measured** — that needs two published releases.

**A denylist, not an allowlist.** An allowlist would have to name every DLL git, curl, ssh, sh,
git-lfs and GCM load, and the PortableGit bump that adds one more ships a git that fails at
first push. A denylist fails the other way: a bump that adds something ships it. Entries that
match nothing are logged (`trim: not present`) rather than thrown, as a prompt to re-read the
list after a bump.

**Kept, and why** (the full list is in the script header): GCM and everything beside it —
MinGit was rejected for lacking it (*PortableGit's helper-selector kept asking*, 2026-08-24);
git-lfs; the DLLs duplicated in `libexec\git-core`, which are deliberate upstream so that
Windows' search order (exe folder, then System32, then PATH) cannot hand git-remote-https a
stray System32 libcrypto; the MSYS runtime, sh, bash, coreutils; ssh; gpg, because
`commit.gpgsign` in a user's global config calls it; `file` + `magic.mgc`, antiword and
pdftotext, astextplain's tools; and every licence file.

**The one thing it cost.** astextplain extracts `.docx` text with `docx2txt.pl` and falls back to
`cat`. Measured with PATH holding only System32: the untrimmed git showed `-Pump one / +Pump
two`, the trimmed one showed the zip's bytes as text. **The first check said both worked** —
this shell's PATH carries a Git for Windows install whose perl answered instead, which is
exactly what a developer box would report. The trim therefore rewrites the eight Word 2007
patterns (`docx docm dotx dotm`, both cases) in the payload's `etc\gitattributes` to `-diff`,
so they read *Binary files differ*, which `UnifiedDiffParser` already handles for every vendor
binary. Keeping perl instead would cost 38.8 MB; if a `.docx` text diff turns out to matter,
that is the trade to revisit. `.odt` was already falling back to `cat` before this — there is
no `odt2txt` in PortableGit — and is untouched.

**Gates.** The pack trims right after the extraction-finished poll (GCM appearing) and before
every check, so `git lfs version`, the new `git credential-manager --version`, a `!` alias
that must print `sh-ok` (the hook path without a repository), and the helper pin all run
against the git that ships. `PackagingManifestTests` pins the order, a denylist that never
names a kept fragment (`credential`, `git-lfs`, `msys-2.0`, `ssh`, `gpg`, `license`, …) and
still names what was measured, and the `-diff` step. Mutation-checked one at a time: GCM
added to the list, `usr\bin\*` added, vim dropped, the call removed, the `-diff` step removed
— each red alone, baseline green. **The second pass found a silent no-op:** entries are
wildcard patterns (`perl5.*.exe`), and the first version joined them onto an unescaped root,
so a checkout under a folder named `[x]` matched nothing — measured, 384.9 → 384.9 MB, every
entry "not present", pack green. The root is now `WildcardPattern.Escape`d, and the pack throws
if the trim removed nothing at all.

**Evidence.** Integration 261/261 and App 874/874 (17 m 53 s, the owner's Versio open beside it)
with the trimmed, manager-pinned git as the **only** git on PATH. A first Integration run
before the pin went 259/261 — the two `CredentialTests` met `helper-selector`, which is the
2026-08-24 finding reproducing, not the trim. From the extracted full package with PATH =
System32 and `GCM_INTERACTIVE=never`: public clone; commit with LFS hooks; push to a bare
repository through the LFS pre-push hook; fetch; ls-remote; a fresh clone that smudged the LFS
object back byte-identical; `credential fill` for github.com answered by GCM from the stored
credential with no window; and ls-remote plus a shallow clone of the private
`fluidstackio/versio-releases`.

**Installed, with the owner's leave** (they closed Versio first): `Setup.exe --silent` over
the 0.4.11 install, `current` 686 → 528 MB. The installed git, PATH = System32,
`GCM_INTERACTIVE=never`: shallow clone and fetch of the private releases repository, and a
push of a throwaway one-commit branch to `fluidstackio/Versio`, read back with ls-remote and
deleted at once. The installed app on `C:\GIT\Versio` logged `git found … current\git\cmd\git.exe`,
`git credential fill ok`, `credential for https://github.com: stored`, and no `ERR` line.
**Not done:** a sign-in from nothing. That means erasing the owner's stored GitHub credential
and completing GCM's browser flow, which is theirs to do; what was proved is that the trimmed
GCM reads, and pushes with, the credential it stored before.

---

### The update card sat on 70% for a minute and a half: Velopack applying a delta in silence

**2026-10-28, the owner's installed 0.4.10 updating to 0.4.11.** The overlay of the entry
below appeared, climbed to 70% and stayed there "a long time or frozen". It wasn't frozen.
The log has the shape:

- `12:36:55` downloading (3,905,057 bytes): the delta;
- `12:36:56` 25% and 50%, 49 ms apart;
- `12:38:27` downloaded in 92.3 s, and the restart.

The bytes took a second. The other 91 s were Velopack rebuilding `Versio-0.4.11-full.nupkg`
(288 MB, the `.partial` growing in `packages/`) from the installed 0.4.10 package plus the
delta, with no progress reported while it does. 0.4.9 → 0.4.10 had the same silent minute
(50% at 10:36:21, done at 10:37:21).

**The fix is the card, not Velopack.** `UpdatesViewModel` now runs a one-second ticker for the
whole download:

- **The card always moves.** An elapsed clock sits beside the percentage.
- **After 3 s with no progress, it says it is applying** (`IsApplying`). The bar empties and
  runs indeterminate, the line reads *Applying the update · 1:12*, and the sentence says this
  can take a minute or two and Versio is still working. With the fill left at 70%, the sweep
  only moved through the empty 30% and still read as stuck.
- **The log says so.** One line when the quiet starts, naming the percentage and the cause,
  then a heartbeat every 15 s, because the log had nothing between *50%* and *downloaded* for
  91 s.

The quiet is inferred rather than tied to a percentage. Where Velopack's download share ends
is its own business; silence is what the user sees.

**Why it is 288 MB, and what is not done here.** Bundled git is 410 MB of the 686 MB install
(PortableGit, kept over MinGit for Git Credential Manager; the 2026-08-24 entry). Every delta
update recompresses that unchanged git into the new full package, which is most of those 91
seconds. ~~Trimming the bundled git is the real cure and is left as its own task.~~ Done the
same day: *PortableGit was two-fifths of every update*, the entry above (384.9 → 244.1 MB;
full package 287.8 → 231.6 MB).

**Evidence.**

- One new App test: a fake reports 50% and then goes quiet. It must turn into *Applying* with
  an empty, indeterminate bar, then reach the restart.
- Removing `SetApplying(true)` from the ticker turns that test red, after its 30 s wait.
- The Try-again test now waits for its restart, because stopping the clock adds an await.
- **Not checked:** whether Velopack honours Cancel while it rebuilds. If it does not, Cancel
  in that phase waits for the rebuild to end.

---

### The history panel named nothing, and its reason was half right

**2026-10-27 → 28, on `HistoryRendering`.** The user: "the commit history just shows raw files
right now, nothing shows in project format, or with the project detection. It needs to render
completely" — like the Explorer and the changed-files list, and showing *what changed in that
commit*. A commit expanded to bare paths (`CommitFileRow`), and a click showed a raw git patch
(`ShowCommitFileAsync`), while every other tree in the app drew object names, type chips,
vendor pills and a Rendered pane with amber / green / struck marks.

**The reason was recorded, and it was right.** `HistoryPanelView.xaml`, GP-J and the entry
below (*A commit's files stay paths, deliberately*): the object index describes the *current*
export, so naming a historical path from it would put today's name on yesterday's file. What
was wrong was the conclusion. The answer was never to name nothing; it was to read
**yesterday's manifest**. The web viewer had been doing exactly that since 2026-10-11
(`RepoModelLoader`: GitHub's tree, `repo.json`, each manifest and catalog at the commit), and
nothing on the desktop called it because it lived in `Versio.Web`.

**What was built**, six commits, each with its mutation check:

- `ProjectSnapshotLoader` in Core (`src/Versio.Core/Projects/ProjectSnapshot.cs`): the web's
  manifest-and-catalog walk over a file list and a reader, with `repo.json`'s parse
  (`RepoConfigJson`) moved out of the Infrastructure store so Core can reach it. The web passes
  its blob fetch; the desktop passes `git show`. The web's behaviour is unchanged (504/504,
  every problem wording the same).
- Two `IGitClient` primitives: `ListTreeAsync` (`ls-tree -r -z --name-only`, the working-tree
  parser's NUL contract) and `ShowAtCommitAsync` (`<commit>:./path`; `ShowAtHeadAsync` is now
  that at `HEAD`, one implementation). A root commit's `^` reads as empty, never a throw.
- The History panel reads a commit as a project on expand (`HistoryViewModel.BuildSnapshotAsync`):
  `ls-tree`, `name-status`, the loader, then `RepoExplorerBuilder` with the commit's changes,
  changed-only — the same builder, the same `TreeRow`, a third copy of the Explorer's row
  template (a shared template is the exact shape of the GP-J regression). The commit rides on
  the row (`TreeRow.Context`), because a row realised three levels down cannot otherwise say
  which commit it belongs to. A task cache so a card click and an expand share one read; a
  failed read is a row that says so and re-arms, where "…" used to stay for the session.
- The Rendered pane at the commit (`ShowCommitNodeAsync`): the one real refactor. Every one of
  the five mark methods read HEAD and the disk *by name*; `PaneSources` (baseline, current, a
  name for the log) now goes through all five and `RenderInputs` (reader, index, what changed)
  through `BuildRenderedContent` and `LinkTypes`. The working tree passes what it always did; a
  commit passes itself and its first parent, and its own index. `StartMarking` is one method
  for both routes. A deleted object is drawn from the parent's copy with every member, line
  and network struck (`StruckOut`) — the web viewer's rule — rather than compared with itself.
- Deleted objects keep their names (`WithDeletedObjectsAsync`): the commit's manifest no longer
  lists them, so the parent's is read for exactly those paths — one extra read per binding with
  a deletion — and they draw as `FB_Motor · POU`, struck, not `App/FB_Motor.st`.
- The commit heading counts by the commit's index, not today's: a block edited in one commit
  and deleted since is "1 POU changed in Machine", not "1 other file".

**Measured**, by the tests that hold the counts: expanding a commit costs `1 ls-tree +
1 name-status + 1 show per config, manifest and catalog [+ 1 parent manifest per binding with a
deletion]` — four spawns on the fixture, five with a deletion, two on a repository with no
`repo.json`; nothing per changed file. A click costs the object's layers at the commit (one or
two), the same at the parent, and the patch.

**Rejected.** `cat-file --batch` for O(1) reads: its size-framed output cannot survive the CLI
runner's line reassembly (the remark on `ShowAtCommitAsync`). The whole project at a commit by
default: the question a commit answers is what it touched; the whole tree is the same snapshot
with `ChangedOnly` off. A loader that also built the tree: the desktop needs the commit's
changes and changed-only, the web the plain tree. Keeping the name `RepoSnapshot`: Core's
`Bindings` already has one (the orphan scanner's), found by the compiler.

**What the mutations proved.** Deleting `History.TreeSelected +=` in the shell turned
`HistoryTreeFlowTests` red; wiring the commit route to the working-tree sources turned both
parent-baseline tests red; counting the heading against `Sync.Objects` again turned the heading
test red. Each was restored, touched and rebuilt before the next run, as the 2026-10-10 lesson
says.

**The web viewer's half** — one section per object on `/commit/<sha>`, an object's history
over every layer — was built in its own session on `claude/inspiring-kowalevski-25da44` (six
commits, the entry below this one) and merged into `HistoryRendering` on 2026-10-28: the code
merged clean, since the two halves share only Core's loader and the web branch forked before
it; the three documents both sides wrote (this file, STATUS, the web ARCHITECTURE) conflicted
and were reconciled by hand, keeping both entries. Web 508 and vitest 216 re-run on the merged
tree. **Left as it is.**
`DeviceTreeFlowTests` went red once in a full App run after the reader seam landed, green alone
and in a 74-test pane-flow run; the message was not captured, and the class is on the watch
list with the catch-up waits.

---

### The commit page drew one section per layer file, and an object's history covered one layer

**2026-10-27, the web viewer, found while bringing commit history to the project format the
Code and Compare views already draw in.** Two gaps with one cause: the commit and history
routes never looked at the project model.

**The commit page.** `GET /commit` called `source.GetCommitAsync` and nothing else, so it had
no `RepoModel.NodeFor` and answered GitHub's raw file list; `CommitPage.tsx` mapped that list to
one section per file. A GVL edited in its native XML was a section headed by `…/Glob_Var.gvl.xml`
with no object name, no type chip, no vendor pill, and the manifest sat between it and the next
object as a third section — while the marked tree in the sidebar, built from the same two
commits, drew the object once with its marks. The tree's pick scrolled to `file-<primary file>`,
and when only the XML had changed no section had that id, so the click did nothing.

**History.** GitHub's list-commits takes one `path`, and `/history` passed the page's path
through, so an object's history was one layer's: the XML-only commit above was absent from the
history of the GVL whose readable file the page had asked about. The fake GitHub ignored `path`
altogether, so no server test could have seen it.

**What was built.** `Repo/TouchedObjects.cs`: `ObjectFor` (the newer-then-older `NodeFor`
expression that `/compare` and `/pulls/{n}/files` each carried a copy of), `Marked` (the `/tree`
base block, extracted), and `Group`, which regroups GitHub's list as one entry per object with
every layer, the touched ones flagged, drawn from the primary file when the commit touched it
and from the first touched layer otherwise, the status read off the marked tree's row. `/commit`
loads the commit and its first parent (the page's own `/tree?base=` loads the same two, cached
by commit) and answers `objects`; a failed load keeps the card and says why in
`objectsUnavailable`. `/history?path=` looks the node up at the ref and lists each of its files,
bounded at four, merged by sha with `touched`. The page: one section per object under
`ChangeSectionHead.tsx`, the header the review's `FileSection` now shares, with a chip per layer;
the commits page names the object and marks each row's touched files; the review's Commits tab
links to the commit page.

**The double pass, twice.** The mutation check for the parent-side fallback in `ObjectFor` was
toothless at first: the test's deleting commit removed the POU's file but left its manifest
entry, so the commit's own index still found the object and the fallback could be deleted green.
An export removes both, and once the test did too, the mutant turned the removed POU into a
nameless file. The same pass found that a removed object is a struck *file* row in the marked
tree (its manifest entry is gone), at the object's own path — so the row index covers every
non-folder row, and a test pins that every object entry's key is a tree row with the same status.

**The look (the fixture preview, `VERSIO_WEB_PREVIEW=1`).** One section per object with chip,
pill and folder; the tree pick scrolled to the section's id and lit it — and the section stayed
on "Drawing the file…", because `near` was seeded from `active` at mount only and the observer
never fires in a pane that does not scroll. An active section is now asked for at once. The
preview's three changed objects were all one-layer, so it gained branch `history`, one commit
that edits only Glob_Var's native XML: one section, the `.xml` chip lit and marked M, the `.st`
chip switching the drawing (both requests in the log), and the object's history page naming
both layers with the XML-only commit touching one. The server log carries one line per request:
`commit … 1 files as 1 objects and 0 plain files`, `history … 2 commits over 2 list(s) of Glob_Var`.

**Rejected.** A `GetCommitAsync` per history row for `touched` without a path (fifty GitHub
calls per page load for a column the commit's page answers; `touched` is empty there, and says
so). A folder path to GitHub's list-commits (pulls every sibling object's commits in). Building
the tree inside `/commit`'s answer (the page already loads it, and the sidebar must agree with the
sections, not duplicate them). Moving `ProjectTreeBuilder.Stronger` out of Core or restating it in
the web (the status is read off the row the tree built, so the two cannot disagree).

---

### The first real update downloaded with nothing on screen; the update overlay

**2026-10-27, night, the owner's own installed 0.4.9.** The first end-to-end update worked, and
looked broken: *Update and restart* was clicked, the dialog closed, and nothing on screen moved
until the window vanished a while later. The download's only voice was `UpdatesViewModel.Status`,
a line under *Check for updates…* in the gear menu, and the menu closes when the item is
clicked, so the progress went into a popup nobody could see. The log had every quarter
(`update 0.4.9: 25% downloaded`…). The screen had none of it. That fits the rule at the top of
AGENTS.md: the code was correct and the user saw a silent failure.

**What replaced it** (the owner approved a mock-up first): `UpdatesViewModel.Stage`, which is
`None`, `WaitingForWork`, `Downloading`, `Restarting` or `Failed`. The window draws it as an
overlay over every row and column (`UpdateOverlay`, ZIndex 100) with:

- a scrim that swallows the mouse;
- the version, a sentence on what happens next, a bar with the percentage, and the download
  size;
- a step strip: Download → Close Versio → Install → Reopen;
- **Cancel** while waiting or downloading, and **Try again** / **Close** after a failure.

It blocks on purpose. Once the user has said update, this Versio is about to close, and a
click on a panel that is about to disappear is a click that will be lost.

**Three things a first draft got wrong, and what was rejected:**

- **Covering the window does not block the keyboard.** Focus stays on whatever panel had it,
  so Enter and Ctrl+O still act behind the scrim. `PreviewKeyDown` now drops keys aimed outside
  the overlay. On the second pass it also drops chords inside it: a Ctrl+O pressed on the
  overlay's own Cancel button would bubble to the window's `KeyBinding`s and open the folder
  picker behind it. That one was found by reading the code, not observed. Tab, Shift+Tab, Enter and Space are what the buttons need. Alt+F4 still passes,
  like the title bar's X, because the close guard owns that path.
- **"5.1 of 12 MB" was rejected.** Velopack reports one percentage across downloading *and*
  applying deltas, so a byte count worked back from it would look measured and not be. The
  card shows the percentage and *about N MB*. The size is the delta chain when there is one,
  otherwise the full package: on a delta update the full package's ~80 MB would be wrong by
  25×.
- **Running work is asked about before the download, not after.** The close guard already asked
  at the restart, but only after a minute of progress bar over an export the user did not know
  would hold the restart. `ShellViewModel.ClearWorkForUpdateAsync` asks the same `CloseApp`
  question first, and tears nothing down: no `StopWatching`, no `Quiesce`, because a cancelled
  download must leave a Versio that still watches its repository. The close guard still runs
  at the restart for anything that started in between (SAFEGUARDS §3.4).

**Evidence.**

- 8 new App tests: 7 in `UpdatesViewModelTests` for the stages, Cancel, Try again, Close, a
  refused restart, and Don't close / Wait over a running import; 1 in
  `UpdateOverlayWindowTests`, which checks the overlay in the real window, because a misspelt
  binding path would fail silently and bring back exactly this defect.
- Four mutations, each red: the stage change not raising `IsOverlayOpen`, the shell not passing
  `ClearWorkForUpdateAsync`, the overlay's `Visibility` unbound, and no `Downloading` stage.
- The keyboard half needs a presented window, so `PackagingManifestTests` pins it by source
  text.
- The card was rendered headless to PNG in the downloading and failed states and looked at.
  The failure toast posted alongside is hit-tested *under* the scrim, which keeps it as the
  record left behind after Close.
- **The full App suite caught one regression**, and it was the overlay's (865/867, 13 m 34 s).
  `ProjectPanelCalmStateTests` hit-tests the empty state's *Open a repository* button, and the
  click landed on `UpdateOverlay`, which reported itself `Collapsed` at the time. With
  `Visibility` bound straight to `IsOverlayOpen`, the scrim starts at the default Visible and
  the binding settles after the first layout, so a full-window Grid was arranged in that gap.
  The test is green on the base commit. Fixed by starting the overlay `Collapsed` in a Style
  and showing it from a `DataTrigger`, so its state is right from construction; the
  misspelt-path mutation still turns the window test red. The other red,
  `CommitWhileIdeHoldsProjectTests`, was green alone, as it was before this change.
- **Not seen yet:** the overlay during a real update on an installed copy, the dark theme, and
  the restarting state live. That needs a 0.5.x published over an installed 0.4.9.

---

### A failure over a project that matches HEAD came back at every launch, and a click was never the fix

**2026-10-27, 18:16, the same two rows after the entry below shipped.** The owner rebuilt
(0.4.7), reinstalled, relaunched — and `CleaningStation_V20_V21.ap21` and
`TC3_PlcSample_reStrText.tsproj` were back on "failed" with the same TwinCAT and Openness
refusals. The log shows why: `161b94dc224c: ReadoptedAfterRestart — previous session (pid
41980) is gone and the work is still unsynced; re-adopting the Failed lock` and the same for
`64eb778c77ff`, then `commit gate: Blocked — 2 projects have a failed sync`. Nobody had
pressed Try again, and the owner's objection was that nobody should have to: *there are no
changes on these projects; they should not be detected and should not be processed.* The
entry below fixed the button and left the launch alone, and the launch is where the rows
come from.

**The fix moves into the launch path.** `AdoptCommittedStateAsync` — the fresh-clone
adoption, which runs between recovery and the catch-up and asks git whether an unrecorded
project matches HEAD — now acts on a **Failed** lock with no export recorded, not only a Clean
one. A failed project that git says is untouched (status clean under the project, its payload
and its mirror, read twice around the hashing, every listed file on disk) has its failure
dropped and is taken as exported: direction Clean, baseline and `LastSynced` set to the
hashes, the breaker and coalescer reset as an acknowledgement would, and one Info line that
carries the reason the failure had held — `the recorded failure (export failed — TcXaeShell…)
had nothing to do: the project and its mirror match HEAD and no export was ever recorded here,
so the failure is dropped`. A failed project that git says **did** change is not adopted and
stays failed with its reason, which is what the owner asked for in the same breath. The
recovery line at Warning still appears once, at the launch that drops it; the next launch
reads `NothingToRecover`.

**On "an abandoned lock is never auto-cleared".** The entry below rejected a relaunch-time
release on that principle, and the principle stands: it guards *unsynced work*. What was
rejected was a release decided inside the recovery policy, which has no git and, with a blank
mirror baseline, could not tell a mirror edit from nothing. The adoption path can: it does not
reason about baselines at all, it asks git, twice, and checks the disk. A failure is dropped
only on git's word that the work it guarded does not exist — and a record of a run that should
never have happened is not a lock guarding anything. The human-in-the-loop guard is kept for
the case it exists for: a failure over a real change.

**One test had modelled exactly the dropped state.**
`A_failed_project_does_not_hide_another_projects_pending_export` wrote a Failed lock with
empty hashes over a project identical to HEAD, as the thing that must stay on the panel; with
the fix it was adopted, the gate exported B, and the assertion read `rows: a=never exported,
b=never exported`. The test is about a failure that is still true, so A now carries a change
too.

**Tests.** `SyncOrchestratorTests`: the one adoption test that asserted "a failure is never
overwritten" is now four — a recorded export is never overwritten, a failure over an untouched
project is dropped and the project adopted (no run, Clean on disk, gate Proceed, the log line
with the reason), a failure over a changed project or mirror stays failed (theory, two rows), a
held direction is never overwritten. `ProjectPanelCalmStateTests`: a first shell fails the
export, the project is committed outside the app, a second shell on the same repository opens
with the row not failed, no second export, and the log line. Mutation: the candidate filter
back to `!= Clean` turns the new orchestrator test red and nothing else.

---

### "Try again" re-ran the toolchain that had just refused, so a failed export of an untouched project was permanent

**2026-10-27, the same screenshot as the entry below, after the scanner fix.** Two rows
left: `CleaningStation_V20_V21.ap21` and `TC3_PlcSample_reStrText.tsproj`, both "failed", the
banner "2 projects failed to sync". The owner's objection was exact: *nothing has changed in
these files, so there is nothing to do to them; they should fail only when I change one and the
machine cannot process it.* This machine cannot run either toolchain — the account is not in
the `Siemens TIA Openness` group, and TwinCAT XAE is not installed — and both projects were
identical to HEAD.

**Where the failures came from.** `.versio/local/state.json` holds both locks with
`AcquiredUtc` 2026-10-17 00:50 and 00:51, `Direction: Failed`, the toolchain's refusal as the
`Reason`, and an **empty** `Baseline.ProjectHash` and `LastSynced.ProjectHash`. That is the
first open of this clone on this machine, the one FINDINGS 2026-10-16 describes: no record of
an export, so the catch-up exported every project, and three refused. The fresh-clone adoption
(`AdoptCommittedStateAsync`, built on 2026-10-17 in answer to exactly that) takes an untouched
project as exported instead — but it is narrow on purpose, and acts only on a Clean lock. These
two were already Failed when it arrived.

**Why nothing could clear them.** Three exits, all shut:

- *Recovery at launch* (`LockRecoveryPolicy`) releases a lock only when the current hashes
  equal its baseline. Nothing hashes to the empty string, so every launch logged
  `ReadoptedAfterRestart — the machine rebooted and the work is still unsynced` for work that
  never existed. Ten days of launches, 10-17 to 10-27, each one re-adopting.
- *Adoption* skips a Failed lock by design ("a lock, a failure, or a real record is left
  exactly as it is").
- *Try again* (`RetryAsync`) acknowledged the failure — which, by design, clears the record of
  what was exported "so the next event exports rather than trusting anything" — and went
  straight to `SatisfyGateAsync`, which exported, which the toolchain refused, which set
  Failed again with the same reason. The button's summary said "Still failing".

**The fix is in the button, not the lock.** After acknowledging, the retry asks git before it
asks the toolchain: `AdoptCommittedStateAsync` — the view model's launch-time call, with its
double status read and its on-disk checks — runs between the acknowledgement and the gate. A
project and mirror that both match HEAD are taken as exported and the gate finds nothing
behind; a project that differs is not adopted and exports as before, failing again with its
reason if it must. One click per project clears the owner's two rows for good, because the
adoption records `LastSynced`, which survives relaunches.

Two App tests in `ProjectPanelCalmStateTests`, through a real repository and the capturing
toolchain set to refuse: Try again on a project that matches HEAD runs no second export and
leaves no failure; Try again on a project that still differs runs the export and reports
"Still failing". Removing the one `await` turns the first red and leaves the second green.

**What was built first and thrown away.** A recovery-policy rule — a Failed lock with no
project baseline, whose mirror is where the failure left it, released at launch as "never
exported here" — with a new `LockRecovery` outcome and seven tests, all green. Rejected
before commit, for the reason the policy's own doc gives: *an abandoned lock is never
auto-cleared*, and a failure vanishing at relaunch without a human acknowledging it is that,
however empty its baseline. It also could not tell a text edit made while Failed from
nothing at all when the mirror baseline was blank too (the first-failure shape, which the
orchestrator harness produces), and releasing that lock would have let the catch-up export
over the edit. The button already has the human in the loop; it only needed to ask the
right question first.

**Still true, and worth knowing on this machine.** A changed TIA or TwinCAT project will fail
here, and should: the row says why, in the toolchain's words. Only an untouched one now
stays quiet.

---

### The repository scanner walked into nested checkouts and tracked another repository's projects

**2026-10-27, from the app showing twelve projects that do not exist in this repository.**
The owner opened `C:\GIT\Versio` and the project panel listed every fixture three times over,
each extra copy "not exported", six things needing attention and a ManifestMismatch naming
`.claude/worktrees/modest-bohr-35127e/fixtures/projects/MachineWithMembers`. Today's log says
what happened, in order:

```
15:56:51.283 DBG [Fs]   found project .claude/worktrees/modest-bohr-35127e/fixtures/projects/MachineWithMembers.project
                        ... (twelve such lines, four fixtures × three worktrees)
15:56:51.896 INF [Sync] tracking .claude/worktrees/modest-bohr-35127e/fixtures/projects/MachineWithMembers.project automatically as fcde73c41007
15:56:51.986 INF [Git]  added 2 rule(s) for the toolchain's scratch files: .claude/worktrees/modest-bohr-35127e/...
```

`.claude/worktrees/` holds three Claude Code worktrees — `git worktree add` checkouts of
this repository, each a full tree with the fixtures and a `.versio/repo.json` of its own.
`RepoScanner` walked into them because its skip list names folders *called* `.git`, and a
linked worktree's `.git` is not a folder at all but a one-line file (`gitdir: C:/GIT/Versio/
.git/worktrees/modest-bohr-35127e`). So the walk found twelve project files, the orphan
scanner adopted every one under a fresh binding id, and every export tree among them —
carrying the id the *real* repository had given that fixture — read as a ManifestMismatch
against the id it had just been handed. The commit made through the app one minute later
("lets go", `b1d5891`) then wrote all of it into git: 12 bindings into `repo.json`, 129 lines
into `.gitignore`, 84 into `.gitattributes`.

**Why the worktrees had not been noticed.** `.git/info/exclude` lists `.claude/worktrees/`, so
git status never showed them and the status bar said "No changes". The exclude is the right
tool for git; the scanner reads the disk, not git, and had no equivalent rule.

**The fix is a rule about children, not a name.** The walk now stops at any child directory
that holds a `.git` entry — `Path.Exists`, not `Directory.Exists`, so a worktree's file counts
— and says so in the log at Debug, because a project that is not offered for tracking must be
findable there. The root is exempt: it always has a `.git` of its own, and applying the rule
there would find nothing anywhere. Three integration tests in `RepoScannerNestedCheckoutTests`:
a worktree (`.git` file) is not walked into, a clone (`.git` folder) is not walked into, and
the root's own `.git`, as a file or a folder, does not stop the scan. The first two go red
with the one `if` removed; the third passes either way, which is the point of it.

**What was rejected.** Honouring git's own exclude and ignore files from the scanner — the
scanner has no git client by design (it runs on a background thread without one in every
unit test), and ignoring a path is a weaker statement than "this is another repository".
Adding `.claude` to the skip list — a name, which is exactly the shape of rule that failed.

**The cleanup.** `.gitignore`, `.gitattributes` and `.versio/repo.json` were restored to the
parent of `b1d5891` (`git checkout b1d5891~1 -- …`, 312 deletions, nothing else in those
files had changed in that commit) and committed on their own. The three worktrees are left in
place; with the scanner fixed they are invisible to the app, as they already were to git.

**Not this.** The "2 projects failed to sync" in the same screenshot are the TIA and TwinCAT
fixtures, whose Failed locks were re-adopted after a reboot; on this machine TwinCAT XAE is not
installed and the account is not in the Openness group, so neither can export. Acknowledging
them clears the commit gate. Separate state, correctly reported.

---

### Auto-update: the stored git sign-in reads the private releases repository, so users set no token

**2026-10-27, measured before building the update check (STATUS item 5, open question 4).**
The plan was GitHub Releases in a private `fluidstackio/versio-releases`, read by Velopack's
`GithubSource` — which needs a token, because GitHub answers a private repository it cannot see
with 404. The fallback was a fine-grained token per user, pasted into Settings and stored with
DPAPI. Before building that, the token Git Credential Manager already stores for github.com was
tried against the API, with `GCM_INTERACTIVE=never` so nothing could prompt:

```
credential: username=Jhonatan-fluid, token length 40, prefix gho_
GET /user                                     -> 200  X-OAuth-Scopes: gist, read:org, repo, workflow
GET /repos/fluidstackio/versio-releases       -> 200  (private: true)
GET /repos/fluidstackio/versio-releases/releases -> 200
GET /repos/fluidstackio/Versio                -> 200
```

**So the token path was not built.** GCM's OAuth token carries `repo` and is already authorized
for the organisation's SSO; the update check reads it (`StoredGitCredential`) and hands it to
Velopack for one check at a time. It is the one place the application takes a secret back from
git, and its remarks say so and why.

**What this did not measure:** a user with only Read on the releases repository (this account
is an admin of it), and downloading an asset (the repository had no release yet). Both are the
first thing to check with a second account; if either fails, the token fallback is the plan's
Phase 5 and nothing else changes — `VelopackUpdateSource` takes the token as a delegate.

**Three things the publish script learned on the way, each now a comment where it bit:**

- **Windows PowerShell 5.1 corrupts a string piped into git.** Piping the request string into
  ``git credential fill`` answered *refusing to work with credential missing protocol field*. The
  script writes stdin through a `Process` with a BOM-less UTF-8 writer instead.
- **Windows PowerShell 5.1's `Invoke-RestMethod` emits a JSON array as one object.** `@(...)`
  around the call counted the empty releases page as one release. `Get-Releases` enumerates it
  and returns with the unary comma.
- **vpk accepts a response file.** `--token` is its only way in, and a command line is written
  to `publish.log` and visible to every process while vpk runs. `@file` was confirmed by passing
  a bogus token that way: vpk answered 401 from GitHub rather than rejecting the argument, so the
  file was read and the token used. The file is deleted in a `finally`.

**The first real run failed, and published nothing (same day, later).** After the notes
were written, the publish stopped on *The property 'Elapsed' cannot be found on this
object*: `Invoke-LoggedProcess` animates the current phase while it waits, and the
Fetch-Deps call was the one place it ran before any phase had started, so the spinner read
a timer that did not exist and strict mode ended the script. Nothing had been built,
bumped or uploaded. Fetch-Deps is now a phase of its own, and `Write-Tick` draws nothing
when no phase is running - checked by running a logged process outside a phase and inside
one. No test covers the scripts' runtime: the Arch tests read them as text.

**The same run showed what Notepad does to the notes.** Windows 11 Notepad's Markdown mode
saved the prefilled `- ` items as `* ` and escaped every underscore, so the prompt would
have read `Publish\_Release.bat`. `UpdatePolicy.NotesForPrompt` now takes the backslash
escapes off and draws every bullet as `- `, with that text as its test.

**And one design point, recorded because the obvious version is wrong:** the publish packs into
`artifacts\publish-releases`, holding only the previous *published* package, not into
`artifacts\releases`. vpk builds the delta against the newest package in its output folder, and
`artifacts\releases` keeps every local pack — a delta against one that never shipped would not
apply on any user's machine.

---

### The review's seventh round: three more, one of them a restore that adopted what it put back

**2026-10-25, the reviewer read `749e3a1` and sent three confirmed items — one P1, two P2.**
Same method as every round before: every claim read against the code, a failing test first,
one commit per idea, the fix mutated away and the test watched go red, then restored. **All
three are real and all three are fixed**; none is refuted and none is deferred. No python is
touched, so the CODESYS gate owes nothing new. Item 2 came in two commits because it was two
ideas — the engine surviving a throw from its store, and the store not throwing — and its test
found a third thing on the way (a transient access-denied on the final replace) that the old
code had been exposed to all along.

**The P1:**

- **1 — restoring an auto-stash adopted the restored edit as synced.** Switching branches
  stashed an unconfirmed mirror edit; restoring it on the way back popped the stash and then
  adopted the tree, as a checkout or a pull is adopted. The restored edit was thereby recorded
  as synced: the banner was gone, the gate beside the direction lock (the second round's item
  1) compared the mirror against a record that now matched it, and the next project save
  exported over the one copy of the edit anywhere. The restore no longer adopts — the tree
  before the pop is HEAD, which the arrival already adopted, and what the pop adds is work in
  flight, judged by the hashes as it was before the stash — and it asks the orchestrator,
  binding by binding, whether the exported text holds an edit nobody has imported
  (`HasUnimportedMirrorEditAsync`, the gate's own question made callable) and raises the import
  for each that does, since the pop ran under the watcher hold and no text event would have.
  One App test against real git over the whole arc, red before with the banner gone after the
  restore; the first version of the test failed for another reason — the rules the shell
  writes on open left the tree dirty and the pop stopped short — and the fixture commits them
  first, which is worth knowing for the next test of this shape. Commit `49d73b2`.

**The P2s:**

- **2 — two bindings persisting at once left automatic sync stuck.** Two causes. The
  coalescer's slot is completed by the caller on the outcome it is handed, so a throw from
  inside the run left the slot held for good and every later change answered Coalesced until a
  restart — the fourth round's item 5 was the same shape for a cancelled read, closed for that
  path only. The attempt now catches whatever the claim-and-run throws, resets the slot,
  records the failure so Try again lights, and answers Failed with the exception's words; the
  existing test that pinned the throw escaping pins the Failed answer now, and says why the
  contract moved (`a4e8d52`). And the throw itself: both bindings wrote `state.json.tmp`, and
  the second open was a sharing violation. `JsonFile` serialises writers of one file within the
  process and gives each a temp name of its own, so another process cannot collide either. The
  test for that found more: under a loaded suite the final replace failed one run in three with
  "access to the path is denied" — a file just written is briefly open elsewhere on Windows,
  the scanner or the indexer — so the replace is tried six times with short waits before it is
  allowed to fail, the remedy `WindowsFileSystem` already uses for its copies; the old code had
  that exposure too, and one such miss was a stuck binding (`c2b4418`). A Core test with a
  store that refuses one write and an Integration test writing thirty-two states at once, ten
  rounds, on a real disk; each red before with the sharing violation.
- **3 — moving a mirror subfolder out triggered no sync.** Two causes again, one per tier: the
  tree watcher asked for no `DirectoryName` events, and Windows reports a folder renamed, moved
  or deleted as one event about the folder and none for the files under it; and the safety
  poll skipped a tracked file that no longer existed, so the fallback tier never reported the
  disappearance either. The watcher now listens for directory events and turns each into the
  tracked files it carried (a deleted or moved-out folder: its files; a renamed folder: its
  old files and everything now under the new name; a folder moved in: its files), the root
  sentinel re-checks the files under an old root the same way, and the poll reports a tracked
  file that is gone once, at Warn, and then forgets it. A single-file watcher enqueues a
  rename's old path only when it is tracked, because the IDE's heartbeat renames a temp onto
  the project file and an unconditional version raised a change per heartbeat for a path nobody
  tracked — found on the second-pass read and pinned. Four Integration tests on a real
  directory and a real watcher; three red at their timeouts with an empty watcher log, and six
  mutants each turned a different one red. The tests name the tracked file deliberately:
  Windows owes only the one folder event, and deriving the files from it is the thing under
  test. Commit `01523ff`.

**The runs.** Over the landed tree, `01523ff`: Core 2,080 (nothing skipped), Integration 254, Arch 36, CODESYS pure 405, parity 104, TIA pure 108, TwinCAT pure 27, Web 504, vitest 211; App 846 — 843/846 in 10 m 57 s, the three red all catch-up waits (`CommitWhileIdeHoldsProjectTests`, `SourceProjectTests` — both named before — and `StagingSelectionFlowTests.The_master_checkbox_ticks_and_clears_the_list`, a new name), all three green alone and in their classes (10/10 in 18 s). Three in one run is more than the one-a-run of the earlier rounds, so the watch item below widens to the suite's waits under load rather than one test. 4,430 across the twelve projects; the toolchain-gated counts (TIA 5, TwinCAT 6, CODESYS 55) are from their last gate runs.

**Still not done**, carried: branch deletion addresses the hardcoded "origin" (the first round's
item 39); the merge twin of the sixth round's item 4 (finishing a merge after "Keep mine" says
"Nothing to commit"); the sixth round's other notes. Named this round and left alone: a
directory's own `Changed` event still reaches the engine as a path that is not a file, which
the hash gate refuses harmlessly; the watcher logs "safety poll every 0s" for a sub-second
interval; and the App suite's catch-up waits under load — three different tests red in one
full run tonight, each green alone — which widens the fourth round's `SourceProjectTests` watch
item from one test to the suite's waits, and has not been looked into.

---

### The review's sixth round: five more, three of them the fifth round's fixes one case short

**2026-10-25, the reviewer read `2d2937e` and sent five confirmed items — two P1, three P2 —
three of them the fifth round's fixes seen from one step further and two new.** Same method as
every round before: every claim read against the code, a failing test first, one commit per
item, the fix mutated away and the test watched go red, then restored. **All five are real and
all five are fixed**; none is refuted and none is deferred. No python is touched, so the
CODESYS gate owes nothing new. The three follow-ups share one shape: each fifth-round fix drew
its boundary where the reported case was — the *tracked* neighbour, the *two*-PLC project, the
*same-suffix* rename — and the reviewer stepped one case past it. The lesson is the one written
under the third round, read again: a refusal protects exactly what it is written against, and
"the common case" is not "the only case".

**The P1s:**

- **1 — adoption still let an export delete an untracked project.** The fifth round refused an
  export root nested in another *binding's*, and on a fresh scan there are no bindings yet:
  `A.project` was adopted first, `A/B.project` was then refused for sitting inside `A`'s export
  root, and `A`'s export deleted it all the same — the refusal protected the inner project from
  being tracked, not from being overwritten. `Adoption.Plan` now takes every project the scan
  found, tracked or not (source-project folders included), and refuses a root that would contain
  any of them, whichever is adopted first; the inner project adopts on its own, and the outer
  one waits for the inner to be moved — the outer, because the outer is the one whose export
  destroys. `RepoSetup.AdoptAsync` hands the caller's scan through and scans for itself when a
  caller has none, so no caller can leave the question unasked; the App's auto-adoption passes
  its snapshot. A four-row Core theory and an Integration test on a real directory with no scan
  handed over; the check neutralised turned two rows and the Integration test red. Commit
  `9bc72ae`.
- **2 — the TIA host still wrote into the wrong PLC when one was left.** The fifth round's
  refusal kept a one-PLC fallback on the reasoning that with a single PLC there is nowhere else
  a file could belong. One PLC is also what a project looks like after its *other* PLC was
  removed in Portal, and an old import for the removed Device2 then wrote Device2's blocks into
  Device1 and saved. `MatchPlc` has no fallback now: a path that names no live PLC is a miss
  however many PLCs remain, and the pre-flight refuses the plan; a device renamed in a one-PLC
  project costs a re-export, which the fatal already names. The test that pinned the fallback
  is rewritten to the reviewer's case and was red with the write landing in the survivor. The
  real TIA gate still owes the refusal on a live Portal. Commit `7cc4309`.

**The P2s:**

- **3 — some renamed files still hid their removed content.** The old path reached the
  renderer after the fifth round, and the renderer then paired every layer — the one the caller
  had just named included — by everything after the file name's first dot, so `old.txt` →
  `new.md` and `Old.Name.st` → `New.st` found no counterpart and drew additions without
  removals. The pair the caller named now decides its own layer outright; the object's other
  layers follow the pair by the stems it reveals (`GVL_Alarm.gvl.xml` follows `GVL_Alarm.gvl.st`
  → `GVL.Alarm2.gvl.st`), and only a sibling the pair cannot place falls back to the suffix. The
  old remark said the pairing was "after the object's name" while the code paired after the
  first dot — the reviewer's example is exactly that gap. A two-case Web theory red with the
  reviewer's numbers and a two-layer rename test; disabling the stem pairing alone turned
  exactly the latter red. Commit `c8324b1`.
- **4 — a conflicted rebase could not be continued through the app.** The banner said
  "Continue the rebase" and the button ran a plain `git commit`: a detached commit, the rebase
  still paused, the branch unchanged, later commits unreplayed. `IGitClient.ContinueRebaseAsync`
  runs `rebase --continue` with `GIT_EDITOR=:` (the environment variable, because git reads it
  ahead of all config) so each replayed commit keeps its own message; while a rebase is paused
  the commit button's plan is `CommitPlan.ContinuingRebase` — captioned "Continue rebase", no
  message needed, the message box left as typed, refused while conflicts remain — and a rebase
  that stops at the next commit's conflict is shown as such rather than reported finished.
  Three App tests against a real diverged upstream with two local commits (red before: caption
  "Commit", button never live), two Integration tests, three Core tests on the plan. One mutant
  survived at first: a shell exporting `GIT_EDITOR` made the editor-suppression test vacuous,
  so the fixture now sets a refusing editor itself and asserts the raw command fails before the
  client succeeds. Commit `bc2e5d0`.
- **5 — `repo.json` with null collections crashed past validation.** `{"bindings":null}`,
  `{"bindings":[null]}` and `{"ignore":null}` deserialised and then threw
  `NullReferenceException` wherever the lists were read. One rule, in `RepoConfigStore.Parse`,
  which both the desktop store and the web viewer go through: a null where the model has a
  default reads as the default and is reported (a Warn per repair on the desktop, a problem on
  the web page); a null where the model requires a value — a null binding, a null id or path, a
  null ignore glob, a file holding `null` — refuses the whole file, as System.Text.Json already
  does when the same path is *absent*. Whole-file rather than drop-the-element, because a
  dropped binding is a project no longer protected while the panel looks healthy. Ten
  Integration tests and two Web tests, eight and two red before. Commit `749e3a1`.

**The runs.** Over the landed tree, `bc2e5d0`: Core 2,079 (nothing skipped), Integration 249, Arch 36, CODESYS pure 405, parity 104, TIA pure 108, TwinCAT pure 27, Web 504, vitest 211; App 845 — 845/845 in 12 m 15 s, nothing else of this session running, the clipboard tests green again. 4,423 across the twelve projects; the toolchain-gated counts (TIA 5, TwinCAT 6, CODESYS 55) are from their last gate runs, and none of this round's commits reaches them.

**Still not done**, carried from the first round: branch deletion addresses the hardcoded
"origin" after item 39. Named this round and left alone: **finishing a merge after "Keep mine"
on a single-file conflict** leaves the index equal to HEAD, so the commit plan says "Nothing to
commit" while the banner says "Commit to finish the merge" — the merge counterpart of item 4,
found by its agent, unfixed; a rebase conflict resolved wholly in the upstream's favour makes
git drop the emptied commit and the panel reports "finished" one commit short with nothing
naming it; the branch compare's file list still detects no renames and the pull page's stale
rename notice still stands (from the fifth round); the `SourceProjectTests` watch item from the
fourth round.

---

### The review's fifth round: six more, one of them a hole the third round's own fix opened

**2026-10-25, the reviewer read `f30af09` and sent six confirmed items — three P1, three P2.**
Same method as the four rounds before: every claim read against the code, a failing test
first, one commit per item, the fix mutated away and the test watched go red, then restored.
**All six are real and all six are fixed**; none is refuted and none is deferred. The one that
matters most for this log is item 1: the third round's item 4 taught the delete check to accept
the hash an older export recorded, and in accepting the oldest normalisation it accepted a lossy
one — a fix for "refuses everything" that opened "permits a deletion it should not". The shape
is the second round's: a fix that closes the reported case and opens the one beside it, and
the test that would have caught it is the one the first round wrote for item 20 (two values
swapped between two children), run against the compatibility path rather than the current one.

**The P1s:**

- **1 — the legacy-hash fallback let a modified native object be deleted.** The oldest
  normalisation sorted a container's LINES; swap two values between two multi-line children
  and the bag of lines is the same, so the old hash is reproduced by a modified object. The
  check took the match as proof, removed the object and wrote `olderHash: true` on the log.
  `form_can_vouch` now decides per object: the current form and the 2026-10-24 chunker are
  permutation-insensitive and nothing else (a sibling keeps its own lines in order; the old
  placeholder only ever made the chunker stricter), so a match under either proves the object
  unchanged. The line sort vouches only where it is lossless for the payload at hand — every
  unordered container holding single-line siblings and nothing else, where the bag of lines
  IS the set of siblings — and anywhere else a match under it is `DELETE_UNVERIFIABLE`: the
  object waits for the next export, which records a hash that can tell, and the warning says
  so. Measured against the fixtures: of eighteen native records, eleven flat property and
  signal files are still vouched for through the chunker form; the seven visualisations and
  text lists, line-sort only, now wait for a re-export rather than being deletable — the
  honest answer for them. One parity test runs the reviewer's exploit (unverifiable, not
  deleted; red before as ok with `olderHash`) beside the lossless shape (single-line
  GUID-bearing siblings: unchanged and reordered vouched for, an edited value refused); the
  round-three tests now expect the wait for the lossy records. Under real CODESYS, the seven
  tests of the earlier re-runs on `ef3b469` with this python embedded — the four from the
  gate session's list and the three that drive the delete check — 7/7 in 26 m 33 s, the
  owner's IDEs open. **Rejected:** comparing the live object against the
  mirror's last export under the new normalisation — that file is the one that was deleted,
  and the python has no git to find it in. Commit `ef3b469`.
- **2 — nested export roots were accepted, and the outer export deleted the inner project.**
  The collision check compared export directories for equality, so `A.project` (exporting
  to `A`) and `A/B.project` (exporting to `A/B`) were both tracked; an export replaces its
  whole directory, so the next export of `A` deleted `A/B.project` and its mirror together.
  Adoption now refuses, at the one gate every adoption passes, an export root inside or
  containing another binding's, an export root that would contain another binding's project
  file, and a project file inside another binding's export root, each with a sentence naming
  what the next export would delete; a directory boundary, not a prefix, so `plc/AB` is not
  under `plc/A`. Three Core theory rows red before and a sibling-prefix control. Commit
  `8d1af02`.
- **3 — the TIA host wrote a plan's payload into the first PLC when its target was gone.**
  `MatchPlc(...) ?? plcs[0]` substituted another PLC when a device had been renamed in Portal
  since the export, so Device2's blocks were generated into Device1 and saved. The host had
  no test seam, so one commit compiles its sources into the pure test project (it is
  reflection-only against Openness, so they compile unchanged, as the TwinCAT host's text
  rules already do) and the next refuses the whole plan before any write when any file
  targets a PLC that is not there — a fatal, not a warning, because a warning becomes
  "succeeded, with notes" and the lock moves to ImportSucceeded with the mirror's edit
  unapplied. `MatchPlc` also compares against the sanitised names the export wrote (a device
  `Line:2` is the directory `Line_2`), a miss the fallback had hidden; the one-PLC rule is
  kept and pinned. Six pure tests with fakes built from the host's own reads, three red
  before; the real TIA gate still owes the refusal on a live two-PLC Portal, on a machine in
  the Openness group. DRIVING.md's §4.3 has the row. Commits `9c2bb8b`, `432d73e`.

**The P2s:**

- **4 — a selected-file first commit failed on "could not resolve HEAD".** Unstaging the
  unticked files used `restore --staged`, which restores from HEAD, and the first commit of a
  repository has none. Before the first commit the index is a diff against the empty tree, so
  that is what is restored from — `--source=<empty tree>`, the id asked of git rather than
  hard-coded because it differs under SHA-256 — and a repository with a HEAD runs exactly
  what it ran before. `git rm --cached` was rejected: it refuses a path edited after staging
  unless forced. Two Integration tests and one App test against real git, red before with the
  reviewer's message; `TempRepo` can now be born unborn. Commit `efc58f2`.
- **5 — the commit page drew a renamed-and-edited file as wholly added.** The page had the
  previous path and no way to send it, and the server's compare view took none, so a rename
  with one edited line read as two additions and no removal; the pull-request view already did
  it right. Both ends now carry `from`, refused with a 404 on a `..` segment as the pull route
  does. One Web test over the existing renamed fixture (red with the reviewer's numbers, 2 and
  0) and one vitest asserting the request. Noted, not fixed: the pull page shows a stale "does
  not yet compare against the old path" notice while doing exactly that, and the branch
  compare's file list detects no renames at all. Commit `fbc8c60`.
- **6 — one malformed `_manifest.json` aborted repository discovery.** `TryGetProperty` on a
  non-object root and `GetString()` on a number both throw an exception the catch was never
  written for, so a manifest holding `null`, `[]` or a numeric id took every other project in
  the repository down with it. The kinds are checked first: a wrong-shaped manifest costs
  exactly what a corrupt one does — its own tree reads as unidentified, named at Warn with
  what it held — and an absent or null id stays quiet as before. Two Integration tests, one red
  before with the reviewer's exception. Widening the catch was rejected: it would report the
  framework's words instead of the file's. Commit `cb045e7`.

**The runs.** Over the landed tree, `efc58f2`: Core 2,072 (nothing skipped), Integration 236, Arch 36, CODESYS pure 405, parity 104, TIA pure 108, TwinCAT pure 27, Web 499, vitest 211; App 842 — 838/842 in 9 m 3 s with the CODESYS run beside it, the four red all `OpenClipboard Failed (CLIPBRD_E_CANT_OPEN)` (three `HistoryCopyTests`, one `LogPanelInteractionTests`), red alone afterwards too and, per the item-4 agent, red on the untouched `5b0e63b` in the same shell — the clipboard flake this file already names, held this evening by another program on the desktop; green in this morning's full runs. 4,395 across the twelve projects; the toolchain-gated counts (TIA 5, TwinCAT 6, CODESYS 55) are from their last gate runs.

**Still not done**, carried from the first round: branch deletion addresses the hardcoded
"origin" after item 39. Named this round and left alone: the pull page's stale rename notice
and the branch compare's missing rename detection (5); `HeadCommitAsync` logs a warning per
call on an unborn branch (4); the TIA host saves even when every file was a missing-file
warning, and its `resolve` argument is unused (3); the `SourceProjectTests` watch item from the
fourth round stands.

---

### The review's fourth round: seven more, two of them ways to lose a project

**2026-10-25, the reviewer read `9d9a0dd` again and sent seven confirmed items — two P1, five
P2 — in files the third round had not touched.** Same method as the rounds before: every claim
read against the code, a failing test first, one commit per item, the fix mutated away and the
test watched go red, then restored. **All seven are real and all seven are fixed**; none is
refuted and none is deferred. None reaches the embedded python, so the CODESYS gate owes
nothing new. The two P1s share a shape with the second round's item 1: a safety mechanism that
was right for the case it was built around and silently wrong one case over — a backup is a
copy, and a copy can be half there; a merge that reaches a tracked project is protected, and
"merge upstream" is a merge.

**The P1s:**

- **1 — a backup whose copy stopped partway restored as if it were whole.** A directory
  project's backup is a tree copy, and a tree copy can stop halfway — a locked file, a full
  disk, a killed process. The half that landed was a directory in the backup folder like any
  other, so the catalog listed it and the restore put it back like any other; and a restore
  deletes what the backup does not have (the first round's item 21), so restoring half a tree
  deleted the other half of the live project and reported success — the reviewer watched a
  live program file vanish. A marker now sits beside the copy from before its first byte until
  after its last (`BackupCatalog.PartialMarkerSuffix`): while it stands the copy is not listed,
  a restore asked for it by path is refused by name, and the next completed copy's prune
  removes copy and marker together, safe there because the prune runs from the writer that has
  just taken its own marker down and a binding has one writer at a time. Old backups carry no
  marker and are listed as before. One Core test walks the whole arc — the import fails and
  the project is untouched, the half-copy is on disk and unlisted, the restore is refused and
  the program survives, the next import sweeps it away — red before at the listing; a
  `FailCopyAt` on the fake filesystem, because `FailCopyTimes` fails the first copies and
  leaves nothing behind. **Rejected:** copy under a temporary name and rename on completion —
  the filesystem seam has no rename, and the marker keeps the directory the only record, which
  `BackupCatalog` insists on. Commit `40f2bd8`.
- **2 — "merge upstream" recorded mismatched PLC content as synchronised.** A named merge asks
  what it would bring and, when that reaches a tracked project, does not adopt the result: the
  binary project keeps one side while the mirror keeps both, so the mirror is put in charge and
  an import is raised (`MergePreflight`'s measurement, P-M1). The upstream merge ran the same
  merge through `WhileGitRewritesTheTreeAsync` with the adopting default, so the disagreement
  was recorded as synced, the engine answered NoChange, and the next project save exported over
  the logic the upstream had brought. `ReconcileAsync` now asks the upstream what it brings
  (three dots against `@{u}`), refuses when git cannot say, asks the same import question
  before the tree is rewritten, and runs through `RewriteTreeUnderPlanAsync` — the try/finally
  that was `MergeBranchAsync`'s alone, moved whole with its reasoning. A rebase replays this
  branch onto the same upstream and arrives at the same tree, so it is asked the same question.
  Three App tests against a real remote: a diverged upstream whose commit edits the mirror,
  merged and rebased, leaves the project untouched and the import pending (both red before,
  the merge landing and no import raised); one whose commit touches nothing tracked is adopted
  as before, which is the mutation the other way. The first version of the test failed for the
  wrong reason — an unanswered tree-rewrite guard cancels the merge without a word — and now
  waits for the merge to land before it asks about protection. Commit `b57935a`.

**The P2s:**

- **3 — excluding a staged rename committed its deletion.** A rename is one row and two index
  entries; `git restore --staged new.st` removes the addition and leaves the deletion where it
  was, so leaving the rename out of a commit committed `old.st`'s removal and added nothing in
  its place. `ChangeRow.RenamedFrom` names the origin for a rename only — a copy's source is
  still its own index entry and possibly its own ticked row — and `UnstageForCommitAsync`
  unstages both paths and says so in the log. An App test against real git ticks one file
  beside a staged rename and commits: HEAD gains the file alone, keeps `old.st`, and the rename
  is whole and pending in the working tree; red before with `old.st` in the commit. Staging the
  other way is not the same defect: git never reports an unstaged rename as one row. Commit
  `b2602dc`.
- **4 — a modify/delete conflict could not keep the deleted side.** `checkout --ours` for the
  side that deleted the file exits 1, "does not have our version", so choosing the deletion
  from the panel left the conflict standing. `ResolveWithSideAsync` reads the unmerged stages
  (`ls-files -u -z`); when the chosen side has none, the resolution is `git rm`, which takes
  the file out of the tree and stages the removal in one step — not `rm --cached`, which would
  leave the other side's bytes on disk untracked. Two Integration theories in both directions
  against real git, two of four rows red before. `rm` without `-f` is not a safeguard on an
  unmerged path, and the remark says so, so nobody assumes a protection that is not there.
  Commit `71c7473`.
- **5 — a Stop during the scratch copy's hash left automatic sync stuck.** The copy's hash is
  taken after the direction is claimed and before the toolchain runs (the first round's item
  2), and that read sat outside both the runner's cancellation handler and the scratch folder's
  finally. A cancelled read escaped the run whole: the binding stayed ProjectAuthoritative with
  no Try again, the copy stayed on disk, and the coalescer's slot was never completed, so every
  later watcher job answered Coalesced until a restart. The hash is taken inside the try beside
  the run, still before it. One Core test with a hook on the fake filesystem that cancels from
  inside a read, red before with the exception escaping `HandleChangeAsync`, checking all
  three leftovers. Commit `9156361`.
- **6 — applying a suggestion rewrote the lines nobody selected.** `Apply` chose one line
  ending for the whole file and `TrimEnd('\r')`-ed every line, so in a file of mixed endings
  every line of the minority kind changed, and a `\r` that was content went with it. Each line
  now keeps its own terminator and only the selected range is rebuilt; the inserted lines take
  the ending of the first line they replace, then the file's dominant one, then `\n`, and the
  final newline's presence is kept as the byte it is. Two Web tests over mixed endings, lone
  CRs and the ending rules, red before. One deliberate edge change, stated in the commit:
  deleting a bare final line leaves the line above it with its own newline, as git would show
  it. Commit `e358f5b`.
- **7 — a CRLF suggestion whose closing fence ended its line was "no suggestion".** .NET's `$`
  under `(?m)` matches before `\n` only, so a closing fence followed by `\r\n` matched nothing
  while the same body with no newline after it matched at end-of-string. The fence ends in a
  lookahead for `\r?$` rather than consuming the `\r`, because `Without` removes the match and
  the `\r\n` after the fence is the prose's own line break. One Web test over four CRLF bodies
  and two `Without` shapes, red before with null; the consuming variant was tried as a mutant
  and left a bare `\n` in CRLF prose. The React page never parses a fence, so there is no twin
  there. Commit `b4340a1`.

**The runs.** Over the landed tree, `b4340a1`: Core 2,068 (nothing skipped), Integration 232, Arch 36, CODESYS pure 405, parity 103, TIA pure 102, TwinCAT pure 27, Web 498, vitest 210; App 841 — 840/841 in 12 m 49 s with no other build or test of this session beside it, the one red `SourceProjectTests.A_program_with_no_variables_is_still_an_empty_table_over_its_code` after 13 s, green alone (3 s) and in its class (10/10, 35 s). The same test was the one red of the third round's full run; two full runs out of three today, so it is named below as a watch item rather than filed as load. 4,378 across the twelve projects; the toolchain-gated counts (TIA 5, TwinCAT 6, CODESYS 55) are from their last gate runs, and none of this round's commits reaches them.

**Still not done**, carried from the first round: branch deletion addresses the hardcoded
"origin" after item 39. Named by the fourth round's agents and left alone: the discard flow
was not audited for the rename shape of item 3 (it never unstages); the panel's "keeping"
wording for a resolution that deletes is accurate and slightly odd. **A watch item:**
`SourceProjectTests.A_program_with_no_variables_is_still_an_empty_table_over_its_code` was the
one red of two of today's three full App runs (13 s and 21 s, a render after `RefreshAsync`),
and green alone and in its class every time; nothing in today's commits touches what it
renders, and it has not been looked into.

---

### The review's third round: four more, two of them what the fixes before had left

**2026-10-25, the reviewer re-ran at `9d9a0dd` — all twelve second-round reproducers pass,
the data-loss case among them — and sent four confirmed P2 items, no new P1.** Same method as
the two rounds before: every claim read against the code, a failing test first, one commit per
item, the fix mutated away and the test watched go red, then restored. **All four are real and
all four are fixed**; none is refuted and none is deferred. Two of the four are the previous
rounds' fixes seen from one step further: the second round's item 9 taught the declaration
table to read a line in pieces and skipped an empty one (1); the first round's item 20 and the
second's item 7 each changed the normaliser without asking what had been *recorded* under the
old one (4). The other two are the same matcher as the second round's items 5 and 6, still
reading a .NET string where git walks bytes (2, 3). The shape worth naming this time: a change
to how something is *read* has to be checked against everything that was *written* under the
old reading — the manifests on disk, the ST a person types — and the rounds before checked it
against the cases in front of them.

- **1 — a semicolon on a line of its own merged two declarations.** `a : INT`, then a line
  holding only `;`, then `b : BOOL;` tabled one variable `a` of type `INT b : BOOL`, and
  nothing said `b` was missing. A regression from the second round's item 9: `Pieces` skipped
  an empty statement as "nothing here", and a semicolon alone after an open declaration is
  exactly an empty statement, so the declaration stayed open and the next line's was glued on.
  An empty statement that arrives while a declaration is open is now a complete, empty piece
  — the caller flushes what it holds, the row ends on the semicolon's line and takes that
  line's comment — and one with nothing open is still nothing. One Core test over four such
  rows, red before with "a : INT b : BOOL" and "c : INT d : INT". Commit `78e10bf`.
- **2 — a range written backwards made the preflight throw.** `v[z-a].bin filter=lfs` built
  the regex `(?!/)[]`, which .NET refuses with `RegexParseException`, so the commit dialog died
  on the rule instead of weighing the commit. The matcher's remark said git matches nothing on a
  reversed range and dropped it whole; git does not: wildmatch tests a range's low end as a
  member before it reads the dash, so `[z-a]` matches `z` and nothing else, and `[!z-a]`
  everything but `z` — asked of `git check-attr` before it was written down. The low end is
  always a member now and the dash-to-high-end only when the range runs forwards. Five rows in
  the Core theory and four in the Integration agreement test, whose expectation git computes at
  test time; three Core rows were red with the exception. Commit `e8cec65`.
- **3 — a non-ASCII file name was matched as characters, and git matches bytes.** `?.bin`
  does not cover `ä.bin` under git — `?` takes one byte of the path's UTF-8 and ä is two — so
  git commits it in full; the preflight matched one `?` to one UTF-16 character, read the rule
  as protection and suppressed the warning. The other direction too: `?.bin -filter` after
  `*.bin filter=lfs`, which git ignores for `ä.bin`, took its protection away here. `[ä]` is two
  members to git and matches neither byte sequence; 😀 is four bytes and `????`. Pattern and
  path now pass through one `ByteForm` before matching — ASCII bytes as themselves, 0x80–0xFF as
  U+E080–U+E0FF, the private-use area rather than Latin-1 because `IgnoreCase` folds Latin-1
  letters and git's folding is ASCII-only, and the area has no case and keeps the bytes' order,
  so a range across 0x80 still compares as git's bytes do. Ten Core rows and nine Integration
  rows against real git, six of each red before, in both directions. **Rejected:** a byte-aware
  class for `?` alone, which leaves `[ä]` and ranges reading characters. Commit `c71662d`.
- **4 — the delete check could no longer read the `nhash` every existing mirror carries.**
  `why_not_delete` re-exports a native object, normalises it and compares the hash with the
  manifest's. The normaliser changed twice in two days — the sibling sort replaced the line sort
  (2026-10-24, item 20) and the GUID placeholder changed shape (2026-10-25, item 7) — and each
  change made every hash recorded before it unreproducible, so an unchanged object exported by
  an older Versio was refused as `DELETE_CONTENT_CHANGED`, "edited since the export", when
  nothing had touched it. Measured before fixing: every native record of both committed fixture
  mirrors, eighteen of eighteen, matches an older form and none the current one; all of them the
  line-sort era. `versio_native_equiv.normalized_forms` now gives the three comparison forms an
  `nhash` may have been taken over, current first, and the check accepts a match under any. The
  live side is not widened by it: a recorded hash came from exactly one form, and agreement
  under another is a SHA-256 collision; what an older form cannot see (the line sort's
  blindness to swapped values, item 20) it could not see when that hash was recorded either,
  and the next export records the current form, which closes that for that mirror. A match
  under an older form travels on the `removed` event as `olderHash` and the runner puts it on
  the log line, so a deletion vouched for by an older export's hash is one the log can explain.
  Two parity tests load the real importer under CPython — its trailer runs `main()`, so the
  module is the text above it, with a stub `scriptengine` for the one class that inherits from
  it — and run the real `why_not_delete`: over every native fixture record, each passing only
  through an older form and each edited copy refused; and over the baseline `GlobalTextList`
  capture with the three forms' hashes pinned, taken once from an independent re-implementation
  of the two older forms and once from `normalized_forms`, which agreed. Four mutants (one form
  only, line sort forgotten, flag never set, mismatch not refused) each turned both tests red.
  **Rejected:** a version number beside `nhash` — it changes the manifest's shape for every
  reader and still needs these three functions for the hashes already written. Under real CODESYS, on `85f88d1` with this python embedded: the four tests the gate session re-ran for the second round and the three that drive the delete check (`An_object_whose_file_is_deleted_leaves_the_project_and_an_edited_one_does_not`, `Deleting_a_GVLs_declaration_removes_it_and_its_one_way_half_does_not_veto`, `A_native_deletion_with_nothing_to_check_it_against_is_refused`), 7/7 in 22 m 34 s, the owner's IDEs open.
  Commit `85f88d1`.

**The runs.** Over the landed tree, `85f88d1`: Core 2,066 (nothing skipped), Integration 228, Arch 36, CODESYS pure 405, parity 103, TIA pure 102, TwinCAT pure 27, Web 495, vitest 210; App 836 — 835/836 in 13 m 29 s with the CODESYS run beside it, the one red a 21 s catch-up wait (`SourceProjectTests.A_program_with_no_variables_is_still_an_empty_table_over_its_code`) that passed alone in 4 s, the same shape as the two days before on a different test. 4,364 across the twelve projects; the toolchain-gated counts (TIA 5, TwinCAT 6, CODESYS 55) are from their last gate runs.

**Still not done**, carried from the first round: branch deletion addresses the hardcoded
"origin" after item 39.

---

### The review's second round: eleven more, the one hole left open, and the shape they share

**2026-10-25, the reviewer read `ba8dbdf` and the forty-four commits under it and sent eleven
new or remaining items plus the one the first round had left open** — three P1 and nine P2, each
with a reproduction against real git or the live code. Same method as the day before: every
claim read against the code, a failing test first, one commit per item, the fix mutated away
and the test watched go red, then restored. **All twelve are real and all twelve are fixed**;
none is refuted and none is deferred. Three of the twelve are fixes to the first round's own
fixes (2, 3, 7), which is the shape worth naming: a fix that closes the reported case and opens
the one beside it. `-z` was added to carry awkward names, and the split that read it trimmed
them. The task walk was scoped to one Application and its docstring promised a stop at a nested
one that the code did not make. The sibling sort counted tags by shape, and the GUID mask was
shaped like a tag. Each had a test for the case it was written for and none for the one next
to it; each now has both.

**The P1s:**

- **1 — a project save exported over a mirror edit nothing had imported.** Named as the open
  hole under the first round's item 3 and left as a skipped test; the reviewer asked for it
  closed. An import that waits for confirmation, or for the IDE to let go, refuses without
  claiming a direction — rightly, since nothing was written — so the binding stays Clean, and
  from Clean the transition table lets an export run. The export regenerates the whole mirror
  and the unconfirmed edit is gone with no backup: an import backs the project up first, an
  export backs nothing up, because the mirror is supposed to be in git, and an uncommitted edit
  is not. A new gate beside the direction lock, `MirrorEditStillPendingAsync`, refuses a project
  job on a Clean binding whose mirror hash differs from the last synced mirror hash, with a new
  verdict `BlockedByPendingEdit`; it reads hashes rather than the reason the import has not
  happened, so the IDE-held case is covered as well as the confirmation one. Only Clean is asked
  (under `ProjectAuthoritative` the text event was already refused out loud and the export is
  what that refusal promised), only a recorded mirror hash is compared (an empty record is a
  fresh clone adoption has not reached), and a checkout does not trip it, because
  `AdoptExternalStateAsync` moves the record before any save is measured against it. The App
  keeps the pending-import banner up on the new verdict too: a restart loses the banner while
  the edit and the record survive on disk, so the refused save can be the first thing to notice
  the edit, and the banner has to come back from it. **Rejected:** entering the `Conflict` state
  dev.md promised. `(Conflict, _)` is Blocked, so it would refuse the confirmed import as well
  and need a resolve act nothing in the app offers, while the two acts that resolve this already
  exist — confirm (text wins; the save goes under the import's backup) or discard (project wins;
  the save exports on its next event) — and a refusal derived from durable facts survives a
  restart without being persisted. dev.md's table now says under `Conflict` that nothing enters
  it, rather than promising a state. Four Core tests (the un-skipped one, the discard way out,
  the IDE-held case, the `ProjectAuthoritative` case that must still export) and two App tests;
  the gate disabled and the banner trigger narrowed each went red. Commit `1806b87`.
- **2 — a leading space in a path bypassed the merge guard.** The `-z` listing the first round
  added for item 15 split with `TrimEntries` to drop the runner's trailing newline, and
  `TrimEntries` trims every field: `" plc/Machine.project"` arrived as `"plc/Machine.project"`,
  matched no tracked root, and the preflight answered Proceed. The one newline the runner appends
  is removed by length now and nothing else is touched; an Integration test commits a
  leading-space directory and file on a branch and asserts the names verbatim and the guard's
  `TouchesTrackedProject`. Commit `5ca8703`.
- **3 — renaming a parent Application's program also rewrote a nested Application's tasks.**
  The first round's item 31 scoped retargeting to the renamed object's own Application, and
  `tasks_under`'s docstring said a nested Application is not descended into; the recursion went
  into every child alike. The walk now takes the caller's Application test, as `application_of`
  already does, and skips a nested Application whole; a rename inside it scopes to it and not to
  its parent. The CPython tree test grows a Simulation inside the nested Application and asserts
  both walks. **Owed:** the CODESYS gate's rename test on the embedded script. Commit `6324523`.

**The P2s:**

- **4 — a partly staged file was weighed on disk; the commit records the index.**
  `ConfirmLargeFilesAsync` sized every staged path with `FileInfo`, so a file staged at 20 MiB
  and overwritten on disk with five bytes passed the preflight at five bytes while the commit
  wrote twenty megabytes into history — the one outcome the gate exists to make somebody choose
  knowingly, and the partly-staged commit fix (`23a5ea4`) had just made that state ordinary. A
  staged file deleted from disk had the same hole under a comment saying it "weighs nothing
  either way". New `IGitClient.IndexBlobSizesAsync`: one `cat-file --batch-check` over
  `:0:<path>` names on stdin, which git resolves literally (probed on spaces, non-ASCII,
  `--odd.st`, parentheses, `^~@#`) with no paths on argv; the preflight asks it for every
  staged path whose unstaged side is not Unmodified and keeps `FileInfo` for the rest; a null
  answer is logged and falls back to the disk reading rather than to silence. Rejected:
  `ls-files -s` plus `cat-file` (no `--pathspec-from-file`, so paths ride argv into the
  CreateProcess limit the staging calls already hit); NUL-delimited input (`-z` arrived in 2.38
  and became `-Z` two releases later); asking the index for every staged path (a spawn per
  commit to re-measure files whose disk copy already matches). Commit `61b58ec`.
- **5 — a bare `filter` and a character class were both read wrong.** `*.bin filter=lfs`
  followed by `huge.bin filter` is, to git, the attribute set to true — `check-attr` answers
  `set` — and no filter driver is called "true", so huge.bin is committed in full;
  `FilterState` knew `-filter`, `!filter` and `filter=…` and let the fourth spelling fall
  through as "says nothing", so the earlier lfs rule stood. `huge[.]bin -filter` failed by
  another road: `ToRegex` escaped the brackets as literals and the override matched nothing.
  The first round's item 13 had fixed the order without widening the vocabulary, and its
  remark that an unreadable rule costs only a spurious question holds for a rule that grants
  protection, not one that revokes it. Probing further found six shapes the old matcher read
  *wider* than git (`a**b.bin` as any depth, `huge\*.bin` as a wildcard), granting protection
  git never gives. The matcher is gitattributes(5)'s wildmatch now: classes with ranges, `!`/`^`
  negation and POSIX names, backslash escapes, `**` as "any depth" only between slashes or ends,
  a class that never matches `/`, and git's two never-matches (unclosed `[`, trailing `\`) left
  out as git leaves them out. A 50-row Core theory is the `check-attr` transcript; a 25-row
  Integration theory re-derives each expectation from git at test time. Rejected: an unclosed
  `[` as a literal bracket (friendlier, and not what git does). Commit `afc3f41`.
- **6 — a quoted pattern was unquoted by dropping the backslash.** `"huge\056bin" -filter` is
  git's own spelling of an override for huge.bin (`\056` is the octal for a dot; `check-attr`
  answers `unset`), and the reader the first round's item 26 added kept the digits, so the line
  was a rule for huge056bin. `GitAttributesPattern.Read` is `unquote_c_style` now: the named
  escapes, the verbatim `\\` and `\"`, three octal digits with the first at most 3, collected as
  bytes and decoded as one UTF-8 run because git quotes a name byte by byte (`ä` arrives as
  `\303\244`). It refuses what git refuses — short or over-range octal, unknown letter, dangling
  backslash, unclosed quote — because git drops such a line entirely and a lenient reading
  would turn a line git ignores into an override git does not apply. `Quote` is `quote_c_style`
  for the same characters so a round trip holds; a non-ASCII name stays bare on purpose, since
  git reads the bare form and every earlier adoption wrote it. Commit `67198f4`.
- **7 — the GUID mask broke the repaired sibling sort.** The sort the first round's item 20
  added counts tag depth by shape, and the placeholder was `<masked-guid>` — an element that
  opens and never closes. With masking on, every chunk after the first GUID was swallowed into
  one and nothing could be reordered, so a payload CODESYS had merely reordered compared
  different; with masking off the same pair compared equivalent. Masking on is the only setting
  an import's delete check uses, so a reordered native object was refused as
  `DELETE_CONTENT_CHANGED`. The placeholder is `{masked-guid}` on both sides: it is only ever
  compared against another mask, so its shape is free, and it must simply not look like a tag.
  A Pure test and a parity corpus case whose *text* pins the shape; each half mutated back went
  red on its own — the python one also against the real CODESYS fixture output, which carries
  GUIDs inside siblings as a matter of course. Commit `2d602ec`.
- **8 — a deleted task vanished from the configuration comparison.** `RenderChoice` dropped a
  child whose current file could not be read *before* building `Files`, the list the committed
  copies are read through, so a deleted task was in neither catalog: deleting VISU_TASK gave two
  catalogs that both held MainTask. A child the tree marks Deleted whose file is gone is kept
  in `Files` with no task of its own; `Reparse` reads its HEAD copy and `CatalogDiff` marks the
  row Removed. The desktop's `MarkCatalogChangesAsync` would have thrown on the missing file,
  warned and left the table unmarked — the deletion hidden a second way — and reads a missing
  working copy as empty now. Rejected: keeping every unreadable child (an I/O failure would read
  as a deleted task). Stated cost: a configuration whose every task was deleted has no live task
  to say what it is and still draws as its own file. Commit `f5b8b38`.
- **9 — several ST declarations on one line were one row, and a one-line VAR block was none.**
  `StDeclarationTable` treated a line as one thing: `a : INT; b : BOOL;` became one variable `a`
  of type `INT; b : BOOL`, and `VAR a : INT; END_VAR` opened a section, drew no row and claimed
  its line as framing, so neither reached `Unshown` — a table a reviewer reads before committing
  was quietly wrong. Each line's code is read in pieces now: a section keyword with its
  qualifiers, each statement up to a top-level semicolon (judged at the depth still open from
  the lines above, which the first draft got wrong and an existing bracket test caught), and
  the open tail for the next line; a keyword sharing a line with a declaration claims nothing,
  and what no row claims lands in `Unshown`. Rejected: cutting a keyword from any following
  word, which would glue an unknown qualifier such as TIA's `DB_SPECIFIC` onto the next
  declaration. The python side parses headers only and has no row parser to keep in step.
  Commit `a84ea19`.
- **10 — removing a final newline compared as "No changes."** `UnifiedDiffWriter` discarded
  the empty last element after a final newline and never recorded whether there had been one,
  so `value\n` and `value` wrote an empty patch; the remark that excused it is true of the
  mirror's writers and false of the raw comparison, which also reads a vendor's JSON and a
  README. Lines are compared with their terminators, as git compares them, and
  `\ No newline at end of file` follows the line it describes, on whichever side, only inside a
  hunk; all four shapes were checked against `git diff --no-index`. The parser, the side-by-side
  pairing and the HTML already read the marker out of git's own patches, so nothing downstream
  changed. Commit `11d64be`.
- **11 — the web refused valid ref names and admitted invalid ones.** `ValidRef` was an
  allow-list of ASCII letters, digits and `_ . / -`, so `release+hotfix` and `Prüfung` were "not
  found" on every ref route, while `.hidden`, `a.lock`, `a//b` and a leading dash passed. The
  gate is git's `check-ref-format` rules verbatim, plus two refusals kept on purpose: a leading
  dash (nothing in the web hands a ref to a process — every ref reaches GitHub over HTTP
  percent-encoded, pinned by a test reading `sha=release%2Bhotfix` off the wire — but an
  option-shaped string should never qualify as a name) and C1 control characters. 44 rows;
  the old gate under them fails 21. Rejected: widening the allow-list, which still refuses the
  next character git takes and still admits `.lock`. Commit `87e1411`.
- **12 — a direct link to an older release fetched its notes and never showed them.** The
  page drew one article per release in the server's capped list and opened the one the URL
  named; a tag past the cap fetched `/releases/view` and had no article to land in. A picked
  tag the list does not carry gets its own article first, its header from the view's own
  server-spelled meta once it answers and the shared notes body always, with the listed rows
  closed below; header and body became two render helpers shared by both so they cannot
  drift. While the view loads or fails only the body is drawn, because the raw tag from the
  URL is the one name on the page nobody has spelled. Rejected: redirecting an unlisted tag to
  GitHub. Commit `ab9f1ec`.

**What this round owes the IDE gates:** `Versio.Plc.Codesys.Tests` over 3 (the rename's task
retargeting) and 7 (the native equivalence the delete check reads), on the embedded python;
nothing new for TIA or TwinCAT. **Not done:** branch deletion still addresses the hardcoded
"origin" after the first round's item 39.

**Counts after the round:** Core 2,050 (the skipped test of the day before is un-skipped and passes; nothing is skipped), Integration 215, Arch 36, CODESYS pure 405, parity 101, TIA pure 102, TwinCAT pure 27, Web 495, vitest 210, tsc clean; App 836 — 835/836 loaded in 13 m 9 s with the CODESYS gate running beside it, the one 30 s catch-up wait (`CommitWhileIdeHoldsProjectTests`) failing as it did the day before and passing alone in 4 s. The TIA gate's import Fact ran green in the worktree (1 passed, 4 skipped, 2 min); TwinCAT skipped; CODESYS is the gate session's run. 4,333 across the twelve projects.

---

### An external review's forty-one findings, verified one by one: sixteen ways to lose work, and the twenty-five behind them

**2026-10-24, the owner pasted a reviewer's list with no other words** — forty-one numbered
findings, sixteen marked P1 (work loss, wrong PLC content, a crash) and twenty-five P2, with the
reviewer's own note that no fix had been applied. Every item was read against the code before
anything was changed, reproduced with a failing test where a test could reach it, and fixed in
its own commit; the few the test cannot reach without a real IDE say so below. **Forty of the
forty-one are real.** The forty-first (28) is real for TIA and TwinCAT and refuted for CODESYS,
whose runner kills its own child. One hole none of the forty-one named was found under item 3 and
is left open, red-fenced, for the owner.

The pattern across the sixteen P1s is worth naming before the list: in twelve of them the code
was *right about the common case and silent about the other one*. A path is a name (until it has
a bracket). The stash at position zero is ours (until a terminal pushes one). `--ours` is this
branch (until a rebase). The live file is what was exported (until the IDE saves during the copy).
Lines inside a container are unordered (until a child spans two). An empty implementation means a
DUT (until it means a deleted body). None of these were guesses when written; each was a rule with
one exception nobody had met.

**The P1s, in the order they were fixed:**

- **15 — a project with an umlaut in its name skipped the merge guard.** `IncomingPathsAsync` was
  the one path listing in `GitCliClient` without `-z`; git's default `core.quotePath` wrote
  `"Pr\303\274fung.project"`, quotes and octal included, which equals no project path and starts
  with no mirror prefix, so `MergePreflight` found nothing affected and the import protection was
  skipped. Listed with `-z` now. Commit `2a89ba2`.
- **1 — discarding `part[1].st` discarded `part1.st`.** Every pathspec is a glob unless told
  otherwise, and staging, discarding and resolving all passed the ticked name through.
  `--literal-pathspecs` precedes every verb whose operand is a file path. **Not**
  `GIT_LITERAL_PATHSPECS=1` on every process: the integration suite caught that on the first
  run — under it `stash push --include-untracked` records the untracked file and then leaves it
  on disk, because git's own clean step inside stash is written with pathspec magic. Commit
  `9ba3374`.
- **24 — the auto-stash offer dropped the wrong stash.** `stash@{n}` was resolved once and held
  across two modal dialogs; a stash pushed from a terminal meanwhile took position zero, so
  Discard dropped the terminal's stash and left ours, and Restore popped the terminal's work
  into the tree. The comment above the loop said the captured ref was safe because nothing
  else moved. Both the drop and the pop re-read the list and match the record's SHA at the
  moment of the act, and refuse with "already gone" otherwise; the drop's result is checked
  before the record is forgotten. Commit `8317c66`.
- **29 — "Keep mine" took the upstream's file during a rebase.** The button's parameter went to
  git as `--ours` unchanged, and under a rebase `--ours` is the upstream. The remark claimed
  the view chose the labels from `MergeState`; the XAML has fixed labels. Mapped by merge state
  now, and logged both ways. Commit `9b043f8`.
- **9 — a rescan blanked a live lock.** `RecoverAsync` mapped only `ReadoptedAfterRestart` to
  the persisted lock; `HeldByLiveSession` — usually this very process rebuilding its engine
  because a project appeared — fell through to a blank `Clean`, and an import could run over
  unpushed project work. The existing test asserted the decision and not the state, which is why
  it stayed green. Commit `0ea364d`.
- **3 — discarding one of two mirror edits recorded the other as synced.**
  `ReleaseAfterDiscardAsync` released unconditionally and recorded the mirror as it stood,
  surviving edits included: the text gate then said NoChange for them and the next project save
  exported over them. Unless the project side holds the lock, the mirror is hashed first and
  nothing is recorded while it still differs from the last synced state. Commit `a0302af`.
  **Found alongside and left open:** a Clean binding with an *unconfirmed* mirror edit exports
  over it on the next project save with or without a discard — `dev.md` promises a `Conflict`
  state for "both sides changed" and nothing in the orchestrator enters it.
  `A_project_save_does_not_export_over_an_unconfirmed_mirror_edit` is in the Core suite, skipped
  with that sentence, until the owner decides how the engine should refuse.
- **2 — a save during a copy export was recorded as exported.** When the IDE holds the project
  the export reads a scratch copy, and the hash recorded as exported was taken afterwards from
  the live file. The copy is hashed before the run (the run leaves sidecars in the scratch
  folder) and that is what `ExportSucceeded` records. Commit `313071a`.
- **33 — three nodes of malformed ladder XML ran the process out of memory.** A contact whose
  in and out pins share a net is an edge from a net to itself; the series step found it as both
  the way into the point and the way out, merged it with itself into a series twice as long,
  removed one edge and went round again. Refused with "feeds itself"; the network draws flat.
  The test timed out at ten seconds before. Commit `adcef1e`.
- **20 — native XML equivalence sorted lines, and lost which child they belonged to.** Two
  multi-line children with their values swapped sorted to the same bag of lines, compared
  equivalent, and the export kept the old file on disk — a real change never reached the
  mirror. Both normalisers now cut a container's body into whole sibling elements by tag depth
  and sort those; six new parity corpus cases pin that the C# and the python cut alike (the
  python mutated back to a line sort diverges on the first). Commit `4b83f89`.
  **The CODESYS gate found the fix's own hole on 2026-10-25** (53/55; the two red are
  `An_import_and_a_re_export_leave_the_mirror_byte_identical` and
  `A_member_inside_a_folder_round_trips_and_moves_nothing_else`, both on `Live_Visu.vis.xml`
  and `Visualization.vis.xml`): masking runs before sorting and writes a GUID as
  `<masked-guid>`, which the shared tag expression reads as an *open* tag. An unordered
  container holding GUIDs therefore never returns to depth zero, its whole body is kept as one
  unterminated chunk, nothing in it is sorted, and the vis files CODESYS re-mints on import
  compare as changed — so the re-export writes them and the mirror churns, the exact thing the
  normaliser exists to stop. `GlobalTextList.xml` holds seventeen GUID lines inside such
  containers and still compared equivalent — with nothing sorted on either side, a file whose
  siblings come back in the same order passes anyway, and the vis files are the ones CODESYS
  reorders, which is what the sort was for. Reproduced without CODESYS by loading the embedded
  python as plain CPython: two swapped siblings in a `Dictionary` compare equivalent with plain
  values and not with GUIDs. The six parity cases and the new pure tests missed it because none
  of them holds a GUID at all. The second review round's item 7 fixes it in both normalisers;
  the two gate tests passed on the re-run against `5cb51ef`, with the item 5 and 31 tests
  beside them, 4/4 in 9 m 7 s on 2026-10-25.
- **22 — restoring the oldest of ten backups deleted it first.** The safety copy made eleven,
  pruning ran inside it, and the restore source went before it was read. The source is pinned
  through the prune. The fake file system's `CopyAsync` used to write an empty file for a
  missing source, which made this a silently emptied project in the suite; it throws now, as
  `File.Copy` does. Commit `a0bd927`.
- **21 — restoring a directory payload overlaid the backup.** A file added after the backup
  survived the restore, so the result was neither the backup nor the damaged state. Files the
  backup does not have are deleted before the copy, inside the own-write scope. Commit `adb34ef`.
- **5 — a DUT holding a literal escape marker came back changed.** The export writes a
  declaration-only object with `normalize_decl_only` and never escapes it; the import read every
  plain `.st` through `split_st`, which unescapes and cuts at a delimiter, so `//!VERSIO-ESC!D`
  in a comment became the delimiter and a literal delimiter line truncated the type. The import
  now chooses by the object, as the export does — no textual implementation, no decoding —
  through `read_textual` in `versio_text`, pinned against `StTextCodec.DeclarationOnly` and
  `Split` by the parity corpus plus three adversarial DUTs. Commit `192b5cf`. ~~*CODESYS gate owed.*~~
  *Passed under real CODESYS on 2026-10-25*
  (`A_declaration_only_object_edited_in_the_mirror_reaches_the_project`).
- **14 — a 600 KB manifest overflowed the stack.** An honest ownership chain four thousand
  generations long passed the cycle guard and `Attach` recursed once per generation. The walk
  records a depth per guid and lays anything past `MaxNesting` (64) flat; the 10,000-deep test
  runs on a 1 MB-stack thread and crashed the test host before. An explicit stack in `Attach`
  alone was rejected: the later walkers and the WPF tree recurse over the same depth.
  Commit `3a60d34`.
- **28 — Stop killed a Portal the engineer had opened.** The TIA and TwinCAT runners reaped
  every IDE process that was not running when the host started — a diff over the whole run.
  Each host now measures the IDE's PID across the creation call alone and reports it as
  `ide_pid`; `ChildProcess.KillOwned` kills that PID only after checking image name and start
  time, and no reported PID means nothing is reaped by name, said in the log. Three Integration
  tests on the decision (a decoy of the same name survives; red with the cross-check removed).
  CODESYS was never affected. Commit `0e862de`. *TIA and TwinCAT gates owed for the emission —
  neither ran on 2026-10-25: no XAE, and Portal refused the host (not an Openness member).*
- **23 — a deleted TwinCAT body stayed in the project.** `SetTexts` skipped the implementation
  write whenever the file had none, right for a DUT and wrong for a cleared function block, and
  only the declaration was read back. `HostText.ImplementationWriteFor` tells the cases apart by
  what the object holds; the body is read back both ways. The host is an exe no test can
  reference, so `HostText.cs` is compiled into the TwinCAT pure tests as a linked source — which
  also pins, for the first time, that the host's codec splits exactly as `StTextCodec` does.
  Commit `66697b7`. *XAE gate owed: whether it accepts an empty `ImplementationText`.*
- **27 — a station comment over 240 characters round-tripped cut.** `TryAttribute` returned
  every string through `Scalar`, the probe's 240-character display clip, and the device
  catalog's export, before-write check and read-back all went through it. Strings come back
  whole; the two listing sites clip for themselves; the gate's Comment marker is 300 characters
  long. Commit `a22a430`. *TIA gate owed.*
- **31 — renaming `ApplicationA/Simulation` rewrote ApplicationB's task.** Task POU lists are
  bare names, unique within an Application; the retargeting walked the whole project. It starts
  at the renamed object's own Application now (`versio_tree.application_of`, through `parent`),
  and an object outside any Application retargets nothing and warns. A CPython parity test builds
  two Applications each calling `Simulation`. Commit `228f2ef`. ~~*CODESYS gate owed.*~~ *Passed
  under real CODESYS on 2026-10-25*
  (`A_POU_renamed_in_the_mirror_keeps_its_identity_rather_than_being_rebuilt` asserts the task
  list); the fixture has one Application, so the second-Application case stays parity-only.

**Two traps the session itself fell into, for the next one.** `refs/stash` is shared across every
worktree of a repository: a `git stash` in one worktree and a `git stash pop` in another swapped
two uncommitted files between the trees, and only `git status` on both sides showed it. And
`GIT_LITERAL_PATHSPECS` is not the same thing as `--literal-pathspecs` on the one command that
needs it — see item 1.

**The P2s in git and the git panel** (eleven, fixed in a worktree and cherry-picked):

- **4 — a modified `README.txt` ahead of a rename lost the rename.** `DetectRenamesAsync`
  scanned the `-z` fields of `diff --name-status --find-renames` itself and took any field
  starting with `R` as a status: a one-path record was skipped a field at a time, `README.txt`
  landed in the status slot and the map came back `{"R100" → "plc/Old.st"}`, so the renamed
  project's binding reported broken and its new path untracked. `GitNameStatusParser.Renames`
  in Core walks each record by its own width and the client has no scan left. Patching the
  scan was rejected as a second parser of the same stream. Commit `16dc428`.
- **13 — a later `-filter` override was read as "still LFS".** The preflight kept only rules
  containing `filter=lfs` and asked whether any matched, so `*.bin filter=lfs` followed by
  `huge.bin -filter` suppressed the warning for a large ordinary blob — a false negative in a
  class whose remarks promised only spurious questions (`filter=lfsx` counted too). Every rule
  that says anything about `filter` is kept in file order with its verdict, last match wins, and
  a rule that does not mention `filter` is left out rather than recorded as "not lfs". Six Core
  tests, four red before. Commit `b4c99a6`.
- **26 — a mirror path with a space wrote a `.gitattributes` line git could not read.** Written
  unquoted, `src/My App/export/** text eol=lf` splits on the space, so the mirror had no LF
  rule (`check-attr` said `unspecified`), and `ExistingPatterns` read the first token, so the
  rule never counted as present and every adoption appended another block. A new
  `GitAttributesPattern` C-quotes a pattern with whitespace, `"` or `\` and reads a quoted token
  back; the planner, the covered-check and the preflight all go through it. The Integration test
  asks git itself (`check-attr eol` → `lf`). Commit `b10fa33`.
- **39 — `origin/topic` plus `upstream/topic` invented a local `topic`.** `BranchInventory.Combine`
  promoted any row found by name to Both when a second remote carried the same short name, so
  deletion ran `git branch -d` on a branch the clone never had and skipped the remote half on
  the failure. Only a LocalOnly row becomes Both now. Left for a follow-up: deletion still
  addresses the hardcoded "origin", and `BranchInfo.RemoteRef` holds one remote. Commit `8e9fc8d`.
- **38 — credentials for `host:8443` were asked for under `host`.** Git keys its store by
  `host=h:8443`; the three credential calls passed the port-stripped `Host`, so the status
  read "none", sign-in stored under a key no push used, and `credential reject` exited 0 having
  erased nothing — the remark in `RemoteUrl` asserted the opposite. `RemoteUrl.CredentialHost`
  (the authority, named for why) goes to all three; the Integration test stores a credential
  under a private `store --file=` helper and measures what git keys by. Commit `b483d14`.
- **25 — in a linked worktree the exclude rule went where git does not look.**
  `rev-parse --absolute-git-dir` answers `.git/worktrees/<name>` there, and git reads
  `info/exclude` from the common directory only, so `.versio/local/` stayed untracked and
  `stash push --include-untracked` swept the auto-stash record — the hole FINDINGS already
  describes for `.gitignore`, back through a worktree. `IGitClient.CommonGitDirectoryAsync`
  (`--git-common-dir`) feeds the exclude; the per-worktree directory stays for the merge
  markers, which live there. A new App class opens a linked worktree through the real shell,
  because that one line had no test. Commit `1c4651b`.
- **16 — the outgoing preview showed the previous branch's commits.** `NoteStatus` compared
  ahead/behind only; two branches each one ahead of their own upstream have equal counts, so a
  checkout left the old `PreviewCommits` under the new badge. The branch name and upstream
  count as movement now. Commit `2ab84f0`.
- **19 — a skipped hunk's removed `-- comment` became a second file header.** `HunkPatchBuilder`
  ran every line of an unselected hunk through `IsFileHeader`, which matches `--- `; the
  2026-09-21 fix (`a5e914f`) had given only the parser a line budget. The builder now reads the
  `@@` counts through the parser's own header reader and treats a skipped hunk's lines as
  content while it owes lines on either side; the Integration test applies hunk 1 of such a diff
  with `git apply --cached`. Commit `88d051a`.
- **30 — a conflicted stash apply had no conflict section.** Visibility was `IsMidMerge`, and the
  merge state reads only merge and rebase markers, so the files moved into Conflicted, the
  commit button said "Resolve the conflicts first", and the Keep buttons were hidden. A new
  `MergeState.Conflicts` is set when the markers say None and the status has conflicts;
  `ShowsConflicts` drives the section, Abandon is neither enabled nor shown (there is no
  operation to abandon), and under a stash apply mine is HEAD and theirs is the stash. Applying
  a stash with conflicts now raises a warning rather than a failure. Commit `8c3f7e6`.
- **32 — "Stage hunk" and then Commit committed both hunks.** The commit re-added every included
  path whole. A row that is staged and still changed in the working tree is `IsPartlyStaged`:
  the commit leaves it as the index stands, says so before the press ("N partly staged file(s)
  go in as staged") and after. A separate staged list with its own button was rejected: the
  one-button shape was `CommitPlan`'s deliberate choice. Commit `23a5ea4`.
- **41 — Amend rewrote whatever HEAD was after the dialog.** HEAD and the unpushed set were read
  before the modal confirmation and `commit --amend` ran after it, so a commit, checkout or
  push landing meanwhile rewrote a commit the dialog never named or one already pushed. Both
  are re-read after the answer; a moved or pushed HEAD is refused with "Not amended." and the
  typed message is kept. A compare-and-swap through `update-ref` was rejected: it closes
  milliseconds, not the dialog, and spreads the amend over three calls. Commit `7f9819a`.

**The P2s in the app, the watcher and the XML reader** (six, fixed in a worktree and
cherry-picked):

- **36 — a machine without CODESYS could not attach a repository.** `CodesysToolchain.ProbeAsync`
  picked the install owning the resolved profile with `?? installs[0]`, which threw
  `ArgumentOutOfRange` before `NoInstallFound` could be reported; the probe runs inside
  `AttachAsync`, so a TIA-only machine lost the orchestrator and the watcher, and the vendors
  after CODESYS were never probed. The owner is `FirstOrDefault` and only the resolved arm uses
  it; `SyncViewModel` now catches a vendor's probe exception, logs it with the stack, records the
  vendor as unavailable with the reason, and the refusing runner says that reason instead of "not
  exported yet". A pure test probes an empty search root through a new internal constructor.
  Commit `d462495`; a follow-up `215c2ff` makes the App test wait on the attach's own log lines
  rather than a tree row that the lock races, one run in ten.
- **12 — `{}` in `tree.json` stopped the repository opening.** The positional
  `TreeExpansion.State` deserialised with null lists, `Restore` called `UnionWith(null)`, and the
  catch covered only JSON, IO and access errors — a file documented as always safe to lose ended
  `AttachAsync` before the orchestrator was built. `Restore` treats a missing list as empty and
  the catch is every exception but cancellation. Measured: with only the catch widened the shell
  cases stay green, so a direct `Restore(null, null)` test watches the `?? []`. Commit `37e59bf`.
- **37 — reordering two tree rows produced three.** `TreeRow.Reconcile` inserted on every path
  mismatch and never removed the stale row: `[A,B]` against `[B,A]` gave `[B,A,B]`, with WPF's
  selection on whichever copy. Reachable through the library level (sorted by SystemLibrary and
  display name while identity is the path) and the task level (the IDE's order).
  `ShellViewModel.ReconcileInto` had the same pass, saved only by callers sorting by path. Both
  now move the matching later row into place, so the instance — selection, expander, tick —
  survives, and trim past `fresh.Count`. Five reorder cases red before. Commit `d858bc2`.
- **40 — a pushed HTML file opened the browser by itself.** `WebPageView` forwarded every
  navigation that was not the shown file to `OpenLink` without asking `IsUserInitiated`, and
  `IsScriptEnabled = false` does not stop a `<meta http-equiv="refresh">`: clicking the file's
  row launched the default browser at any address. The decision is a pure
  `WebPageNavigation.Decide(uri, shown, isUserInitiated, isRedirected)` — Allow, Forward or
  Refuse — tested six ways without WebView2; the handler forwards only a click and reports the
  rest, as the Markdown and ladder views already did. Redirects that began with a click are
  refused too: the user chose the link, not where a server sent it. Commit `be2f433`.
- **18 — `&#10;` in a comment stretched a member's span into the next member's lines.**
  `SafeXml.LinesOf` ended a span at the text's start line plus the newlines in its *decoded*
  value, so an encoded line break on one physical line added a phantom line, `ByLine` (the
  smallest span holding a line) could hand the next member's line to this one, and coverage
  walked the same spans. Openness writes multi-line comments exactly this way; no fixture had
  one. The parser keeps the source beside the document and counts the breaks the file holds
  between the node's position and the next `<` (or `]]>` for CDATA). CODESYS numbers
  declaration rows by splitting the decoded blob under a comment that said "an XML entity never
  adds a line" — true of `&lt;`, false of `&#10;` — so a blob with encoded breaks keeps its rows
  and loses their line numbers (`HasEncodedLineBreaks`): no line to pin rather than the wrong
  one. Mapping decoded lines to physical ones was rejected as machinery for a file CODESYS never
  writes. Commit `e2fb3f4`.
- **11 — the mirror the first export created was never watched.** `WatchTree` on a missing
  root warned and returned, and nothing called it again; the export's rename/rename/delete swap
  moved the watcher's handle into `.versio_trash`. **The review's assumption corrected by the
  test:** the rename raises nothing on the handle, but deleting the trash raises `Error`
  ("Access is denied"), after which the old handler polled known paths once and kept the dead
  handle. Each tree now keeps a sentinel watcher on its nearest existing ancestor and rebuilds
  the tree watcher on whatever is at the path when the root appears, is renamed or deleted; an
  `Error` rebuilds it too; the safety poll enumerates the roots. Two Integration tests on the
  real file system, the poll an hour away so only a rebuilt watcher can deliver; both red
  before. Re-watching from `SyncViewModel` after an export was rejected: it covers one of the
  two ways a root changes under a watcher that can notice both itself. Commit `08efbae`.

**The P2s in the web viewer** (seven, fixed in a worktree and cherry-picked; vitest 200 → 209,
`Versio.Web.Tests` 448 → 450):

- **6 — a release tagged `latest` never opened.** `ReleasesPage` folded the `/releases/latest`
  route marker into the tag's own string, so `/releases/tag/latest` never fetched its notes and
  was redirected to the newest release, or loaded forever. The redirect is keyed on the `latest`
  prop and the fetch on the route parameter alone; a sentinel string was rejected because a tag
  is any ref name. Commit `f9ac533`.
- **7 — a refused milestone pick stayed selected.** The editor kept the picked option until the
  poll agreed, and a refused write changes nothing to agree with, so the rejected value sat under
  a heading naming the real milestone and re-picking it fired no change event. `write.run`'s
  existing false answer lets the pick go; clearing on every run was rejected because that is the
  snap-back the local pick was added for the same day. Commit `20ab639`.
- **8 — the pull counts rewrote a quoted label.** `WithState` swapped the leftmost `is:` token
  with a boundary regex, which the morning's fix stopped only when the token touched its quotes;
  `label:"foo is:closed bar" is:open` had its label rewritten and its state kept. The swap now
  walks the tokenizer's own matches, where a quoted phrase is one token, and the second regex is
  gone — a second regex over the tokenizer's text was the drift. Commit `d9fd7a8`.
- **10 — only the first ladder on a review page had working controls.** The script took
  `document.querySelector('.ld-page')` and returned once that one was scripted; jumps resolved by
  `getElementById` while row ids repeat per diagram; F7 always stepped the first. Every unscripted
  page is set up, a jump resolves inside its page, and the keyboard steps the page last clicked
  or focused. Unique ids per diagram were rejected: `LadderHtml` draws one diagram and does not
  know it is one of several. Four tests over two diagrams sharing ids. Commit `0ce6fb0`.
- **17 — a second thread under a member row spanned one column.** The slot's `colSpan` was read
  from the element it goes after, which for a second thread is the first thread's one-cell slot
  row — the shape fixed in the side-by-side branch on 2026-10-24 and missed in the table branch
  beside it. The span is the member row's own `cells.length`. Commit `ffc6249`.
- **34 — the fork-branch field could not be typed.** `value={fork ? form.head : ''}` emptied the
  field on every keystroke until a colon existed, and each bare character became the head and
  started a `/compare` of a branch called "b"; the existing test pasted `bob:feature` whole. The
  field keeps its own text and commits to the form only with a colon in it; the test types one
  character at a time and asserts no `/compare` was fetched. Commit `4852c53`.
- **35 — every 51st anonymous pull request was never listed.** The list fetched `per_page=51` at
  page N, showed fifty and said next was N+1 — REST pages are in units of `per_page`, so page 2
  began at the 52nd; the fake did the same arithmetic. `per_page=50`, and a full page decides
  `next` with a one-row probe at `page N·50+1`; the page clamp widened so the probe is not
  clamped. The `Link` header was rejected as wider than the defect (the reader returns bodies).
  Cost: one extra call per full page. Commit `abadbb4`.

**Where it stands.** Forty-three commits on `VersioWeb`, one per item (plus one test-only
follow-up and this write-up); every suite without an IDE is green over all of them — Core 1,951
(one skipped, the hole above), Integration 188, Arch 36, CODESYS pure 404 and parity 101, TIA
pure 102, TwinCAT pure 27, Web 450, vitest 209, App 833 — and the TIA and TwinCAT gates skip
cleanly here. **Five fixes still owe their IDE gate**: ~~the CODESYS gate over items 5 and 31 (the
import script and the task retargeting)~~ — **ran 2026-10-25 on `ba8dbdf`, 53/55 in 1 h 22 m
on a loaded machine: 5 and 31 pass, and the two red are item 20's own hole, written up under
it below** — the XAE gate over 23 (an empty `ImplementationText`)
and the ide_pid half of 28, and the TIA gate over 27 (the 300-character Comment marker) and the
other half of 28 — neither IDE answers on the machine that ran the CODESYS gate (TwinCAT
absent, 6 skipped in 4 s; TIA V20 and V21 installed but the account is not an Openness member,
1 passed and 4 skipped in 3 m 1 s), so both are still owed. **Two follow-ups are named and not done:** the unconfirmed-edit export under
item 3 (`EnterConflict` has no caller), and branch deletion still addressing the hardcoded
"origin" after item 39. The reviewer's own validation ran no IDE gate either.

The P2 write-ups were drafted by the three sessions that fixed them, in their own worktrees, and
read here against the commits before being kept.

---

### The owner's first look at the page: GitHub's mark where Versio's belonged, and a review that went read-only without a word

**2026-10-24, the owner looked at the finished shape and said two things.** First: "looks so
much better, just make sure you don't use the GitHub icon, it should be the Versio icon, and
Versio colour and style." The header drew GitHub's octocat and `tokens.css` carried Primer's own
values, which the plan (`web/FINISH.md`, D *Theme*) had decided on as "the owner's ask" — the ask
was GitHub's *shape*, and a page in GitHub's colours under GitHub's mark does not look like
github.com, it claims to be it. The decision is reversed there: Primer's names, the desktop
palette's values (`Palette.Light/Dark.xaml`, pinned colour for colour by `tokens.test.ts`), the
primary button in the accent blue as the desktop's `AccentButton` is, and `VersioMark` in the
header, the reference drawing of `assets/versio.svg` without its tile, held to the SVG's numbers
by its test. Commit `9bd4387`.

Second, after looking for a while: **"why can't I still add any comments, or reply to any
comments?"** Three things lined up to produce a page that simply showed less:

- The fixture preview serves for 25 minutes and then ends. The owner's look outlived two of
  them, and the preview was restarted under the look — three times, the last in a loop.
- Every restart mints a new signed-in session and writes its id to the session file. The cookie
  the owner had pasted was the first run's; from the second run on, the server saw an unknown
  session and answered `/api/me` with "signed out", which it was.
- A signed-out reader of a pull request got **no comment box, no reply buttons, and nothing
  saying why**. `PullPage` built its `writer` only when the snapshot said `writable`, and every
  write affordance hung off the writer; the one sentence about signing in was the status
  panel's "cannot tell whether the pull request has changed", shown only when sign-in is
  configured, which the preview's is not. GitHub draws "Sign in to comment" where the box would
  be. Versio drew the timeline and stopped.

The browser pane confirmed it: the cookie it held was the first run's id, `/api/me` said
`signedIn: false`, the page had no `textarea` and no button matching reply, resolve or review.

**Fixed, three ways.** `VERSIO_WEB_PREVIEW_MINUTES` gives the preview a lifetime (25 stays the
default, so a forgotten one still ends) — commit `989cc33`. The conversation ends with a note
where GitHub puts its own when the reader cannot write, one sentence per reason: *Sign in to
comment, review or merge* with the link when the server has sign-in; *this server has no
sign-in* when it has none; *writes are not turned on for this repository* when a signed-in
reader still cannot write. Nothing while `/api/me` has not answered, so the note cannot flash
before a signed-in page settles — a mutant without that guard fails the first of five new
tests. Looked at live both ways: signed out, the note; with the current session's cookie, the
box and five reply and resolve buttons.

**What it says in general.** A capability the page does not have must be *named* on the page,
not implied by absence. "Show less" is how a stale session, a repository outside the writes
list and a server with no App all look identical to the person, and only one of those is
theirs to fix. The same rule the coverage banner learned the day before: silence is a claim.

---

### A third read: the coverage banner said "complete" four ways while hiding a change, and the first pass's own fixes had five holes

**2026-10-24, the owner asked for "one more review, fix all your bugs".** Four fresh readers
over what the second pass had only skimmed — the reader's internals, the auth and review
helpers, the page's components, Core's catalogs and coverage — and one over the ten fix commits
themselves. Everything below was checked against the code before it was fixed, and every rule
added is pinned by a test that its own mutant fails.

**The coverage banner lied four ways** (`Coverage`, `StDeclarationTable`), each a changed line
reported as shown by a drawing that drew nothing of it:

- A pragma between a row's comment and its declaration (`// the cap` / `{warning 'check'}` /
  `Limit : INT := 5;`) sits inside the row's span, which runs from the comment; the modified row
  vouched for it. With an attribute pragma above, `Flush` even *claimed* the line, so it was not
  listed as unshown either. **Fix:** `Coverage.Table` refuses any line the table lists as unshown
  on that side (`MemberTable.UnshownBefore` carries the older side's), and a row claims its
  attribute lines, its comment lines and its declaration — never the range between.
- A comment on a section or end keyword line (`VAR_INPUT // safety`, `END_VAR // …`) and on a
  method header with no return row was claimed as framing; the STRUCT line checked its comment,
  the others did not. `ClaimKeywordLine` checks for all of them.
- An enumeration claimed every line from its header to `END_TYPE` while drawing only value
  lines: a pragma on a value, a comment no value took, a comment on the header were "shown".
  Claimed line by line now.
- A placeholder network — an IL network in a ladder, an SCL compile unit in a LAD block — holds
  no elements, so its fingerprint is its title and language; two versions with different
  instructions compared as Same and the span was vouched for. Such a network vouches for nothing.

**The first pass's fixes, re-read:** the S9 log fix named the valid repository and then printed
the same decoded text through `Request.Path` (every refusal line spells the path now); a block
comment opened on a directive line (`{attribute 'x'} (* why:`) hid from the new tokenizer state
because the directive claimed the line first; a suggestion that replaced line 1 dropped the BOM
the fix had just promised to keep; the expired-snapshot banner was raised and the poll went on
every thirty seconds; a viewed and folded file, once "wanted", re-read its drawing on every push;
the commit comment box was not keyed by its slot; a "used by" answer cut by a slow minute was
cached as if cut by the project's size.

**The reader and the host:** GitHub's secondary limit (a 403 with `Retry-After` and a remaining
count that is not zero) read as "not found", so the page said "not visible to you" where the
threat model promises the rate-limit words; the writer had mapped it right from the start.
Labels and milestones were read one page deep and the write gates refused the 101st by name
("this repository has no label X"); five pages now, and a cut list does not gate. Every
anonymous request to a public repository the App is not installed on paid a JWT-signed GitHub
call, and the per-repository gates grew without bound; "not installed" is remembered for a
minute and the gates are striped. The `/pulls` counts swapped the state by substring, inside a
quoted value too. A `versio:` tag with a `>` in its payload survived `Strip`. A search over
GitHub's 256 characters was "GitHub could not be reached". The rate limiter partitioned on the
raw session id. `X-Forwarded-Host` was never trusted even from a trusted proxy, and a sign-in
refused for a wrong `redirect_uri` logged nothing that said so. A GraphQL answer with a null
connection or a null commit author threw and failed the whole list. The threat model and
`Documents.cs` still said "never HTML" a week after the web-page tab served it inline under a
sandbox.

**The page:** the peek kept its first path when a second link was clicked (keyed now); the pull
request search box did not follow a filter click; the Documentation drawer could not be opened
at phone width; a permalink on the Conversation tab never scrolled on load; the new pull request
form kept a draft's branches over the link's; four GitHub URL shapes landed on NotFound or asked
the server to draw a folder; Close and Reopen never told the page the snapshot was stale; the
milestone select snapped back on pick; three popovers read shadow tokens that do not exist;
"60 minutes ago" at the unit boundaries; a block with members was never lit as the current
object and folded its members on a second click; `Avatar` promised a fallback it did not have;
seven relayed URLs reached an `href` with no scheme check; the sidebar drew labels unlike
everywhere else; the reaction picker had no Escape or outside click; and "Discard" and "Ask"
where GitHub says "Abandon review" and "Request".

**Not done, said in FINISH.md:** an operand's comment, address or flags changed alone still
compares as Same inside a ladder network (the model does not carry them: the element-anchor
work); the desktop's `DiffView` colours the second line of a block comment as code; avatars on
GitHub Enterprise; a device's slots ordered as text past nine; a cross-folder rename marked in
the explorer only when the caller passes the old path, which the web's compare does not.

---

### A second read of the finished web page: every instance call linked to the first method of that name, and a commit comment's key lived only in React state

**2026-10-24, the owner asked "are you done with the web page? review again your work".** A
second read of the 36 commits on `VersioWeb`, as if somebody else had written them, with the
suites green at 1,905 / 439 / 181. Two defects, neither of which any test had seen.

**An identifier after a dot linked to an object.** Step 3c's `CodeHtml.AppendLine` asked the
resolver for every plain word, and `ObjectIndex` keys methods and properties by their bare name
(`Start`, `P_Active`), so `pump.Start()` in any body linked `Start` to whichever object called
`Start` sorted first by path — a silently wrong link in every file with an instance call, and
the "used by" search counted the same calls as uses of that method. The fixture manifests show
how common the collision is: `RefrigeratorControl` has ten names exported more than once
(`P_Active`, `Get`, `P_ClassName` …). **Fix:** a word after a dot is a member of what precedes it
and is never linked (`CodeHtml.IsMember`). The first version applied the same rule to the
"used by" search and emptied every method's list; the search counts dotted words by name now
(the block-comment bullet below). Mutation: with `IsMember` returning false, `ObjectLinksTests`
fails on the new test. What is still not seen is a local declared with an object's name;
FINISH.md's deferred list says so.

**The commit comment box held its operation key in React state.** Every other comment box keeps
its draft and its held key in session storage (`ReviewWrite.readDraft`), which is what makes a
retry after a reload or a dropped connection the same operation to the server. `CommitCommentBox`
(Step 3b) re-implemented the key rules with `useState`, so an answer of "unknown" followed by a
reload minted a new key and the retry was a second comment — the one failure the ledger exists to
prevent, in the one box written without it. **Fix:** the box reads and writes the shared draft
store under its own slot, and has CommentBox's "Edit text". Mutation: with the write to storage
removed, the new `CommitPage` test (post → unknown → reload → "Try again" sends the same key)
fails.

**Then three independent reads of the whole branch** (server, page, Core and docs), each told to
report only what it had verified in the code, found more — the list below is what survived a
check against the code, with the mutation each fix was pinned by:

- **A retry after a lost delete-branch or restore-branch answer was a 500 forever.** A retry
  reconciles *before* any precondition runs, and both reconciles read the branch the
  precondition had set (`branch!`). The person was stuck behind "Try again" after one lost
  answer. The reconcile reads the merge state itself; the original code fails the new retry
  test with a 500.
- **Opening a pull request from a fork could never be reconciled.** `FindCreatedPullAsync`
  prefixed `owner:` onto a head that was already `bob:feature`, asked GitHub for
  `acme:bob:feature`, and listed nothing — so after a lost answer the form stayed locked while
  the pull request existed. The fake writer matched on the raw head, which is why the existing
  reconcile test could not see it.
- **A suggestion applied to a file that is not UTF-8 would have rewritten the whole file.**
  `Encoding.UTF8.GetString` puts U+FFFD in place of every bad byte and the commit went to the
  head branch as is — the exact noise `Suggestions.cs` says it exists to avoid, and a loss of
  the characters. The bytes must round-trip or the suggestion is refused; a BOM round-trips.
- **An anonymous probe with the forgery headers cost a GitHub read per request on a write
  route**, because access was resolved (the repository's visibility, uncached for a private
  one, under the App's token) before S1 refused the request, and the per-session limiter has no
  session to count. The session is asked for first; the signed-out test asserts no GitHub read.
- **`ActionButton` held its key while its payload flipped.** Resolve answered "unknown"; the
  poll said resolved; the same button offered Unresolve and sent the old key with the opposite
  payload. A key is held for one path and one payload now, as `MergeBox` already did.
- **No review-thread permalink ever matched.** `anchorOf` accepted `issuecomment-9` and
  `pullrequestreview-1` but not `discussion_r7`, GitHub's shape with an underscore; and the move
  to the comment's file dropped the hash on the way. Found by the first test that used one.
- **Identifiers in the body of a `(* … *)` comment were linked and counted as uses**: the
  tokenizer is line-at-a-time and stateless, by design, for diff hunks; whole files are drawn
  and searched here, and `_M_EvaluateCommands.st` in the fixtures has commented-out code naming
  a method. A `TokenState` is carried between lines by the callers that see the file in order.
  And the dot guard above, applied to the search, had made "used by" empty for every method and
  property — the only way a method is used from outside is `x.Start()`. The search counts the
  dotted word and says it searches by name.
- **The "used by" search read the XML layer as typed**, where the embedded ST's comments are not
  comments, and read both layers of every two-layer object; a cut answer was never cached, so a
  project over the bound re-read up to 1,500 blobs on every view.
- **A tab left open past the snapshot's life froze**: `usePolled` swallowed every error. A
  viewed file's drawing was re-read on every fold. Tags and commit comments were cut at a page
  with nothing said. `/pulls` counted "closed" as "merged" under an `is:merged` query. A project
  with no manifest showed "Vendor: Codesys" on both hosts. Two anchors with one id threw instead
  of refusing. Three write exits had no audit line. Fifteen CSS rules nothing emitted, and one
  component's marks with no rules.
- **Not a defect, and kept as a test:** a guard against a nested `FragmentHost` handling a click
  twice survived its own mutation — the portal's React parent is the host's fragment root, so
  the drawing's handler never sees the comment's click. The guard came out; the test stays.
- **The docs:** ARCHITECTURE still said "read-only" and "the viewer only reads", sized the server
  at 2,700 lines (12,600), counted 375 tests (441) and listed four routes nowhere; STATUS was
  dated 2026-09-14, sent the next session to item 2, and gave counts that disagreed with its own
  table; FINISH had §A items 3, 4 and 7, the husk row, nine D-table rows and all of Step 4 open
  while STATUS said Steps 0–10 had landed.

**Also on the way:** `FragmentHost`'s unfold click handler had been pasted three times over in
Step 3's commit (dead, harmless, and nothing noticed); one comment still said "merge request".

**What the pass cost and what it was worth:** three agents, about a million tokens between them,
and most of a day; the four defects at the top of this list are each a person losing a write or
a file, and none had a test that could see it. **The rule:** a box that posts is `CommentBox`,
or it is built on `CommentBox`'s store; a reconcile never reads what only a precondition sets;
a cut list says so; and a review's claim is checked against the code before it is fixed — one
of the eleven page findings was wrong, and the fix for it would have been dead code with a
confident comment.

---

### The `PerformanceTests` flake: a resolution cached after its single flight ended, so a request in between resolved again

**Seen four times, 2026-10-19 to 2026-10-23, in full `Versio.Web.Tests` runs under load;
closed 2026-10-24 (FINISH.md Step 10).** `A_page_load_asks_GitHub_for_each_thing_once` sends a
page load's four requests at once and pins "resolve" at one GitHub call. One run in several
counted two.

**Cause: the cache was written after the flight, not inside it.** `Loader.ResolveAsync` checked
the memory cache, joined a `SingleFlight` keyed on the reference, and wrote the cache once the
flight returned. `SingleFlight` retires its key in a `finally` the moment the shared task
completes — so between that and the caller's `cache.Set`, a fourth request found neither the
flight nor the cache and started a second resolve. A window of microseconds, which a loaded
machine widened into one failure in several runs; the suspect named in STATUS each time it was
seen, never proven because nobody read `SingleFlight`'s `finally` beside the caller.

**Fix:** the cache write moved inside the flight's factory, before the shared task completes.
Nothing else changed; the test is unchanged and is the measurement (ten consecutive class runs
after the fix, in Step 10's commit).

**The rule:** a single flight hands off to a cache only if the cache is written before the flight
lets go. "Suspected, not investigated" written four times in STATUS was the cost of leaving it.

---

### The pull request's Files changed was a flat list of 85 names, and the tree it needed already existed

**2026-10-23, the owner's real pull request.** Files changed listed 85 paths with no folders, no
project and no object: eight rows read `.folder_meta.json`, four names appeared twice (a method of
two function blocks), and nothing said which block a method belonged to without hovering. The
desktop's Project panel shows the same change as a tree with A/M/D marks and roll-ups.

**Cause: the review's inventory was built from GitHub's file list and handed to the page as it
came.** `RepoExplorerBuilder.Build` — the desktop's tree — took no change list on the web, and
the one compare route answered a flat list too (`docs/web/PLAN.md`'s "compare as marks on the
tree" was planned and never built). The page had nothing to group by.

**Fix (Steps 3 and 8):** `RepoExplorerBuilder.Build(files, projects, options, formats, changes)`
marks the tree with a change list, rolls folders up and lists deleted objects struck; the web's
`/tree?base=` and the review's `FilesTree` draw it; every file is stacked under a sticky header
with the object's name and where it lives. The tree was always the desktop's; the web had thrown
the structure away at the list boundary — the same shape as the 2026-10-23 renderer-choice finding
below, one file along.

---

### A Markdown link rule silenced every type link on the web page, and no page test clicked one

**2026-10-24, writing Step 3c's peek test (docs/web/FINISH.md).** The first test that clicked a
member table's type link on the review page found it did nothing. `FragmentHost` sends both a
Markdown link (`data-href`) and a type link (`data-link`) through one `linkTarget`; Step 9 (the
same day) gave that function the desktop's Markdown rule — a relative link stays in the viewer
only when it names a `.md` — and from then on a type link to `FB_Pump.st` resolved to nothing.
Every type link on every drawing, the feature the owner had followed through the MTP types on
2026-10-05, was dead for a few hours of commits.

**Cause: one function, two rules, and the second rule's test covered only the first caller.**
`fragment.test.ts` pinned `linkTarget('/src/a.st', …)` to null as the Markdown rule — right for a
document, wrong for a type link — and nothing on the page clicked a `data-link`. The rule was
"tested" and the regression was invisible.

**Fix:** the path resolution is one private function; `linkTarget` applies the Markdown rule
over it, and `objectLink` applies none. `FragmentHost` uses `objectLink` for `data-link`;
`fragment.test.ts` pins both; the peek test clicks a type link on the review page.

**The rule it restates:** a change that narrows a shared function needs a test per caller, not
per function. The AGENTS.md double pass asks "what did I change that nothing is watching?"; the
answer here was every caller but the one the change was for.

---

### The web drew a library's catalogs and empty objects as raw metadata: the renderer choice was two copies, and the second had lost half its arms

**2026-10-23, the owner's FBS library pull request, on the web review page.** `Communication
Manager`, `OPC UA Server` and `Libraries.json` drew as `__V3_META__` JSON followed by archive
XML. The app draws the first two as the one-line note *This object stores no settings of its
own* and the third as the library manager's table. The owner: the scripts were meant to be
reused, which is why the web and the desktop share a repository.

**Cause: the decision of what a row draws existed twice, and only one copy had been kept up.**
`ShellViewModel.BuildRenderedContent` chose in this order: project card, a reference / task /
device row's catalog or card, Versio's YAML catalogs, the vendor's own catalog files, a member
table (with the husk rule, and the block's ladder, body or whole file underneath), a ladder, the
empty-object note, Markdown, the source, plus the web-page chip. `ViewService.Rendered` chose:
table, ladder, note, Markdown, source — and `RenderedCompare`, the path a pull request takes,
chose table, ladder, source: no note at all, no catalogs, no cards, no husk rule, no page, and
under a table whose format knows no body (a TIA STL block, a TwinCAT graphical POU) nothing where
the app draws the whole file. `ViewService`'s own remarks said "mirrored, not shared; moving it
into Core is later", and `ViewServiceTests` claimed to pin "the desktop's order" while running one
host. Every suite was green through a year of drift.

**Fixed by making the choice Core's** (`RenderChoice.Decide`, `src/Versio.Core/Projects/RenderChoice.cs`):
given a node, a reader and the registered formats it returns what is drawn, from which layer, the
parsed model, a `Reparse` for the committed or older copy, what sits under an interface table, and
the notice over a native file no model draws. Both hosts call it; each keeps its drawing. The
catalogs and cards got Core HTML renderers in the `MemberTableHtml` pattern (`CatalogHtml`,
`Cards` / `CardHtml`), the FBS forms read through the formats' `TryParseLibraryCatalog`,
`TryParseTask` and `TryParseDeviceCatalog` the web had never called, a `.html` answers
`kind: "webpage"` and `/raw` serves it under a `sandbox` policy, and whatever still falls to
source because no model exists says so — *Versio has no drawing for this Visualization yet; this
is its file as the vendor saved it* — rather than showing silent XML.

**The pinning test the claim needed: both hosts over every fixture row.**
`RenderChoiceParityTests` (`tests/Versio.App.Tests`) opens the real shell on a temp repository
holding every fixture project (the two CODESYS mirrors, the FBS project, the TIA and TwinCAT
exports, a README and a web page) and the web's `ViewService` on a fake GitHub holding the same
files, selects every row on both, and compares what each drew. Its first run found four more
web-only defects the drift had hidden, each fixed under it:

| Row | Web | Desktop | Why |
|---|---|---|---|
| `Device`, `EtherCAT_Master`, `ET 200SP station_2`, `Devices` (device objects) | 404 | device table | `RepoModel.NodeFor` indexed objects by their files only; a device object's path is the device, its file the catalog beside it |
| `…tasks.yaml::MainTask` (a task row) | the whole task table | the task's card | the view cache key had the file shown and every blob, not the row: a task row and its configuration share both, so the row got the configuration's cached answer |
| FBS `Task Configuration.taskconfig.xml.v3` | raw XML | the task table | the web read an object's own layers only; the configuration's table comes from its children's files, which the desktop reads from disk |
| grafted reference / slot rows | 404 | catalog / card | the same index gap as the device objects |

Project rows, device objects and grafted rows are indexed by their path now; the key carries
the row; every child's first file is read with the object's layers and is in the key, since a
changed task changes the table.

**Evidence.** `RenderChoiceTests` pins every arm with a stand-in format; `CatalogHtmlTests`,
`CardsTests` and the extended `RendererAllowlistTests` pin the renderers; `RenderedKindsTests`
pins each kind the web answers, the sandboxed page and the tree's grown node;
`RenderChoiceParityTests` runs both hosts. The mutation checks and the suite counts are below
the entry's date in STATUS.

---

### The coverage banner on real code: 55 of 85 files "may not show every change", and the drawing was right

**2026-10-23, the owner's `CodesysStandardLibrary` pull request #3 in the real-pull-request
preview.** Review coverage (docs/web/PLAN.md, C4) decides per changed line whether the drawing
vouches for it, and the page says "The drawing may not show every change in this file" with each
gap. On this pull request — an FBS library, 85 files — the review dialog counted 55 files with
gaps. `FB_Alarm` listed twelve "changed, but not shown as changed" lines; `Symbol Set`, a new
780-line file drawn as a table with every row marked A, said "lines 1–780: changed, but not shown";
`MoveHome`, whose header went `METHOD` → `METHOD PUBLIC`, reported the same edit twice ("as it
was", "as it is") and its empty member table added "1 declaration change is not shown in this
table (line 2)"; `PersistentVars` flagged two comment lines above two new rows.

**Every one of those was the rule being right about the letters and wrong about the drawing.**
The rule as written vouched for a declaration line only when every token on it was a field the
table displays, and for no XML line at all, because XML is mostly markup. So:

| What changed | What the rule saw | What a reviewer saw |
|---|---|---|
| Twelve declarations re-aligned with tabs | lines changed under rows reading unchanged | the same cells, twelve warnings |
| A new file, every row A | 780 XML lines, none a field | a complete table called partial |
| `METHOD` → `METHOD PUBLIC` | one line removed, one added, neither a row | the same edit listed twice, plus the table's note |
| `// SCADA` moved with its row into `VAR_IN_OUT` | a comment line outside the row's span | a drawn comment called hidden |
| `VAR_IN_OUT` opened, a blank line removed | lines no row owns | framing called hidden |
| `STRING(GVL_Alarm.DEFAULT_DESCRIPTION_LENGTH)` | `(` splits a token the parser kept whole | a shown type called hidden |

**Fixed by six rules, in Core only** (`Coverage`, `StDeclarationTable`, `MemberTable.Structural`,
`MarkupLines`, `LadderDocument.Structural`; the packs declare their framing; `ViewService` and the
page were not touched, and the `gaps` contract is unchanged):

1. A file entirely new or deleted that anything drew is complete (`Coverage.Check`).
2. A row drawn A / D / M vouches for every line it spans, pragma chips included; an unchanged row
   vouches for its lines when their text holds the same words (spacing, a comment moved from above
   the declaration to its end). The token-leftover test is gone — the table draws pragmas as chips
   now, so a row's span holds nothing it does not draw.
3. The declaration's framing — the header (when it says nothing beyond its keywords), `VAR` /
   `END_VAR`, `END_TYPE`, blank lines — is declared by the parser and vouched for by the table.
   `HeaderResidue` no longer includes the keyword or the modifiers; the cost, stated in its
   remarks, is that an access modifier changed from one value to another is not reported.
4. A line edited in place is one gap, named as it is now (`Coverage.Check` groups the diff into
   edits; the older side is listed only where it is all there is).
5. An XML table's framing is every markup-only line outside its rows and outside the
   implementation (`MarkupLines`: tags and spacing, at most a GUID; text content is a value and
   stays a gap). The CODESYS and TIA tables declare it; the FBS `(* METADATA *)` / `__V3_META__`
   headers and the closing keyword line are framing too.
6. `Coverage.Ladder`: every network's span on the sides it is drawn on, plus the ladder parser's
   framing (outside the networks, outside the Interface, which is the table's). ~~**Not yet wired in
   `ViewService`**, which another session holds; the hand-over note names the two lines.~~ Wired
   on `VersioWeb` when the branch was merged (2026-10-23): the ladder view and the ladder under a
   table both vouch, and `PullAnchorTests` pins a removed network's file as complete.

Also moved: a row's span now starts at the comment lines above it that became its comment cell
(a review comment on that line lands on the row), and `SafeXml.LinesOf` counts the newlines in a
text node, because a CODESYS declaration is one text node of thirty lines and its span ended on
the first.

**Evidence.** On the same dump, re-made with `tools/web/dump_pull.py` into this session's
scratchpad: 77 of 85 files complete, 8 partial, down from 30 and 55. `FB_Alarm`, `Symbol Set`,
`MoveHome` and `PersistentVars` read complete. The eight that remain are, every one, a `///` doc
comment above an object's header edited, added or removed — `ResetFaults`, `Acknowledge`,
`EvaluateInputs`, `Outputs`, `E_AlarmState`, `FB_Logger`, `Unregister`, `E_LoggerError`. The doc
comment is a line the table has no cell for, so that gap is the truth; drawing the object's doc
comment as a caption would close it, and is a follow-up. A ninth, `GVL_Logger`, was a comment
moved from above `BATCH_CAPACITY` to the end of its line, which is why rule 2 compares words
rather than spacing. Twenty-four mutations (each rule taken out, each pack's exclusion dropped,
the residue's keyword put back, the span not extended, the text newlines not counted) each failed
a test; three first "survived" by not compiling (`if (false)` is an unreachable-code error under
`TreatWarningsAsErrors`, and an unused local function is another), and were rewritten as mutants
that build. `PullPreview` takes `VERSIO_WEB_PREVIEW_PORT` now, because the owner's own fixture
preview held 5199 while this was measured.

---

### The web drew a compared source unlike the app: struck lines, no folding, a removed header

**2026-10-23, branch `PrReview`, the owner's `CodesysStandardLibrary` pull request #3** in the
new real-pull-request preview, beside the app. `MoveHome.meth.st` on the web's Rendered tab
drew every edited line twice (old text struck above the new), a struck `METHOD MoveHome` above
the body, and the whole file. The app drew each line once, nothing above the body, folded. The
owner: the scripts were meant to be reused, which is why the web and the desktop share a repository.

**One cause, three symptoms: the hosts shared the layout and not its last steps.** Both build
rows with `StSourceLayout` (`Lay`, `Mark`, `Body`), and then each did the rest itself:

| Step | Desktop | Web, until now |
|---|---|---|
| Unified leaves removed lines out and marks a pure deletion (2026-10-22) | `DiffView.Unified` | not done: `SourceHtml` drew `SourceRemovedLine` struck |
| Fold to git's context unless *Show full file* (2026-10-22) | `DiffView.Rebuild`, the pane's box | no fold, no box |
| Under a table, hide removals before the old body's start | `MarkSourceChangesAsync` | `AppendInterfaceCode` called `Mark` without it |

The first two arrived on the desktop a day earlier, in the session building it, and
`SourceRemovedLine`'s own remarks recorded that the web still drew it struck. Nothing failed:
each host had its own tests.

**Fixed by making the steps Core's** (`2e19229`, `52752f0`, `cf9eb70`). `StSourceLayout.Unified`
is `DiffView`'s rule moved with its reasoning, its answer carried on the row
(`SourceCodeLine.RemovedNear`) rather than a dictionary only one renderer could read. `DiffView`
and `SourceHtml` call `Fold` and `Unified` in the same order. The web asks `format.CodeBody` for
the old body's start, as the desktop does. *Show full file* is on the review and compare pages,
clear by default as on the desktop, and carried as `full=1`.

**Evidence.** `A_unified_comparison_draws_the_rows_the_desktop_draws` checks the page's file
lines against `Unified(Fold(rows))`, which is what `DiffView` sets as its items.
`A_method_whose_header_changed_draws_no_removed_header_above_its_body` (unified and side by
side) uses the FBS fixture. Eleven mutations each failed a test. One first survived: removed
lines kept in `Unified` drew nothing on the page, since the page can no longer draw one, so a
rows test was added where the rule lives. The mutations ran in a separate worktree, because
another session was building this checkout at the time.
The combined tree then ran every suite: Core 1,837,
Web 364, vitest 128, the inner loop green. Two loaded full runs of `Versio.App.Tests` each had
one different failure (`GitPanelUxTests` Ctrl+Shift+P at 20 s, `CommitWhileIdeHoldsProjectTests`
at 43 s), in areas this did not touch, with two suites running at once. Both classes then
passed alone, 12/12.

**Rule worth keeping:** when two hosts draw one thing, the decision of *what* is drawn belongs
in Core and the host only draws. A rule that lives in one renderer is a rule the other one
does not have, and neither host's tests will say so.

**Not done here:** member tables and ladders were already shared (`MemberTableHtml` and the
desktop's table share `MemberTableDiff`; the desktop's ladder pane hosts `LadderHtml`). The web
viewer's coverage banner ("the drawing may not show every change") is review-only (C4) and has
no desktop counterpart by design.

---

### A POU with no variables drew as source while every other POU drew as a table

**2026-10-23, branch `PrReview`, the owner on `FsLibrary.fbslib`'s `Main`** (`VAR END_VAR`
and two calls): *"I want a POU even if it doesn't have anything to render as the other POUs do
… just show the empty table."*

**Cause: a rule that had outlived its reason.** On 2026-10-14 a table with no rows was ruled a
"husk" and sent to the source layout. The harm then was column headers standing where a
parameterless method's code should have been. Since then a code block's interface has always
drawn its code underneath it (`InterfaceRender`), so that harm cannot recur. The rule only
made one POU look unlike the rest.

**Fix.** The rule now exempts an interface. A type or a variable list with no members still
falls through to its source: the owner asked about POUs, and an empty table over nothing is
still the husk the old rule described. The web viewer never had the rule, so it already drew
the empty table. The 2026-10-14 cost, a block whose members were all deleted shows its struck
rows only in Raw, is gone for code blocks: they now draw the table with the struck rows.

**Evidence.** `A_program_with_no_variables_is_still_an_empty_table_over_its_code` (App) drew
`SourceRender` before the fix. The 2026-10-14 pin is now
`A_parameterless_method_is_an_empty_table_with_its_code_under_it`, which asserts the code under
the empty table, the part that was the point.

---

### An awaited Explorer refresh could return before the tree was built

**2026-10-23, branch `PrReview`, found while testing the entry above.**
`From_the_explorer_a_link_selects_in_the_explorer_and_stays_there` failed 2 times in 8 isolated
runs on a commit without that change. In a full-suite run, the new catalog test found
`Explorer.Tree` empty straight after `await Explorer.RefreshAsync()`.

**Cause.** `ExplorerViewModel.RefreshAsync` bumps a generation, and a refresh that a newer one
supersedes stops early by design. But its caller got control back at that point, with nothing
built. Showing the panel, or a file-watcher event, starts the newer refresh. `OpenObjectAsync`
awaits a refresh to find a type link's target, so a link clicked at the wrong moment found
nothing in the Explorer and jumped the user to the Project panel. That is a real defect, not
only a test's.

**Fix.** Each call follows a chain to the latest refresh before it returns. The early stop is
kept, so a superseded listing still never overwrites a newer one.

**Evidence, a measured rate, not a proof:** 1 failure in 22 isolated runs of the two tests after
the fix, against 2 in 8 before. A full App suite run on HEAD plus only this session's files is
recorded in the commit. The residual failure was not reproduced in the next 12 runs, so it is
noted here rather than explained.

---

### One appended newline, two defects: a clean catalog "changed", and a nameless Explorer row

**2026-10-23, branch `PrReview`, the owner's `FsLibrary.fbslib` in the dev window.** With the
library rows fixed (the entry below), the clean application manager still read *Changed since
the last commit, but not in its rows*, and Raw showed nothing changed. The owner also asked why
the Explorer drew an empty row at the root, between the project and `.gitignore`.

**One cause.** `GitCliClient.RunAsync` rebuilds stdout from `OutputDataReceived` line events and
appends `'\n'` to each line. `ShowAtHeadAsync`'s own remarks already warned that a file without
a final newline cannot be told from one with it. Two callers had not taken that in:

- **The catalog marking compared text.** `MarkCatalogChangesAsync` normalised CRLF but compared
  HEAD's `…}\n` with the disk's `…}`. CODESYS writes `Libraries.json` with no final newline, so
  every clean File-Based Storage manager took the "changed outside its rows" note. Both sides
  are now compared as lines, with the disk side given the final newline the HEAD side always
  has. The classic mirror never showed it, because its YAML is committed with a final newline.
- **The working-tree listing kept the newline as a path.** `ls-files -z` ends with a NUL, so
  the appended `'\n'` came after it as a field of its own, and `WorkingTreeListParser` skipped
  only empty fields. The Explorer drew `"\n"` as a file at the root of every repository. A
  whitespace-only field is now skipped, and no Windows file has such a name.

**Rejected:** making `RunAsync` byte-exact. It is the right long-term answer, but every git
reader in the app goes through it, and the two fixes here are local and tested.

**Evidence.** `An_unchanged_catalog_with_no_final_newline_says_nothing_changed` (App, through the
Explorer as the owner saw it) failed with the owner's exact sentence before the fix.
`The_newline_the_runner_appends_after_the_last_nul_is_not_a_path` (Core) failed before its fix.

---

### An unchanged library manager said "3 changed", over four rows with no name

**2026-10-23, branch `PrReview`, the owner's `FsLibrary.fbslib`, seen in the dev window.** The
application's Libraries table read *Since the last commit: 3 changed* while the tree and status
bar said no changes. Three rows were blank except for a struck-out namespace with an arrow to
another, `CmpOPCUAServer_Interfaces → CmpOPCUAStack_Interfaces`, and the fourth was blank too.

**Two defects, one hiding behind the other.**

- **The reader knew only placeholders.** The library's move to OPC UA added four
  `"$type": "ManagedLibrary"` references, which CODESYS adds as hidden system libraries. They
  name themselves in `libraryId`, a field `CodesysCatalogs.Reference` never read, so all four
  came out with an empty name and no resolution. Now `libraryId` is the name, and it is split
  into title, version and company by a port of the classic export's strict
  `parse_managed_name`. So `"CmpOPCUAServer Interfaces, * (System)"` gets no version rather
  than `*`, the same row the classic YAML gives.
- **The comparison paired by key and kept the first.** `CatalogDiff.Mark` put the baseline in a
  dictionary with `TryAdd`, so every repeat of a key was compared with the first baseline row of
  that key. With four rows sharing the empty name, an identical file reported the second, third
  and fourth as namespace changes. Keys are now paired by occurrence, the second with the
  second. The first occurrence keeps its own key, so an ordinary catalog is compared exactly as
  before. Names can repeat for real, too: nothing stops two references sharing a display name.

**Evidence.** `Rows_sharing_a_name_are_paired_by_occurrence_so_an_unchanged_file_marks_nothing`
(Core) goes red with the current side left unpaired.
`A_managed_library_is_named_by_its_library_id_and_split_as_the_classic_export_splits_it`
(CODESYS pure) goes red with `libraryId` unread. The FBS-to-classic parity test still agrees.

---

### A File-Based Storage library's device tree was filed under POUs

**2026-10-23, branch `PrReview`, the owner's `CodesysStandardLibrary` (`FsLibrary.fbslib`).**
The Project panel's root read `00_Template`, `01_Core` and a `POUs` folder side by side, and
`POUs` held the device `CODESYS_Control_Win_V3_x64` with its whole application (`Main`,
`TestAlarm`, `TestLogger`, the tasks), the library manager, `Project Settings` and `ProjectInfo`.
That is backwards: CODESYS draws the library in its POUs pane and the test application in its
Devices pane. In the same look, `PersistentVars` and `Symbols` drew as raw archive XML on the
Rendered tab.

**Cause of the grouping.** `ProjectTreeBuilder.GroupPoolObjects` guesses: when foldered objects
sit beside root-level ones, the root-level ones are the pool. That is true of the classic mirror,
where the device tree is directories and the pool is files at the root. A File-Based Storage
folder is the other way round. Its device is a root-level `.device.xml.v3` with everything under
its `^` folder, and its pool is the user's folders plus a few root files. So the guess filed the
device under POUs and left the real pool loose.

**Fix.** The vendor says which pane an object is in, and the builder no longer guesses when it
is told. `ProjectObject.View` is new, nullable and left out of the JSON when null, so no existing
manifest serialises differently. `CodesysFileBasedLayout.ViewOf` names `Devices` for a root
`.device.xml.v3` and anything under its `^` folder, and `POUs` for everything else. When any
object names a view, `ProjectTreeBuilder.GroupByView` files each top-level object under it and
`GroupPoolObjects` is skipped. Only top-level objects are moved, because a member belongs with
its owner whatever its own field says. The builder learnt no vendor word: `Devices` is a
constant in the CODESYS pack, and the classic mirror keeps the old guess because its manifest
names no views.

**Where Project Settings and the top-level `Libraries.json` go.** POUs. CODESYS's own tree for
this library, read through its scripting connection, lists the library folders and *Project
Information* apart from `CODESYS_Control_Win_V3_x64`. The device subtree holds only Plc Logic →
Application and the application's own objects. The application's `Libraries.json` is the
application's library manager and stays under it. The root one is the project-level manager,
which is the pool's, as the classic mirror's group already said (see *POUs* in this file). That
connection does not list the library manager or the project settings at all, so their place is
reasoned from being outside the device's `^` folder, not read off the IDE. **The owner should
confirm it against the IDE's own POUs pane.**

**Cause of the raw XML, and fix.** `CodesysMemberTable` tabled an archive-XML declaration only
when the body was an LD/FBD network list. A persistent variable list has an `Interface` and no
`Implementation` at all, so it fell through to raw. It is now tabled as the global-variable list
the IDE shows. That also covers a parameter list kept as `.gvl.xml.v3`. A visualisation has the
same shape, but its text is a bare `VAR_IN_OUT` block with no object keyword, which the parser
does not table. A guard on the table's kind was written to keep visuals out, and a mutation
check showed it could never fire, so it was removed. A test with a real parameter in a visual's
interface pins the behaviour instead. The symbol configuration (`.symbolconfig.xml.v3`) is new
in `CodesysCatalogs.SymbolConfiguration`: one row per option as CODESYS names it. GUID-typed
values are left out because they are CODESYS's references to its own generated objects. Lists
are counted, not listed, because the only configuration in hand selects nothing, so the shape of
a selected variable was never seen. The note under the table says so.

**Then the library moved its symbols to OPC UA** (its commit `77e424f`): `Symbols` went, and
`Communication Manager → OPC UA Server → Symbol Set` came, and the Symbol Set drew as 779 lines
of XML. `CodesysCatalogs.SymbolSet` now draws each published variable with its members nested
under it and the access each is published at. The file keeps three lists that it joins:
`SymbolItems` (the roots), `TypeEntries` (each type's member ids) and `AllEntries` (each member),
and a member of a structured type finds its own members by the type's name. A type that contains
itself is walked once per branch. An unticked member is drawn with "not published", because
ticking is what a change to this file usually is. The Communication Manager, the OPC UA Server,
Plc Logic and the Application have **no settings in their files at all**: each is an empty
archive element. They still show their raw XML, as Plc Logic always has. **Then they said so
(`06fedf4`):** `IPlcProjectFormat.HoldsNoSettings`, default false, is answered by the CODESYS
pack for archive XML with no text anywhere, and `EmptyObjectNote` is the one wording. The web
viewer draws it, and the desktop pane does too, from the same wording: an arm in the Rendered
choice after the ladder and before Markdown. It waited for another session's `ShellViewModel.cs`
edits to land. Deleting the arm turns
`EmptyObjectPaneFlowTests_an_object_with_nothing_in_its_own_file_says_so` red. The Application was expected to be empty and is
not: it holds boot-application, memory and signing settings, and still shows raw.

**Evidence.** `A_library_is_drawn_as_codesys_draws_it_the_pou_pool_apart_from_the_device_tree`
(CODESYS pure) uses the library's real file list. Setting `View = null` in `Describe` turns it
red, along with the fixture's tree test, which now reads `Devices → Device`.
`A_manifest_that_names_its_panes_is_grouped_by_them_and_not_by_the_pool_guess` (Core) goes red
when the builder is put back to the guess alone. The persistent-list and symbol tests each went
red with their branch removed, and so did the Symbol Set test with its reader unhooked or its nesting cut; the self-containing type's test went red with the branch guard swapped for a depth cap.

**Not done.** The App and web viewer were not looked at after the change: the tree's two new
levels and the two tables are tested, not seen. The dev build on `_attrcheck` was closed to
rebuild.

---

### An attribute change showed nowhere on the Rendered tab

**2026-10-23, branch `PrReview`, the owner's `CodesysStandardLibrary`.** `F_UpdateMeta.fn.st`
gained one line, `{attribute 'linkalways'}`, above its header. The tree said M, Raw showed the
line, and the Rendered tab drew the interface table and the code with nothing marked: no row
changed, and no sentence said anything had. A silent hide, the shape AGENTS.md calls a bug even
when every part is correct.

**Cause.** `StDeclarationTable.Clean` dropped every `{…}` pragma from the code so declarations
would parse, and nothing kept them. So an attribute on the object (`linkalways`) and one on a
single variable (`{attribute 'symbol' := 'readwrite'}` above `FB_Alarm.Scada`) were both
invisible, and a change to either one showed nowhere.

**Fix, in three parts, from a mockup the owner approved first:**

- **Attributes are read** in the neutral IEC reader, so CODESYS and TwinCAT get them alike:
  `MemberTable.Attributes` for the ones above the header, `MemberRow.Attributes` for a
  variable's own. Only `attribute 'name' [:= 'value']` counts as an attribute. A `{warning}`,
  an `{IF defined}` or a `{region}` is not one. An attribute waits for the next declaration
  across blank lines, as the compiler does, and a pragma written after a declaration's code
  belongs to the next declaration. A row's line span now starts at its first attribute, so a
  changed attribute marks the row and a review comment on that line lands on it.
- **They are drawn as chips.** The object's chips go in a strip under the title, and a
  variable's go under its name rather than in a column, because most variables have none.
  `MemberTableDiff` matches attributes by name and marks each chip added, changed (with the old
  value struck after it) or removed. A row whose only change is an attribute is a modified row.
  This is the same on the desktop and in the web fragment. Which attribute wins (FB_Alarm's
  block says `symbol = none` and Scada says `readwrite`) is CODESYS's rule, and Core does not
  apply it.
- **The safety net: `MemberTable.UnshownChange`.** The reader records every declaration line
  no row, heading or chip accounts for (a doc comment, a comment between sections, a pragma
  that is not an attribute), plus what the header says beyond what is drawn (`EXTENDS`, a
  modifier, but not a function's return type, which is its Return row). When that set changes,
  the table says "1 declaration change is not shown in this table (line 3)" with an *Open Raw*
  link. The two sides are lined up as sequences, so a line that only moved does not trigger it.
  This is deliberately narrower than the note the owner had removed on 2026-10-11, which fired
  for any change outside the rows, including the code below the table, which marks itself.

`Coverage.Table` vouches for a changed attribute line when its tokens are exactly the drawn
pragma's tokens, so a pull request review counts an attribute change as shown. A pragma
spelled another way (`{ attribute … }`) stays a gap.

**Checked by breaking it:** each of these, mutated, turns at least one Core test red: the
attribute clause in `Differs`, the `UnshownChange` assignment, the table-level
`CompareAttributes` call, the header residue, giving trailing pragmas to the next declaration,
claiming the opening lines of a block comment (this one stayed green until a test was added for
it), the row chips in `Coverage`, and the row chips in `MemberTableHtml`. After every restore the
file was touched and the baseline re-run green.

**Not covered by any test:** the desktop XAML (the chip template, the strip, the warning
border). The App suite drives the view model, not the templates. The dev build opened a copy of
the library with zero `ERR` lines; how it looks was left for the owner to eyeball. Also left
alone: a pragma on one value of an enumeration, which is claimed but not drawn, and a pragma
written after code on a line in the middle of a declaration that spans several lines.

---

### A block's properties sat among its methods, and a deleted one looked like any other

**2026-10-22, branch `PrReview`, the same `FB_Logger` once the fix below had put its deleted
properties back under it.** Two asks from the owner's screenshot: methods first, then
properties; and a deleted property drawn so it reads as deleted.

- **Order.** `ProjectTreeBuilder.ChildrenOf` sorted an owner's members by name alone, so the
  renamed properties (`Busy` new, `P_Busy` deleted) were scattered between methods. Properties
  now sort after everything else, each group by name; folders still come first. Keyed on the
  IEC word `PROPERTY` (`IecObjectTypeNames.Property`), so the neutral builder learns no vendor's
  type list, and a vendor's other method spellings (`METHOD_NORET`) and actions sort with the
  methods. No project-level row is a property, so nothing above the blocks moves. The web viewer
  builds its tree with the same code and gets the same order.
- **Struck name.** `TreeRow.IsDeleted` (the row's own status, never the roll-up: a folder holding
  a deleted method still exists) drives a trigger in the project panel's row template that
  strikes the name and dims it to secondary. The name's foreground had to move from a local value
  into the style, because a local value beats any trigger. The explorer is left alone: it lists
  files on disk, and a deleted one is not there.

**Checked by breaking it:** the ordering line reverted turns the new
`ProjectTreeMembersTests` case red; the trigger keyed to a value it never takes turns the new
`TreeRowChromeTests` case red. That one goes through the real `MainWindow`, because the flag
does nothing without the trigger that reads it.

---

### Deleted properties fell out of their block, and the Rendered tab ignored two of the pane's boxes

**2026-10-22, branch `PrReview`.** The owner, on a CODESYS File-Based Storage library
(`FsLibrary.fbslib`, 69 changed files), reported three things from two screenshots:

| Seen | Cause | Fix |
|---|---|---|
| Seven `01_Core/Utils/Logger/FB_Logger.fb.st^/P_*.prop.st` rows, badged D, as bare paths at the bottom of the project below Project Settings | A source project's manifest is built by `DescribeSourceProject` from the files **on disk**. A deleted file is not on disk, so it was in no manifest, and `WorkspaceTreeBuilder.UnmappedExportFiles` — the catch-all that keeps a changed file from vanishing — drew it as a path | `SyncViewModel.WithDeletedFiles` adds the paths git reports deleted (either side of the index) under the folder before the format describes it. The format is pure and places them under their `^` owner like any other member; the status already says D. The explorer is left alone: it lists what is there |
| Removed lines drawn struck, in place, on the Rendered tab's unified view — every edited line twice | Deliberate since 2026-10-10 (`SourceRemovedLine`'s remark), and the owner now wants them only in side by side | `DiffView.Unified` drops them from the drawn rows; the rows themselves are unchanged, so side by side, the web viewer and coverage still have them. The line now standing in their place carries a red rule and a tooltip with the count, because a deletion-only change would otherwise leave an M object with nothing visible. **Narrowed the same day:** first shipped on every removal, the rule sat above every edited line's green tint and said nothing the tint did not ("what is this red line?"), and it stopped where the line's text did. Now only a pure deletion (no added line on either side) gets it, and it spans the row |
| *Show full file* did nothing | It re-reads git with more context, which only **Raw** draws. The Rendered tab, which opens first, always drew the whole file | `StSourceLayout.Fold` folds runs of unchanged rows further than git's three lines from a change into a `SourceFold` row (unified and, after pairing, side by side). `DiffView.ShowFullFile` is bound to the box. Dividers never fold; a run of one is drawn, not folded; an unmarked file is returned whole |

**What changed for a reader:** with the box clear — its default — a compared file's Rendered tab
now opens folded to its changes, as Raw always did. Ticking it gives the whole file on both tabs.

**Checked by breaking it:** with `WithDeletedFiles` taken out of the tree build, the new
`SourceProjectTests` case goes red. `RenderedSourceTests` pins the template's `ShowFullFile`
binding, the missing removed rows and the rule, and the fold counts in both modes.

**Not touched:** the two `R …/.folder_meta.json` rows in the same screenshot are still drawn as
paths. They are CODESYS's hidden folder state, deliberately not objects (`IsObjectFile`), and the
unmapped-file rule shows them rather than hide a changed file; whether a folder row should claim
its own meta file is a separate question.

---

### An external review of the pull request review work found eight defects, three of them duplicate writes

**2026-10-20, branch `PrReview`, commits after `73817f4`.** A reviewer read all twenty commits of
the pull request review plan (slices 1–6) and reported eight findings; seven were reproduced
against the code, one (the ledger race) traced. Every one held. They fall into three shapes, and
the shapes are the lesson.

**The operation key stopped protecting a retry, three ways.** C5's whole promise is that one
click makes at most one write. (1) The page's write transport read a proxy's 502/504 page, or any
answer that was not this server's JSON, as a refusal and cleared the key, although GitHub may
already have done the write (reproduced with a 504). (2) The review dialog worked its target out
again on every render; after a pending review's submit succeeded with its answer lost, a reload
found no pending review and the retry went to `/reviews`, a different ledger action, so a second
review (reproduced). (3) Taking over a failed or unknown entry was a plain overwrite, so two retries
could both proceed — the first reservation was atomic, the takeover was not. Fixes: anything not
this server's own answer with an outcome is "unknown" and keeps the key; the review target is kept
with the key; `IOperationLedger.TryTakeOverAsync` is a compare-and-set (memory `TryUpdate`, Redis
one script on the stored text). **Rule worth keeping:** the key protects the operation only if
everything that identifies the operation — its route, its target, its ownership — is fixed when the
key is made, and only the server's own word ever releases it.

**Live GitHub data was taken as the snapshot's without proof.** (4) Thread placement and Raw used
GitHub's *current* lines with no revision check; after a push a comment written on the new head sat
on a different declaration of the old snapshot (reproduced). (5) Patches were accepted when the head
matched, but retargeting the base keeps the head and changes the diff (reproduced: `"pull"` chosen
over the compare). Fix: `PullReview` uses live answers only when head, base branch and base tip are
the snapshot's before and after the read; otherwise thread lines are dropped (listed, not placed)
and patches come from the snapshot's own compare. The C1 rule always said "a base move alone
invalidates the snapshot"; the code checked the head alone in two places.

**A check written for its siblings and not this one, and a promise GitHub does not keep.** (6) A
comment edit rewrote the body without the anchor tag it carried, so it fell off the drawing; the
edit now re-reads and re-writes the server's own anchor. (7) Update branch skipped the staleness
check merge and auto-merge make, so a retargeted base was brought in under a confirmation naming
the old one. (8) Auto-merge was described — on the page, in the writer and in PLAN — as a merge of
exactly the reviewed commit that any push turns off. GitHub turns it off only for a push by someone
*without* write access. It is now worded as what it is and needs the person's explicit yes to later
commits being merged.

**Why the earlier passes missed them.** Each slice had mutation checks, all killed — but a mutation
check only tests what the tests describe. None of these was a mutated line; each was a missing case:
a proxy in between, a reload after a lost answer, two retries at once, a base retarget, an edit of an
anchored comment. The review found them by asking what happens around the code rather than in it.
Evidence: the seven commits after `73817f4`, each with its reproduction as a test and a killed
mutation; Web 324 -> 334, vitest 92 -> 95.

**A second review, through `3cce5f9`, found three more; two were the first round's fixes left
half done.** (1) `cc53599` kept the review's target with its key, but the dialog's "needs words"
rule still asked whether a pending review existed *now*: an empty comment submitted on a pending
review, answer lost, reload, and Try again was disabled for good. (2) `55fa7f3` made a proxy's page
"unknown", but JSON `null` still threw on `.outcome` (the box stuck posting) and `{}` on a 200
still read as a refusal. (3) Separately, since slice 1, checks were read one page deep: 100 passing
runs and a failing 101st showed as all green, with no word that the list was cut. Fixes: the words
rule asks the saved target; `readWriteAnswer` states what is known (an outcome on a 2xx, an error
on a 4xx) and calls everything else unknown; checks are paged to 1,000 each and say when cut.
**Rule worth keeping:** a fix of "X is read from the wrong place" is finished only when every
other reader of X has been found and moved too — the target was moved for sending and left behind
for validating. Web 334 -> 337, vitest 95 -> 112.

**A third review, through `86b6b56`, found four, two of them carried over unfixed.** (1) A retry
held its key and route but went under the page's CURRENT snapshot: an approval that never reached
the server, then a push and a reload, would approve a commit the person never saw. Fixed both
ways: the page keeps the snapshot a key was sent under and retries under it, and the server binds
head and base into the operation's hash (another revision is another operation, refused). Keys
outlive snapshots, so an expired but genuine snapshot may still *settle* its own operation (a
read), never start one. (2) Moving between files saved one file's draft under another's: the
reused comment box carried its state into the next slot. Boxes, the review page and the new-PR
form are now keyed by their draft's identity. (3) Pending-review comments and every reconciliation
search read one page; "not found" is now concluded only from a whole list, and past the bound the
answer is "cannot tell". (4) Every GraphQL `errors` answer was read as refused; per the spec an
execution error does not prove nothing ran, so only a request error, or typed pre-execution errors
with nothing returned, are refusals now. **Rule worth keeping:** what identifies an operation —
its revision as much as its route — must be fixed when its key is made; and "not there" or
"refused" is a claim that needs the whole evidence, never the first page or the first error.
Web 337 -> 343, vitest 112 -> 118.

**Then a review of my own, through `81d214d`, found seven more** — the same three shapes again,
one level further out. Keys: the revision binding orphaned entries recorded before it (a deploy
away from a duplicate); the merge box used a fresh key per confirmation, so a merge that happened
came back as "not open"; and the server's own pre-write refusals (the limiter's 429, the kill
switch's 404) had no body, so the page's careful rule read them as "may have posted". Proof:
update-branch called any moved head "updated", someone else's push included. Cost and drift: checks
pages past the first were re-read every poll, the thread-revision check cost four calls a file view
(now read in the threads' own answer, exact and free), and the revision check existed twice. Each
fixed in its own commit (`059a886` to `7fafc5c`) with a reproducing test and killed mutations.
**Rule worth keeping:** a rule that classifies answers ("only this server's JSON is a refusal") makes
every answer this server gives a case of it — the server's own bodies have to be audited against the
rule, not just the client's reading of them. Web 343 -> 351, vitest 118 -> 120.

**A fourth review, through `2cf5b5f`, found three — each in a fix of the round before.** (1)
`059a886` made the limiter's 429 a refusal, and the page let a key go on any refusal: on a retry,
that released a key whose first attempt may have posted. A refusal of a retry proves only that the
retry did nothing. The server now marks a refusal `X-Versio-Settled` only where the ledger recorded
it as the operation's outcome (a precondition's, GitHub's), and one page rule, `releasesKey`, lets a
key go on a refused retry only when marked. (2) `78164e6` kept snapshots with new keys, but keys
saved before it had none, and `sentUnder` silently used today's; such an operation's revision is
unknown, so it is no longer retried at all — the person checks GitHub and resubmits by Edit. (3)
`0f092d3` checked an update's first parent and not its second, so any merge onto the head counted;
the second must be the base. **Rule worth keeping:** a fix that adds a new answer, a new stored
field or a new recognition rule has to be read against the old answers, the old stored data and
the near-misses of the new rule — each of these three was exactly that. Web 351 -> 354, vitest
120 -> 124.

**A second review of my own, through `864dac3`, found six, and the severity kept falling.** A
legacy failed entry could still be taken over and run on a newer revision (now: legacy entries
settle, never run); the settled mark was set on a fresh reservation, where the ledger may simply
have forgotten an earlier attempt (now: only for a key it already held); the merge box held one
key for all its actions (now: one per action); update-branch accepted the live tip of a
retargeted base; the refused-retry rule was tested at one of its five call sites; and ARCHITECTURE
and the threat model still described the old key rule. Each fixed in its own commit (`d2cd19a` to
`462b861`). **On the review loop itself:** the findings have moved from "every user, ordinary use"
(duplicate comments and approvals, rounds one and two) to sequences of several unlikely events
(data from before a deploy, a server restart mid-write, a retarget during a lost answer). The
branch has never been deployed, so the legacy paths guard data that exists only on test machines
and could be deleted rather than refined. More desk review now buys less than the real-GitHub gates
and the penetration test would. Web 354 -> 358, vitest 124 -> 129.

**Then the legacy paths were deleted rather than refined (2026-10-22, `63ca8e6`, `bb8c45c`).**
Three of the last two reviews' findings were about data from before a fix: ledger entries recorded
before revisions were bound, and page drafts saved before a key kept its snapshot. Each fix grew a
rule (accept the bare hash; then settle but never run it; then an "operation-legacy" refusal; on the
page, an "unrecoverable" notice and a disabled retry). None of that data existed outside test
machines, because the branch was never deployed. So the server compares a recorded hash with the
bound one and nothing else, and the page assumes what it always writes — a held key with its
snapshot. New tests pin the simpler rule: an entry with an unbound hash is op-mismatch with nothing
sent, and under an expired snapshot it is "reload", never "done". The settle-path test was written
because re-accepting the bare hash there first survived the mutation check. On the page, the two
reload-onto-a-newer-revision tests kill a key saved without its snapshot, in both the comment box
and the review dialog. **Rule worth keeping:** before deployment, compatibility with your own
unreleased data is a cost with no one to pay it back — delete it, and test the invariant that made
it unnecessary. Web 358 -> 358 (three cases removed, three added), vitest 129 -> 127.

---

### A pull request's "What changed" compared against the base branch's tip, so later commits on the base read as reverted

**2026-10-17, branch `PrReview`.** Found while planning pull request review for Versio Web
(`docs/web/PLAN.md`, *Pull request review*), before any of it was built: the MRs tab's "What
changed" opened `compareUrl(pull.base, pull.headSha)`, where `pull.base` is the base *branch
name*. The compare view resolves that name to the branch's current tip and runs a **two-dot**
tree diff (`ViewService.ChangedPaths`) between it and the head. GitHub's "Files changed" is
**three-dot**: the head against the *merge-base*. The two agree only while nothing has landed on
the base since the branch was cut. After that, every commit merged to the base showed up in the
pull request as something the pull request *removes* — a reviewer of PLC code was shown member
tables and ladder networks changing that the pull request never touched, and GitHub's own diff
disagreed with ours line for line, which inline review comments (which GitHub anchors to its own
hunks) could not have survived.

**Fix.** `IRepoSource.GetMergeBaseAsync` reads the merge-base from
`compare/{baseTip}...{headSha}` (`merge_base_commit.sha`; the only REST answer that names it,
asked with `per_page=1` because the rest of that answer is discarded; a value that is not forty
hex characters is "upstream"). `/pulls/view` resolves the base branch to its tip with the
caller's credential, asks for the merge-base, and returns it as `mergeBase`; the page compares
`mergeBase...headSha`.

**No merge-base is null, never the tip.** Falling back would quietly bring back exactly the wrong
answer; the page says the comparison is unavailable and offers no link, and the description still
shows. `Without_a_merge_base_the_view_says_null_and_still_shows_the_description` holds that:
mutating `MergeBaseAsync` to return the tip on failure turns it red (checked 2026-10-17, file
restored and touched).

**Not fixed here, on purpose:** the plain compare view between two refs is still two-dot, which
is right for "these two commits"; only a pull request's comparison is a question about a branch
point. Rename detection is the next slice's (`pulls/{n}/files` reports `previous_filename`).

---

### Adoption raced the lock and the disk, and a failure hid another project's pending export

**2026-10-17, `main`, second review of the unpushed commits through `9097409`.** Four more,
all reproduced by the reviewer. Three are in the matches-HEAD adoption, and each recorded
something as exported that was not.

1. **A lock taken while adoption was hashing was overwritten.** Adoption read the lock,
   awaited the hashes, and wrote the stale copy back. A text edit that made the binding
   TextAuthoritative in that window came out Clean. The write is now a compare-and-swap: the
   lock is replaced only if it is still the exact instance that was read. It runs under a new
   `_lockWrite` monitor, which ApplyAsync's write now shares, so the check and the write cannot
   interleave with a transition. **Not fixed, and noted:** ApplyAsync's own read, await, write
   predates this and has the same shape. The monitor makes the swap atomic; it does not make
   ApplyAsync's read current.
2. **A save after the status was adopted.** A clean status proved the files matched HEAD when
   git looked, and the hash came later. The orchestrator now takes the status as a function
   and asks twice: status, hash, status, hash. Both statuses must be clean for the binding and
   both hashes must agree. A save before the second status shows up in it, and a save after it
   shows up in the second hash.
3. **A mirror git lists but the disk does not have.** A sparse checkout leaves skip-worktree
   files listed and absent, and the reviewer confirmed it both in Core and at app startup.
   Every listed mirror file, and the project file, must exist on disk.
4. **One failed project hid another's pending export.** Unscoped, the gate stops at a failure
   and its `StaleBindings` names only the failures. That is right for a commit, but the panel
   read the same list, so with A failed and B changed it cleared `HasUnexportedChanges` and
   named only A. `CommitGateResult.UnexportedBindings` now lists every relevant non-failed
   binding that is behind, for every decision, and the panel counts from it.

5. **A directory project's payload was not checked (third review, same day).** The disk check
   covered the pointer and the mirror, and not the program files beside the pointer. A sparse
   checkout kept the first two and dropped `System/program.bin`: git still listed it, the
   project hash was taken over what was left, adoption recorded it, and the gate said Proceed.
   Every listed payload file that counts toward the project hash must now exist. It is judged
   by the same `CountsTowardProjectHash` the hash uses, so a missing log does not cost an export.
   Two tests in `TreePayloadTests`, one per side of that predicate, and each goes red when its
   half is removed.

**Evidence.** One regression test per finding: four in Core, and an App test that seeds the
test repository's own `state.json` with A failed. Each was mutation-checked: compare-and-swap
disabled, second status ignored, second hash ignored, disk check removed, `UnexportedBindings`
emptied, and the panel reverted to `StaleBindings`. Each turned its test red. Core 1,786.

**Lesson, again.** The first review's fixes were tested against the holes that review named,
and the second review found four the first missed. The adoption check is a "decide nothing
changed" function, and those deserve the same adversarial reading as an import: what changes
between the reads, what is listed but absent, and who else writes the same record.

---

### The matches-HEAD check trusted a status git never produced, and missed renames

**2026-10-17, `main`, from review of `373a3e9` and `0a4bb92`.** Three findings, all confirmed,
two of which recorded a changed project as exported, which skips the export the commit gate
exists to force.

1. **A failed `git status` read as a clean tree.** `GitCliClient.StatusAsync` parsed stdout
   without checking whether git succeeded, so a failure came back as an empty status. With
   `ListWorkingTreeAsync` succeeding, `AdoptCommittedStateAsync` saw "nothing changed under the
   binding, and both sides listed", and adopted a modified project. `GitStatus` now carries
   `Failure` (`WasRead` is its negation): set by the client when git fails, and null by default
   so every status built by hand still reads as read. Adoption refuses an unread status, and
   says why at Info, because the safe outcome (exporting instead) is also the costly one. The
   other four `StatusAsync` callers are unchanged. They draw lists that redraw on the next
   refresh, but whether any of them **decides** on `IsClean` is worth a separate look.
2. **Rename sources were ignored.** Only each change's destination was matched against the
   binding, so `plc/A/Simulation.st` moved to `archive/` removed a file from the mirror while
   every reported path sat outside it. `OriginalPath` counts too now.
3. **`LIBRARY_PARAMETERS_EFFECTIVE` was not classified**, so
   `Every_exporter_warning_is_classified_as_losing_an_object_or_not` failed every run.
   `0a4bb92` was committed after the parity and arch suites, but **not the pure suite**. It is
   recorded-anyway: the file is still written, and its content may say more than the project
   holds.

**And the owner's rule, carried through.** "A project with no changes and a valid mirror should
not be detected, even if sync has never run on this computer" was implemented in `373a3e9` only
at open. Discard clears the export record on purpose, so two CODESYS projects discarded back to
HEAD on 2026-10-17 would have been exported by the next commit. The check now runs at the top of
`RefreshStalenessAsync`, which every "what is stale" question goes through. It costs no git call
unless some binding is Clean with no record (`HasUnrecordedBindings`), and it is held off while
git rewrites the tree.

**Two test lessons.** The first App test for the every-refresh wiring discarded the **project
file**. Discard releases a binding only for files inside its **mirror**
(`BindingConventions.OwnerOf`), so the record was never cleared, and the test stayed green
with the wiring deleted. It discards a mirror file now, and goes red without the call. Second,
after the mutation runs, a `--no-build` pass ran the mutants' binaries and showed two reds
that a rebuild cleared: the 2026-10-10 timestamp trap from the other side.

---

### A library default was exported as though the project set it

**2026-10-16, `main`.** Re-exporting RefrigeratorControl and MachineWithMembers under SP22 Patch 4
added `MAX_CLIENTS: "20"` to `#System_VisuElemXYChart` in both library manager files of each
project. Nothing in either project had changed. `CodesysCatalogTests` went red, because the
File-Based Storage copy of RefrigeratorControl has no such parameter, and that copy was saved by
the same Patch 4 (`.auxiliaries/savecontext_profile`), so this was not a newer toolchain adding
something.

**Measured, not inferred** (`tools/codesys/Probe-LibraryParams.ps1`, a read-only copy):

| Manager | Stored (`LibManObject._parameterValues`) | `ref.parameters` reports |
|---|---|---|
| Application | `#Util` IBLOCKSIZE=22800, `#System_VisuElemCamDisplayer` GC_POINTS_PER_CAM=100, `#System_VisuElem3DPath` GC_POINTS_PER_POLYGON=100, `#System_VisuElemXYChart` **empty** | the three, **plus MAX_CLIENTS=20** |
| Top-level | CamDisplayer and 3DPath as above; XYChart **absent** | the same, **plus MAX_CLIENTS=20** |

`ScriptLibraryParameters` exposes only `GetEnumerator` to python. The reference's own LibMan
item stores no values (`ParametersToSerialize` is empty until a save), and `GetParameterValues()`
merges the manager's stored overrides with the resolved library's declared defaults. The mirror
was therefore showing a fact about **the library installed on the exporting machine**. The
Patch 2 export lacked the line for that reason too, not because the project differed. That is
the same class as the effective-resolution and `managed_library.version` rows DRIVING §5.6
already refuses to record.

**Fix.** `versio_export.stored_library_parameters` reads the stored table by reflection: the
wrapper's private `MyItem`, then `_rootLibManObject`, then `_parameterValues`. It hands the table
to `versio_libraries`, which stays pure and records only what the table holds. Private names
can change in a CODESYS update, so when the table cannot be read, the merged view is used and
`LIBRARY_PARAMETERS_EFFECTIVE` is reported for every reference it gives parameters for. Never
silently.

**Evidence the fix is right.** The fixed script, run through real CODESYS on copies of both
projects, produced mirrors **identical to HEAD's** (the Patch 2 exports) except for the
manifest's `toolchainVersion` line. It reported no fallback warning, and all three real
overrides were present. Three parity tests pin it: stored values win over defaults, a
reference missing from the table sets nothing, and the fallback warns. Forcing the merged
path turns two of them red.

**Rejected:** keeping the effective values and labelling them. A library update would then read
as a project edit on every machine whose installed library differs.

---

### Four projects "had changes that had not been exported", and none of them had changed

**2026-10-16, `main`, a fresh clone on a second machine.** The owner opened Versio and saw
four projects marked *not exported*, an orange banner reading *"4 projects have changes that
have not been exported"*, an empty Changes panel, and a failure card that said *"export
failed"* and nothing else. The question was why nothing showed under Changes. Nothing showed
because nothing had changed. Every other part of the screen was wrong.

**What actually happened, from the log.** The machine had never exported these projects, so
local `state.json` had empty project hashes, and the commit gate read that as "changed since last
export". It was not right to ask: see defect 0 below. All four exports failed, for three reasons
that had nothing to do with the projects:

- **CODESYS (two projects).** `.versio/repo.json` pins `CODESYS V3.5 SP22 Patch 2`, recorded
  when the projects were adopted on the first machine. This machine has 3.5.22.40, which is
  SP22 Patch 4. CODESYS printed *"The version profile 'CODESYS V3.5 SP22 Patch 2' is not
  valid."* and exited 1 after 5–8 s. That line appeared at Debug only. Versio said *"CODESYS
  exited with code 1"*, and the details pane said the toolchain was *installed*.
- **TIA Portal.** The user is not in the `Siemens TIA Openness` Windows group. Portal's own
  sentence said exactly that, and it reached the log only.
- **TwinCAT.** XAE is not installed. The probe said so at startup, as a Warn line.

**Four defects, the first underneath the rest.**

0. **A fresh clone exported every project, changed or not.** The owner's question, and the
   right one: *Versio is supposed to trigger only when something changed.* `RecoverAsync` was
   fixed long ago to keep `LastSynced` across a restart, because an empty record reads to the
   gate as "changed since last export", and every launch then re-ran CODESYS over a correct
   mirror. A fresh clone has no `state.json` at all, so it started from exactly that empty
   record, and the open-time catch-up exported all four fixtures. Now
   `SyncOrchestrator.AdoptCommittedStateAsync` runs between recovery and the catch-up. It
   applies the trust `AdoptExternalStateAsync` already gives a checkout: a project and a mirror
   that both match HEAD came out of one commit, and the commit gate refuses to record one
   without the other, so they agree. Narrow on purpose. It takes only Clean bindings with no
   recorded export. Nothing may be changed or untracked under the project, payload or mirror.
   And git must *list* both the project and a mirror file, so a gitignored project, or a mirror
   never committed, is not taken as unchanged just because status is silent about it.
   Mutation-checked on each guard and on the wiring. **Acknowledging a failure still clears the
   record** (that is deliberate: a retry should export), so a binding already failed on a
   machine is adopted at the next open, not at once.

   **Seen working on the real case.** A fresh clone of this repository, opened with the dev
   build, logged *"no export recorded on this machine; the project and its mirror match HEAD,
   so that is taken as exported"* for all four bindings, the TIA directory project included.
   The gate then answered *"every tracked project is exported and up to date"*, the catch-up
   ran nothing, and there were zero `ERR` lines.

   **Three App tests leaned on the defect.** `A_clean_file_shows_itself_and_says_nothing_changed`
   and two ladder tests opened an untouched, committed project and looked for an object in the
   Project tree. The project was there only because it read as "not exported". Each test now
   changes the project file, so the project is listed for a real reason while the object under
   test stays clean. The two ladder tests had passed some runs and failed others, and that is
   unexplained: on the real clone, adoption ran for the TIA project every time.
1. **The failure card never showed the toolchain's words.** `RefreshFailure` shows
   `SyncNarrative.Describe(Failed)` and then `ReasonFor`, whose comment promises the
   toolchain's own words. But every failure path called `ApplyAsync` without a reason, so the
   persisted record held the state machine's "export failed". The sentence that said what to do
   never left the log. Now the record holds `"<direction> failed — <reason>"` for export and
   import failures, including the two early import refusals.
2. **A pinned profile the install lacks was launched anyway.** `ProfileResolver.Resolve`
   already refused a missing pin, and said so well. Nothing ever passed it one: the toolchain
   is probed per vendor, and pins are per binding. `LocalCodesysRunner` now reads the Profiles
   folder beside its executable and refuses the job by name before planning, taking the
   mutex, or launching. The refusal names the pin, says what is installed instead, and gives
   the two ways out. CODESYS's own "is not valid" line is recognised as a backstop. **Not
   substituted:** falling back to the one installed profile is the silent project upgrade that
   `Resolve` was written to refuse.
3. **A failure was called "not exported".** The unscoped gate reports failed bindings as stale
   too, which is right for the gate, since it has to refuse them. The panel turned every stale
   binding into "has changes that have not been exported". Failed bindings are now named as
   failures, in the rows (*failed*) and in the banner (*"… failed to sync — select one to see
   why"*). The Git panel's *"A stale PLC export runs before this commit"* line now keys on a
   new `HasUnexportedChanges`, because a failed project is refused at commit, not exported
   before it.

**Evidence.** `versio-2026-10-16.log`, 15:36–16:03, operations `c8629c6e` and `67906c63`.
Each fix was mutation-checked: dropping the export reason, blinding the preflight, and emptying
the failed set each turn its new test red. The preflight test uses a fake install whose
`CODESYS.exe` is an empty file, so a missing preflight shows up as a failed process start.

**Not fixed, on purpose.** The TIA and TwinCAT failures are this machine's, not Versio's:
the fix is group membership, or installing XAE. The CODESYS pin is the repository's decision:
install Patch 2, or change the two bindings' `"profile"` if the fixtures should move to
Patch 4. The details pane's *"Toolchain: installed"* is still true of the vendor and silent
about the pin; the failure card now carries that half. The CODESYS backstop has no test,
because reaching it needs a real CODESYS in a non-standard layout.

---

### The Mermaid frame never drew anywhere, because three defects stacked under one white box

**2026-10-16, branch `ReviewFixes`.** The owner's look at `dev.md` in Chrome showed a white box
where a diagram belongs. Three separate defects produced that one symptom, and each had to fall
before the next was visible:

1. **The preview served stub frame files.** The header test wrote
   `<!doctype html><title>Diagram</title>` and `// frame` into the test output's `wwwroot` when
   the files were missing — a guard added after an earlier version *overwrote* the real files.
   But a stub, once written, is **newer than the real file**, so no copy-if-newer ever replaced
   it: the stub outlived every rebuild, and every preview since served a frame page with no
   scripts, no styles and a white default background. The white box WAS the stub. Same timestamp
   mechanics as the 2026-10-10 mutation-backup finding: restoring the past without touching the
   clock makes the build believe it. `TestHost.EnsureFramePage` now installs the REAL files from
   `web/public` on every start, overwriting; the header test asserts the served page loads
   Mermaid and the glue, so a stub can never again pass it.
2. **`hidden` lost to `display: block`.** The frame is born hidden until its diagram reports a
   height, but `.mermaid-frame { display: block }` overrides the attribute's user-agent rule, so
   every frame was visible from birth. With defect 1 in place that showed the stub; with it
   fixed it would have shown empty frames during render.
3. **A properly hidden frame cannot measure itself.** Fixing 2 with `[hidden] { display: none }`
   voided the frame's layout, and `scrollHeight` inside a zero-size viewport answered the 40px
   floor for every diagram. The frame is now born at **height 0 instead of hidden**: laid out at
   full width, invisible, and the reported heights are real (180–1310px across `dev.md`'s
   eleven diagrams).

Diagnosed end to end in headless Chrome over CDP, because neither the built-in browser (refuses
sub-frames) nor screenshots could say *why*: the frame census (pre hidden? frame height?) and an
evaluation inside one frame (`window.mermaid`, the body's SVG) separated the three. The deeper
lesson repeats the suite's: this handshake had **never executed anywhere** before today — the
built-in browser cannot run it, no vitest drives an iframe, and the server tests check headers.
Code whose first execution is in front of the owner will fail in front of the owner.

**A fourth, found by the owner the moment the first three fell:** the drawn diagrams sat on an
opaque white slab in the dark page. The page declares `color-scheme: dark`; the frame document
declared nothing, so it defaulted to light, and a browser whose iframe disagrees with its
embedder on colour scheme deliberately paints an opaque white canvas behind it instead of
honouring transparency. `:root { color-scheme: light dark }` in the frame's own stylesheet makes
it follow the browser's preference exactly as the page does; verified over CDP with
`prefers-color-scheme: dark` emulated — transparent frames, dark-theme nodes, no slab.

---

### The viewer's own clickjacking lock blocked its own PDF frame, and only a real browser said so

**2026-10-16, branch `ReviewFixes`.** The Docs tab embeds a repository PDF in an iframe from
`/api/…/raw`. `SecurityHeaders` sends `X-Frame-Options: DENY` and `frame-ancestors 'none'` on
every response except the Mermaid frame — including, until now, the very response the viewer's
own page frames. Chrome draws that refusal as **"127.0.0.1 refused to connect"** inside the
frame, which reads exactly like a dead server; the built-in browser drew a silent white box,
which reads like nothing at all. The owner looked in Chrome and reported it; two earlier
diagnoses (the preview's 25-minute window lapsing, a genuinely malformed fixture PDF — real,
fixed, but not this) each explained the symptom well enough to survive one round, because the
server's window really had lapsed on one of the owner's looks. A symptom with two live causes
hides its third.

The raw route now answers `SAMEORIGIN` and a minimal CSP whose one open door is
`frame-ancestors 'self'` (`Middleware.RawCsp`); everything else keeps DENY, pinned by
`The_raw_route_may_be_framed_by_this_site_and_nothing_else_may_be_framed_at_all` — including a
repository literally named `raw`, whose route is one segment shorter than the document route.
Why no test caught it: `RawDocumentTests` asserted content type and disposition, and no test —
or built-in-browser look — can see what only a real browser's frame refusal shows. The owner's
eyeball list in STATUS exists for exactly this class; the PDF embed is now off it.

---

### A source project's machine-local folders polluted every change list, because nothing ever wrote their rules

**2026-10-14, branch `ReviewFixes`.** Reported from a real library: an unchanged
`FsLibrary.fbslib` wore seven "modified" rows — `.auxiliaries/` caches, a
`.FBS_Project_Related` inspection file, and two `.sidecars/` files, one of them carrying the
engineer's username and machine name in its filename. The folders were already measured and
classified (docs/fbs/PLAN.md §2: sidecars = per-user `.opt` + precompile cache + `.~u`;
auxiliaries = caches) and this repository keeps `.sidecars/` out of git by a hand-typed rule
(b15d365) — but a source project never goes through adoption, so `EnsureGitIgnoreAsync` never
ran for one and no adopted repository ever received the rules. The same shape as the three
comments that once described `.versio/local/` as gitignored while nothing wrote the rule.

`TransientPatternsFor` now answers an FBS root with its two machine-local folders, scoped to
that root; `RepoSetup.EnsureSourceProjectIgnoresAsync` writes them; and the top-up pass runs
them on every scan — including a repository whose only project is a source project, which has
no bindings and used to skip the pass entirely. `.FBS_Project_Related/` stays trackable on
purpose: profile and inspection metadata are small, shared, and a profile change is a change a
reviewer should see. Rules cannot untrack what a repository already committed — that history
decision stays with its owner (`git rm -r --cached` once, then the rules hold).

---

### The commit box's caret blinked mid-box beside a hint that would not step aside

**2026-10-14, branch `ReviewFixes`.** Reported from the running app: clicking the commit
message box put the caret vertically mid-box, right beside a "Summary of the change…" that
stayed put — reading as text to delete.

Two template defects in `FlatTextBox`. Its content host and its placeholder both hardcoded
`VerticalAlignment="Center"`, which made `VerticalContentAlignment` a silent lie at every usage
site — the commit box says `Top` and is 72 px tall, so both floated to the middle. And the
placeholder's visibility hung on `Text=""` alone, so it survived focus and sat under the caret
until the first keystroke. Both now follow the box: the alignment is template-bound (the style's
own default stays Center, so single-line boxes are unchanged), and the hint shows only while the
box is empty AND unfocused — the keyboard arriving is the moment the box becomes the person's,
and the hint returns if they leave it empty. The commit box carries its own vertical padding,
because a Top that is finally honoured would otherwise kiss the border.

Pinned in `FocusVisualTests` through a shown window (keyboard focus needs a
`PresentationSource`); against the old template the pin fails on both counts.

**The sweep after the fix found the one case hide-on-focus makes worse.** The branch and clone
dialogs focus their box on `Loaded`, so a watermark there is the one hint focus hides — and
under the old template it had sat beneath a blinking caret instead, the same reported funk
("same for the new branch"). Their examples (`feature/what-this-changes`, the two address
shapes) moved into each dialog's message line, where a caret can never sit on them and they stay
legible *while* typing; the boxes carry no watermark. Every other watermarked box — the four
find boxes, the log filter, the identity pair, clone's folder — is read before it is clicked and
keeps the standard behaviour. Pinned in both dialogs' own suites: no `Tag`, example present in
the message.

---

### The body-under-a-table view told the declaration's story twice, badly, and drew husks

**2026-10-14, branch `ReviewFixes`.** Three defects from two screenshots of a real library, all
in the rendered pane for blocks shown as a member table over their code.

**A declaration removal leaked into the code pane.** `StSourceLayout.Mark` compares whole files
and surfaces a removal "at the end rather than dropped" when its part is not drawn — right for
every layout that draws the declaration, and wrong for the one that replaces it with a table: a
struck `VAR_INPUT` led a method's body while the table above was already saying what the
declaration lost. `Mark` now takes where the old file's body began (answered through the same
`CodeBody` seam that sliced the current one) and leaves declaration-side removals to the table;
a removal from the body itself, even its first line, still shows. Pinned pure and through the
shell — the shell pin asserts both halves at once: the body's own removed line shown, the
declaration's hidden.

**A parameterless method drew an empty table husk.** Zero members still parse as a valid table,
so the pane drew column headers over a blank where the code should be. A table with nothing in
it is a husk, not an interface: it falls through to the source layout now, which is what a type
with no members always got. The cost, taken knowingly: a block whose members were ALL deleted
shows its struck rows in Raw rather than a rendered table.
~~That rule now spares code blocks~~ (2026-10-23): an interface carries its code underneath it,
so an empty POU table is drawn as the owner asked. Types and variable lists keep the rule. See
*A POU with no variables drew as source* above.

**A removed line of tabs drew as a redaction bar.** Strikethrough over the whole line turned
`\t\t;` into one long dash. The indent is kept and kept undecorated; the strike starts where the
text does.

**2026-10-14, branch `ReviewFixes`.** `StDeclarationTable.Clean` tracked block comments with a
flag, not a depth. IEC 61131-3 permits `(* … (* … *) … *)` and CODESYS enables nested comments
by default, so the comment ended at the first `*)`: everything from there to the real closer was
treated as code — a commented-out declaration became a live row in the member table — while the
declaration's genuine tail was swallowed. The table is what a reviewer reads before committing,
and a table that quietly invents members is the kind of wrong that gets acted on. A depth now;
pinned by a declaration whose nested comment hides a `nStray : BOOL;` that must not become a row.

---

### Deep XML: the ladder parser could overflow the stack, and XDocument itself burns minutes first

**2026-10-14, branch `ReviewFixes`.** A review found the first defect by reading; pinning it
found the second by measuring.

**The parser.** `CodesysLadderParser.Power` and `Data` recurse once per `RValue` nesting level
with no guard, and the web viewer hands every repository `.xml` to `TryParseLadder` — a crafted
file of tens of thousands of nested `And` boxes would end the server process with the one
exception no catch can keep, straight past the "throwing parser degrades to plain text" rule and
faster than the render budget. Both now stop at 128 expression levels; past the cap the drawing
carries an element saying something was cut, like every other shape this parser cannot draw.

**The framework.** The pin test for that guard took 91 seconds, and the time was not the
parser's: **`XDocument`'s tree building is quadratic in nesting depth.** Measured in a harness —
a 5 MB document of 30,000 nested elements streams through a bare `XmlReader` in 51 ms and takes
`XDocument.Load` 70+ seconds, every settings combination alike, so `SafeXml`'s character cap
could never catch it: the cost is the shape, not the size. `SafeXml` now refuses nesting past
512 levels (two orders of magnitude above the deepest real export) in a streaming pre-pass,
before any tree exists — the same `XmlException` every caller already turns into the Raw view.
The eight no-toolchain suites, the 2.5 MB visualisation export included, pass under the cap.

---

### The Markdown parser could be made to kill the process, or to burn minutes of CPU, by a pushed file

**2026-10-14, branch `ReviewFixes`.** Found by a review that measured rather than read: both
defects were verified by running the built `Versio.Core.dll` against generated inputs.

**The crash.** `ParseBlocks` recursed once per blockquote level and once per nested list item,
with no depth limit; ~2,000 leading `>` on one line ended the harness process with
`StackOverflowException` (exit 127). An overflow is the one exception no catch can keep, so it
sailed past the web viewer's renderer catch-all — which exists precisely to turn a throwing
parser into plain text — and would take the whole server down on a file anyone with push access
commits; on the desktop, selecting the file took the app with it. The parser's own docstring
promises "degrades to paragraph text, never to an exception". Blocks now stop nesting at 64
levels and the rest arrives flat: content preserved, styling surrendered.

**The burn.** Every inline scanner (`Delimited`, `LinkAt`, `CodeSpan`, `AutoLink`) looks ahead
for its closer, and an input of openers that never close made each opener scan to the end —
quadratic, measured at 84 s of CPU for a 240 KB file of `*a `, well inside the viewer's 2 MB cap.
The web render budget bounds the reader's wait, not the work, so a few concurrent requests
pinned cores for a minute and a half each; the desktop had no budget at all. `Inline()` now
carries a linear fuel budget that every scanner charges its lookahead against; honest documents
never touch it, and what a degenerate shape loses when it runs out is styling — the rest of the
text arrives as one plain run, byte for byte. Pinned by three adversarial-shape tests that also
assert no character is lost.

---

### An FBS library file without the metadata header rendered its declaration twice and buried its code

**2026-10-14, branch `ReviewFixes`.** Reported from the running app with a real library:
`FsLibrary.fbslib` holds `.fb.st` files that open with a `///` doc comment and **no
`(* METADATA *)` header at all** — every file in the committed fixture has one, which is why the
gate looked safe. `CodesysFileBasedSource.Sections` answered only for files that open with the
header, so these fell through to the classic mirror's splitter, which needs Versio's own
delimiter, and `CodeBody` came back null — the Rendered pane drew the whole file (doc header,
attributes, `VAR` blocks) under a member table that had already tabled that same declaration,
with the actual code buried below it.

A path inside a `.fbsproj`/`.fbslib` folder is CODESYS's own statement of the format, header or
not, so the gate is now header **or** folder; a headerless file outside an FBS tree is still
refused, because a classic mirror's `.st` must keep the layout it has. Pinned both ways, with
the reported file reduced to a specimen.

---

### A parent-GUID cycle made every object in it vanish from the project tree

**2026-10-14, branch `ReviewFixes`.** Found by a review pass over `Projects/`; the method's own
remarks name silent object loss as the one failure the panel must not have, and its cycle story
was only half true. The visited set stops the recursion — but only once `Attach` runs, and for a
cycle it never ran: the root set was "no parent, or parent not in the manifest", and every member
of a cycle has a parent that IS in the manifest, so none qualified and the whole loop fell out of
the tree. The guard against the hang masked the loss. The existing test asserted
`Assert.NotEmpty(Flatten(tree))`, which the root folder satisfies by itself — it pinned "no
hang", not "objects survive".

An ownership chain must end at ground, so `Nest` now checks that it does: a leaf whose chain
loops (itself included) or enters a loop is treated as having no honest owner — flat, as a root,
exactly like the missing-owner case two lines up. Display-only (nothing on disk moves).

**The first fix did not fix it, and its tests could not see that.** It filtered the members
lookup by groundedness and never widened the root set — so a cycle member was neither a member
nor a root, exactly as before — and all three pin tests went green anyway, because every fixture
was all-cycle: with zero grounded members, the `members.Count == 0` early return handed every
leaf back untouched. Beside one honest member — which is every real manifest, since methods nest
under their blocks — the loop still vanished. Caught by an adversarial re-review that built
exactly that mixed manifest and watched the assertion fail. The root set now admits ungrounded
leaves too, groundedness is memoised per guid (one walk per chain, not one per member), and the
mixed shape is the fourth pin: a cycle beside an honest member keeps every object, with the
honest member still nested. Mutation-checked on the root-set clause alone — removing just it
fails the new test, which is the half the first round's mutation check never isolated.

---

### A status stream truncated inside an untracked entry threw out of the whole parse

**2026-10-14, branch `ReviewFixes`.** `PorcelainV2Parser`'s `?` arm was the one unguarded slice:
`line[2..]` on a field that is exactly `"?"` — git killed or cut off right after the tag — threw
`ArgumentOutOfRangeException` out of `Parse`, converting a partial status into a dead refresh.
Every other arm length-checks and drops, and the suite states the contract in words ("A truncated
read must not take down the status refresh") one arm above. Guarded now; a bare `?` or `? ` names
no file and is dropped, never invented.

---

### An Azure DevOps SSH remote got live forge buttons whose every click was a 404

**2026-10-14, branch `ReviewFixes`.** `Forge.Detect` deliberately maps `ssh.dev.azure.com` to
AzureDevOps, but `WebRoot` only re-schemed the parsed authority and path — right for every other
forge, and doubly wrong for Azure's SSH shape: `git@ssh.dev.azure.com:v3/org/project/repo` became
`https://ssh.dev.azure.com/v3/org/project/repo/pullrequestcreate?…`, a host that serves no web UI
and a path its web UI does not use. Create, Existing and List all inherited it, so
`MergeRequestPlan` rendered a working-looking button that could only fail — the exact "wrong link
is a 404 with Versio's name on it" the `ForgeKind` remark forswears. Tests covered only the https
Azure shape.

`WebRoot` now translates the one shape it can vouch for — `v3/{org}/{project}` on that host
becomes `dev.azure.com/{org}/{project}/_git` — and any other path on Azure's SSH host returns
empty, which is what hides the button: no button, never a guess.

---

### The empty state's Open and Clone buttons drew normally and never received a click

**2026-10-14, branch `ReviewFixes`.** Reported from the running app, not derived: with no
repository open, the project panel's "Open a repository…" and "Clone…" buttons did nothing —
they could not even be hovered.

The empty-state `StackPanel` and the `TreeView` share one `Grid`, the StackPanel was declared
first, and the TreeView is always present with `Background="Transparent"` — **and Transparent is
hit-testable**. A Grid hit-tests its later children first, so every click over the empty state
landed on an empty tree's ScrollViewer while the buttons underneath drew normally. The panel's
own row template already documents the inverse trap ("a Grid with none is transparent to
hit-testing everywhere its children are not"); this was the other direction of the same rule.
The wiring was all correct — `ApplicationCommands.Open` routed to the window's picker, the test
asserted visibility, the command and the binding — which is exactly why the test never caught
it: **visibility is not reachability.** The buttons had been broken since they were added
(2026-09-21); the owner-eyeball item that would have clicked them (STATUS item 2) has not run
yet.

The empty state is declared after the TreeView now, with the order named as load-bearing; it
collapses whenever a repository is open, so it never sits over a tree that has rows.
`The_window_says_open_a_repository_only_when_none_is_open` now asks the question the mouse asks
— `VisualTreeHelper.HitTest` at each button's centre must land inside that button — and against
the old order it fails with *"a click at 200.8,478.4 lands on Grid"*.

---

### A repository folder renamed while open raised the same crash toast every 20 seconds

**2026-10-14, branch `ReviewFixes`.** Measured live, not derived: a packaged Versio had
`C:\GIT\Codesys\FS_Codesys_Library` open when the folder was renamed to `FsLibrary`. Renaming a
watched tree succeeds — the watcher handles move with it — so the shell went on polling the old
path, every git call threw `Win32Exception (267)` out of `Process.Start` ("The directory name is
invalid"), and `RunRefreshAsync`'s generic catch raised an identical "Something went wrong" toast
on every 20-second tick for as long as the window stayed open. The log also claimed
`git status … still running` one second after each failed start: the slow-command announcer is
armed before `Process.Start`, and a throw there skipped every cancel, so the heartbeat reported a
process that never existed.

Two scoped fixes, deliberately short of SAFEGUARDS §5.H (which stays deferred — nothing here
classifies toolchain failures or touches the breaker): the shell says the folder is gone **once**,
by name, with the remedy ("put it back, or open the repository from its new location"), repeats at
Debug while it stays gone, and a refresh that works again makes the next disappearance news; and
`GitCliClient.RunAsync` cancels the announcer and logs `could not start` when the process never
started. Pinned by `RepositoryGoneTests` against a really-moved real repository,
mutation-checked both ways: without the suppression the storm returns, and without the
success-reset the second disappearance is never reported. The notification center refreshes an
identical headline rather than stacking it, so the count that proves "said once" is the card's
Error mirror in the log, one line per post.

---

### Every clean exit logged its shutdown into a disposed file sink, which quietly reopened the file

**2026-10-14, branch `ReviewFixes`.** `App.xaml.cs` registered its Exit handlers sink-first and
shell-last, and multicast handlers run in registration order — so on every clean exit the
`RollingFileSink` was disposed before the shell logged its way out (the watcher stop, *"released
this repository"*, the lines that distinguish a clean shutdown from a crash in tomorrow's bug
report). Those lines reached disk anyway, which is why nobody noticed: `Emit` never checked
`_disposed` and reopened the file through a `FileStream` nobody would ever close — directly
contradicting the guard `CurrentFile` carries with the comment *"Named, never reopened, once
disposed"*. The trap was armed for whoever "completed" that guard in `Emit`: the fix-shaped
change would have silently dropped the session-release evidence from disk on every clean exit.

Both halves are fixed together, because each alone is wrong: the sink's dispose is registered
after the shell's, so shutdown lines land in a live sink, and `Emit` now honours `_disposed`, so
nothing reopens the stream. Pinned by
`Emitting_to_a_disposed_sink_writes_nothing_and_reopens_nothing`, mutation-checked: removing the
guard fails it on the leaked reopen.

---

### The CODESYS runner dropped malformed protocol lines, and the task count never reached the log

**2026-10-14, branch `ReviewFixes`.** Two drifts between what is parsed or promised and what is
logged, both in `LocalCodesysRunner`, both the house's named failure mode: a silent failure is a
bug even when the code is correct.

**Malformed lines.** `ScriptProtocol.Parse` collects lines that carry `@@VERSIO@@` but not valid
JSON into `Malformed` — a script killed mid-print leaves its last event exactly like that — and
the runner read the field nowhere, so the one line explaining a death could reach nothing. Both
newer vendors' runners logged these at Warn from their first version, with the comment that a
malformed line may be the fatal that explains everything else; the oldest runner now does the
same.

**The task count.** `versio_export.py` emits `{"t": "tasks", "count"}` under a comment promising
it at Info "for the same reason the library count is" — and `LogScriptEvents` had arms for
`libraries` and `devices` and none for `tasks`, so it fell to the default `· tasks` at Debug.
Same shape as the `· task_set` drift already recorded. The arm exists now.

---

### The export's crash recovery deleted the one copy its own design says to put back

**2026-10-14, branch `ReviewFixes`.** Found by reading `versio_export.py` against its C# twin.

`SwapRecovery.Decide` names the dangerous quadrant precisely: trash present and mirror missing
means a run died between the swap's two renames, the trash is the last complete export in
existence, and the right act is to put it back — its remarks call the delete-loop alternative
"the outcome the three-rename swap was built to prevent". The script did exactly that delete:
every export start cleared `.versio_trash` unconditionally, under a comment that even said
*"If the mirror is missing, THIS is where it is"* — and then removed it without looking.

The window is real: a timeout tree-kills CODESYS between the renames (mirror gone, trash holds
it); the app-side sweep (`SweepKilledSwapsAsync`) restores, but only at repository open, so with
the app still running the retry export is the first thing to arrive — it cleared the trash first
and only then could fail before its own swap (a profile error, a second kill), leaving no mirror
on disk at all. Mitigations were real (the mirror re-derives from the project; git usually holds
a committed copy) but an uncommitted mirror had no copy anywhere.

The script now makes SwapRecovery's decision: mirror present → trash cleared as before; mirror
missing → trash renamed back (`trash-restored`, a Warn in the runner), then exported over — so a
failure anywhere later still leaves a mirror, and `preserve_equivalent` has the old bytes to
keep. The python is embedded; the suites after a rebuild: pure 365, parity 97, both green. The
CODESYS gate ran on 2026-10-14 with the change embedded: **55/55 green in 1 h 58 m** — on a
machine busy with parallel builds, so the duration is load, not regression (the idle norm is
~40 min).

---

### TIA host: a delete never compared the live block, and a loose match could destroy the wrong PLC's

**2026-10-14, branch `ReviewFixes`.** Found by a code review of the pack against the other two
vendors; both held on reading, neither needs Portal to see.

**The deletion check the product documents did not exist for TIA.** `DeletedFile.Hash`'s contract
(`ImportPlan.cs`) says the executing side compares the *live* object before removing anything —
the CODESYS script does (`why_not_delete`), the TwinCAT host does (`LiveMatchesRecorded`), and
SAFEGUARDS claims it as a product property. The TIA host checked only the plan-side
`cannotVerify` string, then `FindObject` → `TryDelete`. The hashes travelled in the plan and were
never read, so a block edited in Portal after the last export (Versio closed, or the edit landing
between export and import) was deleted anyway the moment its mirror file went missing and somebody
confirmed the import — destroying exactly the IDE edit the check exists to keep. The host now has
its own `LiveMatchesRecorded`: GenerateSource text hashed as `HashTextFile` hashed the exported
`.scl`/`.udt`, and a fresh in-session XML export through `HashNormalized` against `nhash`; any
agreement allows the delete, anything else — including a live object that cannot be read back —
keeps it with the sentence naming the remedy.

**Destructive lookup accepted a name-contains.** `MatchesBlock` accepted
`guid.EndsWith("/" + name + "#" + number)` and `Has(guid, "/" + name + "#")` — a case-insensitive
*contains* on the name alone — and `FindObject` returned the first hit iterating PLCs in device
order. Every S7 PLC has an OB named `Main`, so a deletion (or rename: same lookup, `SetProp Name`)
planned as `Dev2/PLC_2/OB/Main#1` matched `PLC_1`'s `Main` first. Combined with the missing live
check there was nothing downstream to catch it — P3 already measured that delete raises no
exception, no cascade, no warning. The lookup is exact identity now, as the TwinCAT host always
was and documents; `MatchesIdentity`'s bare-name clause went with it. A miss — an object renamed
or moved in the IDE — is warned and kept, for renames too: a silently skipped rename diverged the
mirror, because the next export wrote the old name back while the arrived file sat as a duplicate.

**The loose clauses dated from the pack's first commit** (`1df1913`); nothing later depended on
them, and the gate fixture has one PLC, which is why five gate runs never met the scenario.
**Owed:** a `Versio.Plc.TiaPortal.Tests` run on a machine with Portal — this machine has none.
The host has no pure-test seam (net48, Openness via reflection), so the gate is the only
executable check.

**2026-10-12, branch `WebViewer`.** Two reviews; every point held.

**Hidden characters in names reached the page.** Rule 9 (Trojan Source) was built for file contents:
markers in code, tooltips and attributes. Names and messages went into React text as they were:
tree rows, titles, notices, layer chips, the compare list, branch names, commit messages. React
escapes HTML, so nothing could be injected, but git allows a right-to-left override in a file or
branch name, so a row could read as one name and be another. Every earlier review missed it,
mine included, because the rule was stated about code. Fixed on the server with Core's one list
(`CodeHtml.Spelled`), keeping navigation exact: paths and branch names stay as they are, with a
spelled label beside each for the page to show.

**Session renewal, three edges of the previous fix:** a 429 or 408 counted as GitHub refusing, which
ends the session; the minute's pause was stamped before a call that can take thirty seconds; and
in an outage a token with minutes left was answered as signed out. And the left-out-layer notice
promised a difference for a layer that was the same in both versions.

---

### Versio Web: my fix for one race made every page slow, and a notice pointed at nothing

**2026-10-12, branch `WebViewer`.** Two reviews of the previous round's fixes; every point held.

**Not deleting a session whose renewal failed** (`95af1a6`, to stop deleting another instance's
renewal) left the dead session alive: every request carrying its cookie made a doomed call to
GitHub and waited a second, one after another on the gate, so a page took four seconds; its idle
expiry slid with each request, so it lasted a month; and on a public repository the request fell
back to anonymous and succeeded, so the only symptom was a slow site. The mutant reproduces it:
four requests, four seconds. The answer splits what were one case: nothing can renew (no refresh
token, or a spent one: no race, end at once), GitHub refused (look a second, then end), GitHub did
not answer (keep it, and do not ask again for a minute). The last two were one `null` from
`RefreshAsync`, and a network error was not caught at all.

**The left-out-layer notice pointed at Raw for both limits.** A layer over the fetch limit was never
fetched, and its Raw tab says "too large to compare"; and a change only in a left-out layer also
raised "nothing this view draws changed", whose Raw tab is the unchanged file on screen. The test
had asserted the notice for both limits without ever opening the layer it pointed at.

**Lesson, twice in two days:** a fix that stops an action (deleting, refusing) needs the question
"and then what happens to the thing it would have acted on?"

---

### Versio Web: two more reviews, four findings, and two that were already fixed

**2026-10-11, branch `WebViewer`.** Two more reviews, pasted one after the other.

**Two "still open" items were already fixed**, in `95af1a6` (a refused renewal deleting the
winner's write) and `ab76d7c` (a type highlight beside "nothing drawn"; a deleted type never
highlighted). The reviewer read the pushed branch, and none of the session's commits were pushed.
Worth remembering when a review disagrees with the code: check which revision it read.

**Two of the findings pulled opposite ways, both real.** A layer over the draw limit in one version
only has no `Failure`, so the unreadable-side check passed it, and a table living in it drew every
member as added, then was cached. And my own fix for unreadable sides counted "too large" as
unreadable for every layer, so an `.xml` over the fetch limit refused a whole comparison whose
`.st` diff could be drawn. One rule answers both: another layer too large on either side is left
out of both sides and said. **The first test for it proved less than it looked**: a CODESYS GVL's
table comes from its `.st`, so the mutant failed only on the missing notice and never drew a
member as added. On a TIA data type, whose members live in the `.xml` beside the `.udt`, the
mutant reproduces both defects exactly.

**The other two:** views drawn from a model with a failed read were still cached, though the model
no longer was; and with Redis but no shared key ring, a token sealed by another instance threw on
decryption, a 500 for every signed-in call and every sign-out. It now reads as signed out, and
startup warns, since only a shared ring stops the random sign-outs.

---

### Versio Web: the first review's open items and a second review, fourteen findings, one vendor leak

**2026-10-11, branch `WebViewer`.** The first review's items still open, then a second review
pasted mid-way; two items were in both. Each checked against the code first; all held. Each fix
has a commit and a test that fails with it reverted.

**The vendor leak, wider than reported.** `ProjectTreeNode.PrimaryFile` listed `.st`, `.scl`
and `.udt`: two Siemens extensions in a namespace that may learn no vendor. The seam tests look
for brand names and saw none. A second copy the review did not name sat in Core's
`LanguageCatalog`, kept there on purpose by a comment arguing no vendor was named, which the later
`SyntaxFor` seam's own remarks contradict; the desktop's `DiffLayerChoice` held a third. TIA's
format names its text files now, `PlcFormats.LanguageFor` is the one place vendor and catalog
meet, and `VendorSeamTests` refuses vendor-only extensions as literals in neutral namespaces.
On the desktop, a File-Based Storage object's text layer now opens first and is labelled ST.

**Answers built from a failed read, three ways.** (1) A comparison checked only the side it found
first, so a rate-limited older copy drew every member as added and Raw diffed against null. (2)
Every rendered answer, failures included, was cached for half an hour by blob, so "could not be
read" outlived the rate limit; the repository model had the same defect for manifests and
catalogs, found by reading the loader for another item. (3) Concurrent loads of one commit shared
one answer, so an anonymous load's rate limit was handed to a signed-in request that joined it.

**Sessions across instances.** The loser of a renewal peeked once and deleted the session; a
winner's write landing between the two was deleted with it. Deleting bought nothing, so it is
gone. A sign-out during a renewal now revokes the new token rather than writing it back; without
compare-and-swap both are narrowed, not closed, and THREAT-MODEL says what is left.

**Smaller, each real:** a parser that throws was a 500, now the plain-text fallback; "nothing this
view draws changed" ignored a type highlight, and a deleted type was never highlighted; an
unreadable catalog dropped its rows silently; the loader's remark said catalogs and File-Based
Storage were unread, weeks after both were built; LFS downloads were never checked against the
pointer's hash, and there was no test of a successful LFS download at all; one lock serialised all
App token minting; the branch picker stopped at 1,000 in silence; "the latest 50" appeared for
exactly 50 commits.

**What the mutations caught in the tests themselves.** Two mutation checks first printed nothing:
`if (true)` made the following code unreachable, which this build treats as an error, so the
mutant never compiled. An empty mutation report is not a pass; each was redone with a mutation
that compiles. And the first tampered-LFS test changed the length, so the size check alone caught
it and the hash comparison was untested; the tamper is now the same length.

---

### Versio Web: an external review's eight findings, all real, two smaller or larger than stated

**2026-10-11, branch `WebViewer`.** A review of the branch was pasted in with eight findings. Each
was checked against the code before anything changed; all eight held. Each fix has its own commit
and a test that fails with the fix reverted.

1. **A layer chip showed the first layer asked for.** `ViewService` keyed a fragment by every
   layer's blob hash, which one object's layers share, so with the server's one process-wide cache
   the second layer got the first's answer. The earlier chip test built a new service, so a new
   cache, per call, and could never see a warm entry. The key carries the file shown.
2. **A read could put a consumed refresh token back.** `SessionStore.FindAsync` wrote "last seen"
   by replacing the whole entry; a request that read before a renewal and wrote after it restored
   the old token, and the next renewal signed the person out. The four-requests renewal test could
   not see it, because every read in it finished before the renewal. There is no compare-and-swap
   on `IDistributedCache`, so reads stopped writing: idleness is the store's sliding expiry, kept
   alive with `Refresh`. `LastSeen` then had no reader and went, with a test that an entry stored
   with it still reads.
3. **A token GitHub gave no expiry was given eight hours.** With expiry turned off on the App there
   is no `expires_in` and no refresh token, so the session ended at the guess while GitHub still
   honoured the token.
4. **The ladder's "Changes only" checkbox was stripped.** DOMPurify forbade every `input`. A
   checkbox is now kept and every other input removed. Rejected on the way: also forbidding
   `button`, since the ladder's previous and next controls are buttons.
5. **The ladder script was not safe to run twice in one document — smaller than stated.** The web
   page runs it per fragment; each run added a document F7 listener holding its page. The review
   said one press then moved two changes. It does not: each set of handlers keeps its own position
   and they agree. The cost was a listener and a dead page per file viewed. **The first test checked
   the screen and passed against both mutations**, which is how the overstatement showed; the test
   that went in counts registrations, running the script read from `LadderPageAssets.cs` itself.
6. **History's Compare compared the ref with itself.** Now the default branch against the ref. The
   accompanying paging request was declined: the latest 50 is deliberate, and the page now says so
   when the list is full. This is the page's first component test.
7. **Branches past the first 100 were missing.** Now every page, to a cap of ten.
8. **Behind a load balancer the rate limit was one bucket for everyone — and the obvious fix was
   worse.** `Web:TrustedProxies` names the proxies whose forwarded address is believed. **ASP.NET's
   forwarded-headers middleware treats empty known-proxy lists as "trust every sender"**: the first
   version cleared both lists and ran it, and the test sending `X-Forwarded-For` from an untrusted
   client got a fresh bucket per request, which would have let any one client evade the limit
   entirely. With nothing configured the middleware does not run.

**What it says about the tests.** Three of the eight sat behind a test that looked like coverage:
a chip test with a cold cache, a renewal test whose reads all finished first, and (until rewritten)
a ladder test that watched the screen. Each was green for a reason unrelated to the defect.

---

### Versio Web: what the browser, the audit and the mutations found while it was built

**2026-10-11, branch `WebViewer`** (docs/web/PLAN.md). Seven defects, each caught by something
other than the first green run. Grouped because each is small; each has its own test.

**Only a browser could show these.** Every server test was green while:
- **a pasted or reloaded link to any file with an extension was a 404.** ASP.NET's default
  single-page fallback skips paths that look like files, and nearly every PLC file's URL ends in
  `.st` or `.xml`. Clicking inside the page worked, which is why nothing noticed. Fixed with
  explicit fallbacks for the page's URL shapes.
- **that fix then 404'd the page's own script and stylesheet.** Static files stand aside once
  routing has chosen an endpoint, and the new fallbacks matched `/assets/*`. Static files now
  run before an explicit `UseRouting()`. The test serves a real built asset and checks its
  bytes; the first version of the test did not, and passed over the broken order.
- **the desktop's wording leaked:** "No changes since the last commit" on a single commit, a
  table note about uncommitted changes, a tooltip starting "At the last commit". `LadderWording`
  (Core, desktop text by default) and `MemberTable.Note` carry the page's words; a test now looks
  for either phrase across views and caught the tooltip after the first fix.

**The audit, on versions written from memory.** `npm audit` found DOMPurify 3.2.2 (a long list
of XSS fixes since, on the page's sanitiser), React Router 6.28 (an open-redirect advisory with no
6.x fix), and critical-to-moderate advisories in Vite 5 and Vitest 2. Every version is now pinned
exactly, at least two weeks old, with `npm audit` at zero; the page uses react-router 7.

**Mutations that survived, and what was wrong.** Each time the code was right and the test was
not watching:
- the private-repository refusal (security rule 1) could be removed with every test green: the
  fake GitHub refused an anonymous read first, so the resolver's own check never ran. Installation
  tokens now sit behind an interface a test fills with a token that can see the private repository.
- the regex-timeout warning was logged after the startup line the test stopped reading at. The
  state now sits inside that line.
- path-traversal tests passed without validation, because the fake tree simply lacked those
  paths. They now assert nothing reached GitHub at all.
- the "vendor named only in RegisterFormats" Arch rule read calls, and `CodesysProjectFormat.Instance`
  is a field. Its positive control came back empty; it reads field loads and `newobj` now.
- two defensive branches in the new renderers (filling gaps between tokens, a line-kind check
  before a line number) could never run; they were removed and the tokenizer's coverage became
  a seeded test of its own.

**Later the same day: review, performance and the open items found nine more,** each fixed with
a test that fails without it:
- **Random sign-outs about every eight hours.** The page sends four requests at once; near token
  expiry all four renewed with GitHub's single-use refresh token, three failed, and a failed
  renewal ends the session. One renewal per session now; across instances, a re-read first.
- **Layer chips could not show a layer**: an object always opened its primary file.
- **A pooled-memory leak per GitHub call** (undisposed `JsonDocument`), and network failures
  during blob reads escaping as 500s.
- **Resolving a branch downloaded the whole commit** (up to 300 files' patches) to read its SHA.
- **17 GitHub calls per page load** from simultaneous requests each doing the same work; 9 after
  coalescing. The test that pins it then found **one blob fetched twice** in a model load.
- **Rebuilding the page after a change broke the .NET build**: the Web SDK globbed `wwwroot` at
  evaluation, before Vite replaced the hashed files.
- **The diagram frame's policy said `script-src 'self'`**, which matches nothing in a sandboxed,
  opaque origin, so no Mermaid diagram could ever draw.
- **Unhashed static files cached for an hour**, so a browser kept a stale frame page; and a test
  overwrote the built frame with a stub that the preview then served.
- **`NodeFor` walked the whole tree per call**, changed-files times tree-size in a comparison.

**A tooling hazard, for whoever edits next.** Backslash-u escapes typed into the editing tools
arrived in source as the literal invisible characters: U+2028 broke the build, and the others
compiled silently, inside the code that guards against hidden characters. The checks are now
written as code points (`0x202E`) and every new file was checked for non-ASCII.

---

### The Rendered tab drew a changed source file as if it were untouched, on every vendor

**2026-10-10, the owner, with a screenshot of the FBS scratch repository.** `ST_AlarmSettings`
had its `OnDelayTime` default changed from `T#0S` to `T#2S`. The tree marked it `M`, and Raw
showed the change. The Rendered tab drew the eleven lines of the struct with nothing marked.
The same session had already fixed this class of defect for the library, task and device
tables (the entry below).

**Cause:** `SourceRender` rows were built from the working file and never compared with
anything. The member tables, the catalogs and the ladder each have a pass that reads HEAD and
marks; plain source had none. So the gap was not FBS's or CODESYS's. A TIA `.scl`, a classic
CODESYS or TwinCAT `.st`, and a loose text file all rendered unmarked.

**Fix:**
- `LineDiff` (Core, pure) compares two texts.
- `StSourceLayout.Mark` marks the laid-out rows by `FileLine`. New lines are tinted, and
  removed lines come back struck where they stood, as the merged ladder view does.
- `MarkSourceChangesAsync` runs in the shell's existing table → source → ladder sequence.

It marks the code under a member table too. The owner's struct now renders as a member table
anyway (the same day's cross-vendor tables), so `MemberTableDiff` marks it `T#0S → T#2S`.

**Evidence:**
- `RenderedSourceMarkTests` uses a loose `.st` in a plain repository, so the test asserts the
  pane's behaviour and no vendor's.
- With the source pass skipped, `SourceProjectTests`' program test turned red before it was
  split; the marking test is the one that holds it now.

**Also found while doing it:** four BOM literals were written as the invisible character
rather than `'\uFEFF'` (c830e7d). They were all this branch's own, introduced by the editing
tool. A commit subject briefly carried the same character after going through a code page.
`390be02` on the remote is that version, merged beside the corrected `c830e7d`, with an
identical tree.

---

### The library, task and device tables drew the new file under a DIFF header and marked nothing

**2026-10-10, the owner, on `RefrigeratorControl`'s Application Library Manager.** Six
visualisation libraries had moved from `4.9.0.0` (one from `4.8.0.0`) to `4.10.0.0`. Raw
showed six changed `defaultResolution` lines; the tree showed six `M` rows under the manager;
the Rendered tab, under a pane header that says **DIFF**, showed 25 rows with nothing marked.
"That's the whole point of this tool. The rendered view needs to show what changed."

**Cause:** the catalog arms of `BuildRenderedContent` parsed the working file and returned it.
Nothing ever read HEAD for them. The tree has compared each reference against HEAD's copy since
the catalogs were grafted (`LibrarySnapshot { Current, Baseline }`), and the member table and
the ladder had both learned to mark themselves on 2026-10-05, so the three catalog tables were
the only rendered views still drawing a changed file as if it were clean.

**Fix:** `CatalogDiff` (Core, pure) marks a catalog against its committed copy the way
`MemberTableDiff` marks a member table:
- added and modified rows are tinted, with the tree's A / M / D letter beside the name;
- a changed cell shows old → new;
- a deleted row comes back where it was, struck through;
- the name's hover lists every field that moved, including the ones with no column (a
  library parameter, a watchdog time, a device parameter);
- a one-line summary sits over the table.

Rows are matched by the tree's own keys and each row's own `ContentEquals`, so the table and
the tree cannot disagree. `ShowNode` runs it for **every** catalog shown, not only for a
changed row. A library row the tree calls clean still opens its manager's whole table, and
the changed rows are in that table. A clean file costs one `git show`, then returns quietly. A
file that differs only outside its rows (header, order) says so in the summary.

**One design point worth keeping:** the mark is a single `RowMark` property on each row type,
and every `ContentEquals` resets it on both sides. Without that, a row the pane had marked
would compare unequal to its own plain parse. `CatalogDiffTests.A_mark_is_never_part_of_what_a_row_records`
pins it.

**Evidence:**
- Ten Core tests.
- Two flow tests in `RenderedPaneFlowTests` (a manager and a task configuration changed after
  a commit). Disabling the `MarkCatalogChangesAsync` call in `ShowNode` turned both red; it
  was restored.

**Left alone:** the single-task and single-connector detail panes (`TaskDetailRender`,
`DeviceDetailRender`) still show facts without old values. The table they sit beside is now
marked.

---

### A dev build pointed at a scratch TIA binding starts a real Portal export

**2026-10-05, while running the app to look at the new ladder pane.** A scratch repository had
a TIA binding, a one-line `plc/Station.ap21` pointer file, a hand-written `_manifest.json`, and
the LAD specimens under `plc/Station/Blocks`. Launching the dev build on it logged:
- `catch-up: 1 project(s) changed while Versio was not running; exporting`;
- `Clean → ProjectAuthoritative`;
- `Export of …\plc\Station.ap21 via …\Versio.Plc.TiaPortal.Host.exe`.

One second later a `Siemens.Automation.Portal.exe` was running on the owner's desktop.

The commit gate did what it should. A project with no export state has "changed since it was
last exported", and with a Portal installed the host is real, so the export runs with its
20-minute floor against a file that is not a project. Nothing was lost: the scratch mirror was
the only thing in reach. The three processes this launch started were stopped by id after
checking their start times against the launch. The ladder had already drawn by then:
`ladder plc/Station/Blocks/Pair.xml: 3 networks against HEAD; 1 changed, 1 added, 1 removed`,
with zero `ERR`.

**What to do instead:** look at a rendering change on a real project's mirror, or at a
scratch repository with **no binding** (the panes then have files, not objects). Never a fake
`.ap21` on a machine that has Portal. Not changed in code, and one question left open: does the
host refuse a file that is not a project, or wait out its 20 minutes? The processes were
stopped before anyone found out.

---

### Data types and function blocks as tables, with a type's changes shown on the block that uses it

**2026-10-04, asked for by the owner** looking at `AnaVal.udt` and `Service_Interaction_FB.scl`
in the Rendered tab: SCL text, every member's attribute block running off the right edge.

- **UDTs** (`SW.Types.PlcStruct`) are tabled from the native XML the export already writes
  beside each `.udt` - the same Interface tree the data-block table reads, so the TIA parser's
  gate admitted it. `MemberTable.IsDataType` (the IEC notion, not the vendor's word) changes the
  value column to DEFAULT VALUE, what the IDE calls a type's initial values.
- **FBs and FCs** are tabled as an interface: a heading band per non-empty section (Input,
  Output, InOut, Static, Temp, Constant), with the block's **code kept underneath** behind a
  splitter - tabling the interface must not take away what the tab always showed
  (`InterfaceRender`). Organization blocks stay out; a test pins it, as the FB test used to.
- **Type links.** A quoted datatype (`"MotL"`, `Array[1..4] of "AnaVal"`) is a reference the
  format names (`MemberRow.TypeReference`); the app resolves it through a new
  `ObjectIndex.FindByName` to an object in the same project. Resolved, it is a link that opens
  that object exactly as its tree row would. Unresolved - a library or system type the export
  never wrote, like the fixture's `MotL` - it stays text: a link to nowhere is worse than none.
- **A changed type marks the row.** If any file of the linked object is in the working tree's
  change list (the Git panel's own list, so the two cannot disagree), the member's row is
  tinted. This is the one change a reviewer reading the block cannot otherwise see: the
  block's own diff is empty. The link then opens the type **on its diff**, because that is
  where a changed object opens. `MemberTablePaneFlowTests` goes red if the marking is forced
  off (checked); `InterfaceLinkWindowTests` realises the window and finds the drawn link bound
  to the shell's command, the part no view-model test sees.

**The owner's first session with it (2026-10-05), on a scratch copy of CleaningStation,
found two more things.**

- **Following a link left the tree behind.** The pane showed the type while the tree still
  highlighted the block, so it was not clear what was on screen. A link now selects the
  object's row - in the panel on screen, else the other one, switching the rail only on a hit -
  expands its folders, and the panel scrolls it into view (`TreeReveal`: a virtualized tree
  has no container for a row nobody scrolled to, so the walk brings each level's index into
  view on the way down). Selecting the row is what opens it, so pane and tree cannot
  disagree. An object only the Explorer lists, before the Explorer was ever opened, is found by
  loading that tree quietly first.
- **A function's interface said things that are not true.** `RealToAnaVal_1` showed
  "Read/Write" on every parameter and a Return section holding `Ret_Val : Void`, and its code
  pane repeated the thirteen declaration lines the table had just shown. The access walk
  defaults a missing flag to the IDE default, right for DB members and wrong where there is no
  storage to reach: access is now kept only on an FB's Input/Output/InOut/Static; a Void return
  is dropped with its heading; and the code pane starts at the line the format says code
  begins (`IPlcProjectFormat.CodeStartLine`, default null; TIA answers with the `.scl`'s
  `BEGIN`), keeping file line numbers so it matches Raw. The bare "Optimized" under the title
  reads "Memory layout: Optimized".

**Then the point of it (same day).** A link took the owner to the changed UDT - on its raw
diff, 23 lines of SCL attribute blocks around one change. "That's the whole point, it needs
to show on the table ... rendered first always, raw view optional."

- **Rendered first, always.** `ShowRendered` used to default to Raw for a changed file
  ("the diff is what review is"). That held while Rendered could not show a change; it now
  can, so every selection with a rendered view opens on it. `RenderedPaneFlowTests` pinned
  the old rule and now pins the new one.
- **The table marks what changed since the last commit.** After a changed object's table is
  drawn, the same file at HEAD is parsed by the same format and compared
  (`MemberTableDiff`, Core, 6 tests): matched by section and path, never position, so an
  insertion marks one row rather than every row under it. Added rows green, changed amber
  with the committed value on hover, removed rows struck through where they used to be, and
  one summary line under the title. An object that changed outside its members (a comment
  in the SCL) says that, rather than a "modified" row over an unmarked table. Late results
  are dropped if the selection moved. Disabling the swap turns two flow tests red (checked).
- **...and it marked nothing on the owner's machine.** The log said the HEAD copy "is not a
  member table". TIA writes its XML with a UTF-8 byte-order mark; `File.ReadAllText` strips
  it, but `git show` hands it over as a character in front of `<?xml`, which `XDocument`
  rejects - so the committed side never parsed. The flow test's fixture had no BOM, which is
  why it passed. The parser now skips a leading BOM, the fixture is written with one, and
  removing the skip turns both marking tests red (checked).

**The code pane is the body alone.** The owner, on PT1: `BEGIN` and `END_FUNCTION_BLOCK` are
not necessary, the tab on the left should go, and the lines should not start at 22. The
format's hook is now `CodeBody` - the range between `BEGIN` and the last closing keyword alone
on its line - and `StSourceLayout.Body` drops blank edges, strips the indent every line shares
(character for character, so a tab is never taken for four spaces) and numbers from 1, keeping
each line's file line underneath. Raw still shows the file.

**Open: `CommitWhileIdeHoldsProjectTests` times out in the full suite.** Three of nine full
App runs on 2026-10-05 failed it at ~33 s (one 30 s wait plus setup); it passed every time
alone (5 of 5) and in the other six full runs. The first failure followed the Windows SDK TFM
change (503aedb), so that change is not ruled out; nothing in this session's rendering work
touches commit or catch-up. Its messages could not say which wait stalled - one even
snapshotted the log before waiting - so every wait now reports the shell's state and the log
*as it fails* (`Eventually.Until(…, context)`). The next red run should name the cause.

**And one older defect found by looking at it.** "Select a file to see what changed" sits
behind the diff and assumed whatever covers it is opaque. The interface split is not, so the
sentence showed through the code pane while the diff loaded. It now hides whenever a rendered
or web view is on screen.

**Also on this date, the blank page behind the browser views.** Both page views went blank in
the owner's window: `WebView2CompositionControl` renders through Windows.Graphics.Capture and
threw `FileNotFoundException` for `Microsoft.Windows.SDK.NET` from `OnApplyTemplate`, four times
on the UI thread, caught by the dispatcher handler and logged. Headless-Edge previews and the
suite had both been green - neither creates the control. The App and its two test projects now
target `net9.0-windows10.0.17763.0` (a `TargetPlatformVersion` property beside the old TFM is
ignored, measured), with the output folder pinned to `bin\Debug\net9.0-windows` so build.bat and
the tools scripts are untouched. **Cost: +23.6 MB** of SDK projection in the output. Rejected:
the HWND WebView2, which needs nothing but draws over the in-window toasts.

---

### A Markdown file's Rendered tab drew the raw text, and HTML had no page view at all

**Found 2026-10-04** by the owner, on `docs/outpost/PLAN.md`: Rendered pressed, and the pane
drew pipes, hashes and asterisks with line numbers - the same thing Raw draws. Nothing was
broken in the usual sense. `BuildRenderedContent` had arms for catalogs, member tables and the
project card, and everything else that was text fell through to `SourceRender`, whose layout
is the ST one. For code that is the right answer (highlighted source is what an editor shows);
for a document it is not an answer at all, and in a repository whose documentation *is* the
engineering log that was most of the files anybody opens outside the PLC tree.

**What it is now.**

- **Markdown renders as a page.** `Versio.Core.Text.Markdown` parses (pure, no I/O, 23 tests,
  and a test that parses every file under `docs/`); `MarkdownView` draws it as a FlowDocument -
  headings with rules, lists, GFM tables, fenced code, quotes, links. Relative links to other
  `.md` files in the repository open in the same pane; web links go to the browser through a
  logged command; anything else - an anchor, a path out of the repository, a missing file - is
  refused in the log. `MarkdownPaneFlowTests`; deleting the Markdown arm turns it red.
  **Hand-written rather than Markdig**, to keep a package out of `Versio.Core` for one tab; the
  parser's remarks list what it deliberately does not do.
- **HTML gets three views**, the owner's words: Raw (git's diff), Rendered (highlighted
  source, unchanged) and **Web page** (the page as a browser draws it). The page is a WebView2
  created on first use, with **scripts off, every non-file request refused, and links handed
  to the real browser** - opening somebody's branch must not run their JavaScript on this
  machine because a row was clicked. Never the default tab, for the same reason. The three
  chips are one enum, so no combination can show two views or none; `HtmlPaneFlowTests`
  breaks if a selection carries the Web tab onto a row without a page.
- **Rejected:** WPF's built-in `WebBrowser` (the retired IE engine draws modern HTML wrong,
  which is worse than not drawing it). **Cost:** the app's second package,
  `Microsoft.Web.WebView2`, BSD-licensed and in THIRD-PARTY-NOTICES.

**The FlowDocument did not survive the owner's first look (same day).** On PLAN.md's
four-column table it gave the last column almost no width - FlowDocument tables have no
automatic column layout, only equal or weighted shares - so its text wrapped a few letters to
a line and every row stood hundreds of pixels tall. Reproduced off-screen by rendering the view
to a PNG in the test harness. And the ```` ```mermaid ```` flowchart above it was its source,
because a diagram is JavaScript. Both are what a browser does, so the Rendered view is now
`MarkdownPageView`: `MarkdownHtml` (Core, 8 tests) turns the blocks into HTML with **every
character of the document escaped and links in `data-href`, never `href`**; the page runs only
mermaid (11.17.2, embedded, pinned by sha256) and its own click handler, under a CSP that
allows script from the app's origin alone; every request is answered from memory or refused.
Checked in headless Edge in both themes with the CSP enforced: tables size by content, the
diagram draws. The FlowDocument `MarkdownView` stays as the fallback when WebView2 cannot
start. Both page views use the composition control, so the branch menu and toasts still draw
over them, and share one browser process. A theme switch rebuilds the page;
`MarkdownPaneFlowTests` goes red if it does not (checked). **Rejected:** a WebView2 per diagram
inside the FlowDocument (one browser per chart, and the table problem untouched).

**Not covered by any test:** the browser itself (it cannot start in the suite) and the link
binding from the Markdown template to the shell's command. Both are owner-eyeball items in
STATUS. Also not done: the Git panel and History still show `.md`/`.html` as a diff only - the
rendered views are Project panel and Explorer, where the rest of the rendered views live.

---

### Twelve things the running window showed that no test could, and two defects under them

**Found 2026-09-21**, by looking. Until then Versio's UI had only ever been checked through
its log and the owner's eyes: the shell has no interactive desktop. The installed build is in
the Start menu now, so computer-use can see it — and one pass over the four panels of the
owner's own window, on this repository, found everything below. Branch `UiLook`, one commit
per item.

**The ones that were wrong, not merely plain.**

- **The project panel told its user to open the repository they had open.** Four projects in
  sync, and the middle of the panel said *"Open a repository to see what is in it"*. The
  empty-state trigger read `Tree.Count == 0`, which is true both with no repository and with
  nothing changed — the tree hides in-sync projects on purpose. It now reads
  `Sync.HasRepository`, and the hidden projects get an IN SYNC list of their own.
  `ProjectPanelCalmStateTests`; putting the old trigger back turns the window test red.
- **A second launch drew an empty window with a modal naming a pid.** Clicking Versio while it
  ran: "already open in another Versio window (pid 4552)". At startup the refusal is now a
  hand-over — the verdict carries a live local holder's pid, that window is restored and
  focused, and the new one closes. Switching repository inside a running window keeps the
  dialog. Verified live: the installed window came forward and the dev build logged the
  hand-over and exited. `SecondInstanceTests`.
- **A second Versio wrote nothing to disk.** Found while looking for that hand-over's log line
  and finding it nowhere. Both processes aimed at `versio-<date>.log`; the first holds it for
  writing, so every write from the second failed, reported once in its own log panel. It now
  writes `versio-<date>-<pid>.log`. Rejected: sharing the file for writing (interleaved lines
  with nothing saying whose). `RollingFileSinkTests`.
- **An idle window with CODESYS open wrote ~1,400 identical lines an hour.** The IDE rewrites
  its `.~u` marker every five seconds and each settle was logged twice — once by the watcher,
  once by the sync panel repeating it. The duplicate is gone and the watcher folds a repeating
  path through `RepeatCollapser` (first line prints, repeats inside five minutes are counted
  into the next). The event is never folded: the IDE closing is only ever seen as one.
- **The log badge counted Debug.** It climbed by sixty a minute with nothing happening.
  Collapsed, it now says "N new" for unseen warnings and errors, and is absent otherwise.
- **A white square in the dark diff view**, where the scrollbars meet: the stock ScrollViewer
  paints that cell from `SystemColors.ControlBrushKey` (`#F0F0F0`). Redefined as transparent
  in an implicit ScrollViewer style's resources; no bar test could see it.
- **Pointing at a tree row lit every folder above it.** The hover trigger read the
  TreeViewItem's `IsMouseOver`, which is true for every ancestor of the hovered row. Sourced
  from the row's own strip now — the trap the template already avoided for keyboard focus.
- **A disabled Commit stayed a bright accent slab.** Ghost's disabled trigger only dims text;
  on AccentButton the fill is the loud part.
- **A removed line starting `--` was parsed as a file header.** Found by a test written for the
  file titles below. `-- x` removed arrives as `--- x`; the parser matched header prefixes
  anywhere, so the line was dimmed, uncounted, and every line number after it one out (a
  Markdown rule or an SQL comment is enough). The parser now counts down each hunk's budget
  from its `@@` and checks for headers only between hunks; it also stops reading the `@@` line
  at its two range tokens, because the function context after it can start with `-`. Fixed
  in its own commit, `a5e914f`.

**The ones that were plain.** The pane said DIFF over everything, with the path trimmed from
its end so the object's name was what the ellipsis ate (`PaneHeading`); the Rendered/Raw pair
were filter chips whose off state reads as disabled (`SegmentChip`); the tree drew raw type
identifiers (`gvl_persistent`) in a code font while `ObjectTypeNames` sat unused, and nothing
before a row's name (`ProjectTreeNode.TypeName`, `TreeRow.IconKey`); an empty window said "open
a repository" with no button (now Open, bound to `ApplicationCommands.Open` and Ctrl+O, and
Clone); the caption said "Versio" above a brand row saying "Versio" (now `<repository> —
Versio`, so two windows differ in the taskbar); the history had no branch or tag labels (`%D`,
`CommitRef`); and a commit showed as raw `git show` text with no highlighting and no message
body — file header blocks are now one `FileTitle` per file with its counts, each file is
highlighted in its own language, and a header shows the body, author, size and the git
panel's object sentence (`CommitHeading`).

**Rejected.** Drawing a custom title bar to merge the toolbar into it: the caption is already
painted in the theme's surface colour, and custom chrome takes on snap layouts, the system menu
and the resize border for about 30 px. Glyphs per object *type*: which icon a POU deserves is a
vendor's word; the icons are per tree *kind*, which every vendor shares.

**Measured on the way.** `Versio.App.Tests` takes **3.6–3.7 minutes** on this machine — on
`main` (3.70 min, 686 tests) and on `UiLook` (3.63 min, 732 tests) alike, measured one after
the other on 2026-09-22. The "~50 s" in the docs had gone stale; the slowest tests are the
three theme switches (42 s, 22 s, 14 s on `main`), which repaint every window earlier tests
built and never closed. One unrelated flake seen twice: `HistoryCopyTests` fails with
`CLIPBRD_E_CANT_OPEN` when another program holds the clipboard, and passes on rerun.

**Still owed to a person.** Everything visual on this branch: see STATUS *What is next* item 2.

---

### The Rendered view numbered an ST object's code differently from the IDE

**Found 2026-09-20**, from the owner's side-by-side screenshots of `FB_Alarm` in Versio
and in CODESYS, while asking for the blue `@@` line to go and for the variables to be
visibly separated from the code.

**The defect.** The Rendered tab drew a file by wrapping it in a made-up diff of unchanged
lines (`SourceRender.PseudoDiff`) and passing that to `DiffView`. So it showed the file's
own line numbers, and the .st mirror joins the declaration and the implementation into one
file with `// --- BEGIN IMPLEMENTATION ---` between them. The IDE shows two panes, each
numbered from 1, and its compiler messages quote those numbers: `IF NOT Settings.Latch
THEN` was **line 44 in Versio and line 16 in CODESYS**. A reader given a compile error could
not find the line. The made-up diff also brought along an `@@ -1,47 +1,47 @@` header, a second
gutter that always repeated the first, an empty marker column, and a line 47 that was only
the empty string after the last newline.

**The fix.** `Versio.Core.Iec.StSourceLayout` lays the file out as rows: a Declaration
label, the declaration, an Implementation divider standing in for the delimiter line, the
implementation numbered from 1. The numbers are `StTextCodec.Split`'s rather than an
approximation of them — the delimiter is found the way Split finds it, and the whitespace-
only lines Split trims from each section's edges (the codec's own blank line either side
of the delimiter) are left out. A delimiter that shares its line with code, which Split
would cut mid-line, falls back to plain file numbering rather than a divider drawn in the
wrong place. `DiffView.SourceRows` draws the rows with one gutter; Raw keeps the pseudo-
diff, `@@` and file numbers included, because Raw is where the file is shown as it is.
This lives in `Iec/`, beside the codec, because the delimiter is part of the IEC
support TwinCAT shares, not CODESYS's.

**One trap on the way.** The first version of `SourceCodeLine` was `(Number, Text)`. A
blank line 13 in the declaration and a blank line 13 in the implementation are then the
same record, and a WPF list holding two equal items selects both when either is clicked
(and a copy pastes both). `FB_Alarm` has exactly that pair. `FileLine` is on the record so
no two rows are equal.

**Evidence.** `StSourceLayoutTests` (11) measures every number against `Split` itself.
`RenderedSourceTests` (6) covers the wiring. Two deliberate breakages each turned one
test red: rebinding the Rendered template back to `PseudoDiff`, and skipping the
generation bump for source rows (which let a stale diff parse replace them). An offscreen
render of the real `FB_Alarm.st` showed declaration 1–25 and implementation 1–18, matching
the CODESYS screenshot line for line.

---

### With TIA Portal open, a commit recorded a stale mirror and a push erased the export debt

**Found 2026-09-14**, double-checking the LFS fix below and answering *"is there no way to
push while TIA is open — do I really have to close it first?"* Three defects, each read out of
the code, then reproduced by a test that fails against the old code.

1. **A commit went through with a stale mirror.** The commit gate saw a changed project and
   asked for an export. With the Portal window holding the project, that export refuses as
   `BlockedByOpenProject` — a directory project cannot be exported from a copy
   (`ExportHeldViaCopy` is false) — and that verdict is *deliberately* a wait rather than a
   failure, so it neither fails the binding nor feeds the breaker. `CommitAsync` stopped only
   on `IsFailure`, so it staged the old mirror beside the new project and said "Committed."
   **Reproduced end to end:** with the new refusal disabled, the App test's HEAD moved
   (`3cb87ec` → `5b314d7`). **Fix:** `CommitGate.StillStaleAfterExport` — only `Completed` and
   `NoChange` leave a stale binding safe to commit; any other verdict, or no outcome at all,
   refuses with the export's own sentence.
2. **A push erased the export debt.** `ApplyAsync` recorded the project's and the mirror's
   *current* hashes as `LastSynced` on every release to Clean, push included — the value the
   hash gate and the commit gate read as "the mirror reflects this". `dev.md` already said it
   moves on export or import only. So: save in Portal while it holds the project, push anything,
   close Portal — the retry answered `NoChange`, the gate said up to date, and the mirror stayed
   stale until the project changed again. The same held for a mirror edit pushed before its
   import. **Fix:** a push moves `Baseline` only. Discard and failure acknowledgement are
   unchanged: they pass an empty project hash, which forces the next event to export.
3. **The gate did not recognise the program store.** `CommitGate.Owns` matched the pointer
   exactly and the mirror by prefix, so a commit of only `System/PEData.plf` was "no tracked
   project is part of this commit" and skipped the export. Whether Portal rewrites the `.apXX`
   on every save is unmeasured — the fixture's history is one commit — so how often this was
   reachable is unknown; the fix does not depend on it. **Fix:** `BindingFreshness.PayloadPath`,
   filled from `PayloadPathFor`, empty for a file project, the repository root, or a folder
   outside it.

**What working with Portal open now looks like.** Pushing commits already made works — a push
never touches the project. Committing PLC changes is refused with a sentence naming what to
close; close the *project*, not Portal (the marker follows the project, TIA `DRIVING.md` §5).
Committing unrelated files works — untick the project's files.

**Mutation checks.** Refusal disabled in the commit flow → the App test red (the HEAD move
above). The push condition removed → both push tests red, while "a push after a real export
leaves nothing owed" stays green, so the fix does not turn every push into a re-export.
Payload folder emptied at the orchestrator → the orchestrator-level gate test red, the pure
gate tests green.

**Rejected:** re-evaluating the gate after the export to decide the commit. It would hash a
half-gigabyte TIA tree a second time per commit to learn what the outcomes already say, and a
partial read of a file Portal holds open would make the answer depend on timing.

**Not done:** exporting *without* closing, by attaching to the running Portal — unmeasured, needs
the Siemens machine, and an attached export reads unsaved in-memory edits, so it would have to
refuse or ask to save while the project `IsModified`. Also unmeasured: whether git can read
`PEData.plf` while the Portal window holds it. And the LFS entry below has a measured side
effect not yet surfaced in the app: in a repository adopted before those rules, adding them
makes git report every already-committed store file as modified until the next commit
converts them to pointers (history keeps the full blobs).

---

### A TIA project's program store went into git outside LFS, and this repository's own TIA rules resolved wrong

**Found 2026-09-14 answering a question** — how Versio tracks an `.ap20`, and whether the
`.plf` files are what changes.

**The gap.** A TIA Portal `.apXX` is a ~150 KB pointer; the program is `System/PEData.plf`
(~445 MB on the gate fixture) and the store beside it. Adoption's `.gitattributes` step wrote
one rule per registered extension — `*.ap17` … `*.al21` — which protected the pointer and
nothing else. An adopted TIA repository would commit `PEData.plf` as an ordinary git blob;
the commit's large-file check would ask first, which is a backstop, not a rule. This
repository never showed the gap, because its TIA rules were written by hand.

**The hand-written rules were wrong as well, and only `git check-attr` said so:**

1. `merge: lfs` on the `.ap21`, `PEData.plf` and everything under `IM/` — `merge=lfs` after
   `-merge`, the defect `GitAttributesPlan` already documents: git-lfs installs no such
   driver, so git would text-merge the LFS pointer.
2. `diff: unset` on every mirror file. The folder-wide LFS rule was undone for the mirror
   with `-diff -merge`, and `-diff` means binary-for-diffing: `git log -p` on an exported
   `.scl` printed `Binary files differ`.

Both fixed in their own commit with `!diff !merge`, checked with `check-attr` and
`git log -p` before and after, and `git status` showing no fixture file disturbed.

**The fix.** `IPlcProjectFormat.BinaryPayloadPatternsFor(projectPath)`, default none. TIA
answers one scoped rule per program-store folder — `System/**`, `IM/**`, `XRef/**`,
`AdditionalFiles/**`, `UserFiles/**` — listed from `git ls-files` over the fixture rather
than from memory. `RepoSetup` writes them at adoption and `TopUpRepositoryRulesAsync` on
every open, so repositories adopted before the rule get it. The planner **withholds** any
folder rule that would reach a mirror, and `RepoSetup` logs the withheld ones as a warning.

**Rejected:**

- *The whole project folder* (`plc/Station/**`). The mirror lives inside it, so the
  reviewable export would become binary — defect 2 above, generalised — and a project at
  the repository root would turn the rule into `**`.
- *Global extensions* (`*.plf`, `*.idx`, `*.db`). Repository-wide on the strength of one
  project, the overreach `TransientPatternsFor` already refuses; and the store is not only
  `.plf` and `.idx` — `XRef/` is a `.db`, `IM/` a dozen index extensions.
- *Treating an existing `dir/**` rule as covering `dir/System/**`.* That would suppress the
  protective rule underneath somebody's `plc/** text eol=lf` and leave a binary as text.
  Instead this repository carries the five rules literally, so opening Versio on it
  appends nothing.

**The double pass, by mutation.** Removing the project path from the *adoption* call left the
shell-level test green: the open-time top-up runs straight after adoption and wrote the same
rules, so nothing was watching adoption. A direct `RepoSetup.AdoptAsync` test was added. Then
with the adoption path, the top-up path and the withhold guard each disabled, the adoption
test, the top-up test and all three withheld tests went red.

**Not done, on purpose:** `src/` is among Portal's top-level names but carries nothing in the
fixture, so it is not claimed. A folder a later Portal version adds is not covered. TwinCAT
keeps the default: its format calls `.TcPOU` and `.plcproj` the program, and its fixture
commits them without LFS. **Noticed, not fixed:** for a project at the repository root,
`TransientPatternsFor` builds from `PayloadPathFor`, which at the root answers with the
pointer file itself, giving `Station.ap21/Logs/`. The new method computes the folder directly
and does not share it.

---

### The refresh gate did not stop the auto-stash flake: the guard still did not wait

**Found 2026-08-27**, after the 2026-08-26 gate: a full `Versio.App.Tests` run failed two
`AutoStashFlowTests` (`A_stash_dropped_from_a_terminal_is_forgotten_rather_than_offered`,
`Restoring_one_stops_offering_the_rest_until_the_next_arrival`); the class re-run was 9/9
green. Same shape as before — switch abandoned, record not pruned, offer never made —
on binaries that already serialised refreshes through `_refreshGate`.

**The remaining hole.** The gate serialises refreshes against each other. `Ask()` did not
wait for it. A fire-and-forget refresh (watcher, or a Manual follow-up from
`OnRefreshCompleted`) still held `GitRead` Busy, or still had a stale `Changes`, when
the next `SwitchBranchAsync` evaluated the guard. Tests answer every guard question
`StashAndProceed`; git stash on a clean tree creates nothing; the switch is abandoned
and `OfferStashedWorkAsync` never runs. In the app the dispatcher makes the overlap
rarer; in the tests `OnUi` runs inline, so the window is the in-flight `StatusAsync`.

**Fix:** `ClearForContextChangeAsync` drains the gate and re-reads git (no Busy) before
`Ask()`, then releases so WaitThenProceed cannot deadlock with an export that itself
refreshes. Manual follow-ups demote to `WorkingTree` so they do not light GitRead Busy.
The overlap is now a test: `RefreshAsync` in flight, then switch on a clean tree, must
not ask and must land on the other branch.

**What was rejected:** holding the gate across `Ask()` / WaitThenProceed (export ending
in a refresh deadlocks on the same semaphore). Calling `RunRefreshAsync` from inside
the drain (re-entrant `WaitAsync` on the gate we hold). A timeout.

Tests: `AutoStashFlowTests.A_switch_waits_for_an_in_flight_refresh_rather_than_stashing_a_ghost`.
Ten consecutive green class runs (10/10, 22 s each) after the drain.

---

### The data-block pane was aligned text and dropped external access

**Found 2026-08-26**, eyeballing `FIT100Liter_Type` on CleaningStation. Rendered listed
NAME / TYPE / START VALUE / COMMENT as a sparse monospace listing: no grid lines,
no row chrome, and the Interface `BooleanAttribute` flags
(`ExternalAccessible` / `ExternalWritable`) were never read. Every Input member
on that block is locked from outside; nested UDT members omit the flags and
are the IDE default (reachable). The pane showed neither.

**Fix:** the TIA parser collapses the two flags to one opaque `ExternalAccess`
string (`None` / `Read` / `Read/Write`); omitted is the default, not `None`.
The pane is a bordered grid with shared column widths. `ExternalVisible` is
HMI-engineering visibility and is not this column.

**What was rejected:** a WPF `DataGrid` (stock chrome, no theme tokens in this
app). Putting the flag names in Core or in the column header (the seam).

Tests: `TiaPortalMemberTableTests` (explicit flags, omitted default, visible-only,
subelement inherit), `MemberTablePaneFlowTests` (dispatch still fills the field).

---

### TIA data blocks opened as XML in Rendered because nothing parsed the Interface tree

**Found 2026-08-26**, looking at CleaningStation: `FIC100_Type` (an instance DB) and
`Global_Limits_Interlocks` (the one global DB) showed as highlighted SimaticML in the
Rendered pane. The Interface / Member / StartValue tree is already in the native XML.
The 2026-08-22 `GenerateSource` work made a readable `.scl` sibling (`SCL_REVIEW`);
the fixture on disk is still XML-only, and even after a re-export `PrimaryFile`
prefers `.scl`, so Rendered would have been highlighted SCL rather than a table.

**Fix:** the TIA format parses InstanceDB / GlobalDB XML into a vendor-neutral
`MemberTable` (name, type, start value, external access, comment, dotted path). The shell asks
`PlcFormats.All` for a table before falling through to `SourceRender`, and walks
every sibling file so a `.scl` primary still tables from the native XML. The
pane caption does not name a vendor. Raw is unchanged.

**What was rejected:** a YAML sidecar at export (needs a re-export, second source
of truth). Putting SimaticML XPath in App or Core (the seam). Rendering SimaticML
in the diff — that was rejected on 2026-08-22 as a second language for one object,
and this does not undo it. UDT / FB Interface tables — same schema, later pane.
Edit-in-table write-back.

Tests: `TiaPortalMemberTableTests` (inline XML, including FB/UDT/SCL returning
null), `MemberTablePaneFlowTests` (dispatch, `.scl`+XML sibling, FB still source),
`SecondVendorTests` (default `TryParseMemberTable` is null).

---

### The suite's last flake was a real race: nothing serialised the status refreshes

**Found 2026-08-26 by hunting the `AutoStashFlowTests` flake**, which had been documented as
"contention for the git binary and the filesystem… wants a different fix" and had grown worse:
flipping between 9/9 and 2–3 failures on identical binaries, a different test each time,
occasionally even running alone.

**The race.** `StatusRefreshPolicy` gates only the *watcher's* triggers. Every verb ends with
an awaited `RunRefreshAsync`, and a watcher-started refresh can already be in flight when it
begins — two `git status` reads overlapping, and whichever finishes **last** wins. A read that
started *before* an edit, a stash or a checkout and applied *after* the fresh one reconciled
`Changes` back to a state the tree was no longer in. The next switch's guard then reasoned
about a tree that did not exist: it saw stale-dirty, offered a stash, the stash correctly
found a clean tree and created nothing, and the switch was **silently abandoned** — which is
exactly the shape of every observed failure ("expected 2 prompts, got 1"; "record not
pruned"; "offer never made"). In the app the dispatcher makes the interleaving rarer, not
impossible; in the tests there is no dispatcher, so the fire-and-forget refresh runs truly
concurrent with the flow, and load widens the overlap — which is why the rate tracked machine
load and why disposal quirks masked it.

**The fix is a gate, not a timeout:** `RunRefreshAsync` now serialises the whole refresh
through a `SemaphoreSlim(1,1)` — the apply half awaits too (`Sync.RefreshTreeAsync`, the
merge-state probe), so serialising only the read would be the same clobber by instalments.
Acquisition order is completion order, and an awaited caller queues behind any stray, so by
the time it reads, every earlier reading has been applied and superseded. Reentrancy was
checked before adding it: nothing inside `RefreshCoreAsync` calls back into
`RunRefreshAsync`. `OpenAsync`'s initial refresh called `RefreshCoreAsync` *around* the
funnel and now takes the gate too — the watcher deliberately starts before it, so the overlap
was reachable on every open.

**Two accomplices, fixed with it:**

1. **The auto-stash refusal lived only in `StatusSummary`** — which "keeps only durable
   state", by `Notifications`' own remarks — so the next refresh overwrote "Nothing was
   stashed, so nothing was switched" with "No changes" within a second, in the app as in the
   test that flaked on it. An abandoned switch is an operation result; it is a notification
   now, and the test asserts the notification.
2. **`OpenWithDirtyTreeAsync` disposed the shell it returned** — `using var shell` followed
   by `return shell`, the same premature-disposal bug `GitPanelChromeTests.Build` documents
   catching in itself. Every test on that helper ran watcher-less against a torn-down shell,
   which mostly *hid* the race and sometimes raced the disposal itself.

**Measured:** eighteen consecutive green runs of the class on an idle machine — twelve on
the gate + notification + helper fixes, six more after the open-path hardening — against a
pre-fix rate of roughly one failure per three runs under the same conditions, plus a fully
green 672/672 full-suite run. The class also now finishes in 21–23 s per run where it took
63–78 s before; the cause of that was not chased and no claim is made beyond the
observation, but a test class that gets three times faster when its races are serialised
was evidently spending its time somewhere. A second hole — `Ask()` still did not wait
for that gate — showed up the next day under a full-suite run; see the 2026-08-27
entry. The flake's other historical shape —
`GitHealthTests` and friends under full-suite load — shares the mechanism wherever a test
asserts state a stray refresh can rewrite, and the gate is shell-wide.

---

### Ctrl+F skipped the explorer, and a failed git show looked like a copy

**Found 2026-08-26 by an audit of the UX remainder**, on `UxRemainder` before it shipped.
Two defects, both from a first pass that encoded a false fact.

1. **`Ctrl+F` did nothing on the explorer.** The window routed Project / Git / History and
   skipped the fourth rail on the claim that the explorer had no find box. It has had one
   since it shipped (`Explorer.Search`). A test named `Ctrl_F_does_nothing_on_the_explorer`
   locked the miss in. `FocusFindBox` now lives on that panel too, and the theory that
   walks the rail includes it.

2. **`CommitMessageAsync` returned `""` for a failed `git show`.** Copy then said
   "Copied the message of …" while clearing the user's clipboard with empty text — WPF's
   `Clipboard.SetText` **accepts** empty (measured 2026-08-26; WinForms' is the one that
   rejects it), so nothing threw, and the defect was a success message about a copy that
   never happened. Amend's first press loaded the empty string and asked the user to
   press again, which looped. Failure is now null; empty is a blank message; copy and
   amend both name the miss. The verification pass also found the empty-payload guard
   turning "copy an empty subject" into a silent no-op — reachable, because git accepts
   a commit with an empty message — so that copy now names the empty instead of saying
   nothing.

---

### Packaging audit: the state rescue could crash startup, and the TIA payload shipped from an uncleaned bin

**Found 2026-08-24 by an audit of the packaging pass**, requested by the owner before the
branch was committed. Four defects, none of which had bitten yet; all fixed the same day.

1. **`AppUserPaths.Migrate` could take the app down with it.** The copy-out of legacy
   settings and logs ran unguarded inside `OnStartup` and two constructors. Two ordinary
   situations make `File.Copy` throw: an older build still running holds today's log open,
   and two new instances launching at once both pass the exists-check before one copies.
   Either exception was a Versio that will not open — on exactly the machines the migration
   exists to rescue. Now best-effort per file (the originals are never deleted, so a skipped
   file is copied next launch); creating the durable root stays unguarded on purpose,
   because if that fails the settings store and log sink fail on the same path a moment
   later and *that* should surface. The test locks a log with `FileShare.None` mid-migrate.

2. **The TIA host payload was copied from `bin\Release\net48`, which nothing ever cleans.**
   A renamed assembly or dropped dependency lingers in an incremental bin and would ship in
   every installer from then on. The app and the TwinCAT host already published into wiped
   folders; the one net48 payload now builds with `-o` into a wiped `publish\tia` too.

3. **The Velopack/vpk version pact was a comment.** The csproj says the `Velopack` package
   must match the `vpk` tool version and nothing enforced it — bumping one ships an
   installer whose in-app hooks and packer disagree. An arch test now reads the pin from
   `third_party/manifest.json` and asserts the csproj matches.

4. **Two script edges.** `git lfs version` and the config pin ran between the two SFX
   existence polls, i.e. against a tree the extractor could still be writing — both polls
   now finish before anything from the payload is executed (GCM lands among the last files,
   so its appearance is the better "extraction finished" signal). And the final
   Setup.exe/MSI check accepted any file in a releases folder that is never cleaned; it now
   requires `LastWriteTime` at or after this run's vpk start.

Verified with a full pack after the fixes: green end to end in 3 m 47 s, `publish\tia`
holding exactly the 12 files of the current build. Still open from the same audit, not
code: what the per-machine MSI actually does belongs on a clean VM before anyone is handed
it, and the update feed plus in-app `UpdateManager` check remain unbuilt (STATUS item 5).

---

### Setup.exe lived in the same folder as settings.json and deleted the recent list

**Reported 2026-08-24 by the owner**, from the first packaged install: the repository
dropdown was empty after Setup.exe. `%LOCALAPPDATA%\Versio` on that machine was the
Velopack layout (`current\`, `packages\`, `Update.exe`) and `settings.json` was gone.

Settings and logs used that folder because it was a convenient name, before an installer
existed. Per-user Velopack *is* `%LOCALAPPDATA%\{packId}`. The first Setup.exe that
occupied it replaced the directory F5 had been using. An uninstall of that layout would
have taken the logs too. Update replacing `current\` was never the bug; sharing the
install root with user state was.

**Fix:** user state is `%LOCALAPPDATA%\VersioUser\` (`settings.json`, `logs\`). A one-way
copy brings leftover files out of the packId folder and will not overwrite a file already
in the durable location. Repositories themselves were never in AppData.

The recents this install already deleted cannot be reconstructed from the Velopack
directory. Open the repositories once on the new build.

---

### PortableGit's helper-selector kept asking which credential helper to use

**Reported 2026-08-24 by the owner**, from the packaged app: `CredentialHelperSelector`
(`<no helper>` / `wincred` / `manager`) returned on every git credential use even after
picking Manager.

PortableGit is Git for Windows as a folder, not the Git installer. The installer would have
written `credential.helper=manager` during setup; the portable archive instead ships
`helper-selector` so the first credential use can ask. That window is a GUI, so
`GIT_TERMINAL_PROMPT=0` does not suppress it — background fills (the poller, Verify,
HasStored) pop it again. "Always use this from now on" writes PortableGit's system
gitconfig. A per-machine MSI lives under Program Files, which a normal user cannot write,
so the tick cannot persist.

Not a reason to run the Git for Windows installer on top. MinGit was already rejected
(no GCM). Login is still GCM via Versio's Sign in; the selector was in the way of that.

**Fix:** pack writes `git config --system credential.helper manager` into the payload;
every bundled spawn also passes `-c credential.helper=` then `-c credential.helper=manager`
(empty value is git's documented reset of a multi-valued helper list). PATH git is left
alone.

---

### A merged branch reported "Up to date" with an upstream that no longer existed

**Reported 2026-08-23 by the owner**, from using the app: the `continue` branch had been
merged and deleted on GitHub, and after pressing Fetch the panel still said *"Up to date with
origin/continue"* and offered *Go to pull request*. `ls-remote` against the real remote
confirmed only `main` existed; the local repository still had the `origin/continue`
tracking ref.

Three defects stacked, each hiding the next:

1. **The ordinary fetch did not prune.** `FetchAsync` ran plain `git fetch`;
   `FetchPruneAsync` existed and pruned correctly but ran only after an *in-app* remote
   deletion (`ShellViewModel.ReportDeletionAsync`). A branch the forge deleted — the
   ordinary end of every merged PR — kept its stale tracking ref, porcelain kept counting
   against the ghost, and 0/0 rendered as InSync. Fixed by folding `--prune` into
   `FetchAsync` and deleting the second method: two fetches whose only difference was
   honesty meant every caller had to know which one told the truth.

2. **The poller knew and threw the answer away.** `LsRemoteAsync` returned one string, so
   "the branch is not there" (success, no output) and "the remote did not answer" (failure)
   were the same emptiness. `RemotePoller` recorded a deleted upstream as
   `ProbeOutcome.Failed` every interval — *"probe origin/continue: no answer"* — and
   `FailuresBeforeHalt` of those halted the watch as unreachable, over a remote that was
   answering perfectly well. Fixed with `RemoteBranchProbe { Answered, Head }`; a gone
   branch with a stale local ref now triggers the (pruning) fetch once, a gone branch
   already pruned is a quiet probe, and a branch recreated on the remote is ordinary
   movement, so the state heals itself.

3. **Even pruned, the state was unspeakable.** Porcelain v2 reports a gone upstream *by
   omission* — `branch.upstream` with no `branch.ab` line — and `PorcelainV2Parser` let the
   missing line decay to `Ahead == Behind == 0`, which `RemoteStanding.For` rendered as
   InSync. `TrackingState` simply had no member for the situation. Fixed with
   `BranchInfo.UpstreamGone` (the parser remembers whether `branch.ab` appeared at all) and
   `TrackingState.UpstreamGone`, amber: *"origin/x is gone from the remote — deleted after
   a merge, usually. Push to republish it, or switch to another branch."* Push stays live
   (it recreates the branch); Pull goes dead — the one upstream-holding state where the
   useless-pull-is-harmless argument fails, because the pull is a guaranteed error. The
   merge-request button blocks with *"push it again first"* rather than offering a create
   link that would 404.

`DeletedUpstreamTests` proves the chain against real git, including the pre-fix lie: the
test asserts the status *before* the fetch still reads InSync, so if git ever starts
reporting the loss unprompted, the obsolete half of this reasoning fails a test rather than
lingering. The parser flag survived a deliberate mutation check (`&& false` on the flag
assignment: exactly one test failed).

Worth keeping: the badge had been lying for as long as the app existed, and it took a
*person* noticing "I deleted that branch" to see it. States that render as a healthy
default — 0/0, empty string, "up to date" — deserve suspicion precisely because no test of
the healthy path can catch them impersonating it.

---

### TwinCAT create used the I/O-probe argument order; delete called a method that is not on the interface

**Found 2026-08-22**, walking create / rename / object-delete against live XAE
(`Create_rename_and_object_delete_round_trip` on a `%TEMP%` copy of
`TC3_PlcSample_reStrText`). The planner and host already emitted those actions;
the Fact is what proved they did not.

Three refusals, one host method.

1. **`CreateChild(name, 1, "", subType)` treats `1` as `nSubType`.** The I/O probe
   measured that order on TIID in 2026-08-20: the child arrived as `ItemSubType` 1,
   not 111. The PLC host copied it. XAE refused a function block with
   `Couldn't create 'VersioGateFb_…'!`. Beckhoff's signature is
   `CreateChild(name, nSubType, before, vInfo)` — subtype 604 for an FB.

2. **Empty `vInfo` is not "default to ST".** After the arity fix, XAE threw
   `Must specify valid information for parsing in the string.`
   `IECLANGUAGETYPES.IECLANGUAGE_ST` is `1`. Passing that integer creates the POU.
   `DeclarationText` / `ImplementationText` then took the packed file.

3. **`ITcSmTreeItem` has `DeleteChild` on the parent, not `Delete` on the item.**
   `item.Delete()` is not on the interface. InvokeMember failed, the host swallowed
   it as a warning, reported success, and the next export still had the POU.
   `Parent.DeleteChild(Name)` after the PathName + live-hash check removes it, and
   `File.SaveAll` persists that.

Rename (`Name = newName`) was already right; pairing used `departed` because TwinCAT
export does not write `shash` and the temp tree is not git. Folder move is still
Name-only — not this Fact.

**Fix.** `HostImport.CreatePou` calls `CreateChild(name, subType, "", 1)`.
`HostImport` delete looks the object up by PathName, hash-verifies, then
`Parent.DeleteChild`. Gate: 6/6 in 5 m 14 s, 2026-08-22.

---

### The diff pane outlived the thing it was showing

**Reported 2026-08-22 by the owner**, from using the app: "if I click on a file under the
changed files panel, but then I move to commit history, the diff panel needs to clear" — and
"very often a file loaded on the diff keeps showing even after it's committed".

Two defects, one pane.

1. **The rail never touched the pane.** `ActivePanel` toggled panel visibility and woke the
   remote poller; nothing emptied `SelectedDiff`. A diff from the changed list stayed under
   the history, a commit's patch under the explorer.

2. **After a commit, the refresh re-read the file on purpose.** The rule — reload the shown
   path when its status moved — was written so an editor save updates the open diff, and it
   cannot tell "status moved" from "left the list". For a file that had just been committed
   or discarded it showed the whole file under a small "No changes right now" note, which at
   a glance is the change still being there.

**Fix.** `ClearPane()` is the one place the pane's seven fields empty (the repository reset
did it inline; the rail did nothing). The rail calls it and lets go of every panel's
selection; the refresh calls it when the shown path was in the changed set and is no longer
in the status. A path still listed reloads as before —
`A_file_still_in_the_list_keeps_its_diff_across_a_refresh` guards the editor-save case.

**Letting go of the row is half the fix, not polish.** An already-selected row raises no
selection change when clicked again, so a cleared pane beside a still-highlighted row would
stay blank on the way back. `TreeView.SelectedItem` is read-only, so `TreeRow`, `CommitRow`
and `CommitFileRow` carry `IsSelected`, bound two-way in the three container styles, and the
view models null their selection through it. Stale results are dropped too: a click followed
by a rail switch inside the time git takes landed the patch in the emptied pane —
`ShowPathAsync` and the two commit readers now check the pane still points at their target
before writing.

Mutation-checked one guard at a time: without the rail call four of seven tests go red,
without the refresh branch the committed and discarded cases, without the stale check the
late-diff case. Tests: `PaneClearingFlowTests`.

---

### TIA data blocks exported as SimaticML because the language was `DB`

**Found 2026-08-22**, looking at CleaningStation in the project tree: 24 data blocks,
23 of them instance DBs, every one a `NATIVE` `.xml`. Openness reports
`ProgrammingLanguage` as `DB`, and `ExportBlock` only called `GenerateSource` when the
language looked like SCL or was blank. A start-value table opened as a thousand-line
SimaticML blob.

`IGenerateSource` is on `PlcBlock`, including DBs. The header parser already knew
`DATA_BLOCK`. The hole was the language gate, not a missing API.

**Fix:** try `GenerateSource` for DBs too. The `.scl` is `SCL_REVIEW` (export-only, lock
glyph); SimaticML stays `NATIVE` and is what import applies. Making the text importable
first is how property accessors emptied on 2026-08-18, and instance-DB
`GenerateBlocksFromSource` has not been walked against live Portal. The pane prefers
`.scl` over `.xml` so the readable layer opens once a re-export has written it.

**What was rejected:** treating DB source as `SCL` + `XML_REVIEW` (the function-block
shape) without a mutating gate Fact. Rendering SimaticML in the diff without
`GenerateSource` — that would be a second language for one object.

---

### Two-line change rows were clipped into a 24 px tree row

**Found 2026-08-21**, from a screenshot of the git panel: the filename of one row drew on top
of the path of the row above it. Not a density choice. `ChangeRowTemplate` is two caption
lines (object name + directory) and the type chip, and every list row used `RowItem`, whose
`Height` is `Size.Row.Tree` — 24 px, on purpose, for the **project tree**. The
`ContentPresenter` is vertically centred, so both lines were clipped into the same strip as
the next row's first line.

Geometry tests asserted sideways overflow and `ActualHeight > 0`. They never asked whether a
row was tall enough for both lines, and they populated two lists so the overlap was easy to
miss in the numbers.

**Fix:** `Size.Row.Change` (40 px) and a `ChangeRowItem` style that top-aligns. The tree stays
24 px. The git panel is one `CHANGES` list now — staging is no longer a visible verb — so
there is only one container to point at it.

---

### Stop was a caption. Cancel did not kill. Try again never lit.

**Found 2026-08-21**, reviewing why a running export cannot be stopped, the window will not
close, and Task Manager kill of Versio leaves the toolchain running. The cancel plumbing
existed. Four independent holes made it a lie.

1. **TIA and TwinCAT sat in `WaitForExit(timeout)` and only then looked at the token.**
   CODESYS honoured the token once the process was running, but all three vendors ignored
   it for the fifteen-minute machine mutex. "Stop it and continue" waited out the job.
   The popup that "does nothing" is `WhenIdleAsync` hanging on a runner that never dies.

2. **A cancelled `RunAsync` threw after the direction lock was already claimed.**
   `ExportAsync` / `ImportAsync` had no `catch (OperationCanceledException)`. The throw
   left fire-and-forget `_ = SyncSettledChangeAsync`, the lock stayed in-flight, and
   Try again never lit. `ApplyAsync` after cancel used the cancelled token, so even a
   catch that persisted Failed would throw again before `state.json` was written.

3. **Close during import was a hard refusal, and close-Stop called `CancelCancellable`.**
   Changing the guard without changing that call would still wait out an import:
   `CancelCancellable` skips `WorkKind.Import`. There was no Stop on the busy chrome.

4. **No Job Object.** Hidden `--noUI` CODESYS survives Task Manager kill of Versio.exe.
   TIA `WithoutUserInterface` and TwinCAT `TcXaeShell.DTE` are COM servers, not Host
   children; `Kill(entireProcessTree)` on Host skips `TryDispose` / `TryQuit`.

**Fixed the same day.** Status-bar Stop (and Try again when Failed) beside `Busy.Message`.
Stop and close-Stop call `CancelRunning`, which cancels every busy scope including an
import. CloseApp during import asks Stop / Wait / Don't close; branch-switch still
refuses. Shared `ChildProcess.WaitForExitOrKill` / `WaitMutex`. Orchestrator catches OCE
and persists `ExportFailed`/`ImportFailed` with `CancellationToken.None` and explanation
"cancelled on request". Session Job Object with `KILL_ON_JOB_CLOSE` for CODESYS.exe and
git.exe only — Host.exe is left out so it can Quit COM; hosts watch `VERSIO_PARENT_PID`
and stdin EOF, then `TryDispose`/`TryQuit`. TIA Portal and TwinCAT XAE PIDs are snapshotted
before Host starts so a cancel reaps only the COM server this run created. Restore a backup
if a killed import leaves a project that will not open.

**What the log says.** Stop names the operation (`cancelled '…' on request`) and does not
pretend the session is ending — that sentence is logoff only. An import Stop is Error:
`killed '…' mid-import; restore a backup if the project will not open`. Runners then say
whether they were still waiting on the machine mutex, closed stdin so the host can Quit,
killed a CODESYS pid, or reaped leftover Portal/XAE this run started. The lock persists
`cancelled on request` so Try again lights. A cancelled git.exe is Warning, not silent.

**What was rejected:** putting Host.exe in the kill-on-close job (that is how Portal/XAE
survive a parent death today). Auto-restore on a killed import (Restore stays a button).
Changing `WorkKind.Import` off `MustFinish` (the switch path still needs the filter).

---

### PR #15 merged with two suites deterministically red

**Found 2026-08-21** by an audit whose first act was running the fast loop. `main` failed
`Versio.Arch.Tests` and `Versio.Plc.Codesys.Pure.Tests`, both broken by that morning's
IO-HArdware merge — thirty seconds of tests that were evidently not run before the two
commits (`32619b5` "COMMIT", `6b2856e` "commit more!") went in. Two distinct defects,
one cause:

1. **A third `Guid.NewGuid` caller appeared in `Versio.Core`.** The held-export scratch
   isolation named its per-run folder with `Guid.NewGuid().ToString("N")[..8]`.
   `PurityRuleTests` pins that exemption to two callers precisely so a third "has to be
   argued for rather than merely typed" — the pin worked the first time anyone ran it.
   Fixed the same day by naming the folder through `LoggerExtensions.NewOperationId()`,
   the exemption that already exists.

2. **The fixture moved to schema 10 and its pinned test stayed pre-schema-9.**
   `MachineWithMembers` was regenerated by the current exporter: 20 → 24 objects, the
   devices catalog added, members moved from dotted stems beside their owner
   (`FB_Pump.Speed.xml`) into the owner's directory (`FB_Pump/Speed.xml`), the property's
   accessors now `PROPERTY_TEXT` files of their own, and parent-carrying records 3 → 6.
   `The_member_fixture_resolves_by_recorded_parent_rather_than_by_the_dotted_stem` still
   described the old shape, so its first fixture-guard assertion (3 parents) failed before
   any planner claim was even tested. The layout change itself was right and already
   recorded ("Ownership was spelled three ways in one tree", schema 9); only the test was
   stale. Rewritten as `…_under_chain_paths`: same claims where they survive (identity
   from the recorded parent, never from the file name), and the accessors now asserted as
   export-only skips rather than as absent.

**What was rejected:** keeping a copy of the fixture in the dotted-stem layout so the old
test could stand. The legacy layout is already pinned by the `LegacyManifest` tests, and a
second committed fixture would need the old exporter to regenerate — which no longer
exists, so it would be a fixture nothing can ever refresh.

**The third failure that run was not part of this.** `AutoStashFlowTests` failed in the
full run and passed alone immediately after — the known git-subprocess flake, behaving
exactly as documented.

A regenerated fixture is a change to what the tests measure, and the suite that measures
it costs thirty seconds. Same shape as the embedded-python rebuild trap: the artefact
moved, the check that watches it was not looked at.

---

### First-vendor leftovers past the seam: tooltip, log chips, template path

**Found 2026-08-21**, by a three-vendor audit after the session-pin bugs were already closed.
The project graph and IL scanners were clean. What they do not see is Versio.App, file
extensions, and XAML.

Three leftovers a TwinCAT or TIA session still paid for:

1. The Project rail tooltip said "the repository as CODESYS structures it". Shown to
   every vendor. VendorSeamTests never reads BAML.
2. `LogFilterViewModel` imported all three packs and hard-listed `CodesysLog` /
   `TiaPortalLog` / `TwinCatLog`. Adding a vendor was not only a csproj line and a
   `Register()` call.
3. `OrphanScanner.ProposeBinding` always wrote `BindingConventions.ConventionalTemplatePath`,
   which is `*_template.project`. A TwinCAT adopt stored `foo.tsproj_template.project`.

**Fix:** `IPlcProjectFormat.LogCategory` (default `Vendor.Name`) and
`ConventionalTemplatePath` (default null). The log filter walks `PlcFormats.All`.
ProposeBinding asks the format; TIA and TwinCAT have no sibling template. Tooltip
says "toolchain". Arch test: ViewModels do not reference `Versio.Plc.*`.
VendorSeamTests now covers `Iec` and `Settings`.

**What was rejected.** Using `Vendor.Name` in the filter without putting `LogCategory`
on the format — the `*Log.Category` constants would then be free to drift. Moving
`BindingConventions.ProjectExtension` into the CODESYS pack this session — the
behavioral leak was ProposeBinding, and the constants are still what
`CodesysProjectFormat` consumes.

---

### Stage all & commit cannot start git: Win32 206, command line too long

**Found 2026-08-21**, owner clicked Stage all & commit on this repository.
Log: `Stage all & commit: staging 336 file(s)` then
`Win32Exception (206): An error occurred trying to start process 'git' with
working directory 'C:/GIT/Versio'. The filename or extension is too long.`
The process never started. The 336 paths were TIA fixture UDTs whose
repo-relative names are ~180 characters; `git add --` plus those as argv
exceeds Windows CreateProcess (32,767).

**Fix:** `GitCliClient` feeds pathspecs through `--pathspec-from-file=-`
with NUL separators (Stage / Unstage / Discard). Argv stays short; a path
that looks like a flag still stages. Test:
`Staging_hundreds_of_long_paths_does_not_overflow_CreateProcess` — 400
paths whose argv would overflow, so putting them back on the command line
goes red.

**What was rejected:** batching `git add` into chunks of N. Two index
writes for one button, and N is a guess that fails again on a deeper
tree. `--pathspec-from-file` is one invocation, which is what the button
is.

---

### TwinCAT held-copy export succeeds, then a nested-mirror echo fails the lock

**Found 2026-08-21**, first live `ExportHeldViaCopy` with TcXaeShell still holding
`TC3_PlcSample_reStrText`. Versio 25012 copied the folder to
`.versio/local/open/64eb778c77ff/` and the host exported the copy: **36 objects
in 27.4 s**, including `Devices/Devices.devices.yaml`. Two DTE on different
paths is therefore valid.

The conventional export path is `…/TC3_PlcSample_reStrText/TC3_PlcSample_reStrText`,
which sits **inside** the directory payload. Swapping the mirror therefore
settled as a `Project` change (10:32:29). A second copy-export started on the
same scratch folder; the first job then deleted that folder (`DiscardScratchProject`);
the waiter reported `project file is missing` and `ExportFailed` flipped the
lock to Failed. The tree on disk is the successful first export. Commit gate:
"acknowledge the failure before committing."

CODESYS does not hit this: its export folder sits beside the `.project`, not
inside it. The shared scratch path per binding is the other half — two jobs
must not own the same `.versio/local/open/{id}/`. **Fix (same session):** each
held-export copy gets its own folder under that binding, because the commit
gate skips the coalescer and a watcher-driven export can start while the
first copy is still in use. Owner commit on 2026-08-21 was refused with
"acknowledge the failure before committing" — the Git panel has no Try again;
that button is the red failure banner above the tree.

**What was rejected:** treating the Failed lock as a COM refusal of the copy.
The copy exported; the failure is bookkeeping after a successful swap. Clearing
Failed by editing `state.json` while Versio holds it. Letting the commit gate
skip Failed so a stale export is not acknowledged.

---

### TwinCAT I/O Comment does not persist through Save

**Found 2026-08-20**, by the TwinCAT gate after `ioprobe` had shown Comment set and
`ProduceXml` containing the marker. Product import applied `ITcSmTreeItem.Comment` on
`TIID`, verified the read-back, and `File.SaveAll` persisted a companion ST edit. The
re-export still read an empty Comment. Forcing `ITcSysManager.SaveProject` after SaveAll
is `DISP_E_UNKNOWNNAME` (0x80020006) — the same as `.Save`. This fixture's `.tsproj` has
no `<Io>` tree; Comment has nowhere on disk to hang.

Applying Comment and reporting success would confirm an import that silently reverts —
worse than export-only. Product writes form `DEVICES`, the planner skips the file, and
the host throws if a plan still asks to ApplyText a `*.devices.yaml`. CreateChild stays
refused (subtype 111 invalid; PLC argument order created ItemSubType 1, not a real
EtherCAT plug). Dump:
[`vendors/twincat/probes/ioprobe-TC3-2026-08-20.json`](vendors/twincat/probes/ioprobe-TC3-2026-08-20.json).
Driving notes: [`vendors/twincat/DRIVING.md`](vendors/twincat/DRIVING.md) §4.5.

---

### TIA hardware probe: render is real, port partners are not on this fixture, import is SetAttribute not PlugNew

**Found 2026-08-20**, by `hwprobe` on a `%TEMP%` copy of CleaningStation, before any
`*.devices.yaml` was written. Product Export still walks `Devices` only to find
`PlcSoftware` and records Hardware as `SERVICE_OBJECT`. The discarded tree is 13
DeviceItems under one ET 200SP station: CPU `6ES7 514-2DN03-0AB0`, bus adapter, server
module, two PN interfaces, ports. Two untouched dumps were SHA-256 identical.

The first lookup names were wrong, the same class of miss as CODESYS `get_libraries()`:
`GetAttributes` is a no-op for write flags — `GetAttributeInfos` is the list;
`IoConnector` / `IoController` / `IoSystem` live in `Siemens.Engineering.HW`, not
`Features`. `PositionNumber` mixes rack slots with PN-encoded values (32768+) and
OPC UA (254); treating gaps as empty IO-Link slots invented 33,015 holes, then 249
after capping at 256. The real rack still is not eight `<Empty>` children.

`NetworkPort.ConnectedPorts` is empty because this project has **one** station. Subnet
`Plantbus` names node `X1` on `PROFINET interface_1`. That is membership, not
device-A-port-X to device-B-port-Y. `ConnectToPort` exists. Comment `SetAttribute`
applied and restored without Save. `PlugNew` is on the type; `Unplug` and
`CreateWithItem` are not. No hardware `Import`/`Export` method on Device.

**What we did.** `hwprobe` and `tools/tia/Probe-Hardware.ps1`, then product export of
`*.devices.yaml` (form `DEVICE_COMMENT`) after the owner picked Comment
`SetAttribute`. Import verifies the read-back. Evidence in
[`vendors/tia/DRIVING.md`](vendors/tia/DRIVING.md) §4.4. Gate 5/5 green in 9 m on
2026-08-20 after identity was parent+name+`@PositionNumber` (two `Port_1` on this
fixture would otherwise share one path). First gate that day was 15 m 2 s on
name-only paths.

**What we rejected.** Inferring partners from slot order. Dumping hardware SimaticML
as the reviewable form (`UId` already failed as identity for blocks). Using form
`DEVICES` for the TIA file — that form is export-only, so a missing yaml would have
been a silent no-op instead of ApplyText, and making `DEVICES` importable would have
offered CODESYS I/O as an import. Applying a CODESYS `DEVICES` file as TIA Comment
(the planner did that until the second pass). `PlugNew` exists on the type and was
not invoked. Name-only DeviceItem paths.

---

### I/O export review: a failed Device write could delete the application from the mirror

**Found 2026-08-20**, by a second-pass review of the same day's I/O export, before anyone
ran it against a failing write. `FORM_DEVICES` warned `EXPORT_FAILED` and returned
before walking children. Device used to be `FORM_CONTAINER` — no write, children walked
unconditionally. A disk error, AV lock, or path-length `IOError` on the root Device YAML
would swap in a mirror missing Plc Logic, Application and every POU; coverage would name
only the device; the next commit could look like a mass deletion.

The same review found five quieter holes in the same delivery: `CatalogYaml.Split` cut
quoted parameter keys at the first colon (library keys were IEC identifiers; device names
are free text); folder+yaml merge compared the encoded path segment to the manifest's
verbatim name, so `COM1` / `foo<bar>` never merged; an added connector's parameters stayed
`Unmodified` (`markBaseline` meant the opposite of its name); a third same-named parameter
with no id overwrote the second's `#dup` key; the detail pane looked up connectors by id or
interface only, missing the `DisplayName` fallback the tree uses.

**What we did.** Warn and walk children after a failed device write. Quote-aware split in
C# and `versio_yaml._split`. Merge on `{folder}/{stem}.devices.yaml`, not on `Name`.
`hasBaseline` marks an added connector's params Added. Duplicate keys uniquify with `#2`,
`#3`. Pane lookup uses `DeviceConnector.Key` and shared path markers. Unreadable slot
indexes key `unindexed-n` rather than collapsing on `<Empty>`. A skipped slot that reports
children warns and walks them.

**What we rejected.** Teaching `Versio.Core` to percent-decode CODESYS stems — that is a
vendor decoder. The encoded path is already the folder's identity.

**A second look found the one that got away.** Two connectors whose keys collide — id-less
ports sharing an interface; nothing upstream guarantees `Key` is unique, the exporter
writes the toolchain's list verbatim — were still one row: `DeviceNodes` dropped the
second on a seen-set, so a changed parameter on it had no diff row anywhere while the
clicked catalog table showed both ports. `DeviceCatalog.KeyedConnectors()` now suffixes
colliding keys `#2`, `#3` in file order — the same rule the exporter applies to duplicate
parameter names — and the tree rows, their parameter paths, and the detail-pane lookup all
key off it. The same pass fixed two smaller things: README and DRIVING named a constant
that has never existed (`EXPORT_ONLY_SUFFIXES`; the real one is `EXPORT_ONLY_FORMS`), and
`_text` existed three times before it existed once (now `versio_yaml.text_of`, imported by
all three catalog modules). Uniquifying colliding
connector keys in the tree — the fixture's ids are distinct, and collapsing on `Key` is
the same rule libraries already use.

---

### Eight empty IO-Link slots collided onto one file, and the founding plan named APIs that do not exist

**Found 2026-08-20**, by a probe of RefrigeratorControl on CODESYS V3.5 SP22 Patch 2 before
writing the I/O export. The coupler `BNI_XG3_508_0C5_R015` has eight children, all named
`<Empty>`, all GUID `085766fd-043e-4545-8e8d-d651d56d5d3b`, all `connector_id: 1`. Native
export percent-encodes that name to `%3CEmpty%3E.xml` and writes every sibling onto the same
path — last write wins, seven slots vanish, and the manifest records eight
`UNKNOWN_TYPE_GUID` warnings for one leftover file. Identity is the child's `obj.index`
(0–7), not the name and not `connector_id`.

The founding plan's ScriptEngine names were also wrong on this install. `parameters`,
`get_all_parameters`, `device_identification` / `identification`, and `io_mapping` are
missing. The payload that exists is `connector.host_parameters` (`name` / `id` / `value` /
`description` / `unit`). Parameter `id` is stable across two untouched exports; names are
not unique (`---` and `Cycle Time` both recur on the coupler). Two consecutive probe runs
were byte-identical.

**What we did.** Empty slots map as `DEVICE_SLOT` / `FORM_SKIP` / `COVERED_ELSEWHERE` and are
listed in the parent `*.devices.yaml` (`slots:` nested map, keys `"0"`…`"7"`). Each real
device writes `Device/Device.devices.yaml` *inside* its directory (a YAML beside the folder
would be two sibling rows named Device). Duplicate parameter names key as `name#id`. The
walk is CONTAINER-style: incoming `parent_guid` only — passing the device GUID would make
Plc Logic look like a member of Device. The form is `DEVICES`, export-only; parameter
`setattr` and IEC `%I/%Q` mapping are not this pass. Identification is still missing — the
YAML does not record which `.device` description a node is.

**What we rejected.** Analogizing `DEVICE` to a POU (`Main.st` + `Main/`) — one YAML, not
two, and the YAML must live inside the directory. Nested lists of parameter maps — the YAML
subset has one top-level list of mappings (`connectors`) and nested *scalar* maps for
parameters and slots. Treating `id` as the displayed identity — it is the disambiguator, not
what a reviewer reads.

**Evidence.** Two untouched exports of the fixture copy, 2026-08-20: 3 YAML files,
SHA-256-identical across runs; 8 `DEVICE_SLOT` skips; 0 warnings; the collided
`%3CEmpty%3E.xml` gone. Probe scripts kept at `tools/codesys/versio_probe_devices.py` and
`Probe-Devices.ps1`.

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
the only instrument here that answers a question rather than a mood. *(The flake half closed
on 2026-08-26 — see "The suite's last flake was a real race" near the top of this file: it
was unserialised status refreshes, not git contention, and one gate ended it.)*

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

~~**A commit's files stay paths, deliberately.** Naming them from the object index was tempting and
is wrong: the index describes the *current* export, so a renamed object would put today's name
on yesterday's file.~~ — Since 2026-10-28 the rows are named from the manifest *as it was in that
commit*, which is what the objection was asking for all along; see *The history panel named
nothing, and its reason was half right* at the top of this file.

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
