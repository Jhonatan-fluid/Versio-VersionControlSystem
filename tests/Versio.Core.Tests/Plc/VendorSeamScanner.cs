using System.Reflection;

namespace Versio.Core.Tests.Plc;

/// <summary>
/// Reads an assembly's own metadata for vendor knowledge that has leaked into a namespace which
/// must not have any.
/// </summary>
/// <remarks>
/// <para>
/// Separate from the tests that use it because there are two assemblies to check and there
/// should be one copy of the reading. <c>Versio.Integration.Tests</c> links this file — the same
/// arrangement <c>TestPaths</c> already has — because <c>Versio.Core.Tests</c> deliberately does
/// not reference <c>Versio.Infrastructure</c> and must not start.
/// </para>
/// <para>
/// <b>Three kinds of leak, because signatures were only the first kind.</b> The original seam
/// test read fields, properties, parameters and returns, and by the time it was written every
/// leak of that kind had been fixed. The ones still in the tree had settled where it does not
/// look: a sentence shown to the user, and a constant naming one vendor in a table that serves
/// all of them. Those are <i>worse</i> than a leaked type, not better — the compiler cannot
/// notice them at all, and a leaked sentence is the one a person actually reads.
/// </para>
/// </remarks>
internal static class VendorSeamScanner
{
    /// <summary>Every brand name that must not appear outside its own corner.</summary>
    /// <remarks>
    /// The unimplemented vendors are listed deliberately. They cost nothing today and they are
    /// the whole point: the seam is being kept honest for their sake, and whoever adds TwinCAT
    /// should find this failing before they get far enough to regret it.
    /// </remarks>
    public static readonly string[] VendorNames =
    [
        "Codesys", "CODESYS", "TwinCat", "TwinCAT", "TiaPortal", "Siemens", "Rockwell",
        "Studio5000",
    ];

    /// <summary>True when a piece of text names any vendor.</summary>
    public static bool NamesAVendor(string text) =>
        VendorNames.Any(v => text.Contains(v, StringComparison.Ordinal));

    /// <summary>Types declared directly in one of <paramref name="namespaces"/>.</summary>
    /// <remarks>
    /// Nested types come back too, and that is the point rather than an accident: a lambda, an
    /// iterator or an async method compiles into a generated nested type carrying the enclosing
    /// namespace, so a string built inside one would otherwise be invisible to every check here.
    /// </remarks>
    public static IEnumerable<Type> TypesIn(Assembly assembly, IReadOnlyCollection<string> namespaces) =>
        assembly.GetTypes().Where(t => t.Namespace is not null && namespaces.Contains(t.Namespace));

    /// <summary>Type names in <paramref name="ns"/> that carry a vendor's brand.</summary>
    public static IEnumerable<string> VendorNamedTypes(Assembly assembly, string ns) =>
        TypesIn(assembly, [ns])
            .Where(t => NamesAVendor(t.Name))
            .Select(t => t.FullName!);

    /// <summary>
    /// Signature references from <paramref name="ns"/> into a vendor's own namespace.
    /// </summary>
    /// <remarks>
    /// Signatures only — fields, properties, parameters and returns. That is what a second
    /// vendor would actually have to satisfy, and it is what the compiler enforces.
    /// </remarks>
    public static IEnumerable<string> VendorTypeReferences(
        Assembly assembly, string ns, params string[] vendorNamespaces)
    {
        foreach (var type in TypesIn(assembly, [ns]))
        {
            foreach (var referenced in Referenced(type))
            {
                var found = referenced.Namespace;
                if (found is not null
                    && vendorNamespaces.Any(v => found.StartsWith(v, StringComparison.Ordinal)))
                {
                    yield return $"{type.Name} -> {referenced.FullName}";
                }
            }
        }
    }

    /// <summary>
    /// Compile-time string constants in <paramref name="namespaces"/> that name a vendor.
    /// </summary>
    /// <remarks>
    /// <c>LogCategory.Codesys</c> is why this exists: a table of log categories serving every
    /// vendor, with one of them named in it. Reflection can read a literal's value directly, so
    /// unlike the IL walk below this one is exact.
    /// </remarks>
    public static IEnumerable<string> VendorNamingConstants(
        Assembly assembly, IReadOnlyCollection<string> namespaces)
    {
        foreach (var type in TypesIn(assembly, namespaces))
        {
            foreach (var field in type.GetFields(Everything))
            {
                if (!field.IsLiteral || field.FieldType != typeof(string))
                {
                    continue;
                }

                if (field.GetRawConstantValue() is string value && NamesAVendor(value))
                {
                    yield return $"{type.FullName}.{field.Name} = \"{value}\"";
                }
            }
        }
    }

    /// <summary>
    /// String literals compiled into methods in <paramref name="namespaces"/> that name a vendor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The kind of leak that reaches a person's screen.</b> "no CODESYS projects are tracked
    /// in this repository" is a sentence the commit gate shows, from a namespace that is supposed
    /// to know nothing about brands — and it would go on being shown, word for word, to somebody
    /// whose repository holds only TwinCAT projects.
    /// </para>
    /// <para>
    /// <b>Read from IL rather than from source.</b> Source would need the repository to be beside
    /// the test binary and would re-implement a comment parser badly; IL carries only what was
    /// actually compiled, so a brand inside a comment — where it is legitimate and often the
    /// clearest way to explain the rule — is invisible here, exactly as it should be.
    /// </para>
    /// <para>
    /// <b>The walk is deliberately naive and that is safe in one direction only.</b> It scans for
    /// the <c>ldstr</c> opcode a byte at a time rather than decoding every instruction, so it can
    /// land inside another instruction's operand. Two things keep that harmless: a real
    /// <c>ldstr</c> token always has <c>0x70</c> in its high byte, and <c>ResolveString</c>
    /// rejects a token that is not a user string. What survives both is a token that resolves to
    /// a real string in this assembly — so a false positive still means the brand is in there
    /// somewhere, and the cost is a misattributed method name in the failure message. It cannot
    /// produce a false <i>negative</i>, which is the direction that would matter.
    /// </para>
    /// </remarks>
    public static IEnumerable<string> VendorNamingLiterals(
        Assembly assembly, IReadOnlyCollection<string> namespaces)
    {
        foreach (var type in TypesIn(assembly, namespaces))
        {
            foreach (var method in Methods(type))
            {
                foreach (var text in LiteralsIn(method, assembly.ManifestModule))
                {
                    if (NamesAVendor(text))
                    {
                        yield return $"{Describe(type, method)}: \"{text}\"";
                    }
                }
            }
        }
    }

    private const BindingFlags Everything =
        BindingFlags.Public | BindingFlags.NonPublic |
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    /// <summary>The <c>ldstr</c> opcode: push a string literal.</summary>
    private const byte Ldstr = 0x72;

    /// <summary>Tokens into the user-string heap carry this in their high byte.</summary>
    private const int UserStringToken = 0x70000000;

    private static IEnumerable<MethodBase> Methods(Type type)
    {
        foreach (var method in type.GetMethods(Everything))
        {
            yield return method;
        }

        foreach (var ctor in type.GetConstructors(Everything))
        {
            yield return ctor;
        }

        // The type initialiser separately, because GetConstructors does not return it and it is
        // where a static readonly table of strings is actually built.
        if (type.TypeInitializer is { } initializer)
        {
            yield return initializer;
        }
    }

    private static IEnumerable<string> LiteralsIn(MethodBase method, Module module)
    {
        var il = Body(method);
        if (il is null)
        {
            yield break;
        }

        for (var i = 0; i + 5 <= il.Length; i++)
        {
            if (il[i] != Ldstr)
            {
                continue;
            }

            var token = BitConverter.ToInt32(il, i + 1);
            if ((token & unchecked((int)0xFF000000)) != UserStringToken)
            {
                continue;
            }

            if (Resolve(module, token) is { } text)
            {
                yield return text;
            }
        }
    }

    private static byte[]? Body(MethodBase method)
    {
        // Abstract, extern and runtime-provided methods have no body to read, and asking throws
        // rather than answering null for some of them.
        try
        {
            return method.GetMethodBody()?.GetILAsByteArray();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static string? Resolve(Module module, int token)
    {
        try
        {
            return module.ResolveString(token);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string Describe(Type type, MethodBase method) =>
        // A generated nested type names its own enclosing method far better than its mangled
        // type name does - "<ScanFor>b__12_0" beside OrphanScanner is what a reader needs.
        type.IsNested ? $"{type.DeclaringType?.Name}.{type.Name}.{method.Name}"
                      : $"{type.Name}.{method.Name}";

    private static IEnumerable<Type> Referenced(Type type)
    {
        foreach (var field in type.GetFields(Everything))
        {
            foreach (var t in Unwrap(field.FieldType))
            {
                yield return t;
            }
        }

        foreach (var property in type.GetProperties(Everything))
        {
            foreach (var t in Unwrap(property.PropertyType))
            {
                yield return t;
            }
        }

        foreach (var method in type.GetMethods(Everything))
        {
            foreach (var t in Unwrap(method.ReturnType))
            {
                yield return t;
            }

            foreach (var parameter in method.GetParameters())
            {
                foreach (var t in Unwrap(parameter.ParameterType))
                {
                    yield return t;
                }
            }
        }

        foreach (var ctor in type.GetConstructors(Everything))
        {
            foreach (var parameter in ctor.GetParameters())
            {
                foreach (var t in Unwrap(parameter.ParameterType))
                {
                    yield return t;
                }
            }
        }
    }

    /// <summary>A type, plus anything hiding inside it.</summary>
    /// <remarks>
    /// <b>Without this a vendor type travels in a collection unseen.</b> The namespace of
    /// <c>IReadOnlyList&lt;CodesysProjectFormat&gt;</c> is <c>System.Collections.Generic</c>, so
    /// the original check read it as neutral and waved it through — and a list of vendor objects
    /// crossing the seam is a more likely leak than a bare one, not a less likely one. Arrays
    /// hide a type the same way.
    /// </remarks>
    private static IEnumerable<Type> Unwrap(Type type)
    {
        yield return type;

        if (type.HasElementType && type.GetElementType() is { } element)
        {
            foreach (var t in Unwrap(element))
            {
                yield return t;
            }
        }

        foreach (var argument in type.GetGenericArguments())
        {
            foreach (var t in Unwrap(argument))
            {
                yield return t;
            }
        }
    }
}
