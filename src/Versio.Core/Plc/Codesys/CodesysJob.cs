using System.Text.Json;
using System.Text.Json.Serialization;

namespace Versio.Core.Plc.Codesys;

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
/// <b>Carries a LIST of projects on purpose.</b> Cold start is 17–31 s and exporting one
/// small project is about a second of actual work, so a job opens, processes and closes
/// each project inside a single CODESYS session. Two projects cost ~19 s batched versus
/// ~35 s sequentially, and it scales with binding count.
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

    /// <summary>Serialise for the script. Written to a file rather than passed on the
    /// command line, so Windows <c>--scriptargs</c> quoting is never load-bearing.</summary>
    public string ToJson() => JsonSerializer.Serialize(new JobDto
    {
        Op = Operation.ToString().ToLowerInvariant(),
        Schema = 1,
        OperationId = OperationId,
        Profile = Profile,
        ScrubTimestamps = ScrubTimestamps,
        EmitPlcopen = EmitPlcopen,
        Save = Save,
        // Single-project scripts read these; multi-project ones read Projects.
        Project = Projects.Count == 1 ? Projects[0].ProjectPath : null,
        Out = Operation == CodesysOperation.Export && Projects.Count == 1 ? Projects[0].Path : null,
        Src = Operation == CodesysOperation.Import && Projects.Count == 1 ? Projects[0].Path : null,
        BindingId = Projects.Count == 1 ? Projects[0].BindingId : null,
        Projects = Projects.Select(p => new ProjectDto
        {
            BindingId = p.BindingId,
            Project = p.ProjectPath,
            Path = p.Path,
        }).ToList(),
    }, JsonOptions);

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

public readonly record struct CodesysProgress(
    string OperationId,
    string Phase,
    int? Done,
    int? Total,
    string? Detail,
    TimeSpan Elapsed);
