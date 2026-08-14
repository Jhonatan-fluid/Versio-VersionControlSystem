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

        return JsonSerializer.Serialize(new JobDto
        {
            Op = Operation.ToString().ToLowerInvariant(),
            Schema = 1,
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
        [JsonPropertyName("projects")] public List<ProjectDto> Projects { get; set; } = [];
    }

    private sealed class ProjectDto
    {
        [JsonPropertyName("bindingId")] public string BindingId { get; set; } = "";
        [JsonPropertyName("project")] public string Project { get; set; } = "";
        [JsonPropertyName("path")] public string Path { get; set; } = "";
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
