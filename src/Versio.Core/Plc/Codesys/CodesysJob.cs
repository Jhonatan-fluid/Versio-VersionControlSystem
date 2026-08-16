using System.Text.Json;
using System.Text.Json.Serialization;

namespace Versio.Core.Plc.Codesys;

/// <summary>Which script the CODESYS subprocess should run.</summary>
public enum CodesysOperation
{
    Probe,
    Export,
    Import,
}

/// <summary>One project inside a job.</summary>
public sealed record ProjectJob
{
    public required string BindingId { get; init; }
    public required string ProjectPath { get; init; }

    /// <summary>Export destination, or import source, depending on the operation.</summary>
    public required string Path { get; init; }

    /// <summary>
    /// What an import of <see cref="Path"/> should do, decided C#-side. Required for an
    /// import — <see cref="CodesysJob.ToJson"/> refuses one without it — and meaningless for
    /// an export or a probe.
    /// </summary>
    /// <remarks>
    /// The script executes exactly this and decides nothing itself; the filename-derived
    /// walk it used to run is gone, because that walk was the shared root cause of G3–G6.
    /// <c>LocalCodesysRunner</c> computes a plan when its caller did not supply one, so
    /// every import is plan-driven whichever door it came through.
    /// </remarks>
    public ImportPlan? Plan { get; init; }
}

/// <summary>
/// A unit of CODESYS work.
/// </summary>
/// <remarks>
/// <para>
/// <b>Exactly one project per job today, and <see cref="ToJson"/> refuses anything else.</b>
/// </para>
/// <para>
/// This type used to claim the opposite — that a list was carried on purpose so one CODESYS
/// session could open, process and close every project, two costing ~19 s batched against
/// ~35 s sequentially. The arithmetic is right and the batching does not exist: no script
/// reads the <c>projects</c> array. <c>versio_export.py</c> and <c>versio_import.py</c> read
/// the flat <c>project</c> / <c>out</c> / <c>src</c> keys, which are written only for a
/// single-project job, so a second project made the script die on a <c>KeyError</c> inside
/// IronPython — reported as a <c>fatal</c> event after CODESYS had already been started and
/// the cold start already paid.
/// </para>
/// <para>
/// The refusal is here rather than at the seam because this is the last place that still
/// knows what the scripts can read. <see cref="Versio.Core.Plc.PlcJob"/> keeps its list: a
/// vendor whose tool really can batch is free to use it, and the limitation is CODESYS's
/// script protocol rather than the abstraction's.
/// </para>
/// </remarks>
public sealed record CodesysJob
{
    public required CodesysOperation Operation { get; init; }

    /// <summary>Required: CODESYS refuses to start in <c>--noUI</c> mode without one.</summary>
    public required string Profile { get; init; }

    public required IReadOnlyList<ProjectJob> Projects { get; init; }

    /// <summary>Correlates every log line from this operation, across threads and across
    /// the python subprocess.</summary>
    public string OperationId { get; init; } = Guid.NewGuid().ToString("N")[..8];

    public bool ScrubTimestamps { get; init; } = true;
    public bool EmitPlcopen { get; init; }
    public bool Save { get; init; } = true;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Serialise for the script. Written to a file rather than passed on the command line, so
    /// Windows <c>--scriptargs</c> quoting is never load-bearing.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The job does not carry exactly one project. Thrown here, before CODESYS is started,
    /// because the alternative is a 17–31 s cold start followed by a <c>KeyError</c> from
    /// inside IronPython that names a python dictionary key rather than the mistake.
    /// </exception>
    public string ToJson()
    {
        if (Projects.Count != 1)
        {
            throw new InvalidOperationException(
                $"a CODESYS job carries exactly one project; this one carries {Projects.Count}. " +
                "The scripts read the flat project/out/src keys and have no loop over the " +
                "projects array — see the remarks on CodesysJob.");
        }

        var only = Projects[0];

        if (Operation == CodesysOperation.Import && only.Plan is null)
        {
            // Same reasoning as the single-project refusal above: fail here, before the
            // 17-31 s cold start, in words that name the missing thing. The script executes
            // a plan and nothing else - handing it an import job without one would be asking
            // it to fall back to the filename-derived walk this schema exists to retire.
            throw new InvalidOperationException(
                "an import job carries a plan; this one does not. LocalCodesysRunner "
                + "computes one from the mirror when its caller has not — a job built by "
                + "hand must do the same through IPlcProjectFormat.PlanImport.");
        }

        return JsonSerializer.Serialize(new JobDto
        {
            Op = Operation.ToString().ToLowerInvariant(),

            // 2 since the plan-driven import (Phase 1 of docs/CODESYS-IMPORT-PLAN.md).
            // The scripts REFUSE a schema they do not expect rather than half-executing
            // it — a stale extracted script meeting a newer job file must die loudly.
            Schema = 2,
            OperationId = OperationId,
            Profile = Profile,
            ScrubTimestamps = ScrubTimestamps,
            EmitPlcopen = EmitPlcopen,
            Save = Save,
            Project = only.ProjectPath,

            // Export writes the mirror here; the probe writes its report.json here. Both read
            // job["out"] unconditionally, so omitting it for a probe made versio_probe.py walk
            // the entire tree, emit every object event, and then die on its last statement.
            Out = Operation is CodesysOperation.Export or CodesysOperation.Probe
                ? only.Path
                : null,

            Src = Operation == CodesysOperation.Import ? only.Path : null,
            BindingId = only.BindingId,
            Plan = only.Plan is null ? null : PlanDto.From(only.Plan),

            // Still written, and still read by nothing. Kept because it costs one line and is
            // the shape a batching protocol would take; the refusal above is what makes its
            // absence from the scripts harmless rather than latent.
            Projects = [new ProjectDto
            {
                BindingId = only.BindingId,
                Project = only.ProjectPath,
                Path = only.Path,
            }],
        }, JsonOptions);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private sealed class JobDto
    {
        [JsonPropertyName("op")] public string Op { get; set; } = "";
        [JsonPropertyName("schema")] public int Schema { get; set; }
        [JsonPropertyName("operationId")] public string OperationId { get; set; } = "";
        [JsonPropertyName("profile")] public string Profile { get; set; } = "";
        [JsonPropertyName("scrubTimestamps")] public bool ScrubTimestamps { get; set; }
        [JsonPropertyName("emitPlcopen")] public bool EmitPlcopen { get; set; }
        [JsonPropertyName("save")] public bool Save { get; set; }
        [JsonPropertyName("project")] public string? Project { get; set; }
        [JsonPropertyName("out")] public string? Out { get; set; }
        [JsonPropertyName("src")] public string? Src { get; set; }
        [JsonPropertyName("bindingId")] public string? BindingId { get; set; }
        [JsonPropertyName("plan")] public PlanDto? Plan { get; set; }
        [JsonPropertyName("projects")] public List<ProjectDto> Projects { get; set; } = [];
    }

    private sealed class ProjectDto
    {
        [JsonPropertyName("bindingId")] public string BindingId { get; set; } = "";
        [JsonPropertyName("project")] public string Project { get; set; } = "";
        [JsonPropertyName("path")] public string Path { get; set; } = "";
    }

    /// <summary>The plan as the script reads it.</summary>
    /// <remarks>
    /// The action and skip tokens below are the wire protocol — <c>versio_import.py</c>
    /// dispatches on them by string, so renaming an enum member C#-side must not change
    /// them. Warnings and the missing-file list deliberately do not travel: they are for the
    /// person and the log, both of which live on this side of the process boundary.
    /// </remarks>
    private sealed class PlanDto
    {
        [JsonPropertyName("files")] public List<PlanFileDto> Files { get; set; } = [];

        /// <summary>
        /// Objects to remove. Every precondition the planner can check has already passed;
        /// the script checks the one it cannot — that the live object still matches what was
        /// exported — and refuses its own deletion if it does not.
        /// </summary>
        [JsonPropertyName("deletions")] public List<PlanDeletionDto>? Deletions { get; set; }

        public static PlanDto From(ImportPlan plan) => new()
        {
            Deletions = plan.Deletions.Count == 0
                ? null
                : [.. plan.Deletions.Select(d => new PlanDeletionDto
                {
                    Guid = d.Guid,
                    Name = d.Name,
                    Resolve = [.. d.Resolve],
                    Files = [.. d.Files.Select(f => new DeletedFileDto
                    {
                        Path = f.Path, Hash = f.Hash, Form = f.Form,
                    })],
                })],
            Files = [.. plan.Files.Select(f => new PlanFileDto
            {
                Path = f.Path,
                Action = f.Action switch
                {
                    PlannedAction.ApplyText => "text",
                    PlannedAction.ApplyTyped => "typed",
                    PlannedAction.ApplyNative => "native",
                    PlannedAction.Create => "create",
                    _ => "skip",
                },
                Skip = f.Skip switch
                {
                    PlannedSkip.NotSkipped => null,
                    PlannedSkip.Bookkeeping => "bookkeeping",
                    PlannedSkip.ExportOnly => "exportOnly",
                    PlannedSkip.Stray => "stray",
                    _ => "unknownForm",
                },
                Name = f.Name,
                Resolve = f.Resolve.Count > 0 ? [.. f.Resolve] : null,
                Sibling = f.ImportableSibling,
            })],
        };
    }

    private sealed class PlanFileDto
    {
        [JsonPropertyName("path")] public string Path { get; set; } = "";
        [JsonPropertyName("action")] public string Action { get; set; } = "";
        [JsonPropertyName("skip")] public string? Skip { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("resolve")] public List<string>? Resolve { get; set; }
        [JsonPropertyName("sibling")] public string? Sibling { get; set; }
    }

    private sealed class PlanDeletionDto
    {
        [JsonPropertyName("guid")] public string Guid { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";

        /// <summary>Real decoded names from the project root. The script walks these; it
        /// never derives a location from a path, because the file is gone.</summary>
        [JsonPropertyName("resolve")] public List<string> Resolve { get; set; } = [];

        [JsonPropertyName("files")] public List<DeletedFileDto> Files { get; set; } = [];
    }

    private sealed class DeletedFileDto
    {
        [JsonPropertyName("path")] public string Path { get; set; } = "";

        /// <summary>What the export recorded for these bytes. The script rebuilds the live
        /// object's text the way the exporter did and compares — a mismatch means the object
        /// changed in the IDE since, so its absence from the mirror is not permission.</summary>
        [JsonPropertyName("hash")] public string Hash { get; set; } = "";

        [JsonPropertyName("form")] public string Form { get; set; } = "";
    }
}

/// <summary>What one CODESYS run did: how it ended, how long it took, what it reported.</summary>
public sealed record CodesysResult
{
    public required int ExitCode { get; init; }
    public required bool TimedOut { get; init; }
    public required TimeSpan Duration { get; init; }
    public required ScriptOutput Output { get; init; }

    /// <summary>Set when CODESYS refused to start because no profile was supplied — a
    /// distinct, actionable failure rather than a bare non-zero exit.</summary>
    public bool MissingProfile { get; init; }

    /// <summary>Succeeded means: the process exited cleanly, the script reported rc 0, and
    /// it actually reached its "done" event. A script that dies silently must not count as
    /// success just because the process exited 0.</summary>
    public bool Succeeded =>
        !TimedOut && ExitCode == 0 && Output.ScriptExitCode is 0 or null && Output.Completed;

    public string? FailureReason
    {
        get
        {
            if (TimedOut)
            {
                return $"CODESYS did not finish within the timeout and was terminated";
            }

            if (MissingProfile)
            {
                return "CODESYS requires an explicit --profile in headless mode";
            }

            if (Output.Fatal is { } fatal)
            {
                return fatal.String("msg") ?? "the script raised an unhandled exception";
            }

            if (ExitCode != 0)
            {
                return $"CODESYS exited with code {ExitCode}";
            }

            return !Output.Completed
                ? "the script stopped before reporting completion"
                : null;
        }
    }
}

/// <summary>A progress report from inside a running job, for the heartbeat and status bar.</summary>
public readonly record struct CodesysProgress(
    string OperationId,
    string Phase,
    int? Done,
    int? Total,
    string? Detail,
    TimeSpan Elapsed);
