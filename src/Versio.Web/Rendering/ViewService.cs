using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Versio.Core.Diff;
using Versio.Core.Iec;
using Versio.Core.Iec.Ladder;
using Versio.Core.Logging;
using Versio.Core.Plc;
using Versio.Core.Projects;
using Versio.Core.Text;
using Versio.Web.Repo;
using Versio.Web.Sources;

namespace Versio.Web.Rendering;

/// <summary>Which tab of the pane: the object drawn, or the file as text.</summary>
public enum PaneView
{
    Rendered,
    Raw,
}

/// <summary>What the page shows for one object: an HTML fragment and what to say above it.</summary>
/// <param name="Kind">source, table, ladder, markdown, diff, binary, too-large or missing.</param>
/// <param name="Html">Built by the Core renderers, escaped there; the page places it and draws nothing.</param>
/// <param name="Layers">Every file of the object, for the layer chips, with the one shown first.</param>
public sealed record ViewResult(
    string Kind,
    string Title,
    string Path,
    string Html,
    IReadOnlyList<string> Notices,
    IReadOnlyList<string> Layers);

/// <summary>
/// Turns an object of a commit, or the same object in two commits, into the fragment the page
/// shows, with the renderers the desktop pane draws with.
/// </summary>
/// <remarks>
/// <para>
/// <b>The decision order is the desktop's</b> (<c>ShellViewModel</c>'s rendered-content switch):
/// a member table from any registered format over any layer of the object, with the block's
/// ladder or code body underneath when it is an interface; else a ladder; else Markdown; else
/// the source laid out as the IDE numbers it. The desktop's version is tied to its tree rows,
/// its disk and its pending marks, so it is mirrored here rather than shared. Moving it into Core
/// for both is listed as later in docs/web/PLAN.md; until then a change to one is a change to
/// the other, and <c>ViewServiceTests</c> pins this side's order.
/// </para>
/// <para>
/// <b>Bounded work</b> (security rule 3). A file over <see cref="WebOptions.MaxDrawBytes"/> is
/// shown as plain text without parsing. Rendering runs under <see cref="WebOptions.RenderBudget"/>;
/// past it the file is shown as plain escaped text and the log says so. The work itself is
/// bounded too: SafeXml caps documents, LineDiff caps its table, and the process-wide regex
/// timeout caps every pattern, so an abandoned render cannot run forever.
/// </para>
/// <para>
/// <b>Nothing drawn is not nothing changed</b> (security rule 10). When two versions' files
/// differ but the drawing shows no difference, the page says so and points at Raw: a change in
/// a part of the XML the ladder does not draw must not read as an untouched block.
/// </para>
/// <para>
/// <b>Cached by content, commit and the file shown.</b> A fragment's key is the renderer version,
/// the repository, the commit or commits, the file being shown, the view and every file's blob
/// hash on both sides. The file shown is in it because one object's layers share every blob hash:
/// without it, a layer chip got the first layer's cached answer back (found in review; the first
/// test of the chips built a new cache per call and never saw a warm entry). The commit is in it
/// because a member table's links and "type changed" marks depend on files outside the object:
/// the same blobs at another commit can link elsewhere. A renderer change bumps
/// <see cref="RendererVersion"/>. The caller resolved both commits with
/// this person's credential before anything here runs (security rule 2).
/// </para>
/// </remarks>
public sealed class ViewService(
    IRepoSource source,
    IMemoryCache cache,
    IOptions<WebOptions> options,
    IVersioLogger log)
{
    /// <summary>Bumped whenever any renderer's output changes, so no stale fragment is served.</summary>
    public const int RendererVersion = 3;

    private WebOptions O => options.Value;

    /// <summary>One object at one commit.</summary>
    public async Task<RepoResult<ViewResult>> ViewAsync(
        RepoAccess access, RepoModel model, string path, PaneView view, bool sideBySide, CancellationToken ct)
    {
        if (model.NodeFor(path) is not { } node)
        {
            return RepoResult<ViewResult>.Fail(RepoFailure.NotFound);
        }

        var files = await ReadAllAsync(access, model, node, ct).ConfigureAwait(false);
        var shown = Shown(node, path);
        var key = Key("view", model, model.CommitSha + ":" + shown, view, sideBySide, files, null);
        return await CachedAsync(key, path, () => view == PaneView.Raw
            ? RawSingle(shown, files)
            : Rendered(model, node, shown, files), notice => Plain(shown, files, notice), Lasting(files.Values) && !model.Partial).ConfigureAwait(false);
    }

    /// <summary>One object in two commits: the older one, then the newer.</summary>
    public async Task<RepoResult<ViewResult>> CompareAsync(
        RepoAccess access, RepoModel older, RepoModel newer, string path, PaneView view, bool sideBySide, CancellationToken ct)
    {
        var node = newer.NodeFor(path) ?? older.NodeFor(path);
        if (node is null)
        {
            return RepoResult<ViewResult>.Fail(RepoFailure.NotFound);
        }

        var reads = await Task.WhenAll(ReadAllAsync(access, older, node, ct), ReadAllAsync(access, newer, node, ct)).ConfigureAwait(false);
        var before = reads[0];
        var after = reads[1];
        var shown = Shown(node, path);
        var key = Key("compare", newer, older.CommitSha + ".." + newer.CommitSha + ":" + shown, view, sideBySide, after, before);
        return await CachedAsync(key, path, () => view == PaneView.Raw
            ? RawCompare(shown, before, after, sideBySide)
            : RenderedCompare(older, newer, node, shown, before, after, sideBySide), notice => Plain(shown, after.ContainsKey(shown) ? after : before, notice),
            Lasting(before.Values.Concat(after.Values)) && !older.Partial && !newer.Partial).ConfigureAwait(false);
    }

    /// <summary>The paths that differ between two commits, with what happened to each.</summary>
    public static IReadOnlyList<(string Path, string Change)> ChangedPaths(RepoModel older, RepoModel newer)
    {
        ArgumentNullException.ThrowIfNull(older);
        ArgumentNullException.ThrowIfNull(newer);
        var changes = new List<(string, string)>();
        foreach (var (path, entry) in newer.Files)
        {
            if (!older.Files.TryGetValue(path, out var was))
            {
                changes.Add((path, "added"));
            }
            else if (was.BlobSha != entry.BlobSha)
            {
                changes.Add((path, "modified"));
            }
        }

        changes.AddRange(older.Files.Keys.Where(p => !newer.Files.ContainsKey(p)).Select(p => (p, "deleted")));
        return changes.OrderBy(c => c.Item1, StringComparer.Ordinal).ToList();
    }

    // ---- reading ----------------------------------------------------------------------------

    /// <summary>One layer of an object as fetched: its text, or why there is none.</summary>
    private sealed record Layer(string Path, string BlobSha, string? Text, RepoFailure Failure, bool TooLargeToDraw, bool Binary);

    /// <remarks>
    /// Every layer at once: an object is often two or three files (.st and .xml), each a GitHub
    /// round trip, and none waits on another.
    /// </remarks>
    private async Task<IReadOnlyDictionary<string, Layer>> ReadAllAsync(RepoAccess access, RepoModel model, ProjectTreeNode node, CancellationToken ct)
    {
        var reads = node.Files.Distinct(StringComparer.Ordinal)
            .Where(model.Files.ContainsKey)
            .Select(file => ReadLayerAsync(access, model, file, model.Files[file], ct))
            .ToList();
        var layers = await Task.WhenAll(reads).ConfigureAwait(false);
        return layers.ToDictionary(l => l.Path, StringComparer.Ordinal);
    }

    private async Task<Layer> ReadLayerAsync(RepoAccess access, RepoModel model, string file, TreeEntry entry, CancellationToken ct)
    {
        if (entry.Size > O.MaxBlobBytes)
        {
            return new Layer(file, entry.BlobSha, null, RepoFailure.TooLarge, true, false);
        }

        var key = $"blob:{model.Owner.ToLowerInvariant()}/{model.Repo.ToLowerInvariant()}:{entry.BlobSha}";
        if (!cache.TryGetValue(key, out byte[]? bytes) || bytes is null)
        {
            var read = await source.GetBlobAsync(access, model.Owner, model.Repo, entry.BlobSha, O.MaxBlobBytes, ct).ConfigureAwait(false);
            if (!read.Ok)
            {
                return new Layer(file, entry.BlobSha, null, read.Failure, read.Failure == RepoFailure.TooLarge, false);
            }

            bytes = read.Value!;
            cache.Set(key, bytes, new MemoryCacheEntryOptions { Size = Math.Max(1, bytes.Length / 1024), SlidingExpiration = TimeSpan.FromMinutes(30) });
        }

        var binary = Array.IndexOf(bytes, (byte)0) >= 0;
        return new Layer(file, entry.BlobSha, binary ? null : Decode(bytes), RepoFailure.None, bytes.Length > O.MaxDrawBytes, binary);
    }

    /// <summary>UTF-8, with a byte-order mark removed as File.ReadAllText removes it on the desktop.</summary>
    private static string Decode(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
            ? Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3)
            : Encoding.UTF8.GetString(bytes);

    /// <summary>
    /// The file a request shows: the one asked for when it is a layer of the object, otherwise the
    /// object's primary file. The first version always took the primary file for an object, so
    /// the page's layer chips could never show the native .xml beside an .st (found reviewing).
    /// </summary>
    private static string Shown(ProjectTreeNode node, string path) =>
        node.Files.Contains(path, StringComparer.Ordinal) ? path : node.PrimaryFile ?? path;

    // ---- one version --------------------------------------------------------------------------

    private ViewResult Rendered(RepoModel model, ProjectTreeNode node, string shown, IReadOnlyDictionary<string, Layer> files)
    {
        if (Unreadable(node, shown, files) is { } refused)
        {
            return refused;
        }

        var notices = new List<string>();
        var drawable = files.Values.Where(l => l.Text is not null && !l.TooLargeToDraw).ToList();

        // A member table from any format over any layer: the readable layer is often the primary
        // file while the table lives in a native one beside it (the desktop's own comment).
        foreach (var format in PlcFormats.All)
        {
            foreach (var layer in drawable)
            {
                if (format.TryParseMemberTable(layer.Path, layer.Text!) is { IsValid: true } table)
                {
                    var html = new StringBuilder(MemberTableHtml.Fragment(WebNote(LinkTypes(table, layer.Path, model, null))));
                    if (table.IsInterface)
                    {
                        AppendInterfaceCode(html, format, node, files, older: null);
                    }

                    return Result("table", node, shown, html.ToString(), notices);
                }
            }
        }

        foreach (var format in PlcFormats.All)
        {
            foreach (var layer in drawable)
            {
                if (format.TryParseLadder(layer.Path, layer.Text!) is { } ladder)
                {
                    return Result("ladder", node, shown,
                        LadderHtml.Fragment(LadderDiff.Compare(ladder.Body, ladder.Body), ladder.Style, wording: SingleWording), notices);
                }
            }
        }

        var text = files[shown].Text!;
        if (IsMarkdown(shown))
        {
            return Result("markdown", node, shown, "<div class=\"md-page\">" + MarkdownHtml.Fragment(Markdown.Parse(text)) + "</div>", notices);
        }

        return Result("source", node, shown, SourceHtml.Fragment(StSourceLayout.Lay(text, PlcFormats.SectionsFor(shown, text)), LanguageFor(shown)), notices);
    }

    private ViewResult RawSingle(string shown, IReadOnlyDictionary<string, Layer> files)
    {
        var node = new ProjectTreeNode { Name = Name(shown), Path = shown, Kind = ProjectNodeKind.File, Files = [shown] };
        if (Unreadable(node, shown, files, allowLarge: true) is { } refused)
        {
            return refused;
        }

        var text = files[shown].Text!;
        var language = files[shown].TooLargeToDraw ? SyntaxLanguage.PlainText : LanguageFor(shown);
        var notices = files[shown].TooLargeToDraw ? new List<string> { TooLargeToDrawNotice } : [];
        return Result("source", node, shown, SourceHtml.Fragment(FileRows(text), language), notices) with { Layers = files.Keys.ToList() };
    }

    // ---- two versions -------------------------------------------------------------------------

    private ViewResult RenderedCompare(
        RepoModel olderModel, RepoModel newerModel, ProjectTreeNode node, string shown,
        IReadOnlyDictionary<string, Layer> before, IReadOnlyDictionary<string, Layer> after, bool sideBySide)
    {
        var notices = new List<string>();
        var newer = after.TryGetValue(shown, out var a) ? a : null;
        var older = before.TryGetValue(shown, out var b) ? b : null;
        if (newer is null && older is null)
        {
            return Missing(node, shown);
        }

        // Both sides, not whichever is found first. A side that could not be read has no text,
        // and no text is how "added" and "removed" are spelt below: the first version checked
        // only the newer copy, so a rate-limited older one drew every member as added and said
        // nothing (found in review). RawCompare says what could not be read.
        if (Undrawable(newer) || Undrawable(older))
        {
            return RawCompare(shown, before, after, sideBySide);
        }

        var changedFiles = node.Files.Any(f => (before.TryGetValue(f, out var x) ? x.BlobSha : null) != (after.TryGetValue(f, out var y) ? y.BlobSha : null));

        // Another layer too large to draw, on either side, is left out of BOTH sides and said.
        // Two reviews pulled opposite ways here. Left out of one side only, a native .xml over the
        // draw limit in the older version and under it in the newer drew every member as added,
        // and was cached (a draw-limit layer has no Failure, so the unreadable check passed it).
        // Refused outright, as the first fix for unreadable sides did with "too large", it threw
        // away the .st diff beside it that could be drawn. Out of both, the rest still compares
        // and nothing reads as new; the shown file's own size is handled above.
        var tooLarge = node.Files.Where(f => f != shown && (TooLarge(before, f) || TooLarge(after, f))).ToList();
        if (tooLarge.Count > 0)
        {
            before = Without(before, tooLarge);
            after = Without(after, tooLarge);
            notices.AddRange(tooLarge.Select(f => string.Format(CultureInfo.InvariantCulture, LeftOutNotice, Name(f))));
        }

        // The object's other layers too: a table or a ladder can come from the .xml beside an .st.
        if (CouldNotRead(node, shown, node.Files, before, after) is { } refused)
        {
            return refused;
        }

        foreach (var format in PlcFormats.All)
        {
            foreach (var path in node.Files)
            {
                var oldTable = Text(before, path) is { } ot ? format.TryParseMemberTable(path, ot) : null;
                var newTable = Text(after, path) is { } nt ? format.TryParseMemberTable(path, nt) : null;
                if (oldTable is not { IsValid: true } && newTable is not { IsValid: true })
                {
                    continue;
                }

                var baseline = oldTable is { IsValid: true } ? oldTable : new MemberTable { Name = newTable!.Name, IsInterface = newTable.IsInterface, IsDataType = newTable.IsDataType, IsVariableList = newTable.IsVariableList };
                var current = newTable is { IsValid: true } ? newTable : baseline with { Members = [] };
                // A member's type that changed between the two versions changes this block's
                // meaning while its own text may not move: highlighted, and said above the table.
                var changedPaths = ChangedPaths(olderModel, newerModel).Select(c => c.Path).ToHashSet(StringComparer.Ordinal);
                var compared = LinkTypes(Reword(MemberTableDiff.Compare(baseline, current)), path, newerModel, changedPaths, olderModel);
                if (compared.Members.Any(m => m.ReferenceChanged))
                {
                    notices.Add(TypeChangedNotice);
                }

                var html = new StringBuilder(MemberTableHtml.Fragment(WebNote(compared)));
                // A highlighted type counts as drawn: it is the highlight. Counting only the rows'
                // own changes said "the type changed" and "nothing this view draws did" together
                // (found in review).
                var tableChanged = compared.Members.Any(m => m.Change != MemberChange.None || m.ReferenceChanged);
                var codeChanged = false;
                if (compared.IsInterface)
                {
                    codeChanged = AppendInterfaceCode(html, format, node, after, before, sideBySide);
                }

                if (changedFiles && !tableChanged && !codeChanged)
                {
                    notices.Add(NothingDrawnChanged);
                }

                return Result("table", node, shown, html.ToString(), notices);
            }
        }

        foreach (var format in PlcFormats.All)
        {
            foreach (var path in node.Files)
            {
                var oldLadder = Text(before, path) is { } ot ? format.TryParseLadder(path, ot) : null;
                var newLadder = Text(after, path) is { } nt ? format.TryParseLadder(path, nt) : null;
                if (oldLadder is null && newLadder is null)
                {
                    continue;
                }

                var comparison = newLadder is null
                    ? LadderDiff.Compare(oldLadder!.Body, new LadderBody(oldLadder.Body.BlockName, []))
                    : LadderDiff.Compare(oldLadder?.Body, newLadder.Body);
                if (changedFiles && !comparison.HasChanges)
                {
                    notices.Add(NothingDrawnChanged);
                }

                return Result("ladder", node, shown,
                    LadderHtml.Fragment(comparison, (newLadder ?? oldLadder!).Style, sideBySide ? LadderView.SideBySide : LadderView.Merged, CompareWording), notices);
            }
        }

        var oldText = older?.Text;
        var newText = newer?.Text;
        var rows = StSourceLayout.Mark(StSourceLayout.Lay(newText, PlcFormats.SectionsFor(shown, newText ?? "")), oldText, newText);
        var marked = rows.Any(r => r is SourceRemovedLine or SourceCodeLine { Change: SourceChange.Added });
        if (changedFiles && !marked)
        {
            // Lines outside what the layout draws (a vendor's header) can be all that changed;
            // Mark shows those removals at the end, but an edit there with nothing removed would
            // otherwise read as no change at all.
            notices.Add(NothingDrawnChanged);
        }

        return Result("source", node, shown, SourceHtml.Fragment(rows, LanguageFor(shown), sideBySide ? SourceView.SideBySide : SourceView.Unified), notices);
    }

    private ViewResult RawCompare(string shown, IReadOnlyDictionary<string, Layer> before, IReadOnlyDictionary<string, Layer> after, bool sideBySide)
    {
        var node = new ProjectTreeNode { Name = Name(shown), Path = shown, Kind = ProjectNodeKind.File, Files = [shown] };
        var older = before.TryGetValue(shown, out var b) ? b : null;
        var newer = after.TryGetValue(shown, out var a) ? a : null;
        if (older?.Binary == true || newer?.Binary == true)
        {
            return Result("binary", node, shown, "<p class=\"cd-empty\">Binary file: there is no text to compare.</p>", []);
        }

        if (older?.Failure is RepoFailure.TooLarge || newer?.Failure is RepoFailure.TooLarge)
        {
            return Result("too-large", node, shown, "<p class=\"cd-empty\">This file is too large to compare here.</p>", []);
        }

        // Diffed against null, an unread side reads as a file added or deleted outright.
        if (CouldNotRead(node, shown, [shown], before, after) is { } refused)
        {
            return refused;
        }

        var large = older?.TooLargeToDraw == true || newer?.TooLargeToDraw == true;
        var document = UnifiedDiffParser.Parse(UnifiedDiffWriter.Write(older?.Text, newer?.Text));
        var language = large ? SyntaxLanguage.PlainText : LanguageFor(shown);
        return Result("diff", node, shown, TextDiffHtml.Fragment(document, language, sideBySide ? TextDiffView.SideBySide : TextDiffView.Unified),
            large ? [TooLargeToDrawNotice] : []);
    }

    /// <summary>
    /// The code under an interface table: the ladder when the primary layer is one, else the
    /// code body. Returns whether the code changed between the two versions.
    /// </summary>
    private static bool AppendInterfaceCode(
        StringBuilder html, IPlcProjectFormat format, ProjectTreeNode node,
        IReadOnlyDictionary<string, Layer> newer, IReadOnlyDictionary<string, Layer>? older, bool sideBySide = false)
    {
        if (node.PrimaryFile is not { } code)
        {
            return false;
        }

        var newText = Text(newer, code);
        var oldText = older is null ? null : Text(older, code);
        var ladderNew = newText is null ? null : format.TryParseLadder(code, newText);
        var ladderOld = oldText is null ? null : format.TryParseLadder(code, oldText);
        if (ladderNew is not null || ladderOld is not null)
        {
            var comparison = older is null
                ? LadderDiff.Compare(ladderNew!.Body, ladderNew.Body)
                : ladderNew is null
                    ? LadderDiff.Compare(ladderOld!.Body, new LadderBody(ladderOld.Body.BlockName, []))
                    : LadderDiff.Compare(ladderOld?.Body, ladderNew.Body);
            html.Append(LadderHtml.Fragment(comparison, (ladderNew ?? ladderOld!).Style, sideBySide ? LadderView.SideBySide : LadderView.Merged, older is null ? SingleWording : CompareWording));
            return older is not null && comparison.HasChanges;
        }

        var text = newText ?? oldText;
        if (text is null || format.CodeBody(code, text) is not { } body)
        {
            return false;
        }

        var rows = StSourceLayout.Body(text, body.Start, body.End);
        if (older is not null)
        {
            rows = StSourceLayout.Mark(rows, oldText, newText);
        }

        html.Append(SourceHtml.Fragment(rows, LanguageFor(code), sideBySide ? SourceView.SideBySide : SourceView.Unified));
        return older is not null && rows.Any(r => r is SourceRemovedLine or SourceCodeLine { Change: SourceChange.Added });
    }

    // ---- helpers -------------------------------------------------------------------------------

    internal const string NothingDrawnChanged =
        "The file changed, but nothing this view draws did. The change is in a part the drawing leaves out; the Raw tab shows it.";

    internal const string TooLargeToDrawNotice =
        "This file is too large to draw, so it is shown as plain text.";

    internal const string TypeChangedNotice =
        "A highlighted member's type changed between these two versions, which changes this block even where its own text did not.";

    /// <summary>
    /// Each member's type linked to the object it names in the same project, through the index
    /// the desktop's <c>LinkTypes</c> uses; and, in a comparison, marked when that object's files
    /// changed between the two versions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The link is the target's file in this repository, so the page opens it in the viewer. A
    /// library or system type finds nothing and stays text, as on the desktop: a link to nowhere
    /// is worse than none.
    /// </para>
    /// <para>
    /// A type deleted between the versions is looked for in <paramref name="older"/>: the newer
    /// commit's index cannot find it, so it stayed plain text and unmarked, the one change to a
    /// member's meaning the table could not show (found in review). It is marked, with no link,
    /// since there is nothing in this version to open.
    /// </para>
    /// </remarks>
    internal static MemberTable LinkTypes(MemberTable table, string sourceFile, RepoModel model, IReadOnlySet<string>? changed, RepoModel? older = null)
    {
        if (!table.Members.Any(m => m.TypeReference.Length > 0) || model.Objects.Lookup(sourceFile) is not { } owner)
        {
            return table;
        }

        return table with
        {
            Members = table.Members.Select(member =>
            {
                if (member.TypeReference.Length == 0)
                {
                    return member;
                }

                if (model.Objects.FindByName(owner.Project, member.TypeReference) is not { } target)
                {
                    var gone = changed is not null
                        && older?.Objects.Lookup(sourceFile) is { } was
                        && older.Objects.FindByName(was.Project, member.TypeReference) is { } removed
                        && removed.Paths.Any(changed.Contains);
                    return gone ? member with { ReferenceChanged = true } : member;
                }

                var open = target.Paths.Select(p => model.NodeFor(p)?.PrimaryFile).FirstOrDefault(p => p is not null) ?? target.Paths[0];
                return member with
                {
                    LinkGuid = open,
                    ReferenceChanged = changed is not null && target.Paths.Any(changed.Contains),
                };
            }).ToList(),
        };
    }

    internal const string OverBudgetNotice =
        "This file took too long to draw, so it is shown as plain text.";

    internal const string LeftOutNotice =
        "{0} is too large to draw in one of these versions, so this comparison leaves it out on both sides; its Raw tab shows it.";

    internal const string ParserFailedNotice =
        "This file could not be drawn, so it is shown as plain text.";

    /// <summary>One version shown: there is nothing it is compared with, so nothing to say about changes.</summary>
    internal static readonly LadderWording SingleWording = new("", "Every {0} is new.");

    /// <summary>Two versions the reader chose, never "the last commit".</summary>
    internal static readonly LadderWording CompareWording = new(
        "No changes between these two versions.", "Not in the older version: every {0} is new.");

    /// <summary>
    /// The table's note in this page's words. The desktop's note speaks of uncommitted changes and
    /// write-back, neither of which a browser viewing a commit has. A note a vendor set on purpose
    /// (a text list, say) is kept.
    /// </summary>
    internal static MemberTable WebNote(MemberTable table) => table.Note is not null ? table : table with
    {
        Note = (table.IsDataType, table.IsInterface, table.IsVariableList) switch
        {
            (true, _, _) => "The data type's members.",
            (_, true, _) => "The block interface.",
            (_, _, true) => "The global variables.",
            _ => "The data block interface.",
        },
    };

    /// <summary>
    /// The member table's summary in this page's words. <c>MemberTableDiff</c> writes for the
    /// desktop, where the comparison is always against the last commit; here it is between two
    /// commits the reader chose. <c>ViewServiceTests</c> pins the phrase this replaces, so a
    /// rewording in Core fails a test instead of reaching the page unnoticed.
    /// </summary>
    internal static MemberTable Reword(MemberTable table) => table with
    {
        ChangeSummary = table.ChangeSummary.Replace("Since the last commit", "Since the older version", StringComparison.Ordinal),

        // Each modified row's "was" tooltip too, which the first version missed and the
        // no-last-commit test caught.
        Members = table.Members
            .Select(m => m.Was.Length == 0 ? m : m with { Was = m.Was.Replace("At the last commit", "In the older version", StringComparison.Ordinal) })
            .ToList(),
    };

    private ViewResult? Unreadable(ProjectTreeNode node, string shown, IReadOnlyDictionary<string, Layer> files, bool allowLarge = false)
    {
        if (!files.TryGetValue(shown, out var layer))
        {
            return Missing(node, shown);
        }

        if (layer.Binary)
        {
            return Result("binary", node, shown, "<p class=\"cd-empty\">Binary file: there is no text to show.</p>", []);
        }

        if (layer.Failure == RepoFailure.TooLarge)
        {
            return Result("too-large", node, shown, "<p class=\"cd-empty\">This file is too large to show here.</p>", []);
        }

        if (layer.Failure != RepoFailure.None)
        {
            return Result("missing", node, shown, $"<p class=\"cd-empty\">This file could not be read ({CodeHtml.E(layer.Failure.ToString())}).</p>", []);
        }

        if (layer.TooLargeToDraw && !allowLarge)
        {
            return Result("source", node, shown, SourceHtml.Fragment(FileRows(layer.Text!), SyntaxLanguage.PlainText), [TooLargeToDrawNotice]);
        }

        return null;
    }

    /// <summary>Whether an answer drawn from these layers holds as long as their blobs do.</summary>
    private static bool Lasting(IEnumerable<Layer> layers) =>
        layers.All(l => l.Failure is RepoFailure.None or RepoFailure.TooLarge);

    private static bool TooLarge(IReadOnlyDictionary<string, Layer> layers, string file) =>
        layers.TryGetValue(file, out var layer) && (layer.TooLargeToDraw || layer.Failure == RepoFailure.TooLarge);

    private static IReadOnlyDictionary<string, Layer> Without(IReadOnlyDictionary<string, Layer> layers, IReadOnlyCollection<string> files) =>
        layers.Where(l => !files.Contains(l.Key)).ToDictionary(l => l.Key, l => l.Value, StringComparer.Ordinal);

    private static bool Undrawable(Layer? layer) =>
        layer is not null && (layer.Binary || layer.Failure != RepoFailure.None || layer.TooLargeToDraw);

    /// <summary>
    /// The first of <paramref name="files"/> that is in a version but could not be read from it,
    /// said as such; null when every one that exists was read. A file that is simply not in a
    /// version is a real addition or deletion and is drawn as one.
    /// </summary>
    private static ViewResult? CouldNotRead(ProjectTreeNode node, string shown, IEnumerable<string> files,
        IReadOnlyDictionary<string, Layer> before, IReadOnlyDictionary<string, Layer> after)
    {
        foreach (var file in files)
        {
            foreach (var (side, layers) in new[] { ("older", before), ("newer", after) })
            {
                if (layers.TryGetValue(file, out var layer) && layer.Failure != RepoFailure.None)
                {
                    var what = file == shown ? "this file" : CodeHtml.E(Name(file));
                    return Result("missing", node, shown,
                        $"<p class=\"cd-empty\">The {side} version of {what} could not be read ({CodeHtml.E(layer.Failure.ToString())}), "
                        + "so it is not compared: drawn without it, everything in it would read as new or gone.</p>", []);
                }
            }
        }

        return null;
    }

    private static ViewResult Plain(string shown, IReadOnlyDictionary<string, Layer> files, string notice)
    {
        var node = new ProjectTreeNode { Name = Name(shown), Path = shown, Kind = ProjectNodeKind.File, Files = [shown] };
        return files.TryGetValue(shown, out var layer) && layer.Text is { } text
            ? Result("source", node, shown, SourceHtml.Fragment(FileRows(text), SyntaxLanguage.PlainText), [notice])
            : Result("missing", node, shown, "<p class=\"cd-empty\">" + CodeHtml.E(notice) + "</p>", [notice]);
    }

    private static ViewResult Missing(ProjectTreeNode node, string shown) =>
        Result("missing", node, shown, "<p class=\"cd-empty\">This file is not in this version.</p>", []);

    private static ViewResult Result(string kind, ProjectTreeNode node, string shown, string html, IReadOnlyList<string> notices) =>
        new(kind, node.Kind == ProjectNodeKind.Object ? node.Name : Name(shown), shown, html, notices,
            node.Files.OrderByDescending(f => f == shown).ToList());

    private static string? Text(IReadOnlyDictionary<string, Layer> files, string path) =>
        files.TryGetValue(path, out var l) && !l.TooLargeToDraw ? l.Text : null;

    private static string Name(string path) => path[(path.LastIndexOf('/') + 1)..];

    private static bool IsMarkdown(string path) =>
        path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase);

    /// <summary>A vendor names its own extensions (a .gvl is Structured Text); the catalog the rest, as the desktop does.</summary>
    private static SyntaxLanguage LanguageFor(string path) => PlcFormats.LanguageFor(path);

    /// <summary>The file as itself: every line, numbered as the file numbers it.</summary>
    internal static IReadOnlyList<SourceRow> FileRows(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var count = lines.Length > 1 && lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;
        var rows = new SourceRow[count];
        for (var i = 0; i < count; i++)
        {
            rows[i] = new SourceCodeLine(i + 1, lines[i], i + 1);
        }

        return rows;
    }

    private static string Key(string what, RepoModel model, string commits, PaneView view, bool sideBySide,
        IReadOnlyDictionary<string, Layer> files, IReadOnlyDictionary<string, Layer>? before)
    {
        static string Shas(IReadOnlyDictionary<string, Layer>? f) =>
            f is null ? "-" : string.Join(',', f.Values.OrderBy(l => l.Path, StringComparer.Ordinal).Select(l => l.Path + "=" + l.BlobSha));

        return string.Create(CultureInfo.InvariantCulture,
            $"frag:{RendererVersion}:{what}:{model.Owner.ToLowerInvariant()}/{model.Repo.ToLowerInvariant()}:{commits}:{view}:{sideBySide}:{Shas(before)}:{Shas(files)}");
    }

    /// <summary>The render, cached by content and held to the budget.</summary>
    /// <param name="fallback">
    /// The file as escaped plain text, for a render that ran out of time. Escaping and
    /// hidden-character marking only: no tokenizer, no parser, nothing that can be slow.
    /// </param>
    /// <param name="remember">
    /// False when a layer's read failed for a reason of the moment (a rate limit, an outage): the
    /// answer says "could not be read", and cached under the blobs' key it went on saying so for
    /// half an hour after GitHub answered again (found in review). Too large is about the file,
    /// so it lasts. False too when the model the view was drawn from is Partial: a manifest
    /// that could not be read takes type links and object rows with it, and the view kept that
    /// for half an hour after the model itself was read again (found in review).
    /// </param>
    private async Task<RepoResult<ViewResult>> CachedAsync(string key, string path, Func<ViewResult> render, Func<string, ViewResult> fallback, bool remember)
    {
        if (cache.TryGetValue(key, out ViewResult? hit) && hit is not null)
        {
            return RepoResult<ViewResult>.Of(hit);
        }

        var work = Task.Run(render);
        ViewResult result;
        try
        {
            result = await work.WaitAsync(O.RenderBudget).ConfigureAwait(false);
        }
        catch (RegexMatchTimeoutException)
        {
            // Before TimeoutException, which it derives from.
            log.Warn(LogCategory.Web, $"A pattern timed out rendering {path}; shown as plain text (security rule 3)");
            return RepoResult<ViewResult>.Of(fallback(OverBudgetNotice));
        }
        catch (TimeoutException)
        {
            // The render keeps running to its own bounds; the reader is not kept waiting for it.
            // Not cached: a slow render on a busy machine may be quick on an idle one.
            log.Warn(LogCategory.Web, $"Rendering {path} exceeded {O.RenderBudget.TotalSeconds:0.#} s; shown as plain text (security rule 3)");
            return RepoResult<ViewResult>.Of(fallback(OverBudgetNotice));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A parser that throws on a hostile file. Only the two timeouts above were caught at
            // first, so this was a 500 for the reader instead of the file as text (found in
            // review). The exception's type and where it was thrown are logged, not its message:
            // a parser's message can quote the file, and file contents never reach the log. Not
            // cached, so a fixed parser is picked up without waiting out the entry.
            log.Error(LogCategory.Web,
                $"Rendering {path} threw {ex.GetType().Name} in {ex.TargetSite?.DeclaringType?.FullName}.{ex.TargetSite?.Name}; shown as plain text (security rule 3)");
            return RepoResult<ViewResult>.Of(fallback(ParserFailedNotice));
        }

        if (!remember)
        {
            return RepoResult<ViewResult>.Of(result);
        }

        cache.Set(key, result, new MemoryCacheEntryOptions { Size = Math.Max(1, result.Html.Length / 1024), SlidingExpiration = TimeSpan.FromMinutes(30) });
        return RepoResult<ViewResult>.Of(result);
    }
}
