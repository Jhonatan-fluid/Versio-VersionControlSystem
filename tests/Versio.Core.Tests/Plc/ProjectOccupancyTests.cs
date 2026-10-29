using Versio.Core.Locking;
using Versio.Core.Plc;
using Versio.Plc.Codesys;
using Xunit;

namespace Versio.Core.Tests.Plc;

/// <summary>
/// Whether a vendor IDE has a project open, and whether to believe the marker that says so.
/// </summary>
/// <remarks>
/// Measured behaviour, not invented: CODESYS writes <c>&lt;project&gt;.~u</c> beside a project
/// it holds and refuses any second instance with <i>"currently in use by 'x' on 'y'"</i>. The
/// contents below are copied from a real IDE session.
/// </remarks>
public sealed class ProjectOccupancyTests
{
    private static readonly IPlcProjectFormat Codesys = CodesysProjectFormat.Instance;

    /// <summary>Exactly what CODESYS 3.5.22.20 writes.</summary>
    private const string RealMarker = "elli_\nROG-JVM\n45812\n639221606143479369\n";

    private sealed class Machine(string name) : IMachineIdentity
    {
        public string MachineName { get; } = name;
        public string UserName => "somebody";
        public int CurrentProcessId => 1;
        public DateTimeOffset BootId => DateTimeOffset.UnixEpoch;
    }

    private sealed class Processes(bool alive, bool ideRunning = false) : IProcessProbe
    {
        public bool IsVersioProcessAlive(int processId, string sessionId) => alive;
        public bool IsProcessAlive(int processId) => alive;

        public List<string> AskedFor { get; } = [];

        public bool IsAnyProcessRunning(string processName)
        {
            AskedFor.Add(processName);
            return ideRunning;
        }
    }

    /// <summary>
    /// A vendor whose marker records who and where but no pid, and which names the one
    /// process that can hold its projects — the TIA Portal shape, without the TIA pack.
    /// </summary>
    private sealed class PidlessVendor : IPlcProjectFormat
    {
        public PlcVendor Vendor => PlcVendor.TiaPortal;
        public string DisplayName => "Pidless IDE";
        public PlcCapabilities Capabilities => new();
        public IReadOnlyList<string> ProjectExtensions => [".pl"];
        public bool IsTemplate(string path) => false;
        public string ConventionalExportPath(string projectPath) => projectPath[..^3];
        public string? OccupancyMarkerFor(string projectPath) =>
            System.IO.Path.ChangeExtension(projectPath, ".held");
        public IReadOnlyList<string> HolderProcessNames => ["Pidless.Ide"];
        public IReadOnlyList<string> TransientPatternsFor(string projectPath) => [];
        public ProjectOccupancy ParseOccupancy(string markerContents)
        {
            var lines = markerContents.Split('\n');
            return new()
            {
                Verdict = OccupancyVerdict.Held,
                User = lines[0].Trim(),
                Machine = lines.Length > 1 ? lines[1].Trim() : string.Empty,
            };
        }
        public bool NeedsTextToPlan(string relativePath) => false;
        public ImportPlan PlanImport(
            Versio.Core.Projects.ProjectManifest? manifest,
            IReadOnlyList<MirrorFile> mirror,
            IReadOnlyDictionary<string, string> departed) => new() { Files = [] };
        public Versio.Core.Projects.ObjectTypeName? DisplayNameFor(string type) => null;
    }

    /// <summary>What a pid-less vendor's marker parses to, before the policy.</summary>
    private static ProjectOccupancy Pidless(string machine = "ROG-JVM") =>
        new PidlessVendor().ParseOccupancy($"engineer\n{machine}\n");

    private static ProjectOccupancy Resolve(
        string marker, string onMachine = "ROG-JVM", bool pidAlive = true) =>
        ProjectOccupancyProbe.Resolve(
            Codesys.ParseOccupancy(marker), new Machine(onMachine), new Processes(pidAlive));

    [Fact]
    public void The_marker_sits_beside_the_project_with_the_extension_replaced()
    {
        // Machine.project is locked by Machine.~u, not Machine.project.~u. Appending instead
        // of replacing would look right and never find the file.
        var marker = Codesys.OccupancyMarkerFor(@"C:\work\plc\Machine.project");

        Assert.Equal(@"C:\work\plc\Machine.~u", marker);
    }

    [Fact]
    public void A_real_marker_yields_the_user_the_machine_and_the_pid()
    {
        var parsed = Codesys.ParseOccupancy(RealMarker);

        Assert.Equal(OccupancyVerdict.Held, parsed.Verdict);
        Assert.Equal("elli_", parsed.User);
        Assert.Equal("ROG-JVM", parsed.Machine);
        Assert.Equal(45812, parsed.ProcessId);
        Assert.Equal("elli_ on ROG-JVM", parsed.Owner);
    }

    [Fact]
    public async Task The_probe_names_the_file_it_read_so_a_refusal_can_say_what_to_delete()
    {
        // The parser sees contents alone; only the probe knows the path. A vendor whose
        // marker carries no pid (TIA Portal, 2026-10-28) leaves the person to delete the
        // leftover by hand, which they can only do if the sentence names it.
        var fs = new Fakes.FakeFileSystem();
        fs.Write(@"C:\work\plc\Machine.~u", RealMarker);
        var probe = new ProjectOccupancyProbe(
            fs, new Machine("ROG-JVM"), new Processes(alive: true), _ => Codesys);

        var occupancy = await probe.CheckAsync(
            new Versio.Core.Bindings.ProjectBinding
            {
                Id = "b", ProjectPath = "plc/Machine.project", ExportPath = "plc/Machine",
            },
            @"C:\work\plc\Machine.project", CancellationToken.None);

        Assert.True(occupancy.IsHeld);
        Assert.Equal(@"C:\work\plc\Machine.~u", occupancy.Marker);
    }

    [Fact]
    public void A_marker_naming_a_dead_process_on_this_machine_is_stale()
    {
        // A crashed IDE must not block sync forever. This is the same reasoning
        // LockRecoveryPolicy applies to Versio's own locks, and for the same reason: the
        // alternative is a user hunting for a file they have never heard of.
        var occupancy = Resolve(RealMarker, onMachine: "ROG-JVM", pidAlive: false);

        Assert.Equal(OccupancyVerdict.Stale, occupancy.Verdict);
        Assert.False(occupancy.IsHeld);
    }

    [Fact]
    public void A_marker_from_another_machine_is_believed_however_dead_the_pid_looks()
    {
        // The pid belongs to a process on somebody else's computer, so probing it here is
        // meaningless - and a project on a share really can be open on another desk. Guessing
        // free would put two writers on one project.
        var occupancy = Resolve(RealMarker, onMachine: "SOME-OTHER-PC", pidAlive: false);

        Assert.Equal(OccupancyVerdict.Held, occupancy.Verdict);
        Assert.True(occupancy.IsHeld);
    }

    [Fact]
    public void A_marker_with_no_pid_is_believed_rather_than_dismissed()
    {
        // A future CODESYS writing fewer fields must not turn "somebody has this open" into
        // "nobody does". That is the one way this could lose work. CODESYS names no holder
        // processes, so nothing here is asked about its IDE — the marker simply stands.
        var occupancy = Resolve("elli_\nROG-JVM\n", pidAlive: false);

        Assert.Equal(OccupancyVerdict.Held, occupancy.Verdict);
    }

    // --- a vendor whose marker names no pid, but whose IDE has a name ------------------

    [Fact]
    public void A_pidless_marker_from_this_machine_is_stale_when_the_vendors_IDE_is_not_running()
    {
        // 2026-10-29: a TIA Portal .info written by the session that created the project was
        // copied into the repository with it, and blocked every export of a project nobody
        // had ever opened in a window. The marker names this machine and no pid; the one
        // question left is whether a Portal is running here at all, and none was.
        var processes = new Processes(alive: false, ideRunning: false);

        var occupancy = ProjectOccupancyProbe.Resolve(
            Pidless(), new Machine("ROG-JVM"), processes, ["Pidless.Ide"]);

        Assert.Equal(OccupancyVerdict.Stale, occupancy.Verdict);
        Assert.False(occupancy.IsHeld);
        Assert.Equal(["Pidless.Ide"], processes.AskedFor);
    }

    [Fact]
    public void A_pidless_marker_is_believed_while_the_vendors_IDE_runs_here()
    {
        // The running Portal may be the one holding this project. Held, and the refusal
        // downstream still names the file for the case where it is not.
        var occupancy = ProjectOccupancyProbe.Resolve(
            Pidless(), new Machine("ROG-JVM"),
            new Processes(alive: false, ideRunning: true), ["Pidless.Ide"]);

        Assert.Equal(OccupancyVerdict.Held, occupancy.Verdict);
    }

    [Fact]
    public void A_pidless_marker_from_another_machine_is_believed_whatever_runs_here()
    {
        // A project on a share, open on somebody else's desk: no Portal here says nothing
        // about theirs. The same rule as the pid, for the same reason.
        var processes = new Processes(alive: false, ideRunning: false);

        var occupancy = ProjectOccupancyProbe.Resolve(
            Pidless(machine: "SOME-OTHER-PC"), new Machine("ROG-JVM"), processes, ["Pidless.Ide"]);

        Assert.Equal(OccupancyVerdict.Held, occupancy.Verdict);
        Assert.Empty(processes.AskedFor);
    }

    [Fact]
    public void A_pidless_marker_naming_no_machine_is_believed_rather_than_rescued()
    {
        // No machine means a marker in a shape Versio did not recognise. It could be from
        // anywhere, so "no IDE running here" settles nothing, and guessing free is the
        // two-writers defect.
        var unrecognised = new ProjectOccupancy { Verdict = OccupancyVerdict.Held };

        var occupancy = ProjectOccupancyProbe.Resolve(
            unrecognised, new Machine("ROG-JVM"),
            new Processes(alive: false, ideRunning: false), ["Pidless.Ide"]);

        Assert.Equal(OccupancyVerdict.Held, occupancy.Verdict);
    }

    [Fact]
    public async Task The_probe_asks_the_vendor_which_processes_to_look_for_and_logs_a_marker_it_dismisses()
    {
        // End to end through CheckAsync: the format's HolderProcessNames reach the process
        // probe, and a dismissed marker is said out loud — an export that runs over a marker
        // with nothing in the log looks like the defect the marker exists to prevent.
        var fs = new Fakes.FakeFileSystem();
        fs.Write(@"C:\work\plc\Line.held", "engineer\nROG-JVM\n");
        var processes = new Processes(alive: false, ideRunning: false);
        var sink = new Versio.Core.Logging.InMemoryLogSink();
        var probe = new ProjectOccupancyProbe(
            fs, new Machine("ROG-JVM"), processes, _ => new PidlessVendor(),
            new Versio.Core.Logging.VersioLogger(new Versio.Core.Abstractions.FakeClock(DateTimeOffset.UnixEpoch)).AddSink(sink));

        var occupancy = await probe.CheckAsync(
            new Versio.Core.Bindings.ProjectBinding
            {
                Id = "b", ProjectPath = "plc/Line.pl", ExportPath = "plc/Line",
                Vendor = PlcVendor.TiaPortal,
            },
            @"C:\work\plc\Line.pl", CancellationToken.None);

        Assert.Equal(OccupancyVerdict.Stale, occupancy.Verdict);
        Assert.Equal(["Pidless.Ide"], processes.AskedFor);
        var line = Assert.Single(sink.Snapshot(), e => e.Message.Contains("left its marker behind", StringComparison.Ordinal));
        Assert.Contains("Pidless IDE", line.Message, StringComparison.Ordinal);
        Assert.Contains("engineer on ROG-JVM", line.Message, StringComparison.Ordinal);
        Assert.Contains(@"C:\work\plc\Line.held", line.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unrecognisable_marker_still_means_the_project_is_held()
    {
        // The file format is undocumented. Its mere existence is the signal; everything else
        // is decoration used for the wording, so an unreadable one must still block.
        var occupancy = Resolve("something a later CODESYS wrote");

        Assert.Equal(OccupancyVerdict.Held, occupancy.Verdict);

        // Whatever it said is echoed rather than invented. Reporting a confident owner we
        // did not read would be worse than reporting an odd-looking one we did.
        Assert.Equal("something a later CODESYS wrote", occupancy.Owner);
    }

    [Fact]
    public void An_empty_marker_still_blocks_and_names_nobody()
    {
        var occupancy = Resolve(string.Empty);

        Assert.Equal(OccupancyVerdict.Held, occupancy.Verdict);
        Assert.Equal("another session", occupancy.Owner);
    }

    [Fact]
    public void Crlf_and_lf_markers_parse_the_same()
    {
        var lf = Codesys.ParseOccupancy("elli_\nROG-JVM\n45812\n");
        var crlf = Codesys.ParseOccupancy("elli_\r\nROG-JVM\r\n45812\r\n");

        Assert.Equal(lf.User, crlf.User);
        Assert.Equal(lf.Machine, crlf.Machine);
        Assert.Equal(lf.ProcessId, crlf.ProcessId);
    }

    [Fact]
    public void A_nonsense_pid_is_treated_as_no_pid_rather_than_as_process_zero()
    {
        // Probing pid 0 would ask about the system idle process, which is always alive - so a
        // malformed field would make every marker permanently held.
        Assert.Null(Codesys.ParseOccupancy("u\nm\n0\n").ProcessId);
        Assert.Null(Codesys.ParseOccupancy("u\nm\nnot-a-number\n").ProcessId);
        Assert.Null(Codesys.ParseOccupancy("u\nm\n-4\n").ProcessId);
    }

    [Fact]
    public void A_free_project_stays_free_through_the_policy()
    {
        var occupancy = ProjectOccupancyProbe.Resolve(
            ProjectOccupancy.Free, new Machine("ROG-JVM"), new Processes(alive: true));

        Assert.Equal(OccupancyVerdict.Free, occupancy.Verdict);
        Assert.False(occupancy.IsHeld);
    }

    [Fact]
    public void A_vendor_with_no_marker_convention_reports_nothing_rather_than_blocking()
    {
        // Registering a format must not make its projects unsyncable just because it cannot
        // answer this question. Every vendor but CODESYS is in that position today.
        Assert.Null(Codesys.OccupancyMarkerFor(string.Empty));
    }
}
