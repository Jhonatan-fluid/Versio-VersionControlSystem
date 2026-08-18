using System.Diagnostics;
using System.IO;
using Xunit;

namespace Versio.Plc.Codesys.Tests;

/// <summary>
/// Runs one snippet against the embedded scripts under CPython, through files.
/// </summary>
/// <remarks>
/// <para>
/// <b>This existed five times before it existed once</b> — four copies in
/// <c>PythonFileNameTests</c> and a fifth in <c>PythonLibraryRecordTests</c>, each writing
/// three temp files, each leaking the <c>GetTempFileName</c> base file its <c>.py</c> name
/// was derived from, and only the newest copy carrying the deadlock fix below. Five
/// near-copies of a fixture is five places to fix the next such thing, and four of them
/// will be missed — the argument is <c>TempRepo</c>'s, one suite over.
/// </para>
/// <para>
/// <b>Everything passes through files, nothing through the command line or stdout.</b>
/// The inputs contain quotes, backslashes and pipes, and arguing with two levels of shell
/// quoting is how a test ends up asserting against something it did not send. Python on
/// Windows writes stdout in the console codepage, which turns "Kühlschrank Regelung" into
/// "K?hlschrank Regelung" — the same finding the README records for the C# side of this
/// boundary, arriving from the other direction.
/// </para>
/// <para>
/// <b>One temp directory per run, deleted whole.</b> The copies used
/// <c>GetTempFileName() + ".py"</c>, which creates a zero-byte base file and then never
/// deletes it — a slow leak of empty files into %TEMP% that nothing ever noticed because
/// nothing ever looks there.
/// </para>
/// </remarks>
internal static class CPythonHarness
{
    private static readonly System.Text.UTF8Encoding Utf8 = new(false);

    /// <summary>Write <paramref name="inputContent"/>, run the script, read the result.</summary>
    /// <param name="makeScript">
    /// Builds the script text given the input and result paths. The caller owns the whole
    /// script, because the five call sites wrap their payloads three different ways and a
    /// harness that owned the wrapper would be the sixth.
    /// </param>
    public static string Run(string inputContent, Func<string, string, string> makeScript)
    {
        var dir = Directory.CreateTempSubdirectory("versio-parity-");
        var input = Path.Combine(dir.FullName, "input.txt");
        var result = Path.Combine(dir.FullName, "result.txt");
        var script = Path.Combine(dir.FullName, "run.py");

        try
        {
            File.WriteAllText(input, inputContent, Utf8);
            File.WriteAllText(script, makeScript(input, result), Utf8);

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

            // BOTH streams are drained before WaitForExit, and that is not tidiness. A
            // redirected stream nobody reads fills its pipe buffer and the child blocks
            // writing to it while the parent blocks waiting for the child — a deadlock that
            // appears the first time a snippet prints more than a few KB, which is exactly
            // the day somebody adds a print() to debug one of these.
            var stdout = python!.StandardOutput.ReadToEndAsync();
            var stderr = python.StandardError.ReadToEnd();
            python.WaitForExit();
            Assert.True(python.ExitCode == 0, stderr + stdout.Result);

            return File.ReadAllText(result, Utf8);
        }
        finally
        {
            try
            {
                dir.Delete(recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp directory is not worth failing a test over.
            }
        }
    }

    /// <summary>
    /// The shape the JSON call sites share: <c>payload</c> loaded for the body, whatever
    /// the body leaves in <c>out</c> written back.
    /// </summary>
    /// <param name="jsonOut">
    /// Whether <c>out</c> goes through <c>json.dumps</c> on the way out — the two JSON
    /// call sites disagree, and both are right for what they read back.
    /// </param>
    public static string RunJson(string payloadJson, string body, bool jsonOut) =>
        Run(payloadJson, (input, result) =>
            "# -*- coding: utf-8 -*-\n" +
            "import io, json, sys\n" +
            $"sys.path.insert(0, r\"{Versio.TestSupport.TestPaths.Scripts}\")\n" +
            $"payload = json.load(io.open(r\"{input}\", encoding=\"utf-8\"))\n" +

            // The snippet sits at the generated file's top level, so it must arrive with
            // no leading indentation of its own.
            string.Join("\n", body.Replace("\r\n", "\n").Split('\n')) + "\n" +
            (jsonOut
                ? $"io.open(r\"{result}\", \"w\", encoding=\"utf-8\").write(json.dumps(out))\n"
                : $"io.open(r\"{result}\", \"w\", encoding=\"utf-8\").write(out)\n"));

    /// <summary>Whether these tests can run at all. Checked once per process.</summary>
    public static bool PythonIsAvailable()
    {
        if (_pythonAvailable is { } known)
        {
            return known;
        }

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
            _pythonAvailable = python.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
                                      or InvalidOperationException)
        {
            _pythonAvailable = false;
        }

        return _pythonAvailable.Value;
    }

    private static bool? _pythonAvailable;
}
