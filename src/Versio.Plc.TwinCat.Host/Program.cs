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

    // Same escaping as HostApp.Esc, duplicated because this fatal fires before HostApp
    // is involved: a control character in an exception message must not make the one
    // line explaining the failure unparseable.
    private static string JsonEscape(string value)
    {
        var sb = new System.Text.StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (ch == '\\')
            {
                sb.Append("\\\\");
            }
            else if (ch == '"')
            {
                sb.Append("\\\"");
            }
            else if (ch is '\r' or '\n')
            {
                sb.Append(' ');
            }
            else if (ch < ' ')
            {
                sb.Append("\\u").Append(((int)ch).ToString("x4"));
            }
            else
            {
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }
}
