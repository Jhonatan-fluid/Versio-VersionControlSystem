import { useState } from 'react';
import { Link } from 'react-router';
import { api } from '../api';
import { blobUrl } from '../routes';
import { useAsync } from '../useAsync';
import { FragmentHost } from './FragmentHost';
import { ErrorPanel } from './Pane';
import { Icon, Loading } from './ui';

interface Props {
  owner: string;
  repo: string;
  /** The commit the object is read at: a review's head, a comparison's newer version. */
  refName: string;
  path: string;
  onClose: () => void;
  signInUrl: string | null;
}

/**
 * A look at an object without leaving where the reader is (docs/web/FINISH.md, C2 "stay in
 * context"): a type link or an identifier in a review or a comparison, to something that is not
 * part of it, opens here beside the page rather than navigating away from the review. Links
 * inside the peek move the peek; "Open" goes to the object's own page. Rendered only, at the
 * version named.
 */
export function PeekPane({ owner, repo, refName, path: start, onClose, signInUrl }: Props) {
  // The peek's own trail: a link inside it opens the next object here, and Back returns.
  const [trail, setTrail] = useState<string[]>([start]);
  const path = trail[trail.length - 1]!;
  const result = useAsync((s) => api.view(owner, repo, refName, path, 'rendered', 'unified', s), [owner, repo, refName, path]);
  const label = result.state === 'ok' ? result.value.layerLabels[result.value.layers.indexOf(result.value.path)] ?? result.value.path : path;
  return (
    <aside className="peek" aria-label="Peek at an object">
      <header className="peek-head">
        {trail.length > 1 && (
          <button type="button" className="btn btn-invisible btn-small" onClick={() => setTrail(trail.slice(0, -1))} aria-label="Back">
            <Icon name="chevron-left" size={12} />
          </button>
        )}
        <span className="peek-title text-bold" title={label}>{result.state === 'ok' ? result.value.title : label.slice(label.lastIndexOf('/') + 1)}</span>
        <span className="peek-at text-muted text-small">at {refName.length === 40 ? refName.slice(0, 7) : refName}</span>
        <Link className="btn btn-small" to={blobUrl(owner, repo, refName, path)}>Open</Link>
        <button type="button" className="btn btn-invisible btn-small" onClick={onClose} aria-label="Close the peek"><Icon name="x" size={14} /></button>
      </header>
      <div className="peek-body">
        {result.state === 'loading' && <Loading what="Reading the object…" />}
        {result.state === 'error' && <ErrorPanel error={result.error} signInUrl={signInUrl} />}
        {result.state === 'ok' && (
          <>
            {result.value.notices.map((n) => <p key={n} className="notice" role="note">{n}</p>)}
            <FragmentHost html={result.value.html} path={result.value.path} onOpenPath={(p) => setTrail([...trail, p])} line={null} onLine={() => {}} />
          </>
        )}
      </div>
    </aside>
  );
}
