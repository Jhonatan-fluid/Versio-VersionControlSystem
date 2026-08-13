using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace Versio.Integration.Tests;

/// <summary>
/// The exporter's filename rule, exercised by running the module itself.
/// </summary>
/// <remarks>
/// <para>
/// <c>versio_text.py</c> runs inside CODESYS on IronPython 2.7, and its pure half — no
/// <c>scriptengine</c> import — runs on CPython too, which is what this uses. It is the first
/// automated coverage the python modules have had; until now the only way to exercise them was
/// a 17–31 second CODESYS launch, so the rules in them were tested by whichever export happened
/// to touch them.
/// </para>
/// <para>
/// <b>The case that prompted it.</b> CODESYS gives an external-file reference an identity like
/// <c>{00000000-…}|C:\Users\m.lamp\Documents\license.txt</c>, and percent-encoding that
/// produced a 120-character filename carrying a username and a directory layout into a shared
/// repository — the same class of leak as the <c>-&lt;user&gt;-&lt;machine&gt;.opt</c> files
/// adoption gitignores, arriving through a different door. It was found in a real mirror, not
/// in a test.
/// </para>
/// <para>
/// Skips cleanly without python, exactly as the CODESYS tests skip without CODESYS. No python
/// is shipped or required at run time — CODESYS brings its own interpreter.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class PythonFileNameTests(ITestOutputHelper output)
{
    private static readonly string Scripts = Path.Combine(
        Core.Tests.TestPaths.Root, "src", "Versio.Scripts.Codesys");

    /// <summary>Run <c>safe_file_stem</c> over one name and return what it produced.</summary>
    private string Stem(string name)
    {
        // Passed through a file rather than the command line: these names contain quotes,
        // backslashes and pipes, and arguing with two levels of shell quoting is how a test
        // ends up asserting against something it did not send.
        var input = Path.GetTempFileName();
        var result = Path.GetTempFileName();
        var script = Path.GetTempFileName() + ".py";

        try
        {
            File.WriteAllText(input, name, new System.Text.UTF8Encoding(false));
            File.WriteAllText(script, $"""
                import io, sys
                sys.path.insert(0, r"{Scripts}")
                import versio_text
                name = io.open(r"{input}", encoding="utf-8").read()
                out = versio_text.safe_file_stem(name)
                io.open(r"{result}", "w", encoding="utf-8").write(out)
                """);

            using var python = Process.Start(new ProcessStartInfo
            {
                FileName = "python",
                Arguments = $"\"{script}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            });

            var stderr = python!.StandardError.ReadToEnd();
            python.WaitForExit();

            Assert.True(python.ExitCode == 0, stderr);

            // Through a file, not stdout. Python on Windows writes stdout in the console
            // codepage, which turns "Kühlschrank Regelung" into "K?hlschrank Regelung" -
            // the same finding the README records for the C# side of this boundary, arriving
            // from the other direction.
            var produced = File.ReadAllText(result, new System.Text.UTF8Encoding(false));
            output.WriteLine($"{name} -> {produced}");
            return produced;
        }
        finally
        {
            File.Delete(input);
            File.Delete(result);
            File.Delete(script);
        }
    }

    private static bool PythonIsAvailable()
    {
        try
        {
            using var python = Process.Start(new ProcessStartInfo
            {
                FileName = "python",
                Arguments = "--version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            });

            python!.WaitForExit();
            return python.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    [SkippableTheory]
    [InlineData("clsLight")]
    [InlineData("Main")]
    [InlineData("Kühlschrank Regelung")]
    [InlineData("Task Configuration")]
    public void An_ordinary_object_name_is_its_own_filename(string name)
    {
        // No churn. Every existing mirror was written with these rules, and a change here
        // would rename every file in every export somebody has already committed.
        Skip.IfNot(PythonIsAvailable(), "python is not on PATH");

        Assert.Equal(name, Stem(name));
    }

    [SkippableFact]
    public void A_name_that_is_really_a_path_does_not_put_a_username_in_the_repository()
    {
        Skip.IfNot(PythonIsAvailable(), "python is not on PATH");

        var stem = Stem(@"{00000000-0000-0000-0000-000000000000}|C:\Users\m.lamp\Documents\license.txt");

        Assert.DoesNotContain("m.lamp", stem, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Users", stem, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("C%3A", stem, StringComparison.OrdinalIgnoreCase);

        // What survives is the part a human would recognise it by.
        Assert.StartsWith("license.txt", stem, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void Two_external_files_with_the_same_name_do_not_collide()
    {
        // The reason the shortened form carries a digest at all: one file per object is what
        // makes the manifest's path mapping meaningful, and two objects landing on one file
        // would silently lose one of them.
        Skip.IfNot(PythonIsAvailable(), "python is not on PATH");

        var first = Stem(@"{0000}|C:\Users\a\Documents\license.txt");
        var second = Stem(@"{0000}|D:\shared\legal\license.txt");

        Assert.NotEqual(first, second);
        Assert.StartsWith("license.txt", first, StringComparison.Ordinal);
        Assert.StartsWith("license.txt", second, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void A_name_nobody_could_open_is_shortened_rather_than_written()
    {
        // Windows still enforces 260 characters in most configurations, and a deep CODESYS
        // folder chain plus a percent-encoded name reaches it. A file nobody can open is
        // worse than a filename that is abbreviated.
        Skip.IfNot(PythonIsAvailable(), "python is not on PATH");

        var stem = Stem(new string('A', 300));

        Assert.True(stem.Length < 100, $"stem was {stem.Length} characters");
    }

    [SkippableFact]
    public void The_windows_rules_that_were_already_there_still_hold()
    {
        // The percent-encoding is not replaced, only bypassed for names that are paths. A
        // reserved device name and an illegal character must still round-trip into something
        // Windows will accept.
        Skip.IfNot(PythonIsAvailable(), "python is not on PATH");

        Assert.Equal("CON%20", Stem("CON"));
        Assert.Equal("weird%3Cname%3E", Stem("weird<name>"));
    }
}
