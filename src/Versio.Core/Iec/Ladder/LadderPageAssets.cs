namespace Versio.Core.Iec.Ladder;

/// <summary>
/// The stylesheet and the script that go with <see cref="LadderHtml"/>, for every page that
/// shows it: this app's pane, and the Outpost React page.
/// </summary>
/// <remarks>
/// <para>
/// <b>Text constants, not embedded files.</b> Core does no I/O, and a resource stream is I/O.
/// A constant also keeps the stylesheet in the same review as the markup it styles. Its class
/// names are a contract, and a rename that missed one side would be a silent unstyled page.
/// </para>
/// <para>
/// <b>Colours are variables, set on <c>:root</c>.</b> A host overrides them by declaring the
/// same variables later or closer, which is how the app maps its own theme brushes onto them,
/// so the diagram's added, removed and changed colours match the Raw diff beside it. The defaults
/// here follow the system theme, for a host that sets nothing.
/// </para>
/// </remarks>
public static class LadderPageAssets
{
    public const string Css = """
        :root {
          --ld-fg: #1f2328; --ld-bg: #ffffff; --ld-muted: #656d76; --ld-wire: #1f2328;
          --ld-box: #f6f8fa; --ld-border: #d0d7de;
          --ld-added: #1a7f37; --ld-added-bg: rgba(46, 160, 67, 0.15);
          --ld-removed: #cf222e; --ld-removed-bg: rgba(248, 81, 73, 0.15);
          --ld-changed: #9a6700; --ld-changed-bg: rgba(212, 167, 44, 0.22);
          --ld-moved: #0969da; --ld-moved-bg: rgba(84, 174, 255, 0.15);
          --ld-mono: "Cascadia Mono", Consolas, monospace;
          --ld-ui: "Segoe UI", system-ui, sans-serif;
        }
        @media (prefers-color-scheme: dark) {
          :root {
            --ld-fg: #e6edf3; --ld-bg: #0d1117; --ld-muted: #8d96a0; --ld-wire: #c9d1d9;
            --ld-box: #161b22; --ld-border: #30363d;
            --ld-added: #3fb950; --ld-added-bg: rgba(46, 160, 67, 0.2);
            --ld-removed: #f85149; --ld-removed-bg: rgba(248, 81, 73, 0.2);
            --ld-changed: #d29922; --ld-changed-bg: rgba(187, 128, 9, 0.25);
            --ld-moved: #58a6ff; --ld-moved-bg: rgba(56, 139, 253, 0.2);
          }
        }
        .ld-page { container-type: inline-size; color: var(--ld-fg); font: 13px var(--ld-ui); }
        .ld-summary { position: sticky; top: 0; z-index: 1; display: flex; flex-wrap: wrap; gap: 4px;
          align-items: center; padding: 6px 0 8px; background: var(--ld-bg); }
        .ld-count { cursor: pointer; font-weight: 600; text-decoration: none; }
        .ld-count.ld-c-changed { color: var(--ld-changed); }
        .ld-count.ld-c-added { color: var(--ld-added); }
        .ld-count.ld-c-removed { color: var(--ld-removed); }
        .ld-count.ld-c-moved { color: var(--ld-moved); }
        .ld-note, .ld-sep { color: var(--ld-muted); }
        .ld-tools { display: none; margin-left: auto; gap: 6px; align-items: center; }
        .ld-scripted .ld-tools { display: inline-flex; }
        .ld-tools button { font: inherit; color: var(--ld-fg); background: var(--ld-box);
          border: 1px solid var(--ld-border); border-radius: 4px; padding: 0 6px; cursor: pointer; }
        .ld-rows { display: flex; flex-direction: column; gap: 10px; }
        .ld-two .ld-row { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 1fr); gap: 12px; }
        .ld-row.ld-current .ld-side { outline: 2px solid var(--ld-moved); outline-offset: 2px; }
        @container (max-width: 900px) {
          .ld-two .ld-row { grid-template-columns: minmax(0, 1fr); gap: 4px; }
          .ld-gap { display: none; }
        }
        .ld-side { min-width: 0; border-left: 4px solid transparent; padding-left: 8px; }
        .ld-side.ld-c-added { border-left-color: var(--ld-added); }
        .ld-side.ld-c-removed { border-left: 4px dashed var(--ld-removed); }
        .ld-side.ld-c-changed { border-left-color: var(--ld-changed); }
        .ld-side.ld-c-moved { border-left-color: var(--ld-moved); }
        .ld-side.ld-c-moved-changed { border-left-color: var(--ld-moved);
          box-shadow: inset 4px 0 0 var(--ld-changed); }
        .ld-gap { min-height: 40px; border-radius: 4px; opacity: 0.7;
          background: repeating-linear-gradient(45deg, transparent 0 6px, var(--ld-border) 6px 7px); }
        .ld-head { display: flex; flex-wrap: wrap; gap: 8px; align-items: baseline; font-weight: 600; }
        .ld-text-changed { background: var(--ld-changed-bg); border-radius: 3px; padding: 0 3px; }
        .ld-was, .ld-fallback { color: var(--ld-muted); font-weight: 400; font-size: 12px; }
        .ld-title { font-weight: 400; }
        .ld-badge { font-weight: 600; font-size: 12px; }
        .ld-c-added .ld-badge { color: var(--ld-added); }
        .ld-c-removed .ld-badge { color: var(--ld-removed); }
        .ld-c-changed .ld-badge, .ld-c-moved-changed .ld-badge { color: var(--ld-changed); }
        .ld-c-moved .ld-badge { color: var(--ld-moved); }
        .ld-comment { margin: 2px 0 4px; color: var(--ld-muted); white-space: pre-wrap; width: fit-content; }
        .ld-label { font-family: var(--ld-mono); font-weight: 600; }
        .ld-scroll { overflow-x: auto; }
        .ld-net { display: block; width: var(--w); max-width: 100%; min-width: calc(var(--w) * 0.6); height: auto; }
        .ld-net line, .ld-net path { stroke: var(--ld-wire); stroke-width: 1.4; fill: none; }
        .ld-net .ld-frame { fill: var(--ld-box); stroke: var(--ld-wire); stroke-width: 1.2; }
        .ld-net .ld-dashed { stroke-dasharray: 4 3; stroke: var(--ld-muted); }
        .ld-net .ld-neg { fill: var(--ld-bg); stroke: var(--ld-wire); }
        .ld-net text { font: 12px var(--ld-mono); fill: var(--ld-fg); }
        .ld-net .ld-t-type { font-weight: 700; }
        .ld-net .ld-t-pin, .ld-net .ld-t-detail, .ld-net .ld-t-placeholder { fill: var(--ld-muted); }
        .ld-net .ld-t-instance { fill: var(--ld-muted); }
        .ld-net .ld-mark { stroke-width: 1.5; }
        .ld-m-added .ld-mark { fill: var(--ld-added-bg); stroke: var(--ld-added); }
        .ld-m-removed .ld-mark { fill: var(--ld-removed-bg); stroke: var(--ld-removed); stroke-dasharray: 4 3; }
        .ld-m-removed .ld-t-operand { text-decoration: line-through; }
        .ld-m-modified .ld-mark { fill: var(--ld-changed-bg); stroke: var(--ld-changed); }
        .ld-m-modified .ld-t-operand { fill: var(--ld-changed); font-weight: 700; }
        .ld-stub { color: var(--ld-moved); font-size: 12px; padding: 2px 12px;
          border-left: 4px solid var(--ld-moved); }
        .ld-stub-text { cursor: pointer; }
        .ld-changes-only .ld-row[data-change="same"] .ld-scroll,
        .ld-changes-only .ld-row[data-change="same"] .ld-comment,
        .ld-changes-only .ld-row[data-change="same"] .ld-labels { display: none; }
        .ld-changes-only .ld-row[data-change="same"] .ld-side { opacity: 0.7; }
        .ld-changes-only .ld-row[data-change="same"] .ld-old { display: none; }
        """;

    /// <summary>
    /// Navigation between changes, the "changes only" toggle, and old and new scrolling sideways
    /// together. Optional: the page reads the same without it.
    /// </summary>
    public const string Script = """
        (() => {
          const page = document.querySelector('.ld-page');
          if (!page) return;
          page.classList.add('ld-scripted');
          const rows = [...page.querySelectorAll('.ld-row')];
          const changed = rows.filter(r => r.dataset.change !== 'same');
          let at = -1;
          const show = (row) => {
            rows.forEach(r => r.classList.remove('ld-current'));
            row.classList.add('ld-current');
            row.scrollIntoView({ block: 'center' });
            at = changed.indexOf(row);
          };
          const step = (by) => {
            if (!changed.length) return;
            show(changed[(at + by + changed.length) % changed.length]);
          };
          page.addEventListener('click', (e) => {
            const t = e.target.closest('[data-action],[data-jump]');
            if (!t) return;
            if (t.dataset.jump) {
              const row = document.getElementById(t.dataset.jump);
              if (row) show(row);
            } else if (t.dataset.action === 'next') step(1);
            else if (t.dataset.action === 'prev') step(-1);
          });
          const only = page.querySelector('[data-action="changes-only"]');
          if (only) {
            only.checked = page.dataset.changesOnly === 'true';
            const apply = () => page.classList.toggle('ld-changes-only', only.checked);
            only.addEventListener('change', apply);
            apply();
          }
          document.addEventListener('keydown', (e) => {
            if (e.key === 'F7') { e.preventDefault(); step(e.shiftKey ? -1 : 1); }
          });
          rows.forEach((row) => {
            const panes = row.querySelectorAll('.ld-scroll');
            if (panes.length !== 2) return;
            let syncing = false;
            panes.forEach((pane, i) => pane.addEventListener('scroll', () => {
              if (syncing) return;
              syncing = true;
              panes[1 - i].scrollLeft = pane.scrollLeft;
              requestAnimationFrame(() => { syncing = false; });
            }));
          });
        })();
        """;
}
