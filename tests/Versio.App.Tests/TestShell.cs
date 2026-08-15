using Versio.App.ViewModels;
using Versio.Core.Locking;
using Versio.Core.Logging;
using Versio.Infrastructure.Platform;

namespace Versio.App.Tests;

/// <summary>
/// A shell wired the way every flow test wires one.
/// </summary>
/// <remarks>
/// <para>
/// Thirteen files built this by hand, ten of them from character-identical code: a sink, a
/// logger over a <see cref="SystemClock"/>, a <see cref="LogViewModel"/> reading the same
/// sink, and no toolchains. That last argument is the one that matters and the one most
/// likely to be forgotten - a shell that resolves real toolchains goes looking for CODESYS on
/// the machine running the test.
/// </para>
/// <para>
/// The per-file <c>Shell()</c> helpers are kept, delegating here, rather than replaced at
/// every call site. They read well where they are used and several take arguments of their
/// own; what was worth removing was thirteen copies of the construction, not the name.
/// </para>
/// </remarks>
internal static class TestShell
{
    /// <summary>A shell with nothing listening to its log.</summary>
    public static ShellViewModel Build(IProcessProbe? processes = null) =>
        Build(out _, processes);

    /// <summary>
    /// A shell, and the sink its log lands in.
    /// </summary>
    /// <param name="sink">
    /// The same instance the <see cref="LogViewModel"/> reads, so a test can assert on what
    /// was logged and on what the panel would show without wiring two.
    /// </param>
    /// <param name="processes">
    /// Injected only by the tests that need the live-instance guard to refuse. The real probe
    /// requires a process name starting with "Versio", so no pid a test can reach ever looks
    /// like a live instance.
    /// </param>
    public static ShellViewModel Build(out InMemoryLogSink sink, IProcessProbe? processes = null)
    {
        sink = new InMemoryLogSink();

        var logger = new VersioLogger(new SystemClock()).AddSink(sink);

        return new ShellViewModel(
            logger, new LogViewModel(sink), toolchains: (_, _) => null, processes: processes);
    }
}
