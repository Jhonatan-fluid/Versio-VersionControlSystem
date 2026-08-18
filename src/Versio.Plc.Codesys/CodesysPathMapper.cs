using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Text;
using Versio.Core.Plc;
using Versio.Plc.Codesys;

namespace Versio.Core.Serialization;

/// <summary>
/// Maps a CODESYS object to its file name and back.
/// </summary>
/// <remarks>
/// <para>
/// CODESYS object names are far freer than Windows file names: they may contain characters
/// Windows forbids, may collide with reserved device names, and a visualisation frequently
/// shares a name with a POU. Encoding is percent-based, so two distinct objects can never
/// collapse onto one file.
/// </para>
/// <para>
/// <b>Encoding is no longer total-reversible, and that is deliberate.</b> Two shapes of name
/// are <i>shortened</i> to a readable tail plus a digest rather than encoded:
/// </para>
/// <list type="number">
///   <item>
///     <b>A name that is really a path.</b> CODESYS gives an external-file reference an
///     identity like <c>{00000000-…}|C:\Users\m.lamp\Documents\license.txt</c>. Percent-encoding
///     that produced a 120-character file name carrying somebody's username and directory
///     layout into a shared repository — the same class of leak as the
///     <c>-&lt;user&gt;-&lt;machine&gt;.opt</c> files adoption gitignores, arriving by another
///     door. Found in a real mirror, not in a test.
///   </item>
///   <item>
///     <b>A name past <see cref="MaxStem"/> characters.</b> Windows still enforces 260
///     characters in most configurations, and a file nobody can open is worse than an
///     abbreviated name. It shortens one <i>segment</i>; it does not bound the path, and the
///     remarks on <see cref="MaxStem"/> say what does.
///   </item>
/// </list>
/// <para>
/// Reversibility is not lost by this, because it was never used: nothing calls
/// <see cref="DecodeStem"/> outside its own tests, and an import resolves objects through the
/// project tree and the native XML's own identity rather than by parsing file names back.
/// The digest is what keeps two external files with the same basename apart, and the full
/// name still reaches the reader — the manifest records <c>name</c> verbatim.
/// </para>
/// <para>
/// <b>Must stay in step with <c>safe_file_stem</c> in <c>versio_text.py</c></b>, which is the
/// implementation that actually runs. <c>PythonFileNameParityTests</c> runs both over one
/// corpus and compares, because this rule living in two languages is exactly how the GVL
/// suffix bug happened.
/// </para>
/// <para>
/// The file name is a convenience for humans. The manifest's GUID is the source of truth
/// for identity (plan R12).
/// </para>
/// </remarks>
public static partial class PathMapper
{
    /// <summary>
    /// Longest stem written before a name is shortened instead.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Long enough for any object name a person types. Must equal <c>_MAX_STEM</c> in
    /// <c>versio_text.py</c>.
    /// </para>
    /// <para>
    /// <b>It does not bound the path, and it said it did until 2026-08-16</b> — "short enough
    /// that a deep CODESYS folder chain plus this still clears Windows' 260-character path
    /// limit". Two things make that false, both measured rather than read.
    /// <see cref="Shortened"/> returns <c>tail[..80] + "~" + six hex</c>, so the cap is really
    /// <b>87</b>. And a Method, Property, Action or Transition meets <b>two</b> of these on
    /// one path — its owner's directory and its own file — so that is <b>190</b> characters at
    /// worst, which leaves 19 for the repository root with the ordinary
    /// <c>Device/Plc Logic/Application</c> chain and no deep chain at all. Until manifest
    /// schema 9 the same two met inside one <i>file name</i> as <c>Owner.Member.xml</c>; the
    /// arithmetic is unchanged and the separator is a slash now.
    /// </para>
    /// <para>
    /// The path is bounded by <c>too_long_for_any_machine</c> and
    /// <c>too_long_for_this_machine</c> in <c>versio_text.py</c>, per <i>file</i> rather than
    /// per object. This number bounds one segment, which is all a per-name rule can honestly
    /// bound — and it is the only shortening that can key on something every machine agrees
    /// about, which is why the guards report rather than shorten.
    /// </para>
    /// </remarks>
    private const int MaxStem = 80;

    [GeneratedRegex(@"[<>:""/\\|?*\x00-\x1f]")]
    private static partial Regex InvalidWindowsChars();

    [GeneratedRegex("%([0-9A-Fa-f]{2})")]
    private static partial Regex PercentEscape();

    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Type-specific infix that keeps same-named objects of different types apart.
    /// A "Main" visualisation and a "Main" POU are a common real collision.</summary>
    private static readonly Dictionary<CodesysObjectType, string> Infix = new()
    {
        [CodesysObjectType.Visualisation] = ".vis",
        [CodesysObjectType.Gvl] = ".gvl",
        [CodesysObjectType.GvlPersistent] = ".gvl",
        [CodesysObjectType.Action] = ".action",
        [CodesysObjectType.Transition] = ".transition",
    };

    /// <summary>Types whose file name is prefixed with their owner, so a flat folder can
    /// still reconstruct ownership on import.</summary>
    private static readonly HashSet<CodesysObjectType> ParentPrefixed =
    [
        CodesysObjectType.Method,
        CodesysObjectType.MethodNoReturn,
        CodesysObjectType.Property,
        CodesysObjectType.Action,
        CodesysObjectType.Transition,
    ];

    /// <summary>
    /// Turn an object name into a Windows-legal file stem.
    /// </summary>
    /// <remarks>
    /// Percent-encoding for anything Windows forbids; shortening for the two shapes where
    /// encoding produces a file name that is a liability rather than a mapping — see the
    /// remarks on <see cref="PathMapper"/>.
    /// </remarks>
    public static string EncodeStem(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "_unnamed";
        }

        // Checked before encoding, because it is the RAW name that reveals a path: once the
        // separators are percent-encoded there is nothing left to recognise.
        if (LooksLikeAPath(name))
        {
            return Shortened(name);
        }

        var encoded = InvalidWindowsChars().Replace(name, m => $"%{(int)m.Value[0]:X2}");

        // Windows silently strips trailing spaces and dots, which would make the mapping
        // lossy. Encode them instead.
        var sb = new StringBuilder();
        for (var i = 0; i < encoded.Length; i++)
        {
            var c = encoded[i];
            var isTrailing = i == encoded.Length - 1;
            if (isTrailing && (c == ' ' || c == '.'))
            {
                sb.Append($"%{(int)c:X2}");
            }
            else
            {
                sb.Append(c);
            }
        }

        var stem = sb.ToString();
        if (ReservedWindowsNames.Contains(stem))
        {
            stem += "%20";
        }

        // Encoding can grow a name several times over — one forbidden character becomes three
        // — so the length test belongs here, on the result, rather than on the input.
        if (stem.Length > MaxStem)
        {
            return Shortened(name);
        }

        return stem.Length == 0 ? "_unnamed" : stem;
    }

    /// <summary>A name carrying a directory separator or a drive letter is not a name.</summary>
    private static bool LooksLikeAPath(string name) =>
        name.Contains('\\') || name.Contains('/') || name.Contains(':');

    /// <summary>
    /// Last segment of a path-like name, plus a digest of the whole thing.
    /// </summary>
    /// <remarks>
    /// The digest is not decoration: two external files called <c>license.txt</c> in different
    /// folders must not collide onto one file, because one object per file is what makes the
    /// manifest's path mapping mean anything. Six hex characters is enough to separate them and
    /// short enough to read past.
    /// <para>
    /// Forbidden characters in the tail are <b>removed</b> rather than encoded. The tail is a
    /// label for a human at this point — the digest is carrying the identity — and a tail full
    /// of percent escapes would defeat the reason for shortening in the first place.
    /// </para>
    /// </remarks>
    private static string Shortened(string name)
    {
        var tail = name.Split('\\', '/')[^1].Trim();
        if (tail.Length == 0)
        {
            tail = name;
        }

        tail = InvalidWindowsChars().Replace(tail, string.Empty).TrimEnd(' ', '.');

        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)))
            .ToLowerInvariant()[..6];

        if (tail.Length == 0 || ReservedWindowsNames.Contains(tail))
        {
            tail = "object";
        }

        return $"{(tail.Length <= MaxStem ? tail : tail[..MaxStem])}~{digest}";
    }

    /// <summary>
    /// Inverse of <see cref="EncodeStem"/> <b>for encoded names only</b>.
    /// </summary>
    /// <remarks>
    /// A <i>shortened</i> stem — <c>license.txt~3f9a1c</c> — cannot be decoded, because the
    /// name it came from was deliberately not carried in it. This returns such a stem
    /// unchanged rather than pretending, and nothing depends on the difference: no production
    /// code calls this. Read the real name from the manifest's <c>name</c> field, which records
    /// it verbatim.
    /// </remarks>
    public static string DecodeStem(string? stem)
    {
        if (string.IsNullOrEmpty(stem))
        {
            return string.Empty;
        }

        var s = stem;
        if (s.EndsWith("%20", StringComparison.OrdinalIgnoreCase) &&
            ReservedWindowsNames.Contains(s[..^3]))
        {
            s = s[..^3];
        }

        return PercentEscape().Replace(s, m =>
            ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
    }

    /// <summary>
    /// The file suffix one export form writes.
    /// </summary>
    /// <remarks>
    /// Must stay in step with <c>SUFFIX</c> in <c>versio_types.py</c>: the two halves build
    /// the same paths from opposite sides, and a mismatch means the importer looks for a file
    /// the exporter never wrote. A missing entry there once made an ST write land on the
    /// native export's path and destroy it.
    /// </remarks>
    public static string SuffixFor(ExportForm form) => form switch
    {
        ExportForm.StFull or ExportForm.StDeclaration or ExportForm.StImplementation
            or ExportForm.PropertyBoth or ExportForm.PropertyText => ".st",
        ExportForm.Libraries => ".libraries.yaml",
        _ => ".xml",
    };

    /// <summary>Build the on-disk file name for an object.</summary>
    /// <param name="parentName">
    /// The owner, for the <b>pre-schema-9</b> naming only.
    /// </param>
    /// <remarks>
    /// <b>Today's exporter never passes an owner</b> — a member goes into a directory named
    /// after it, so a prefix as well would give <c>FB_Alarm/FB_Alarm.M_Reset.st</c>. The
    /// parameter stays because every mirror committed before 2026-08-18 is named the old way
    /// and <c>CodesysImportPlanner.FallBackToStemSplit</c> still has to reason about those
    /// names; deleting the rule would leave that fallback describing a convention nothing in
    /// the repository states any more.
    /// </remarks>
    public static string FileNameFor(string name, CodesysObjectType type, ExportForm form,
                                     string? parentName = null)
    {
        var stem = EncodeStem(name);
        if (parentName is not null && ParentPrefixed.Contains(type))
        {
            stem = EncodeStem(parentName) + "." + stem;
        }

        var infix = Infix.TryGetValue(type, out var i) ? i : string.Empty;
        return stem + infix + SuffixFor(form);
    }

    /// <summary>Normalise a repo-relative path for storage and comparison: forward slashes,
    /// no leading separator. Git records forward slashes regardless of platform.</summary>
    public static string NormalizeRelative(string path) =>
        path.Replace('\\', '/').TrimStart('/');
}
