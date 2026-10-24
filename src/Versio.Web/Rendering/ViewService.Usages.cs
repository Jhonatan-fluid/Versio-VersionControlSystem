using System.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Versio.Core.Diff;
using Versio.Core.Logging;
using Versio.Core.Plc;
using Versio.Core.Projects;
using Versio.Web.Repo;
using Versio.Web.Sources;

namespace Versio.Web.Rendering;

/// <summary>One object whose source names the one asked about: where it is in the tree, and what it is.</summary>
public sealed record Usage(string Path, string Label, string Name, string Type);

/// <summary>
/// The "used by" answer (docs/web/FINISH.md, C2 and Step 3c): the objects whose sources name this
/// one, and whether the search was cut short.
/// </summary>
public sealed record Usages(string Name, IReadOnlyList<Usage> UsedBy, bool Truncated, int Searched);

public sealed partial class ViewService
{
    /// <summary>Files read for one search at most: a project of thousands of objects answers with what was read, and says so.</summary>
    internal const int MaxUsageReads = 1500;

    /// <summary>
    /// The objects of the same project whose source names the object at <paramref name="path"/>
    /// — the reverse of an identifier link, built by reading every object's text once at this
    /// commit (the blobs are cached, so the drawings and the search share each read) and asking
    /// Core which names it refers to outside comments and strings.
    /// </summary>
    /// <remarks>
    /// Per commit the answer is fixed, so it is cached by commit and object. The search stops at
    /// <see cref="MaxUsageReads"/> files or the render budget, and the answer says it was cut
    /// rather than reading as "used nowhere else". A file no exported object owns is "not found":
    /// nothing has a name to look for. The search is by name: an object whose source says
    /// <c>x.Start()</c> names a method called Start, whichever block's it is, and is listed under
    /// every Start — the page says "names", and the declaration-aware version is FINISH.md's.
    /// </remarks>
    public async Task<RepoResult<Usages>> UsagesAsync(RepoAccess access, RepoModel model, string path, CancellationToken ct)
    {
        if (model.NodeFor(path) is not { } node || model.Objects.Lookup(node.PrimaryFile ?? path) is not { } owner)
        {
            return RepoResult<Usages>.Fail(RepoFailure.NotFound);
        }

        var key = $"usages:v{RendererVersion}:{model.Owner.ToLowerInvariant()}/{model.Repo.ToLowerInvariant()}:{model.CommitSha}:{owner.Project}:{owner.Guid}";
        if (cache.TryGetValue(key, out Usages? cached) && cached is not null)
        {
            return RepoResult<Usages>.Of(cached);
        }

        var watch = Stopwatch.StartNew();
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { owner.Name };
        var usedBy = new List<Usage>();
        var searched = 0;
        var truncated = false;
        foreach (var (other, files) in model.Objects.ObjectsIn(owner.Project))
        {
            if (other.Guid == owner.Guid)
            {
                continue;
            }

            // The object's text layers: source in a language the tokenizer knows words of. A
            // native XML layer beside an .st names the same things and more (GUIDs, attributes),
            // and holds the declaration as element text where an ST comment is not a comment to
            // the XML tokenizer — so the XML is read only where there is nothing else (review,
            // 2026-10-24: the first version counted .xml as typed and read both layers of every
            // two-layer object, strings and comments included).
            var typed = files.Paths.Where(p => LanguageFor(p) is var language && !ReferenceEquals(language, SyntaxLanguage.PlainText) && !ReferenceEquals(language, LanguageCatalog.Xml)).ToList();
            var layers = typed.Count > 0 ? typed : files.Paths.ToList();
            var hit = false;
            foreach (var file in layers.Where(model.Files.ContainsKey))
            {
                if (searched >= MaxUsageReads || watch.Elapsed > O.RenderBudget)
                {
                    truncated = true;
                    break;
                }

                searched++;
                var layer = await ReadLayerAsync(access, model, file, model.Files[file], ct).ConfigureAwait(false);
                if (layer.Text is null)
                {
                    continue;
                }

                if (ObjectLinks.NamesReferenced(layer.Text, LanguageFor(file), candidates).Count > 0)
                {
                    hit = true;
                    break;
                }
            }

            if (hit)
            {
                var open = files.Paths.Select(p => model.NodeFor(p)?.PrimaryFile).FirstOrDefault(p => p is not null) ?? files.Paths[0];
                usedBy.Add(new Usage(open, CodeHtml.Spelled(open), CodeHtml.Spelled(other.Name), other.TypeLabel));
            }

            if (truncated)
            {
                break;
            }
        }

        var result = new Usages(CodeHtml.Spelled(owner.Name), usedBy, truncated, searched);
        log.Info(LogCategory.Web, $"usages of {owner.Name} at {model.CommitSha[..Math.Min(8, model.CommitSha.Length)]}: {usedBy.Count} object(s) of {searched} file(s) read in {watch.ElapsedMilliseconds} ms{(truncated ? ", cut short" : "")}");
        // A cut answer is cached too, briefly: it is as true in five minutes as now, and a project
        // over the bound otherwise re-read up to 1,500 blobs on every view of every object
        // (review, 2026-10-24).
        cache.Set(key, result, new MemoryCacheEntryOptions { Size = 1 + usedBy.Count / 16, SlidingExpiration = truncated ? TimeSpan.FromMinutes(5) : TimeSpan.FromMinutes(30) });
        return RepoResult<Usages>.Of(result);
    }
}
