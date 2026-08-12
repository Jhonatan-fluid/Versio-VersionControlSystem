using Versio.Infrastructure.Codesys;
using Xunit;

namespace Versio.Integration.Tests;

/// <summary>
/// Regression cover for the command line CODESYS actually accepts.
/// </summary>
/// <remarks>
/// .NET's <c>ProcessStartInfo.ArgumentList</c> quotes the WHOLE argument when it contains a
/// space, producing <c>"--profile=CODESYS V3.5 SP22 Patch 2"</c>. CODESYS does not parse
/// that: it exits 1 in under a second having done nothing, with no output to explain why.
/// The quotes must wrap the VALUE only.
/// </remarks>
public class ArgumentBuildingTests
{
    private const string Profile = "CODESYS V3.5 SP22 Patch 2";
    private const string Script = @"C:\Temp\Versio\scripts\abc\versio_export.py";
    private const string JobFile = @"C:\Temp\Versio\jobs\job-1.json";

    [Fact]
    public void Quotes_wrap_the_value_not_the_whole_argument()
    {
        var args = LocalCodesysRunner.BuildArguments(Profile, Script, JobFile);

        Assert.Contains($"--profile=\"{Profile}\"", args);
        Assert.DoesNotContain($"\"--profile={Profile}\"", args);
    }

    [Fact]
    public void All_four_arguments_are_present_in_the_order_CODESYS_expects()
    {
        var args = LocalCodesysRunner.BuildArguments(Profile, Script, JobFile);

        Assert.Equal(
            $"--profile=\"{Profile}\" --noUI --runscript=\"{Script}\" --scriptargs:\"{JobFile}\"",
            args);
    }

    [Fact]
    public void Paths_with_spaces_are_quoted()
    {
        var args = LocalCodesysRunner.BuildArguments(
            Profile, @"C:\Program Files\s\x.py", @"C:\My Jobs\j.json");

        Assert.Contains(@"--runscript=""C:\Program Files\s\x.py""", args);
        Assert.Contains(@"--scriptargs:""C:\My Jobs\j.json""", args);
    }

    [Fact]
    public void A_blank_profile_is_omitted_rather_than_passed_as_an_empty_value()
    {
        // Omitting it produces CODESYS's own "you must specify a profile" refusal, which
        // the runner recognises and turns into an actionable message. Passing --profile=""
        // would produce an opaque parse failure instead.
        var args = LocalCodesysRunner.BuildArguments("", Script, JobFile);

        Assert.DoesNotContain("--profile", args);
        Assert.StartsWith("--noUI", args);
    }

    [Fact]
    public void ScriptArgs_uses_a_colon_and_runscript_uses_an_equals()
    {
        // Not a typo in the CODESYS CLI - the two flags genuinely differ.
        var args = LocalCodesysRunner.BuildArguments(Profile, Script, JobFile);

        Assert.Contains("--runscript=", args);
        Assert.Contains("--scriptargs:", args);
        Assert.DoesNotContain("--scriptargs=", args);
    }
}
