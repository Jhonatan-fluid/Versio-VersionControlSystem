namespace Versio.Core.Plc;

/// <summary>
/// Every project format Versio can recognise.
/// </summary>
/// <remarks>
/// <para>
/// The scanner asks this rather than being handed one vendor, so a repository holding a
/// CODESYS project beside a TwinCAT one is discovered as two tracked projects rather than
/// one and a blind spot. That case is not hypothetical for a systems integrator, which is the
/// audience this tool is for.
/// </para>
/// <para>
/// <b>Registering a format is the whole of "Versio can see this vendor".</b> Driving it needs
/// an <see cref="IPlcRunner"/> as well, and a format registered without one is deliberately
/// useful: the projects are detected, listed and explained as unsupported, rather than
/// silently ignored — which is how somebody concludes the tool is broken.
/// </para>
/// </remarks>
public static class PlcFormats
{
    /// <summary>
    /// Empty until somebody registers. <b>Deliberately.</b>
    /// </summary>
    /// <remarks>
    /// This held <c>[CodesysProjectFormat.Instance]</c>, which meant <c>Versio.Core</c> — the
    /// assembly the architectural rule exists to keep free of vendor knowledge — named a vendor
    /// in a field initialiser, and the "registry" was a constant with a mutator bolted on.
    /// Registration is now the only way a vendor gets in, which is what the README, CLAUDE.md
    /// and this file's own remarks have always said it was.
    /// </remarks>
    private static readonly List<IPlcProjectFormat> Registered = [];

    /// <summary>Formats in registration order.</summary>
    public static IReadOnlyList<IPlcProjectFormat> All
    {
        get
        {
            lock (Registered)
            {
                return [.. Registered];
            }
        }
    }

    /// <summary>Add a format, or replace the one already registered for its vendor.</summary>
    /// <remarks>
    /// Replace rather than duplicate, so a host swapping in its own implementation of a
    /// vendor does not end up with two formats claiming the same extensions and a project
    /// that matches whichever happened to be registered first.
    /// </remarks>
    public static void Register(IPlcProjectFormat format)
    {
        lock (Registered)
        {
            Registered.RemoveAll(f => f.Vendor == format.Vendor);
            Registered.Add(format);
        }
    }

    /// <summary>Forget a vendor's format. True when one was registered.</summary>
    /// <remarks>
    /// <para>
    /// The other half of <see cref="Register"/>, and it was missing. A host that can swap in its
    /// own implementation of a vendor — the case <see cref="Register"/> exists for — can only do
    /// so in one direction without this, so a registration was permanent for the life of the
    /// process and there was no way back to a known state.
    /// </para>
    /// <para>
    /// <b>Written because the first test ever to call <see cref="Register"/> could not clean up
    /// after itself.</b> This registry is static and xunit runs test classes in parallel by
    /// default, so a fake vendor left behind is not a tidiness problem — it is one class quietly
    /// changing what every other class sees.
    /// </para>
    /// </remarks>
    public static bool Unregister(PlcVendor vendor)
    {
        lock (Registered)
        {
            return Registered.RemoveAll(f => f.Vendor == vendor) > 0;
        }
    }

    /// <summary>
    /// The format that claims a path, or null when none does.
    /// </summary>
    /// <remarks>
    /// Extension alone today. A second vendor sharing an extension with a first would need
    /// content sniffing, and that is a real possibility — TwinCAT is CODESYS-derived — but
    /// inventing the mechanism before there is a collision to resolve would be guessing at
    /// which signal matters.
    /// </remarks>
    public static IPlcProjectFormat? ForPath(string path)
    {
        var extension = System.IO.Path.GetExtension(path);
        if (string.IsNullOrEmpty(extension))
        {
            return null;
        }

        return All.FirstOrDefault(
            f => f.ProjectExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The format that claims a folder as a source project, or null when none does.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="ForPath"/> so that nothing asking "is this a project to track"
    /// can be answered yes for a folder. See <see cref="IPlcProjectFormat.SourceProjectExtensions"/>
    /// for why a source project is never tracked.
    /// </remarks>
    public static IPlcProjectFormat? ForSourceProject(string directoryPath)
    {
        var extension = System.IO.Path.GetExtension(directoryPath.TrimEnd('/', '\\'));
        if (string.IsNullOrEmpty(extension))
        {
            return null;
        }

        return All.FirstOrDefault(
            f => f.SourceProjectExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>The format for a vendor, or null when none is registered.</summary>
    public static IPlcProjectFormat? ForVendor(PlcVendor vendor) =>
        All.FirstOrDefault(f => f.Vendor == vendor);

    /// <summary>Every extension any registered format claims.</summary>
    public static IReadOnlyList<string> AllExtensions =>
        [.. All.SelectMany(f => f.ProjectExtensions)
               .Distinct(StringComparer.OrdinalIgnoreCase)];
}
