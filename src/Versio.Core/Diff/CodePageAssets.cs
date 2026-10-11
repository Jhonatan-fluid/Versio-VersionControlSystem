namespace Versio.Core.Diff;

/// <summary>
/// The stylesheet that goes with <see cref="CodeHtml"/> and the renderers built on it, for every
/// page that shows code: the Versio Web viewer, and later Outpost and the desktop pane.
/// </summary>
/// <remarks>
/// <para>
/// <b>A text constant, as <c>LadderPageAssets</c> is, for its reasons.</b> Core does no I/O, and
/// the class names are a contract with the markup: a rename that missed one side would be a
/// silent unstyled page, so <c>SourceHtmlTests</c> checks every class the fragment emits is here.
/// </para>
/// <para>
/// <b>The default colours are the desktop's own</b>, copied from <c>Palette.Light.xaml</c> and
/// <c>Palette.Dark.xaml</c>, so the same file reads the same in the window and in a browser. A
/// host overrides the variables by declaring them later or closer, as <c>LadderPage</c> maps the
/// window's brushes onto the ladder's.
/// </para>
/// <para>
/// <b>Long files are left to the browser, in blocks.</b> The desktop virtualises a 33,644-line
/// file; a page cannot. Rows come in <c>.cd-block</c> groups, and <c>content-visibility: auto</c>
/// lets the browser skip laying out the blocks nobody can see. Whether that is enough on the
/// fixture's largest file is measured in docs/web/PLAN.md Step 1 before paging is built.
/// </para>
/// </remarks>
public static class CodePageAssets
{
    public const string Css = """
        :root {
          --cd-fg: #1f2328; --cd-bg: #ffffff; --cd-muted: #6e7781; --cd-gutter: #8c959f;
          --cd-border: #d0d7de;
          --cd-added: #1a7f37; --cd-removed: #cf222e;
          --cd-added-bg: #e6f6ec; --cd-removed-bg: #fceaea;
          --cd-added-word: #bbebcb; --cd-removed-word: #f9cfcf;
          --cd-hidden: #9a6700; --cd-hidden-bg: rgba(212, 167, 44, 0.22);
          --cd-t-keyword: #8250df; --cd-t-type: #1a7f8e; --cd-t-comment: #6e7781;
          --cd-t-string: #0a6b34; --cd-t-number: #0550ae; --cd-t-directive: #953800;
          --cd-mono: "Cascadia Mono", Consolas, monospace;
          --cd-ui: "Segoe UI", system-ui, sans-serif;
        }
        @media (prefers-color-scheme: dark) {
          :root {
            --cd-fg: #e6edf3; --cd-bg: #0d1117; --cd-muted: #8b949e; --cd-gutter: #6e7681;
            --cd-border: #30363d;
            --cd-added: #3fb950; --cd-removed: #f85149;
            --cd-added-bg: #12301f; --cd-removed-bg: #331617;
            --cd-added-word: #1b5334; --cd-removed-word: #5c2326;
            --cd-hidden: #d29922; --cd-hidden-bg: rgba(187, 128, 9, 0.25);
            --cd-t-keyword: #d2a8ff; --cd-t-type: #5fd3e0; --cd-t-comment: #8b949e;
            --cd-t-string: #7ee787; --cd-t-number: #79c0ff; --cd-t-directive: #ffa657;
          }
        }
        .cd-page { color: var(--cd-fg); font: 13px var(--cd-ui); }
        .cd-notice { margin: 0 0 8px; padding: 6px 10px; border-left: 4px solid var(--cd-hidden);
          background: var(--cd-hidden-bg); }
        .cd-rows { font: 13px/20px var(--cd-mono); overflow-x: auto; }
        .cd-block { content-visibility: auto; contain-intrinsic-size: auto 5120px; }
        .cd-row { display: grid; grid-template-columns: 46px max-content; min-height: 20px; }
        .cd-split { display: grid; grid-template-columns: minmax(0, 1fr) 1px minmax(0, 1fr); }
        .cd-split > .cd-rule { background: var(--cd-border); }
        .cd-cell { display: grid; grid-template-columns: 46px minmax(0, 1fr); min-height: 20px; }
        .cd-cell .cd-code { overflow: hidden; text-overflow: ellipsis; }
        .cd-num { color: var(--cd-gutter); text-align: right; padding-right: 8px; user-select: none; }
        .cd-code { white-space: pre; padding: 0 12px 0 6px; }
        .cd-added { background: var(--cd-added-bg); }
        .cd-removed { background: var(--cd-removed-bg); }
        .cd-removed > .cd-num { color: var(--cd-removed); }
        .cd-removed .cd-code del { text-decoration: line-through; }
        .cd-added .cd-em { background: var(--cd-added-word); color: inherit; }
        .cd-removed .cd-em { background: var(--cd-removed-word); color: inherit; }
        .cd-em { background: transparent; color: inherit; }
        .cd-divider { display: grid; grid-template-columns: 46px max-content 1fr; align-items: center;
          margin: 10px 0 4px; font: 600 11px var(--cd-ui); color: var(--cd-muted);
          text-transform: uppercase; }
        .cd-divider:first-child { margin-top: 2px; }
        .cd-divider > span { padding: 0 8px 0 6px; }
        .cd-divider::after { content: ""; height: 1px; margin-right: 12px; background: var(--cd-border); }
        .cd-divider::before { content: ""; }
        .cd-hidden { color: var(--cd-hidden); background: var(--cd-hidden-bg); border-radius: 3px;
          padding: 0 2px; font-size: 11px; }
        .cd-t-keyword { color: var(--cd-t-keyword); }
        .cd-t-type { color: var(--cd-t-type); }
        .cd-t-comment { color: var(--cd-t-comment); }
        .cd-t-string { color: var(--cd-t-string); }
        .cd-t-number { color: var(--cd-t-number); }
        .cd-t-directive { color: var(--cd-t-directive); }
        """;
}
