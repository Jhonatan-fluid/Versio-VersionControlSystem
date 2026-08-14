using Versio.App.Mvvm;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// Waiting for something a command started, and pressing one only once it will listen.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AsyncCommand.Execute"/> is <c>async void</c> — an
/// <see cref="System.Windows.Input.ICommand"/> has nowhere to return a task — so a test cannot
/// await one. A bounded poll is the alternative, and it is bounded because a test that hangs on
/// something that will never happen tells you nothing and takes the whole run with it.
/// </para>
/// <para>
/// <b><see cref="Press(AsyncCommand, string)"/> exists because of a flake that took two
/// sightings to understand.</b> <c>Execute</c> silently returns when <c>CanExecute</c> is false,
/// and every git command in this application is gated on one shared busy flag — so a press that
/// lands while a refresh or a diff load is still running does <i>nothing at all</i>, and the
/// wait that follows it times out looking like a real defect. It passed in isolation and failed
/// under full-suite load, twice, before the cause was clear.
/// </para>
/// <para>
/// This is not a test-only concession: it is what a user does. The button is disabled while
/// something is running, and they wait for it. A test that presses a disabled button is testing
/// something that cannot happen.
/// </para>
/// </remarks>
public static class Eventually
{
    /// <summary>
    /// How long a wait may take before it is called a failure.
    /// </summary>
    /// <remarks>
    /// Thirty seconds, and that is not generosity. These commands spawn real git subprocesses —
    /// setting an identity is four of them — and the suite runs hundreds of tests alongside.
    /// The deadline is a backstop against a hang, not a latency assertion.
    /// </remarks>
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

    /// <summary>Wait for a command to become live, then press it.</summary>
    public static async Task Press(AsyncCommand command, string what)
    {
        await Until(() => command.CanExecute(null), $"{what} became available");
        command.Execute(null);
    }

    /// <summary>Wait for a parameterised command to become live, then press it.</summary>
    public static async Task Press<T>(AsyncCommand<T> command, T parameter, string what)
    {
        await Until(() => command.CanExecute(parameter), $"{what} became available");
        command.Execute(parameter);
    }
}
