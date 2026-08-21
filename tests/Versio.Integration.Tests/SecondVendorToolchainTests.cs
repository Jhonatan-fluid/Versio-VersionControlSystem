using Versio.Core.Plc;
using Versio.Infrastructure.Plc;
using Xunit;

namespace Versio.Integration.Tests;

/// <summary>
/// The other half of registering a second vendor: what Versio can <i>drive</i>, not merely see.
/// </summary>
/// <remarks>
/// <para>
/// <c>PlcToolchains.Register</c> had zero callers, exactly as <c>PlcFormats.Register</c> did, and
/// for the same reason: the composition was a hardcoded dictionary with one entry. This is the
/// first time either has run.
/// </para>
/// <para>
/// <b>The case worth proving is the unfinished one.</b> A format registered with no toolchain
/// behind it is deliberately useful — the projects are detected, listed and explained as
/// unsupported rather than silently ignored, which is how somebody otherwise concludes the tool
/// is broken. That behaviour is stated in <c>PlcFormats</c>' docstring and in <c>PlcToolchains</c>'
/// docstring, and until now nothing anywhere asserted it.
/// </para>
/// <para>
/// In this project rather than <c>Versio.Core.Tests</c> because <c>PlcToolchains</c> lives in
/// <c>Versio.Infrastructure</c> — a toolchain starts processes, which <c>Versio.Core</c> may not.
/// It launches nothing, so it belongs in the fast lane.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class SecondVendorToolchainTests
{
    private sealed class FakeStudio5000Toolchain : IPlcToolchain
    {
        public IPlcProjectFormat Format => throw new NotSupportedException("not needed here");

        public Task<PlcToolchainStatus> ProbeAsync(CancellationToken ct) =>
            Task.FromResult(new PlcToolchainStatus { Available = false, Explanation = "stand-in" });

        public IPlcRunner CreateRunner(PlcToolchainStatus status) =>
            throw new NotSupportedException("not needed here");
    }

    [Fact]
    public void A_vendor_Versio_cannot_drive_answers_null_rather_than_throwing()
    {
        // A repository can legitimately hold a project Versio can see and not run - a Studio
        // 5000 project on a machine that only has the vendors this build registered. That is
        // a state to report, not an exception to handle, and it is the whole reason formats
        // and toolchains are two registries rather than one.
        Assert.Null(PlcToolchains.For(PlcVendor.Studio5000));
        Assert.DoesNotContain(PlcVendor.Studio5000, PlcToolchains.Supported);
    }

    [Fact]
    public void A_registered_toolchain_becomes_drivable_and_leaves_the_first_alone()
    {
        try
        {
            PlcToolchains.Register(PlcVendor.Studio5000, _ => new FakeStudio5000Toolchain());

            Assert.NotNull(PlcToolchains.For(PlcVendor.Studio5000));
            Assert.Contains(PlcVendor.Studio5000, PlcToolchains.Supported);

            // The one that was already there is untouched. Registering a vendor must not be a
            // way to accidentally deregister another.
            Assert.Contains(PlcVendor.Codesys, PlcToolchains.Supported);
        }
        finally
        {
            PlcToolchains.Unregister(PlcVendor.Studio5000);
        }

        Assert.Null(PlcToolchains.For(PlcVendor.Studio5000));
    }

    [Fact]
    public void Unregistering_something_that_was_never_there_says_so_rather_than_throwing()
    {
        Assert.False(PlcToolchains.Unregister(PlcVendor.TwinCat));
    }
}
