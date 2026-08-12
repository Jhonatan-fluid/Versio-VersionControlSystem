using Versio.Core.Plc.Codesys;
using Xunit;

namespace Versio.Core.Tests.Plc.Codesys;

/// <summary>
/// Parsing must be total: a script that dies mid-write still has to yield everything it
/// managed to emit, because that is exactly when the diagnostics matter.
/// </summary>
public class ScriptProtocolTests
{
    [Fact]
    public void A_marked_line_becomes_an_event()
    {
        var output = ScriptProtocol.Parse("""@@VERSIO@@{"t":"opened","path":"C:\\x.project"}""");

        var evt = Assert.Single(output.Events);
        Assert.Equal("opened", evt.Type);
        Assert.Equal(@"C:\x.project", evt.String("path"));
    }

    [Fact]
    public void Unmarked_output_is_kept_as_chatter()
    {
        // CODESYS shares stdout and cannot be silenced. When something goes wrong the
        // reason is usually in here.
        var output = ScriptProtocol.Parse("some CODESYS message\n@@VERSIO@@{\"t\":\"done\"}");

        Assert.Single(output.Events);
        Assert.Equal(["some CODESYS message"], output.Chatter);
    }

    [Fact]
    public void Chatter_sharing_a_line_with_the_marker_is_split_off()
    {
        var output = ScriptProtocol.Parse("""[INFO] noise @@VERSIO@@{"t":"done"}""");

        Assert.Single(output.Events);
        Assert.Equal(["[INFO] noise"], output.Chatter);
    }

    [Fact]
    public void Malformed_json_is_collected_not_thrown()
    {
        var output = ScriptProtocol.Parse(
            "@@VERSIO@@{\"t\":\"ok\"}\n@@VERSIO@@{ this is not json\n@@VERSIO@@{\"t\":\"done\"}");

        Assert.Equal(2, output.Events.Count);
        Assert.Single(output.Malformed);
        Assert.True(output.Completed);
    }

    [Fact]
    public void A_truncated_final_line_does_not_lose_the_earlier_events()
    {
        // What a killed process actually looks like.
        var output = ScriptProtocol.Parse(
            "@@VERSIO@@{\"t\":\"start\"}\n@@VERSIO@@{\"t\":\"progress\",\"done\":3}\n@@VERSIO@@{\"t\":\"prog");

        Assert.Equal(2, output.Events.Count);
        Assert.Single(output.Malformed);
        Assert.False(output.Completed);
    }

    [Fact]
    public void Json_without_a_type_field_is_rejected()
    {
        var output = ScriptProtocol.Parse("""@@VERSIO@@{"nope":1}""");

        Assert.Empty(output.Events);
        Assert.Single(output.Malformed);
    }

    [Fact]
    public void Summary_and_fatal_are_surfaced()
    {
        var output = ScriptProtocol.Parse("""
            @@VERSIO@@{"t":"summary","written":10,"skipped":6}
            @@VERSIO@@{"t":"fatal","msg":"boom"}
            """);

        Assert.Equal(10, output.Summary!.Int("written"));
        Assert.Equal("boom", output.Fatal!.String("msg"));
    }

    [Fact]
    public void Warnings_are_enumerated()
    {
        var output = ScriptProtocol.Parse("""
            @@VERSIO@@{"t":"warning","code":"UNKNOWN_TYPE_GUID"}
            @@VERSIO@@{"t":"warning","code":"EXPORT_FAILED"}
            @@VERSIO@@{"t":"done"}
            """);

        Assert.Equal(2, output.Warnings.Count());
        Assert.Equal("UNKNOWN_TYPE_GUID", output.Warnings.First().String("code"));
    }

    [Fact]
    public void Script_exit_code_is_read_from_the_exit_event()
    {
        Assert.Equal(0, ScriptProtocol.Parse("""@@VERSIO@@{"t":"exit","rc":0}""").ScriptExitCode);
        Assert.Equal(1, ScriptProtocol.Parse("""@@VERSIO@@{"t":"exit","rc":1}""").ScriptExitCode);
        Assert.Null(ScriptProtocol.Parse("").ScriptExitCode);
    }

    [Fact]
    public void Crlf_and_lf_parse_identically()
    {
        var lf = ScriptProtocol.Parse("@@VERSIO@@{\"t\":\"a\"}\n@@VERSIO@@{\"t\":\"b\"}");
        var crlf = ScriptProtocol.Parse("@@VERSIO@@{\"t\":\"a\"}\r\n@@VERSIO@@{\"t\":\"b\"}");

        Assert.Equal(lf.Events.Count, crlf.Events.Count);
    }

    [Fact]
    public void Empty_and_null_input_are_tolerated()
    {
        Assert.Empty(ScriptProtocol.Parse(null).Events);
        Assert.Empty(ScriptProtocol.Parse("").Events);
    }

    [Fact]
    public void Non_ascii_survives_parsing()
    {
        var output = ScriptProtocol.Parse(
            """@@VERSIO@@{"t":"object","path":"Kühlschrank/Температура.st"}""");

        Assert.Equal("Kühlschrank/Температура.st", output.Events[0].String("path"));
    }

    [Fact]
    public void Typed_accessors_return_null_on_a_type_mismatch()
    {
        var evt = ScriptProtocol.Parse("""@@VERSIO@@{"t":"x","n":"not-a-number"}""").Events[0];

        Assert.Null(evt.Int("n"));
        Assert.Null(evt.Bool("n"));
        Assert.Equal("not-a-number", evt.String("n"));
        Assert.Null(evt.String("absent"));
    }
}

public class CodesysJobTests
{
    private static CodesysJob Job(params ProjectJob[] projects) => new()
    {
        Operation = CodesysOperation.Export,
        Profile = "CODESYS V3.5 SP22 Patch 2",
        Projects = projects,
        OperationId = "7f3a0001",
    };

    [Fact]
    public void A_single_project_job_carries_the_flat_fields_the_scripts_read()
    {
        var json = Job(new ProjectJob
        {
            BindingId = "b-1", ProjectPath = @"C:\w\A.project", Path = @"C:\w\A",
        }).ToJson();

        Assert.Contains("\"project\"", json);
        Assert.Contains("\"out\"", json);
        Assert.Contains("b-1", json);
    }

    [Fact]
    public void An_import_job_uses_src_rather_than_out()
    {
        var json = (Job(new ProjectJob
        {
            BindingId = "b-1", ProjectPath = @"C:\w\A.project", Path = @"C:\w\A",
        }) with { Operation = CodesysOperation.Import }).ToJson();

        Assert.Contains("\"src\"", json);
        Assert.DoesNotContain("\"out\"", json);
    }

    [Fact]
    public void A_multi_project_job_batches_them_into_one_session()
    {
        // Cold start is 17-31 s and dominates the work, so batching is the whole point.
        var json = Job(
            new ProjectJob { BindingId = "b-1", ProjectPath = @"C:\w\A.project", Path = @"C:\w\A" },
            new ProjectJob { BindingId = "b-2", ProjectPath = @"C:\w\B.project", Path = @"C:\w\B" })
            .ToJson();

        Assert.Contains("b-1", json);
        Assert.Contains("b-2", json);
        // Ambiguous flat fields are omitted when there is more than one project.
        Assert.DoesNotContain("\"out\":", json);
    }

    [Fact]
    public void The_profile_is_always_present_because_noUI_requires_it()
    {
        Assert.Contains("CODESYS V3.5 SP22 Patch 2",
            Job(new ProjectJob { BindingId = "b", ProjectPath = "p", Path = "o" }).ToJson());
    }

    [Fact]
    public void Windows_paths_survive_serialisation()
    {
        var json = Job(new ProjectJob
        {
            BindingId = "b", ProjectPath = @"C:\Program Files\x\A.project", Path = @"C:\w\A",
        }).ToJson();

        Assert.Contains(@"C:\\Program Files\\x\\A.project", json);
    }
}

public class CodesysResultTests
{
    private static CodesysResult Result(int exit, string stdout, bool timedOut = false) => new()
    {
        ExitCode = exit,
        TimedOut = timedOut,
        Duration = TimeSpan.FromSeconds(17),
        Output = ScriptProtocol.Parse(stdout),
    };

    [Fact]
    public void Success_requires_the_script_to_have_actually_finished()
    {
        // A process exiting 0 is not enough: a script that dies silently would otherwise
        // count as a clean run, which is how a masked failure ships.
        Assert.True(Result(0, """
            @@VERSIO@@{"t":"summary","written":10}
            @@VERSIO@@{"t":"done"}
            @@VERSIO@@{"t":"exit","rc":0}
            """).Succeeded);

        Assert.False(Result(0, """@@VERSIO@@{"t":"summary","written":10}""").Succeeded);
    }

    [Fact]
    public void A_nonzero_script_rc_fails_even_when_the_process_exited_zero()
    {
        var r = Result(0, """
            @@VERSIO@@{"t":"fatal","msg":"No JSON object could be decoded"}
            @@VERSIO@@{"t":"exit","rc":1}
            """);

        Assert.False(r.Succeeded);
        Assert.Equal("No JSON object could be decoded", r.FailureReason);
    }

    [Fact]
    public void A_timeout_reports_as_a_timeout()
    {
        var r = Result(-1, "", timedOut: true);

        Assert.False(r.Succeeded);
        Assert.Contains("timeout", r.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_missing_profile_is_an_actionable_reason_not_a_bare_exit_code()
    {
        var r = Result(1, "") with { MissingProfile = true };

        Assert.Contains("--profile", r.FailureReason);
    }

    [Fact]
    public void A_successful_run_has_no_failure_reason()
    {
        Assert.Null(Result(0, """
            @@VERSIO@@{"t":"done"}
            @@VERSIO@@{"t":"exit","rc":0}
            """).FailureReason);
    }
}
