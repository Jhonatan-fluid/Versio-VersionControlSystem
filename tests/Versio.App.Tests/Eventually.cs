using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// Waiting for something a command started.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Versio.App.Mvvm.AsyncCommand.Execute"/> is <c>async void</c> — an
/// <see cref="System.Windows.Input.ICommand"/> has nowhere to return a task — so a test cannot
/// await one. A bounded poll is the alternative, and it is bounded because a test that hangs on
/// something that will never happen tells you nothing and takes the whole run with it.
/// </para>
/// <para>
/// <b>Thirty seconds, and that is not generosity.</b> These commands spawn real git
/// subprocesses — setting an identity is four of them — and the suite runs hundreds of tests
/// alongside. A ten-second deadline passed in isolation and failed under full load, which is
/// the worst kind of flake: it looks like a real defect and is a scheduling artefact. The
/// deadline is a backstop against a hang, not a latency assertion; if a wait genuinely needs
/// thirty seconds, something else is wrong and the message says which wait it was.
/// </para>
/// </remarks>
public static class Eventually
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    public static async Task Until(Func<bool> condition, string what)
    {
        var until = DateTime.UtcNow + Deadline;

        while (DateTime.UtcNow < until)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail($"waited {Deadline.TotalSeconds:F0}s and {what} never happened");
    }
}
