namespace Versio.Plc.TiaPortal.Host;

/// <summary>
/// Entry point. Assembly resolve is installed before any Openness type is loaded.
/// </summary>
public static class Program
{
    // Openness constructs TiaPortal on an STA thread. Main without this attribute
    // is MTA, and Projects.Open then fails with a threading exception that looks
    // like a missing install.
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length < 1 || string.IsNullOrWhiteSpace(args[0]))
            {
                Console.Error.WriteLine(
                    "@@VERSIO@@{\"t\":\"fatal\",\"msg\":\"no job file argument\"}");
                return 2;
            }

            var jobPath = args[0].Trim('"');
            var json = File.ReadAllText(jobPath);
            var api = JobReader.ApiFolder(json);
            OpennessLoader.Install(api);
            return HostApp.Run(json);
        }
        catch (Exception ex)
        {
            var msg = JsonEscape(ex.Message);
            Console.Error.WriteLine(
                "@@VERSIO@@{\"t\":\"fatal\",\"msg\":\"" + msg + "\"}");
            return 1;
        }
    }

    private static string JsonEscape(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");
}
