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
            Plan = new Versio.Core.Plc.ImportPlan { Files = [] },
        }) with { Operation = CodesysOperation.Import }).ToJson();

        Assert.Contains("\"src\"", json);
        Assert.DoesNotContain("\"out\"", json);
    }

    [Fact]
    public void An_import_job_without_a_plan_is_refused_here_rather_than_by_python()
    {
        // Same argument as the multi-project refusal below: fail before the 17-31 s cold
        // start, in words naming the missing thing. The script executes a plan and nothing
        // else - its filename-derived walk is gone, so a planless job has no meaning it
        // could fall back to.
        var ex = Assert.Throws<InvalidOperationException>(() => (Job(new ProjectJob
        {
            BindingId = "b-1", ProjectPath = @"C:\w\A.project", Path = @"C:\w\A",
        }) with { Operation = CodesysOperation.Import }).ToJson());

        Assert.Contains("plan", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_planned_deletion_crosses_the_wire_with_what_the_script_needs_to_refuse_it()
    {
        // This test used to assert the OPPOSITE, and that was its job. While the planner
        // proposed deletions and the script could not execute them, it held the two halves
        // together: adding remove() without this field would have produced a script that
        // deletes nothing while everything above it said deletions were planned, and adding
        // the field without the script would have sent a key nothing reads.
        //
        // Both halves are here now, so it flips to asserting what crosses — and what crosses
        // is chosen by what the script must be able to REFUSE with, not by what it needs to
        // act. The resolve chain, because the file is gone and a location can no longer be
        // derived from a path. The recorded hash and form per file, because the one check
        // only the CODESYS process can make is whether the live object still matches what was
        // exported — a mirror file being absent says somebody deleted it, not that nobody
        // edited the object since.
        var json = (Job(new ProjectJob
        {
            BindingId = "b-1", ProjectPath = @"C:\w\A.project", Path = @"C:\w\A",
            Plan = new Versio.Core.Plc.ImportPlan
            {
                Files = [],
                Deletions =
                [
                    new Versio.Core.Plc.PlannedDeletion
                    {
                        Guid = "g1",
                        Name = "FB_Motor",
                        Resolve = ["Device", "Plc Logic", "Application", "FB_Motor"],
                        Files = [new Versio.Core.Plc.DeletedFile(
                            "App/FB_Motor.st", "sha256:aaa", "ST_FULL")],
                    },
                ],
            },
        }) with { Operation = CodesysOperation.Import }).ToJson();

        Assert.Contains("\"deletions\"", json, StringComparison.Ordinal);
        Assert.Contains("FB_Motor", json, StringComparison.Ordinal);
        Assert.Contains("Plc Logic", json, StringComparison.Ordinal);
        Assert.Contains("sha256:aaa", json, StringComparison.Ordinal);
        Assert.Contains("ST_FULL", json, StringComparison.Ordinal);

        // A textual record carries no nhash, and the key is omitted rather than sent empty —
        // so a mirror with nothing normalised to send produces the job file it always did.
        Assert.DoesNotContain("nhash", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_native_deletion_carries_the_normalised_hash_the_script_compares_against()
    {
        // The raw hash is over bytes that churn between exports, so it is not what a native
        // deletion is checked against and sending it alone would have the script refuse every
        // one of them. nhash is the whole of the native check crossing the wire.
        var json = (Job(new ProjectJob
        {
            BindingId = "b-1", ProjectPath = @"C:\w\A.project", Path = @"C:\w\A",
            Plan = new Versio.Core.Plc.ImportPlan
            {
                Files = [],
                Deletions =
                [
                    new Versio.Core.Plc.PlannedDeletion
                    {
                        Guid = "g1",
                        Name = "Glob_Var",
                        Resolve = ["Device", "Glob_Var"],
                        Files = [new Versio.Core.Plc.DeletedFile(
                            "App/Glob_Var.gvl.xml", "sha256:raw", "NATIVE", "sha256:norm")],
                    },
                ],
            },
        }) with { Operation = CodesysOperation.Import }).ToJson();

        Assert.Contains("\"nhash\": \"sha256:norm\"", json, StringComparison.Ordinal);
        Assert.Contains("sha256:raw", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_plan_with_nothing_to_delete_sends_no_deletions_key_at_all()
    {
        // Omitted rather than sent empty. Every import carries a plan and almost none of them
        // delete anything, so an always-present "deletions": [] would put the most dangerous
        // word in the protocol into every job file Versio has ever written — and a key that
        // is normally there is a key nobody looks twice at on the day it is not empty.
        var json = (Job(new ProjectJob
        {
            BindingId = "b-1", ProjectPath = @"C:\w\A.project", Path = @"C:\w\A",
            Plan = new Versio.Core.Plc.ImportPlan { Files = [] },
        }) with { Operation = CodesysOperation.Import }).ToJson();

        Assert.DoesNotContain("deletions", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_plan_travels_in_the_words_the_script_dispatches_on()
    {
        // The action and skip tokens are the wire protocol: versio_import.py switches on
        // these strings, so a C#-side enum rename must not reach the JSON. Asserted
        // against the serialized text for exactly that reason.
        var json = (Job(new ProjectJob
        {
            BindingId = "b-1", ProjectPath = @"C:\w\A.project", Path = @"C:\w\A",
            Plan = new Versio.Core.Plc.ImportPlan
            {
                Files =
                [
                    new Versio.Core.Plc.PlannedFile
                    {
                        Path = "App/Simulation.st",
                        Action = Versio.Core.Plc.PlannedAction.ApplyText,
                        Name = "Simulation",
                        Resolve = ["App", "Simulation"],
                    },
                    new Versio.Core.Plc.PlannedFile
                    {
                        Path = "App/Task Configuration.xml",
                        Action = Versio.Core.Plc.PlannedAction.Skip,
                        Skip = Versio.Core.Plc.PlannedSkip.ExportOnly,
                        ImportableSibling = "App/Task Configuration.tasks.yaml",
                    },
                ],
            },
        }) with { Operation = CodesysOperation.Import }).ToJson();

        Assert.Contains("\"schema\": 2", json);
        Assert.Contains("\"action\": \"text\"", json);
        Assert.Contains("\"action\": \"skip\"", json);
        Assert.Contains("\"skip\": \"exportOnly\"", json);
        Assert.Contains("\"sibling\": \"App/Task Configuration.tasks.yaml\"", json);
        Assert.Contains("\"resolve\"", json);

        // Warnings and the missing-file list stay on this side of the process boundary -
        // they are for the person and the log, and the script has no use for them.
        Assert.DoesNotContain("missingFromMirror", json);
        Assert.DoesNotContain("warnings", json);
    }

    [Fact]
    public void A_probe_job_gets_out_too_because_the_probe_writes_its_report_there()
    {
        // versio_probe.py reads job["out"] unconditionally and writes report.json to it.
        // Omitting the key made the probe walk the whole tree, emit every object event and
        // then die on its last statement - a KeyError, after the cold start was already paid.
        var json = (Job(new ProjectJob
        {
            BindingId = "b-1", ProjectPath = @"C:\w\A.project", Path = @"C:\w\report.json",
        }) with { Operation = CodesysOperation.Probe }).ToJson();

        Assert.Contains("\"out\"", json);
        Assert.DoesNotContain("\"src\"", json);
    }

    [Fact]
    public void A_multi_project_job_is_refused_here_rather_than_by_python()
    {
        // THIS TEST USED TO ASSERT THE OPPOSITE, and was called
        // A_multi_project_job_batches_them_into_one_session. It passed, because the C# really
        // does serialise a projects[] array and really does omit the ambiguous flat fields.
        //
        // What it never checked is whether anything could READ that. No script does:
        // versio_export.py and versio_import.py read job["project"], job["out"] and
        // job["src"] unconditionally, so the very omission this test asserted was correct is
        // what killed the run - a KeyError inside IronPython, after CODESYS had started and
        // the 17-31 s cold start had been paid, reported as a fatal event naming a python
        // dictionary key rather than the mistake.
        //
        // A green test asserting half of a mechanism is how the batching stayed believed for
        // as long as it did. Batching is still worth building; until it is, this fails in C#
        // with a sentence.
        var job = Job(
            new ProjectJob { BindingId = "b-1", ProjectPath = @"C:\w\A.project", Path = @"C:\w\A" },
            new ProjectJob { BindingId = "b-2", ProjectPath = @"C:\w\B.project", Path = @"C:\w\B" });

        var ex = Assert.Throws<InvalidOperationException>(() => job.ToJson());
        Assert.Contains("exactly one project", ex.Message);
        Assert.Contains("2", ex.Message);
    }

    [Fact]
    public void A_job_with_no_projects_is_refused_for_the_same_reason()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Job().ToJson());
        Assert.Contains("exactly one project", ex.Message);
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
