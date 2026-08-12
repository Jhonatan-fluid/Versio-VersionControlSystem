using Versio.Core.Locking;
using Versio.Core.Plc;
using Versio.Core.Plc.Codesys;
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

    private sealed class Processes(bool alive) : IProcessProbe
    {
        public bool IsVersioProcessAlive(int processId, string sessionId) => alive;
        public bool IsProcessAlive(int processId) => alive;
    }

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
        // "nobody does". That is the one way this could lose work.
        var occupancy = Resolve("elli_\nROG-JVM\n", pidAlive: false);

        Assert.Equal(OccupancyVerdict.Held, occupancy.Verdict);
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
