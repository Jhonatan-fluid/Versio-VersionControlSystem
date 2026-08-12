using Versio.Core.Codesys;

namespace Versio.Core.Abstractions;

/// <summary>
/// The entire CODESYS surface, behind one method.
/// </summary>
/// <remarks>
/// Deliberately stateless and file-in / file-out. A <c>LocalCodesysRunner</c> spawns
/// CODESYS.exe; a <c>RemoteCodesysRunner</c> talking to a server-hosted agent would drop in
/// unchanged. Keeping this seam narrow is what makes the server-hosted option (plan §13.8)
/// free to defer.
/// </remarks>
public interface ICodesysRunner
{
    Task<CodesysResult> RunAsync(
        CodesysJob job,
        IProgress<CodesysProgress>? progress,
        CancellationToken cancellationToken);
}

/// <summary>Locates the CODESYS installation and its usable profiles.</summary>
public interface ICodesysLocator
{
    IReadOnlyList<CodesysInstall> FindInstalls();
}
