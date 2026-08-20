using System.Text.Json;
using System.Text.Json.Serialization;
using Versio.Core.Plc;

namespace Versio.Plc.TwinCat;

/// <summary>
/// A unit of TwinCAT work, serialised for the helper host.
/// </summary>
/// <remarks>
/// The host cannot reference Versio.Core, so this JSON is the contract. Schema 1: one
/// project, a plan on import, never a silent conversion. Wire field names stay short
/// (<c>op</c>, <c>t</c>, <c>plan</c>) — renaming this type does not rename the host's
/// job file.
/// </remarks>
public sealed record TwinCatJob
{
    public required string Operation { get; init; }

    public required string ProjectPath { get; init; }

    public required string Path { get; init; }

    public string ProgId { get; init; } = "TcXaeShell.DTE.17.0";

    public string InstallFolder { get; init; } = string.Empty;

    public string Version { get; init; } = string.Empty;

    public string OperationId { get; init; } = Guid.NewGuid().ToString("N")[..8];

    public bool Save { get; init; } = true;

    public int TimeoutSeconds { get; init; } = 1200;

    /// <summary>
    /// Only a once-only conversion of a copy may set this. The runner never does —
    /// opening a 4024 project on 4026 without asking is the OpenWithUpgrade class of
    /// mistake.
    /// </summary>
    public bool AllowConvert { get; init; }

    public ImportPlan? Plan { get; init; }

    public string ToJson()
    {
        if (string.Equals(Operation, "import", StringComparison.OrdinalIgnoreCase)
            && Plan is null)
        {
            throw new InvalidOperationException(
                "an import job carries a plan; this one does not.");
        }

        return JsonSerializer.Serialize(new JobDto
        {
            Op = Operation.ToLowerInvariant(),
            Schema = 1,
            OperationId = OperationId,
            Project = ProjectPath,
            Out = Operation.Equals("import", StringComparison.OrdinalIgnoreCase) ? null : Path,
            Src = Operation.Equals("import", StringComparison.OrdinalIgnoreCase) ? Path : null,
            ProgId = ProgId,
            Install = InstallFolder,
            Version = Version,
            Save = Save,
            TimeoutSeconds = TimeoutSeconds,
            AllowConvert = AllowConvert ? true : null,
            Plan = Plan,
        }, JsonOptions);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private sealed class JobDto
    {
        public string Op { get; set; } = "";
        public int Schema { get; set; }
        public string OperationId { get; set; } = "";
        public string? Project { get; set; }
        public string? Out { get; set; }
        public string? Src { get; set; }
        public string ProgId { get; set; } = "";
        public string Install { get; set; } = "";
        public string Version { get; set; } = "";
        public bool Save { get; set; }
        public int TimeoutSeconds { get; set; }
        public bool? AllowConvert { get; set; }
        public ImportPlan? Plan { get; set; }
    }
}
