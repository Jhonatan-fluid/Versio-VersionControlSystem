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
/// <b>Cached by content.</b> A fragment's key is the renderer version, the repository, the view
/// and every file's blob hash on both sides. Blobs never change, so neither does the fragment;
/// a renderer change bumps <see cref="RendererVersion"/>. The caller resolved both commits with
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
    public const int RendererVersion = 1;

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
        var key = Key("view", model, view, sideBySide, files, null);
        return await CachedAsync(key, path, () => view == PaneView.Raw
            ? RawSingle(shown, files)
            : Rendered(node, shown, files), () => Plain(shown, files)).ConfigureAwait(false);
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

        var before = await ReadAllAsync(access, older, node, ct).ConfigureAwait(false);
        var after = await ReadAllAsync(access, newer, node, ct).ConfigureAwait(false);
        var shown = Shown(node, path);
        var key = Key("compare", newer, view, sideBySide, after, before);
        return await CachedAsync(key, path, () => view == PaneView.Raw
            ? RawCompare(shown, before, after, sideBySide)
            : RenderedCompare(node, shown, before, after, sideBySide), () => Plain(shown, after.ContainsKey(shown) ? after : before)).ConfigureAwait(false);
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

    private async Task<IReadOnlyDictionary<string, Layer>> ReadAllAsync(RepoAccess access, RepoModel model, ProjectTreeNode node, CancellationToken ct)
    {
        var layers = new Dictionary<string, Layer>(StringComparer.Ordinal);
        foreach (var file in node.Files.Distinct(StringComparer.Ordinal))
        {
            if (!model.Files.TryGetValue(file, out var entry))
            {
                continue;
            }

            if (entry.Size > O.MaxBlobBytes)
            {
                layers[file] = new Layer(file, entry.BlobSha, null, RepoFailure.TooLarge, true, false);
                continue;
            }

            var key = $"blob:{model.Owner.ToLowerInvariant()}/{model.Repo.ToLowerInvariant()}:{entry.BlobSha}";
            if (!cache.TryGetValue(key, out byte[]? bytes) || bytes is null)
            {
                var read = await source.GetBlobAsync(access, model.Owner, model.Repo, entry.BlobSha, O.MaxBlobBytes, ct).ConfigureAwait(false);
                if (!read.Ok)
                {
                    layers[file] = new Layer(file, entry.BlobSha, null, read.Failure, read.Failure == RepoFailure.TooLarge, false);
                    continue;
                }

                bytes = read.Value!;
                cache.Set(key, bytes, new MemoryCacheEntryOptions { Size = Math.Max(1, bytes.Length / 1024), SlidingExpiration = TimeSpan.FromMinutes(30) });
            }

            var binary = Array.IndexOf(bytes, (byte)0) >= 0;
            layers[file] = new Layer(file, entry.BlobSha, binary ? null : Decode(bytes), RepoFailure.None, bytes.Length > O.MaxDrawBytes, binary);
        }

        return layers;
    }

    /// <summary>UTF-8, with a byte-order mark removed as File.ReadAllText removes it on the desktop.</summary>
    private static string Decode(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
            ? Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3)
            : Encoding.UTF8.GetString(bytes);

    private static string Shown(ProjectTreeNode node, string path) =>
        node.Files.Contains(path, StringComparer.Ordinal) && node.Kind != ProjectNodeKind.Object
            ? path
            : node.PrimaryFile ?? path;

    // ---- one version --------------------------------------------------------------------------

    private ViewResult Rendered(ProjectTreeNode node, string shown, IReadOnlyDictionary<string, Layer> files)
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
                    var html = new StringBuilder(MemberTableHtml.Fragment(table));
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
                        LadderHtml.Fragment(LadderDiff.Compare(ladder.Body, ladder.Body), ladder.Style), notices);
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
        ProjectTreeNode node, string shown, IReadOnlyDictionary<string, Layer> before, IReadOnlyDictionary<string, Layer> after, bool sideBySide)
    {
        var notices = new List<string>();
        var newer = after.TryGetValue(shown, out var a) ? a : null;
        var older = before.TryGetValue(shown, out var b) ? b : null;
        if ((newer ?? older) is not { } any)
        {
            return Missing(node, shown);
        }

        if (any.Binary || any.Failure != RepoFailure.None || any.TooLargeToDraw)
        {
            return RawCompare(shown, before, after, sideBySide);
        }

        var changedFiles = node.Files.Any(f => (before.TryGetValue(f, out var x) ? x.BlobSha : null) != (after.TryGetValue(f, out var y) ? y.BlobSha : null));

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
                var compared = Reword(MemberTableDiff.Compare(baseline, current));
                var html = new StringBuilder(MemberTableHtml.Fragment(compared));
                var tableChanged = compared.Members.Any(m => m.Change != MemberChange.None);
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
                    LadderHtml.Fragment(comparison, (newLadder ?? oldLadder!).Style, sideBySide ? LadderView.SideBySide : LadderView.Merged), notices);
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
            html.Append(LadderHtml.Fragment(comparison, (ladderNew ?? ladderOld!).Style, sideBySide ? LadderView.SideBySide : LadderView.Merged));
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

    internal const string OverBudgetNotice =
        "This file took too long to draw, so it is shown as plain text.";

    /// <summary>
    /// The member table's summary in this page's words. <c>MemberTableDiff</c> writes for the
    /// desktop, where the comparison is always against the last commit; here it is between two
    /// commits the reader chose. <c>ViewServiceTests</c> pins the phrase this replaces, so a
    /// rewording in Core fails a test instead of reaching the page unnoticed.
    /// </summary>
    internal static MemberTable Reword(MemberTable table) =>
        table with { ChangeSummary = table.ChangeSummary.Replace("Since the last commit", "Since the older version", StringComparison.Ordinal) };

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

    private static ViewResult Plain(string shown, IReadOnlyDictionary<string, Layer> files)
    {
        var node = new ProjectTreeNode { Name = Name(shown), Path = shown, Kind = ProjectNodeKind.File, Files = [shown] };
        return files.TryGetValue(shown, out var layer) && layer.Text is { } text
            ? Result("source", node, shown, SourceHtml.Fragment(FileRows(text), SyntaxLanguage.PlainText), [OverBudgetNotice])
            : Result("missing", node, shown, "<p class=\"cd-empty\">" + CodeHtml.E(OverBudgetNotice) + "</p>", [OverBudgetNotice]);
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
    private static SyntaxLanguage LanguageFor(string path) => PlcFormats.SyntaxFor(path) ?? LanguageCatalog.ForPath(path);

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

    private static string Key(string what, RepoModel model, PaneView view, bool sideBySide,
        IReadOnlyDictionary<string, Layer> files, IReadOnlyDictionary<string, Layer>? before)
    {
        static string Shas(IReadOnlyDictionary<string, Layer>? f) =>
            f is null ? "-" : string.Join(',', f.Values.OrderBy(l => l.Path, StringComparer.Ordinal).Select(l => l.Path + "=" + l.BlobSha));

        return string.Create(CultureInfo.InvariantCulture,
            $"frag:{RendererVersion}:{what}:{model.Owner.ToLowerInvariant()}/{model.Repo.ToLowerInvariant()}:{view}:{sideBySide}:{Shas(before)}:{Shas(files)}");
    }

    /// <summary>The render, cached by content and held to the budget.</summary>
    /// <param name="fallback">
    /// The file as escaped plain text, for a render that ran out of time. Escaping and
    /// hidden-character marking only: no tokenizer, no parser, nothing that can be slow.
    /// </param>
    private async Task<RepoResult<ViewResult>> CachedAsync(string key, string path, Func<ViewResult> render, Func<ViewResult> fallback)
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
            return RepoResult<ViewResult>.Of(fallback());
        }
        catch (TimeoutException)
        {
            // The render keeps running to its own bounds; the reader is not kept waiting for it.
            // Not cached: a slow render on a busy machine may be quick on an idle one.
            log.Warn(LogCategory.Web, $"Rendering {path} exceeded {O.RenderBudget.TotalSeconds:0.#} s; shown as plain text (security rule 3)");
            return RepoResult<ViewResult>.Of(fallback());
        }

        cache.Set(key, result, new MemoryCacheEntryOptions { Size = Math.Max(1, result.Html.Length / 1024), SlidingExpiration = TimeSpan.FromMinutes(30) });
        return RepoResult<ViewResult>.Of(result);
    }
}
