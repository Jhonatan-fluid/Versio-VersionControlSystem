using System.Diagnostics;
using System.IO;

namespace Versio.App.Tests;

/// <summary>
/// A throwaway git repository, built to order.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists because four test files had grown their own copy.</b> Each was a slightly
/// different <c>Repo</c> or <c>TempRepository</c> with the same job — init, set an identity,
/// write a file, clean up — and they had already started to drift: one wrote empty identity
/// values, one wrote real ones, one added a bare remote, one staged its files. Four near-copies
/// of a fixture is four places to fix a Windows file-attribute problem, and three of them will
/// be missed.
/// </para>
/// <para>
/// <b>Nothing here touches the developer's own git configuration.</b> Every write is
/// <c>--local</c>. A test that rewrote a real <c>user.email</c> would only be noticed by
/// whoever later wondered why their commits said "Versio Test", which is a genuinely nasty
/// thing to leave behind in a suite.
/// </para>
/// <para>
/// An identity is written <b>empty</b> rather than left unset when one is not wanted: a bare
/// <c>git init</c> on a developer machine inherits a perfectly good global identity, so
/// without that the interesting tests would be vacuous on every machine that has ever
/// configured git.
/// </para>
/// </remarks>
public sealed class TempRepo : IDisposable
{
    private readonly string _root;

    /// <summary>The working tree. This is what a test opens.</summary>
    public string Path { get; }

    /// <summary>The bare repository acting as <c>origin</c>, when one was asked for.</summary>
    public string RemotePath { get; }

    /// <param name="identity">Write a usable <c>user.name</c> and <c>user.email</c>.</param>
    /// <param name="withRemote">Create a bare repository beside it and add it as origin.</param>
    /// <param name="published">Push the first commit and set an upstream. Implies a remote.</param>
    /// <param name="originUrl">
    /// Use this as origin instead of a real one. For tests about what a URL <i>means</i> —
    /// transport, host — where nothing is ever fetched.
    /// </param>
    /// <param name="unstagedFiles">How many changed files to leave in the working tree.</param>
    /// <param name="stagedFiles">How many of those to stage.</param>
    public TempRepo(
        bool identity = true,
        bool withRemote = false,
        bool published = false,
        string? originUrl = null,
        int unstagedFiles = 0,
        int stagedFiles = 0)
    {
        _root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "versio-t-" + Guid.NewGuid().ToString("N")[..8]);

        Path = System.IO.Path.Combine(_root, "work");
        RemotePath = System.IO.Path.Combine(_root, "remote.git");

        Directory.CreateDirectory(Path);

        if (withRemote || published)
        {
            Directory.CreateDirectory(RemotePath);
            Git(RemotePath, "init -q --bare -b main");
        }

        // -b main: this git's default is master, and the branch name appears in assertions.
        Git(Path, "init -q -b main .");

        Git(Path, identity ? "config --local user.name TestUser" : "config --local user.name \"\"");
        Git(Path, identity
            ? "config --local user.email test@versio.local"
            : "config --local user.email \"\"");

        // Something to have a history at all. Several tests need a HEAD before they mean
        // anything: ahead/behind is undefined on an unborn branch.
        File.WriteAllText(System.IO.Path.Combine(Path, "seed.txt"), "seed\n");
        Git(Path, "add .");

        if (identity)
        {
            Git(Path, "commit -qm first");
        }

        if (originUrl is not null)
        {
            Git(Path, $"remote add origin \"{originUrl}\"");
        }
        else if (withRemote || published)
        {
            Git(Path, $"remote add origin \"{RemotePath}\"");
        }

        if (published)
        {
            Git(Path, "push -q --set-upstream origin main");
        }

        for (var i = 0; i < unstagedFiles; i++)
        {
            File.WriteAllText(System.IO.Path.Combine(Path, $"file{i}.st"), $"PROGRAM P{i}\n");
        }

        for (var i = 0; i < stagedFiles && i < unstagedFiles; i++)
        {
            Git(Path, $"add file{i}.st");
        }
    }

    /// <summary>Change a file, the way an editor save would.</summary>
    public void Touch(string name) =>
        File.WriteAllText(System.IO.Path.Combine(Path, name), $"changed {Guid.NewGuid():N}\n");

    /// <summary>Delete a file from the working tree.</summary>
    public void Delete(string name) =>
        File.Delete(System.IO.Path.Combine(Path, name));

    /// <summary>Add a commit on top of whatever is there.</summary>
    public void Commit(string text = "more")
    {
        File.AppendAllText(System.IO.Path.Combine(Path, "seed.txt"), text + "\n");
        Git(Path, "add .");
        Git(Path, $"commit -qm \"{text}\"");
    }

    /// <summary>Run git in this repository.</summary>
    public void Git(string arguments) => Git(Path, arguments);

    /// <summary>Run git anywhere.</summary>
    public static void Git(string workingDirectory, string arguments)
    {
        using var git = Process.Start(new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });

        git!.WaitForExit();
    }

    /// <summary>Read a single config value back, for a test that wants git's own answer.</summary>
    public string ConfigValue(string key)
    {
        using var git = Process.Start(new ProcessStartInfo
        {
            FileName = "git",
            Arguments = $"config --local --get {key}",
            WorkingDirectory = Path,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        });

        var value = git!.StandardOutput.ReadToEnd();
        git.WaitForExit();
        return value.Trim();
    }

    public void Dispose()
    {
        try
        {
            // git marks objects read-only, so a plain recursive delete fails on Windows.
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or DirectoryNotFoundException or UnauthorizedAccessException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }
}
