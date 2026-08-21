using System.Text.Json;

namespace Versio.Plc.TwinCat.Host;

internal sealed class JobModel
{
    public string Op { get; set; } = "";
    public int Schema { get; set; }
    public string OperationId { get; set; } = "";
    public string? Project { get; set; }
    public string? Out { get; set; }
    public string? Src { get; set; }
    public string ProgId { get; set; } = "TcXaeShell.DTE.17.0";
    public string Install { get; set; } = "";
    public string Version { get; set; } = "";
    public bool Save { get; set; } = true;
    public int TimeoutSeconds { get; set; } = 1200;
    public bool AllowConvert { get; set; }
    public JsonElement Plan { get; set; }
}

internal static class JobReader
{
    public static JobModel Parse(string json)
    {
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return JsonSerializer.Deserialize<JobModel>(json, opts)
               ?? throw new InvalidOperationException("job file was empty");
    }
}
