import { useEffect, useState } from 'react';
import type { Async } from '../useAsync';
import type { Layout, PaneView, View } from '../api';
import { ApiError } from '../api';
import { FragmentHost } from './FragmentHost';

interface Props {
  result: Async<View>;
  view: PaneView;
  layout: Layout;
  onView: (view: PaneView) => void;
  onLayout: (layout: Layout) => void;
  onLayer: (path: string) => void;
  onOpenPath: (path: string) => void;
  line: number | null;
  onLine: (line: number) => void;
  /** A comparison shows the layout switch on both tabs; a single version only on Raw diffs. */
  comparing: boolean;
  /** The desktop pane's Show full file, shown with a comparison: clear folds it to its changes. */
  full?: boolean;
  onFull?: (full: boolean) => void;
  signInUrl: string | null;
  /**
   * Where the file's own bytes are served, for an HTML object's "Web page" tab (the desktop's
   * third chip, FINISH.md Step 9): framed sandboxed — no script, no origin, no form — with the
   * raw route's own sandbox policy behind the frame's attribute.
   */
  pageUrl?: string | null;
}

const kinds: Record<string, string> = {
  table: 'Member table',
  ladder: 'Networks',
  markdown: 'Document',
  source: 'Source',
  diff: 'Text diff',
  binary: 'Binary',
  'too-large': 'Too large',
  missing: 'Missing',
};

/** Everything up to the last slash, for the quiet part of the heading. */
function folderOf(path: string): string {
  const slash = path.lastIndexOf('/');
  return slash < 0 ? '' : path.slice(0, slash);
}

/** The object pane: what the desktop's Rendered and Raw tabs show, placed from the server's HTML. */
export function Pane({ result, view, layout, onView, onLayout, onLayer, onOpenPath, line, onLine, comparing, signInUrl, full = false, onFull, pageUrl }: Props) {
  // The web page tab is the pane's own: not in the URL, since a pasted link should open on the
  // drawing, and gone the moment another file opens.
  const [page, setPage] = useState(false);
  const shownPath = result.state === 'ok' ? result.value.path : null;
  useEffect(() => setPage(false), [shownPath]);
  if (result.state === 'loading') {
    return <section className="pane pane-empty" aria-busy="true">Loading...</section>;
  }

  if (result.state === 'error') {
    return <ErrorPanel error={result.error} signInUrl={signInUrl} />;
  }

  const v = result.value;
  const webPage = v.kind === 'webpage' && pageUrl ? pageUrl : null;
  // The desktop's PaneHeading: a small label for the KIND of view, the name in the face an
  // ellipsis cannot eat, the folder after it in the quiet one, the full path as the tooltip.
  // The folder comes from the SPELLED label (security rule 9), not the raw path — the raw path
  // is for navigation and may hold the very characters the label spells out.
  const shownLabel = v.layerLabels[v.layers.indexOf(v.path)] ?? v.path;
  const folder = folderOf(shownLabel);
  return (
    <section className="pane">
      <header className="pane-head">
        <div className="pane-title" title={shownLabel}>
          <span className="pane-label" title={kinds[v.kind] ?? v.kind}>{comparing ? 'DIFF' : 'FILE'}</span>
          <h1>{v.title}</h1>
          {folder && <span className="pane-folder">{folder}</span>}
        </div>
        <div className="pane-tools">
          <div className="segmented" role="tablist" aria-label="View">
            <button type="button" role="tab" aria-selected={view === 'rendered' && !page} onClick={() => { setPage(false); onView('rendered'); }}>Rendered</button>
            <button type="button" role="tab" aria-selected={view === 'raw' && !page} onClick={() => { setPage(false); onView('raw'); }}>Raw</button>
            {webPage && <button type="button" role="tab" aria-selected={page} onClick={() => setPage(true)}>Web page</button>}
          </div>
          {comparing && onFull && (
            <label className="full-file-label" title="Every line, on both tabs; clear, the drawing is folded to the lines around each change">
              <input type="checkbox" checked={full} onChange={(e) => onFull(e.target.checked)} /> Show full file
            </label>
          )}
          {(comparing || view === 'raw') && (
            <div className="segmented" role="group" aria-label="Layout">
              <button type="button" aria-pressed={layout === 'unified'} onClick={() => onLayout('unified')}>Unified</button>
              <button type="button" aria-pressed={layout === 'split'} onClick={() => onLayout('split')}>Side by side</button>
            </div>
          )}
        </div>
      </header>
      {v.layers.length > 1 && (
        <div className="layers" aria-label="Files of this object">
          {v.layers.map((layer, i) => {
            const label = v.layerLabels[i] ?? layer;
            return (
              <button key={layer} type="button" className={`chip${layer === v.path ? ' is-on' : ''}`} onClick={() => onLayer(layer)} title={label}>
                {label.slice(label.lastIndexOf('/') + 1)}
              </button>
            );
          })}
        </div>
      )}
      {v.notices.map((n) => (
        <p key={n} className="notice" role="note">{n}</p>
      ))}
      {page && webPage ? (
        // sandbox with nothing allowed: no script, no same-origin, no form, no popup. The raw
        // route answers with the same sandbox in its policy, so the page is inert even opened
        // directly; the desktop draws it with its scripts off, as this does.
        <iframe className="page-frame" sandbox="" src={webPage} title={`${v.title} as a web page`} />
      ) : (
        <FragmentHost html={v.html} path={v.path} onOpenPath={onOpenPath} line={line} onLine={onLine} />
      )}
    </section>
  );
}

export function ErrorPanel({ error, signInUrl }: { error: Error; signInUrl: string | null }) {
  const api = error instanceof ApiError ? error : null;
  return (
    <section className="pane pane-empty" role="alert">
      <p className="error-text">{api?.message ?? 'Something went wrong. Try again.'}</p>
      {api?.status === 401 && signInUrl && (
        <a className="button" href={signInUrl}>Sign in with GitHub</a>
      )}
      {api?.status === 404 && signInUrl && (
        <p className="muted">
          If this repository is private, <a href={signInUrl}>sign in</a> to see it.
        </p>
      )}
    </section>
  );
}
