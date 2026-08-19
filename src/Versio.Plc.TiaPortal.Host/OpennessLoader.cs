using System.Reflection;

namespace Versio.Plc.TiaPortal.Host;

/// <summary>
/// Loads Openness from the install. The DLLs are never shipped.
/// </summary>
internal static class OpennessLoader
{
    private static string _apiFolder = "";

    public static void Install(string apiFolder)
    {
        _apiFolder = apiFolder ?? "";
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        if (!string.IsNullOrEmpty(_apiFolder) && Directory.Exists(_apiFolder))
        {
            foreach (var dll in Directory.EnumerateFiles(_apiFolder, "Siemens.Engineering*.dll"))
            {
                try
                {
                    Assembly.LoadFrom(dll);
                }
                catch (Exception)
                {
                    // Optional products (Safety, WinCC) may be absent; Base and Step7 are required.
                }
            }
        }
    }

    private static Assembly? Resolve(object? sender, ResolveEventArgs args)
    {
        var name = new AssemblyName(args.Name).Name;
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(_apiFolder))
        {
            return null;
        }

        var path = Path.Combine(_apiFolder, name + ".dll");
        return File.Exists(path) ? Assembly.LoadFrom(path) : null;
    }
}
