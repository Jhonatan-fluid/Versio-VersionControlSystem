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
/// <param name="Title">For display, hidden characters spelled (security rule 9), as are <paramref name="Notices"/>.</param>
/// <param name="Path">For navigation: the file's exact path. <paramref name="Layers"/> likewise.</param>
public sealed record ViewResult(
    string Kind,
    string Title,
    string Path,
    string Html,
    IReadOnlyList<string> Notices,
    IReadOnlyList<string> Layers)
{
    /// <summary>Each layer's path for display, spelled; the page's chips show these, and ask for <see cref="Layers"/>.</summary>
    public IReadOnlyList<string> LayerLabels => Layers.Select(CodeHtml.Spelled).ToList();

    /// <summary>
    /// What a review comment can sit on in this drawing, matching the fragment's <c>data-anchors</c>
    /// (docs/web/PLAN.md, *Pull request review*, C3). Server-side only: placing a thread and
    /// translating a comment to GitHub lines are decided here, never from what a page says an
    /// element is.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<Versio.Core.Review.RenderedAnchor> Anchors { get; init; } = [];

    /// <summary>
    /// For a rendered comparison: which changed lines of the object's files the drawing can vouch
    /// for, and which it may not show (docs/web/PLAN.md, *Pull request review*, C4). Null for
    /// anything else. Server-side; the review route sends it on.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Versio.Core.Review.ReviewCoverage? Coverage { get; init; }
}

/// <summary>
/// Turns an object of a commit, or the same object in two commits, into the fragment the page
/// shows, with the renderers the desktop pane draws with.
/// </summary>
/// <remarks>
/// <para>
/// <b>The decision of what is drawn is Core's</b> (<see cref="RenderChoice.Decide"/>), shared with
/// the desktop pane since 2026-10-23. Until then this class mirrored the desktop's switch by hand,
/// and the two drifted: no project card, no catalogs, no husk rule, no web page here, and in a
/// pull request a directory-form project's container objects, which hold nothing, drew as raw metadata and XML
/// (FINDINGS 2026-10-23). This class reads the layers, hands Core the reader, and draws what it
/// chose with the renderers the desktop pane draws with; <c>RenderChoiceParityTests</c> runs both
/// hosts over every fixture object and asserts the same choice.
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
/// <b>Cached by content, commit, row and the file shown.</b> A fragment's key is the renderer version,
/// the repository, the commit or commits, the row, the file being shown, the view and every file's blob
/// hash on both sides. The file shown is in it because one object's layers share every blob hash:
/// without it, a layer chip got the first layer's cached answer back (found in review; the first
/// test of the chips built a new cache per call and never saw a warm entry). The row is in it
/// because a task row and its configuration share the one YAML file and every blob: without it
/// the task row was handed the configuration's whole table (RenderChoiceParityTests, 2026-10-23;
/// unseen before because both drew the YAML as source). The commit is in it
/// because a member table's links and "type changed" marks depend on files outside the object:
/// the same blobs at another commit can link elsewhere. A renderer change bumps
/// <see cref="RendererVersion"/>. The caller resolved both commits with
/// this person's credential before anything here runs (security rule 2).
/// </para>
/// </remarks>
public sealed partial class ViewService(
    IRepoSource source,
    IMemoryCache cache,
    IOptions<WebOptions> options,
    IVersioLogger log)
{
    /// <summary>Bumped whenever any renderer's output changes, so no stale fragment is served.</summary>
    public const int RendererVersion = 7;

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
        var key = Key("view", model, model.CommitSha + ":" + node.Path + ":" + shown, view, sideBySide, files, null);
        return await CachedAsync(key, path, () => view == PaneView.Raw
            ? RawSingle(shown, files)
            : Rendered(model, node, shown, files), notice => Plain(shown, files, notice), Lasting(files.Values) && !model.Partial).ConfigureAwait(false);
    }

    /// <summary>One object in two commits: the older one, then the newer.</summary>
    /// <param name="fullFile">
    /// The desktop pane's <i>Show full file</i>: false folds Rendered to git's context either side
    /// of each change and gives Raw that context; true draws every line on both tabs.
    /// </param>
    /// <param name="oldPath">
    /// Where the object was in the older commit, when it was renamed: its older layers are read from
    /// there and compared as the layers they became. Only a rename the caller was told about (GitHub
    /// reports them); null compares path with path.
    /// </param>
    public async Task<RepoResult<ViewResult>> CompareAsync(
        RepoAccess access, RepoModel older, RepoModel newer, string path, PaneView view, bool sideBySide, CancellationToken ct, string? oldPath = null, bool fullFile = false)
    {
        var node = newer.NodeFor(path) ?? older.NodeFor(path);
        if (node is null)
        {
            return RepoResult<ViewResult>.Fail(RepoFailure.NotFound);
        }

        var oldNode = oldPath is null ? node : older.NodeFor(oldPath) ?? node;
        var reads = await Task.WhenAll(ReadAllAsync(access, older, oldNode, ct), ReadAllAsync(access, newer, node, ct)).ConfigureAwait(false);
        var shown = Shown(node, path);
        var before = ReferenceEquals(oldNode, node) ? reads[0] : Renamed(reads[0], Shown(oldNode, oldPath!), node, shown);
        var after = reads[1];
        var key = Key("compare", newer, older.CommitSha + ".." + newer.CommitSha + ":" + node.Path + ":" + shown + (oldPath is null ? "" : "<-" + oldPath) + (fullFile ? ":full" : ""), view, sideBySide, after, before);
        return await CachedAsync(key, path, () => view == PaneView.Raw
            ? RawCompare(shown, before, after, sideBySide, fullFile)
            : RenderedCompare(older, newer, node, shown, before, after, sideBySide, fullFile), notice => Plain(shown, after.ContainsKey(shown) ? after : before, notice),
            Lasting(before.Values.Concat(after.Values)) && !older.Partial && !newer.Partial).ConfigureAwait(false);
    }

    /// <summary>
    /// A renamed object's older layers, keyed by the layers they became. The pair the caller named
    /// decides its own layer: <paramref name="oldPath"/> became <paramref name="newPath"/>, whatever
    /// the two are called. The object's other layers follow that pair by the stems it reveals
    /// (<c>GVL_Alarm.gvl.xml</c> follows <c>GVL_Alarm.gvl.st</c> → <c>GVL.Alarm2.gvl.st</c> to
    /// <c>GVL.Alarm2.gvl.xml</c>); one the pair cannot place is paired by the part of its file name
    /// after the first dot (a textual layer's suffix, a native one's, a two-part one's), which a
    /// rename usually keeps.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The drawing then compares the object with itself, as GitHub's rename does, instead of drawing
    /// the old path as deleted and the new one as wholly added — which reads as every line changed.
    /// A layer with no counterpart keeps its old path and is shown as gone. Moved here from slice 1
    /// of pull request review (docs/web/PLAN.md), because review anchors need the same mapping.
    /// </para>
    /// <para>
    /// The suffix rule was the whole mapping until review 2026-10-25 (P2): every layer, the one the
    /// caller had just named included, was paired by everything after its file name's first dot, so
    /// <c>old.txt</c> → <c>new.md</c> found no counterpart and <c>Old.Name.st</c> → <c>New.st</c>
    /// found none either, and both drew additions without removals — the old side the caller named
    /// was thrown away for want of a matching extension. The rename is a fact GitHub reported; the
    /// suffix is a guess about which layer is which, and the guess may not overrule the fact. It is
    /// kept for the sibling layers only because they are named by nobody: GitHub reports one file's
    /// rename at a time, and the pair itself says what the siblings became only when the two names
    /// share a dotted tail to take the stems from.
    /// </para>
    /// </remarks>
    private static IReadOnlyDictionary<string, Layer> Renamed(IReadOnlyDictionary<string, Layer> older, string oldPath, ProjectTreeNode node, string newPath)
    {
        static string Suffix(string path)
        {
            var file = Name(path);
            var dot = file.IndexOf('.', StringComparison.Ordinal);
            return dot < 0 ? "" : file[dot..];
        }

        var stems = Stems(Name(oldPath), Name(newPath));
        var result = new Dictionary<string, Layer>(StringComparer.Ordinal);
        foreach (var layer in older.Values.OrderByDescending(l => l.Path == oldPath))
        {
            string? match;
            if (layer.Path == oldPath)
            {
                match = node.Files.Contains(newPath, StringComparer.Ordinal) ? newPath : null;
            }
            else
            {
                var name = Name(layer.Path);
                var sibling = stems is { } s && name.StartsWith(s.Old + ".", StringComparison.Ordinal) ? s.New + name[s.Old.Length..] : null;
                match = node.Files.FirstOrDefault(f => Name(f) == sibling && !result.ContainsKey(f))
                    ?? node.Files.FirstOrDefault(f => Suffix(f) == Suffix(layer.Path) && !result.ContainsKey(f));
            }

            var target = match ?? layer.Path;
            result[target] = layer with { Path = target };
        }

        return result;
    }

    /// <summary>
    /// What two file names of a renamed object's layer have in front of the dotted tail they share:
    /// <c>GVL_Alarm</c> and <c>GVL.Alarm2</c> for <c>GVL_Alarm.gvl.st</c> and <c>GVL.Alarm2.gvl.st</c>.
    /// Null when they share no tail starting at a dot (<c>old.txt</c>, <c>new.md</c>), since then
    /// nothing says where one name's stem ends.
    /// </summary>
    private static (string Old, string New)? Stems(string oldName, string newName)
    {
        var shared = 0;
        while (shared < oldName.Length && shared < newName.Length && oldName[^(shared + 1)] == newName[^(shared + 1)])
        {
            shared++;
        }

        var dot = oldName[^shared..].IndexOf('.', StringComparison.Ordinal);
        if (dot < 0)
        {
            return null;
        }

        var tail = shared - dot;
        var stems = (oldName[..^tail], newName[..^tail]);
        return stems.Item1.Length == 0 || stems.Item2.Length == 0 ? null : stems;
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
    /// <para>
    /// Every layer at once: an object is often two or three files (.st and .xml), each a GitHub
    /// round trip, and none waits on another.
    /// </para>
    /// <para>
    /// <b>And each child's first file</b>, since 2026-10-23: a task configuration saved as one
    /// file per task is drawn from its children's files (<see cref="RenderChoice"/>, as the
    /// desktop reads them from disk), and a reader handed only the object's own layers found no
    /// task and drew the configuration's XML as source (RenderChoiceParityTests). The children's
    /// blobs are in the view's cache key too, which is right: a changed task changes the table.
    /// A block's methods cost their blob reads once each; the blob cache keeps repeats cheap.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyDictionary<string, Layer>> ReadAllAsync(RepoAccess access, RepoModel model, ProjectTreeNode node, CancellationToken ct)
    {
        var reads = node.Files.Concat(node.Children.Select(c => c.Files.FirstOrDefault()).OfType<string>()).Distinct(StringComparer.Ordinal)
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
        // Core's decision over the layers as fetched: a layer too large to draw, or binary, reads
        // as unreadable, which is what the desktop's reader answers for a file it cannot open.
        var choice = Choose(node, f => Text(files, f), shown);

        // The card before the readability check: a project row's own file is the vendor's binary,
        // or a folder with no file at all, and the card is drawn from the model, not from it.
        if (choice.Kind == RenderKind.ProjectCard)
        {
            return ProjectCard(model, node, shown);
        }

        if (Unreadable(node, shown, files) is { } refused)
        {
            return refused;
        }

        var notices = new List<string>();
        switch (choice.Kind)
        {
            case RenderKind.LibraryCatalog or RenderKind.TaskCatalog or RenderKind.DeviceCatalog:
                return Catalog(choice, choice.Model!, node, shown, notices, compared: false);

            case RenderKind.TaskCard:
            {
                var task = (TaskRecord)choice.Model!;
                return Result("task", node, shown, CardHtml.Fragment("task", task.Name, Cards.Task(task), choice.File!), notices);
            }

            case RenderKind.DeviceCard:
                return Result("device", node, shown, CardHtml.Fragment("device", node.Name, Cards.Device((DeviceCatalog)choice.Model!, node), choice.File!), notices);

            case RenderKind.MemberTable:
            {
                var html = new StringBuilder(MemberTableHtml.Fragment(WebNote(LinkTypes((MemberTable)choice.Model!, choice.File!, model, null))));
                AppendInterfaceCode(html, choice, files, older: null, links: choice.CodeFile is { } codeFile ? Links(model, codeFile) : null);
                return Result("table", node, shown, html.ToString(), notices);
            }

            case RenderKind.Ladder:
            {
                var ladder = (LadderDocument)choice.Model!;
                return Result("ladder", node, shown,
                    LadderHtml.Fragment(LadderDiff.Compare(ladder.Body, ladder.Body), ladder.Style, wording: SingleWording), notices);
            }

            case RenderKind.EmptyObject:
                return Result("note", node, shown, Note(node), notices);

            case RenderKind.Markdown:
                return Result("markdown", node, shown, MarkdownPage(files[shown].Text!), notices);

            case RenderKind.WebPage or RenderKind.Source:
            {
                var text = files[shown].Text!;
                if (choice.Notice is { } notice)
                {
                    notices.Add(notice);
                }

                return Result(choice.Kind == RenderKind.WebPage ? "webpage" : "source", node, shown,
                    SourceHtml.Fragment(StSourceLayout.Lay(text, PlcFormats.SectionsFor(shown, text)), LanguageFor(shown), links: Links(model, shown)), notices);
            }

            default:
                return Missing(node, shown);
        }
    }

    /// <summary>
    /// The choice for an object, with the layer chip honoured: a table or a ladder comes from any
    /// layer and its code from the object's own primary file, whichever chip is pressed; the text
    /// arms (the note, Markdown, the page, the source) draw the layer asked for.
    /// </summary>
    /// <remarks>
    /// Two calls rather than one with the chip as primary: with the chip on an object's native
    /// XML, one call would put that XML under the block's interface table where its code body
    /// belongs. The second call changes only the text arms, since the arms before them do not
    /// read the primary file.
    /// </remarks>
    private static RenderChoice Choose(ProjectTreeNode node, Func<string, string?> read, string shown)
    {
        var choice = RenderChoice.Decide(node, read);
        return shown != node.PrimaryFile
               && choice.Kind is RenderKind.EmptyObject or RenderKind.Markdown or RenderKind.WebPage or RenderKind.Source or RenderKind.Nothing
            ? RenderChoice.Decide(node, read, shown)
            : choice;
    }

    /// <summary>
    /// The project row's card, from what the commit holds (<see cref="Cards.Project"/>): the
    /// model's project for this row, and how many files its folder has for a source project.
    /// </summary>
    private static ViewResult ProjectCard(RepoModel model, ProjectTreeNode node, string shown)
    {
        var project = model.Projects.FirstOrDefault(p => string.Equals(p.ProjectPath.Replace('\\', '/').TrimEnd('/'), node.Path.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            ?? new WorkspaceProject { ProjectPath = node.Path, ExportPath = node.Path };
        var prefix = node.Path.TrimEnd('/') + "/";
        var fileCount = model.Files.Keys.Count(k => k.StartsWith(prefix, StringComparison.Ordinal));
        return Result("project", node, shown, CardHtml.Fragment("project", node.Name, Cards.Project(project, fileCount)), []);
    }

    /// <summary>A catalog as its table, with the anchors its rows stand for.</summary>
    private static ViewResult Catalog(RenderChoice choice, object catalog, ProjectTreeNode node, string shown, List<string> notices, bool compared)
    {
        var file = choice.File ?? shown;
        var (kind, html, anchors) = catalog switch
        {
            LibraryCatalog l => ("libraries", CatalogHtml.Fragment(l, compared, file), CatalogHtml.Anchors(l, compared, file)),
            TaskCatalog t => ("tasks", CatalogHtml.Fragment(t, compared, file), CatalogHtml.Anchors(t, compared, file)),
            DeviceCatalog d => ("devices", CatalogHtml.Fragment(d, compared, file), CatalogHtml.Anchors(d, compared, file)),
            _ => throw new InvalidOperationException($"{catalog.GetType().Name} is not a catalog"),
        };
        return Result(kind, node, shown, html, notices) with { Anchors = anchors };
    }

    /// <summary>
    /// The empty-object note as a page: an object with nothing in its own file (the logic root of
    /// a device, a communication manager) says so, where one line of archive XML read as a view
    /// that had failed. Raw keeps the file.
    /// </summary>
    private static string Note(ProjectTreeNode node) =>
        MarkdownPage(EmptyObjectNote.For(hasChildren: node.Children.Count > 0));

    private static string MarkdownPage(string markdown) =>
        "<div class=\"md-page\">" + MarkdownHtml.Fragment(Markdown.Parse(markdown)) + "</div>";

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
        IReadOnlyDictionary<string, Layer> before, IReadOnlyDictionary<string, Layer> after, bool sideBySide, bool fullFile)
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
            return RawCompare(shown, before, after, sideBySide, fullFile);
        }

        bool Differs(string f) => (before.TryGetValue(f, out var x) ? x.BlobSha : null) != (after.TryGetValue(f, out var y) ? y.BlobSha : null);

        // Another layer too large to draw, on either side, is left out of BOTH sides and said.
        // Two reviews pulled opposite ways here. Left out of one side only, a native .xml over the
        // draw limit in the older version and under it in the newer drew every member as added,
        // and was cached (a draw-limit layer has no Failure, so the unreadable check passed it).
        // Refused outright, as the first fix for unreadable sides did with "too large", it threw
        // away the .st diff beside it that could be drawn. Out of both, the rest still compares
        // and nothing reads as new; the shown file's own size is handled above.
        //
        // The notice says what is true for each limit (found in review): a layer over the draw
        // limit was fetched, and its own Raw tab shows the difference; one over the fetch limit was
        // never fetched, so nothing here can show it, and pointing at Raw sent the reader to "too
        // large to compare". A left-out layer that changed says so itself, and does not count
        // toward "nothing this view draws changed", whose Raw tab is the file on screen and holds
        // no such change.
        var tooLarge = node.Files.Where(f => f != shown && (TooLarge(before, f) || TooLarge(after, f))).ToList();
        foreach (var f in tooLarge)
        {
            notices.Add(LeftOut(Name(f), TooLarge(before, f) && TooLarge(after, f), !Unfetched(before, f) && !Unfetched(after, f), Differs(f)));
        }

        var changedFiles = node.Files.Except(tooLarge).Any(Differs);
        if (tooLarge.Count > 0)
        {
            before = Without(before, tooLarge);
            after = Without(after, tooLarge);
        }

        // What each renderer below can vouch for, by the file it drew from (C4). A file drawn with
        // no evidence (a ladder, an XML table) is reported whole as "no-evidence"; one not drawn at
        // all as "not-drawn"; one over the draw limit as "too-large", without reading its lines.
        var evidence = new Dictionary<string, List<Versio.Core.Review.Coverage.Evidence>>(StringComparer.Ordinal);
        var drawn = new HashSet<string>(StringComparer.Ordinal);
        var b4 = before;
        var af = after;
        Versio.Core.Review.ReviewCoverage Covered()
        {
            var report = Versio.Core.Review.Coverage.Check(
                node.Files.Except(tooLarge).Distinct().Where(Differs).Select(f => (f, Text(b4, f), Text(af, f))),
                evidence.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<Versio.Core.Review.Coverage.Evidence>)kv.Value),
                f => drawn.Contains(f) ? "no-evidence" : "not-drawn");
            var large = tooLarge.Where(Differs).Select(f => new Versio.Core.Review.CoverageGap(f, Versio.Core.Review.ReviewSide.New, 0, 0, "too-large"));
            return report with { Gaps = [.. report.Gaps, .. large] };
        }

        void Vouch(string path, Versio.Core.Review.Coverage.Evidence e)
        {
            drawn.Add(path);
            (evidence.TryGetValue(path, out var list) ? list : evidence[path] = []).Add(e);
        }

        // The object's other layers too: a table or a ladder can come from the .xml beside an .st.
        if (CouldNotRead(node, shown, node.Files, before, after) is { } refused)
        {
            return refused;
        }

        // Core's decision, from the newer copy of each layer and the older one where the newer is
        // gone: a deleted object is still drawn as what it was, every row struck.
        var choice = Choose(node, f => Text(after, f) ?? Text(before, f), shown);
        switch (choice.Kind)
        {
            case RenderKind.ProjectCard:
                // A project row compared: the newer card. Its facts are not compared; the files
                // under the row are, each as its own object.
                return ProjectCard(newerModel, node, shown);

            case RenderKind.LibraryCatalog or RenderKind.TaskCatalog or RenderKind.DeviceCatalog:
            {
                // Both sides through the one reader the choice carries, so a vendor's file and
                // Versio's YAML each compare like with like. A side with no file at all is no
                // catalog, and every row reads as added or removed, which is what happened.
                var olds = choice.Files.Select(f => Text(before, f)).ToList();
                var news = choice.Files.Select(f => Text(after, f)).ToList();
                var compared = RewordCatalog(ComparedCatalog(
                    olds.All(t => t is null) ? null : choice.Reparse!(olds),
                    news.All(t => t is null) ? null : choice.Reparse!(news)));
                var marked = compared switch
                {
                    LibraryCatalog l => l.References.Any(r => r.Mark.Change != MemberChange.None),
                    TaskCatalog t => t.Tasks.Any(r => r.Mark.Change != MemberChange.None),
                    DeviceCatalog d => d.Connectors.Any(r => r.Mark.Change != MemberChange.None) || d.ChangeSummary.Length > 0,
                    _ => false,
                };
                if (changedFiles && !marked)
                {
                    notices.Add(NothingDrawnChanged);
                }

                // A catalog's rows carry no line evidence: drawn, and reported whole, so the
                // view stays partial where its file changed outside the rows.
                foreach (var file in choice.Files)
                {
                    drawn.Add(file);
                }

                return Catalog(choice, compared, node, shown, notices, compared: true) with { Coverage = Covered() };
            }

            // A card, a note or a document draws one version and marks nothing, so a change in
            // its file is said rather than shown: the Raw tab has it.
            case RenderKind.TaskCard:
            {
                var task = (TaskRecord)choice.Model!;
                if (changedFiles)
                {
                    notices.Add(NothingDrawnChanged);
                }

                return Result("task", node, shown, CardHtml.Fragment("task", task.Name, Cards.Task(task), choice.File!), notices) with { Coverage = Covered() };
            }

            case RenderKind.DeviceCard:
                if (changedFiles)
                {
                    notices.Add(NothingDrawnChanged);
                }

                return Result("device", node, shown, CardHtml.Fragment("device", node.Name, Cards.Device((DeviceCatalog)choice.Model!, node), choice.File!), notices) with { Coverage = Covered() };

            case RenderKind.EmptyObject:
                if (changedFiles)
                {
                    notices.Add(NothingDrawnChanged);
                }

                return Result("note", node, shown, Note(node), notices) with { Coverage = Covered() };

            case RenderKind.Markdown:
                if (changedFiles)
                {
                    notices.Add(NothingDrawnChanged);
                }

                return Result("markdown", node, shown, MarkdownPage((newer ?? older)!.Text!), notices) with { Coverage = Covered() };

            case RenderKind.MemberTable:
            {
                var path = choice.File!;
                var format = choice.Format!;
                var oldTable = Text(before, path) is { } ot ? format.TryParseMemberTable(path, ot) : null;
                var newTable = Text(after, path) is { } nt ? format.TryParseMemberTable(path, nt) : null;
                var baseline = oldTable is { IsValid: true } ? oldTable : new MemberTable { Name = newTable!.Name, IsInterface = newTable.IsInterface, IsDataType = newTable.IsDataType, IsVariableList = newTable.IsVariableList };
                // A deleted file's "current" table has no rows and no framing either: the older
                // framing carried over would answer for new-side lines that do not exist (latent,
                // rule 1 short-circuits a missing side; closed all the same, review 2026-10-24).
                var current = newTable is { IsValid: true } ? newTable : baseline with { Members = [], Structural = [], Unshown = [] };
                // A member's type that changed between the two versions changes this block's
                // meaning while its own text may not move: highlighted, and said above the table.
                var changedPaths = ChangedPaths(olderModel, newerModel).Select(c => c.Path).ToHashSet(StringComparer.Ordinal);
                var compared = LinkTypes(Reword(MemberTableDiff.Compare(baseline, current)), path, newerModel, changedPaths, olderModel) with { SourcePath = path };
                if (compared.Members.Any(m => m.ReferenceChanged))
                {
                    notices.Add(TypeChangedNotice);
                }

                var html = new StringBuilder(MemberTableHtml.Fragment(WebNote(compared), compared: true));
                var anchors = new List<Versio.Core.Review.RenderedAnchor>(MemberTableHtml.Anchors(compared, compared: true));
                Vouch(path, Versio.Core.Review.Coverage.Table(compared, Text(before, path), Text(after, path)));
                // A highlighted type counts as drawn: it is the highlight. Counting only the rows'
                // own changes said "the type changed" and "nothing this view draws did" together
                // (found in review).
                var tableChanged = compared.Members.Any(m => m.Change != MemberChange.None || m.ReferenceChanged);
                var codeChanged = AppendInterfaceCode(html, choice, after, before, sideBySide, anchors, Vouch, fullFile, choice.CodeFile is { } codeFile ? Links(newerModel, codeFile) : null);
                if (changedFiles && !tableChanged && !codeChanged)
                {
                    notices.Add(NothingDrawnChanged);
                }

                return Result("table", node, shown, html.ToString(), notices) with { Anchors = anchors, Coverage = Covered() };
            }

            case RenderKind.Ladder:
            {
                var path = choice.File!;
                var format = choice.Format!;
                var oldLadder = Text(before, path) is { } ot ? format.TryParseLadder(path, ot) : null;
                var newLadder = Text(after, path) is { } nt ? format.TryParseLadder(path, nt) : null;
                var comparison = newLadder is null
                    ? LadderDiff.Compare(oldLadder!.Body, new LadderBody(oldLadder.Body.BlockName, []))
                    : LadderDiff.Compare(oldLadder?.Body, newLadder.Body);
                if (changedFiles && !comparison.HasChanges)
                {
                    notices.Add(NothingDrawnChanged);
                }

                // The ladder's evidence (Coverage.Ladder): every network vouches for its span on the sides
                // it is drawn on, and the parser's framing outside the networks is vouched for too. It
                // was "drawn, no evidence" until the coverage rewrite of 2026-10-23 (FINDINGS, "the
                // coverage banner on real code"), which left these two lines to this file.
                Vouch(path, Versio.Core.Review.Coverage.Ladder(comparison, oldLadder?.Structural ?? [], newLadder?.Structural ?? []));
                return Result("ladder", node, shown,
                    LadderHtml.Fragment(comparison, (newLadder ?? oldLadder!).Style, sideBySide ? LadderView.SideBySide : LadderView.Merged, CompareWording, path), notices)
                    with { Anchors = LadderHtml.Anchors(comparison, path), Coverage = Covered() };
            }

            case RenderKind.WebPage or RenderKind.Source:
            {
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

                if (choice.Notice is { } notice)
                {
                    notices.Add(notice);
                }

                Vouch(shown, Versio.Core.Review.Coverage.Source(rows));
                return Result(choice.Kind == RenderKind.WebPage ? "webpage" : "source", node, shown,
                    SourceHtml.Fragment(rows, LanguageFor(shown), sideBySide ? SourceView.SideBySide : SourceView.Unified, shown, fullFile, Links(newerModel, shown)), notices)
                    with { Coverage = Covered() };
            }

            default:
                return Missing(node, shown);
        }
    }

    /// <summary>
    /// Two readings of one catalog compared: a side that is no catalog is an empty one, so a
    /// manager added in the newer version has every reference added, and one deleted has every
    /// reference removed, as the desktop draws a never-committed file.
    /// </summary>
    private static object ComparedCatalog(object? older, object? newer) => (older, newer) switch
    {
        (LibraryCatalog { IsValid: true } o, LibraryCatalog n) => CatalogDiff.Compare(o, n),
        (_, LibraryCatalog n) => CatalogDiff.Compare(null, n),
        (LibraryCatalog o, _) => CatalogDiff.Compare(o, o with { References = [] }),
        (TaskCatalog { IsValid: true } o, TaskCatalog n) => CatalogDiff.Compare(o, n),
        (_, TaskCatalog n) => CatalogDiff.Compare(null, n),
        (TaskCatalog o, _) => CatalogDiff.Compare(o, o with { Tasks = [] }),
        (DeviceCatalog { IsValid: true } o, DeviceCatalog n) => CatalogDiff.Compare(o, n),
        (_, DeviceCatalog n) => CatalogDiff.Compare(null, n),
        (DeviceCatalog o, _) => CatalogDiff.Compare(o, o with { Connectors = [], Slots = new Dictionary<string, string>(StringComparer.Ordinal) }),
        _ => throw new InvalidOperationException("neither side is a catalog"),
    };

    private ViewResult RawCompare(string shown, IReadOnlyDictionary<string, Layer> before, IReadOnlyDictionary<string, Layer> after, bool sideBySide, bool fullFile = false)
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
        // Show full file on Raw is the desktop's re-read with the whole file as context.
        var context = fullFile ? int.MaxValue / 2 : UnifiedDiffWriter.DefaultContext;
        var document = UnifiedDiffParser.Parse(UnifiedDiffWriter.Write(older?.Text, newer?.Text, context));
        var language = large ? SyntaxLanguage.PlainText : LanguageFor(shown);
        return Result("diff", node, shown, TextDiffHtml.Fragment(document, language, sideBySide ? TextDiffView.SideBySide : TextDiffView.Unified),
            large ? [TooLargeToDrawNotice] : []);
    }

    /// <summary>
    /// The code under an interface table, as the choice says: the ladder when the primary layer is
    /// one, the code body when the format finds one, else the whole file as the desktop has always
    /// drawn it (a block saved as the vendor's XML, a graphical block's text header). Returns
    /// whether the code changed between the two versions.
    /// </summary>
    /// <param name="anchors">Where a comparison's review anchors go: the code's ladder adds its networks.</param>
    /// <param name="vouch">Where a comparison's coverage evidence goes: a drawn code body vouches for its marked lines.</param>
    private static bool AppendInterfaceCode(
        StringBuilder html, RenderChoice choice,
        IReadOnlyDictionary<string, Layer> newer, IReadOnlyDictionary<string, Layer>? older, bool sideBySide = false,
        List<Versio.Core.Review.RenderedAnchor>? anchors = null,
        Action<string, Versio.Core.Review.Coverage.Evidence>? vouch = null,
        bool fullFile = false,
        Func<string, string?>? links = null)
    {
        if (choice.Code == CodeUnder.None || choice.CodeFile is not { } code || choice.Format is not { } format)
        {
            return false;
        }

        var newText = Text(newer, code);
        var oldText = older is null ? null : Text(older, code);
        if (choice.Code == CodeUnder.Ladder)
        {
            var ladderNew = newText is null ? null : format.TryParseLadder(code, newText);
            var ladderOld = oldText is null ? null : format.TryParseLadder(code, oldText);
            if (ladderNew is null && ladderOld is null)
            {
                return false;
            }

            var comparison = older is null
                ? LadderDiff.Compare(ladderNew!.Body, ladderNew.Body)
                : ladderNew is null
                    ? LadderDiff.Compare(ladderOld!.Body, new LadderBody(ladderOld.Body.BlockName, []))
                    : LadderDiff.Compare(ladderOld?.Body, ladderNew.Body);
            html.Append(LadderHtml.Fragment(comparison, (ladderNew ?? ladderOld!).Style, sideBySide ? LadderView.SideBySide : LadderView.Merged, older is null ? SingleWording : CompareWording, code));
            anchors?.AddRange(LadderHtml.Anchors(comparison, code));
            vouch?.Invoke(code, Versio.Core.Review.Coverage.Ladder(comparison, ladderOld?.Structural ?? [], ladderNew?.Structural ?? []));
            return older is not null && comparison.HasChanges;
        }

        var text = newText ?? oldText;
        if (text is null)
        {
            return false;
        }

        IReadOnlyList<SourceRow> rows;
        var hideRemovalsBefore = 0;
        if (choice.Code == CodeUnder.Body && format.CodeBody(code, text) is { } body)
        {
            rows = StSourceLayout.Body(text, body.Start, body.End);

            // A body drawn under its table draws no declaration, so a removal from the OLD file's
            // declaration must not surface as a struck line at the top of the code: the table
            // already says what the declaration lost. The desktop's MarkSourceChangesAsync rule,
            // through the same seam; without it a method whose header line went drew
            // "METHOD MoveHome" struck above its body here and not in the app (the owner,
            // comparing the two on a real pull request, 2026-10-23).
            hideRemovalsBefore = oldText is null ? 0 : format.CodeBody(code, oldText)?.Start ?? 0;
        }
        else
        {
            rows = StSourceLayout.Lay(text, PlcFormats.SectionsFor(code, text));
        }

        if (older is not null)
        {
            rows = StSourceLayout.Mark(rows, oldText, newText, hideRemovalsBefore);
            vouch?.Invoke(code, Versio.Core.Review.Coverage.Source(rows));
        }

        html.Append(SourceHtml.Fragment(rows, LanguageFor(code), sideBySide ? SourceView.SideBySide : SourceView.Unified, code, older is null || fullFile, links));
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

    /// <summary>
    /// What is true of a left-out layer, said in one sentence each: which limit, in one version or
    /// both, and whether it differs. The first wording promised "its Raw tab shows the difference"
    /// whenever the layer had been fetched, including when it was the same in both versions, and
    /// said "in one of these versions" when both were over the limit (found in review).
    /// </summary>
    internal static string LeftOut(string name, bool inBoth, bool fetched, bool differs) =>
        $"{name} is too large to {(fetched ? "draw" : "fetch")} in {(inBoth ? "both versions" : "one of these versions")}, "
        + "so this comparison leaves it out on both sides. "
        + (!differs
            ? "It is the same in both."
            : fetched
                ? $"It differs between them; open {name} and its Raw tab shows how."
                : "It differs between them, and that cannot be shown here.");

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
    /// <summary>
    /// A catalog's marks in this page's words, as <see cref="Reword"/> does for a member table:
    /// <see cref="CatalogDiff"/> writes "since the last commit" for the desktop, and here the two
    /// versions are the reader's. <c>ViewServiceTests</c> pins the phrase this replaces.
    /// </summary>
    internal static object RewordCatalog(object catalog)
    {
        static string Words(string text) => text
            .Replace("Since the last commit", "Since the older version", StringComparison.Ordinal)
            .Replace("since the last commit", "since the older version", StringComparison.Ordinal);
        static RowMark Marked(RowMark mark) => mark.Was.Length == 0 ? mark : mark with { Was = Words(mark.Was) };
        return catalog switch
        {
            LibraryCatalog l => l with { ChangeSummary = Words(l.ChangeSummary), References = l.References.Select(r => r with { Mark = Marked(r.Mark) }).ToList() },
            TaskCatalog t => t with { ChangeSummary = Words(t.ChangeSummary), Tasks = t.Tasks.Select(r => r with { Mark = Marked(r.Mark) }).ToList() },
            DeviceCatalog d => d with { ChangeSummary = Words(d.ChangeSummary), Connectors = d.Connectors.Select(r => r with { Mark = Marked(r.Mark) }).ToList() },
            _ => catalog,
        };
    }

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

    private static bool Unfetched(IReadOnlyDictionary<string, Layer> layers, string file) =>
        layers.TryGetValue(file, out var layer) && layer.Failure == RepoFailure.TooLarge;

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
        new(kind, CodeHtml.Spelled(node.Kind == ProjectNodeKind.Object ? node.Name : Name(shown)), shown, html, notices.Select(CodeHtml.Spelled).ToList(),
            node.Files.OrderByDescending(f => f == shown).ToList());

    private static string? Text(IReadOnlyDictionary<string, Layer> files, string path) =>
        files.TryGetValue(path, out var l) && !l.TooLargeToDraw ? l.Text : null;

    private static string Name(string path) => path[(path.LastIndexOf('/') + 1)..];

    /// <summary>A vendor names its own extensions (a variable list's is Structured Text); the catalog the rest, as the desktop does.</summary>
    private static SyntaxLanguage LanguageFor(string path) => PlcFormats.LanguageFor(path);

    /// <summary>
    /// What an identifier in <paramref name="sourceFile"/> opens (Step 3c): the object of that name
    /// in the same project, as the file the page shows for it — the same target a member table's
    /// type link has, through Core's <see cref="ObjectLinks"/>. Null for a file no object owns.
    /// </summary>
    private static Func<string, string?>? Links(RepoModel model, string sourceFile) =>
        ObjectLinks.Resolver(model.Objects, sourceFile, files => files.Paths.Select(p => model.NodeFor(p)?.PrimaryFile).FirstOrDefault(p => p is not null) ?? files.Paths[0]);

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
