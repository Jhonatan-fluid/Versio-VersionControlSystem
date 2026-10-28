using System.Reflection;

namespace Versio.App;

/// <summary>
/// The product version this process was built as.
/// </summary>
/// <remarks>
/// One helper so the startup log and System information cannot pick different attributes.
/// <see cref="Assembly.GetName"/>.Version is four-part (<c>0.1.0.0</c>); the installer and
/// <c>VersioVersion</c> are three-part. Informational version is the one that matches.
/// A <c>+</c> source-revision suffix is stripped so a human comparing this line to Setup.exe
/// sees the same number.
/// </remarks>
internal static class AppVersion
{
    public static string Display
    {
        get
        {
            var assembly = Assembly.GetExecutingAssembly();
            var raw = assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                    ?.InformationalVersion
                ?? assembly.GetName().Version?.ToString()
                ?? "unknown";
            var plus = raw.IndexOf('+');
            return plus < 0 ? raw : raw[..plus];
        }
    }

    /// <summary>
    /// The GitHub repository updates are published to, or empty when this build names none.
    /// </summary>
    /// <remarks>
    /// Baked in at build time from <c>VersioUpdateRepo</c> in Directory.Build.props - the same
    /// property the publish script uploads to, so the two cannot drift apart.
    /// </remarks>
    public static string UpdateRepository =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "VersioUpdateRepo")
            ?.Value ?? string.Empty;
}
