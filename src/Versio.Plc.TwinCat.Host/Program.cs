namespace Versio.Plc.TwinCat.Host;

/// <summary>
/// Entry point. The message filter is registered before any DTE member is touched.
/// </summary>
public static class Program
{
    // DTE is STA. Main without this attribute is MTA, and the first call then fails
    // with a threading exception that looks like a missing install.
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
            MessageFilter.Register();
            try
            {
                return HostApp.Run(json);
            }
            finally
            {
                MessageFilter.Revoke();
            }
        }
        catch (Exception ex)
        {
            var msg = JsonEscape(Innermost(ex));
            Console.Error.WriteLine(
                "@@VERSIO@@{\"t\":\"fatal\",\"msg\":\"" + msg + "\"}");
            return 1;
        }
    }

    private static string Innermost(Exception ex)
    {
        while (ex.InnerException is not null)
        {
            ex = ex.InnerException;
        }

        return ex.Message;
    }

    private static string JsonEscape(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");
}
