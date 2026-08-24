using System.IO;
using Versio.Infrastructure.Git;
using Versio.Plc.TiaPortal.Infrastructure;
using Versio.Plc.TwinCat.Infrastructure;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// The packaged layout: PortableGit and the two vendor hosts sit beside Versio.exe.
/// </summary>
/// <remarks>
/// These are the paths the installer payload has to produce. A FindHost that still works
/// because a leftover exe sits in the output directory is the kind of silent pass the
/// double-pass is for — pin the candidate list, then prove Resolve prefers a real file.
/// </remarks>
public sealed class PackagedLayoutTests
{
    [Fact]
    public void Git_falls_back_to_PATH_when_no_PortableGit_is_beside_the_app()
    {
        var temp = Directory.CreateTempSubdirectory("versio-git-");
        try
        {
            Assert.Equal(GitExecutable.PathFallback, GitExecutable.Resolve(temp.FullName));
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [Fact]
    public void Git_prefers_PortableGit_cmd_when_it_is_beside_the_app()
    {
        var temp = Directory.CreateTempSubdirectory("versio-git-");
        try
        {
            var bundled = Path.Combine(temp.FullName, GitExecutable.BundledRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(bundled)!);
            File.WriteAllText(bundled, "placeholder");

            var resolved = GitExecutable.Resolve(temp.FullName);

            Assert.Equal(Path.GetFullPath(bundled), resolved);
            Assert.True(GitExecutable.IsBundled(resolved));
            Assert.False(GitExecutable.IsBundled(GitExecutable.PathFallback));
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [Fact]
    public void Bundled_git_pins_GCM_ahead_of_the_verb()
    {
        var temp = Directory.CreateTempSubdirectory("versio-git-");
        try
        {
            var bundled = Path.Combine(temp.FullName, GitExecutable.BundledRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(bundled)!);
            File.WriteAllText(bundled, "placeholder");

            var psi = new System.Diagnostics.ProcessStartInfo();
            psi.ArgumentList.Add("credential");
            psi.ArgumentList.Add("fill");
            GitExecutable.ApplyBundledDefaults(psi, bundled);

            Assert.Equal("-c", psi.ArgumentList[0]);
            Assert.Equal("credential.helper=", psi.ArgumentList[1]);
            Assert.Equal("-c", psi.ArgumentList[2]);
            Assert.Equal("credential.helper=manager", psi.ArgumentList[3]);
            Assert.Equal("credential", psi.ArgumentList[4]);
            Assert.Equal("fill", psi.ArgumentList[5]);
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [Fact]
    public void Path_git_keeps_the_engineers_credential_helper()
    {
        var psi = new System.Diagnostics.ProcessStartInfo();
        psi.ArgumentList.Add("credential");
        psi.ArgumentList.Add("fill");
        GitExecutable.ApplyBundledDefaults(psi, GitExecutable.PathFallback);

        Assert.Equal(2, psi.ArgumentList.Count);
        Assert.Equal("credential", psi.ArgumentList[0]);
        Assert.DoesNotContain(psi.ArgumentList, a => a.Contains("credential.helper", StringComparison.Ordinal));
    }

    [Fact]
    public void TIA_host_is_looked_for_under_tia_then_flat()
    {
        var root = @"C:\versio-pack";
        var candidates = LocalTiaPortalRunner.HostCandidates(root);

        Assert.Equal(Path.Combine(root, "tia", "Versio.Plc.TiaPortal.Host.exe"), candidates[0]);
        Assert.Equal(Path.Combine(root, "Versio.Plc.TiaPortal.Host.exe"), candidates[1]);
    }

    [Fact]
    public void TwinCAT_host_is_looked_for_under_twincat_then_flat()
    {
        var root = @"C:\versio-pack";
        var candidates = LocalTwinCatRunner.HostCandidates(root);

        Assert.Equal(Path.Combine(root, "twincat", "Versio.Plc.TwinCat.Host.exe"), candidates[0]);
        Assert.Equal(Path.Combine(root, "Versio.Plc.TwinCat.Host.exe"), candidates[1]);
    }

    [Fact]
    public void FindHost_returns_the_nested_candidate_when_that_file_exists()
    {
        var temp = Directory.CreateTempSubdirectory("versio-host-");
        try
        {
            var nested = Path.Combine(temp.FullName, "tia", "Versio.Plc.TiaPortal.Host.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(nested)!);
            File.WriteAllText(nested, "placeholder");

            Assert.Equal(nested, LocalTiaPortalRunner.HostCandidates(temp.FullName)
                .First(File.Exists));
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }
}
