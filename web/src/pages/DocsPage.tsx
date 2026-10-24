import { useState } from 'react';
import { useLocation, useNavigate, useParams, useSearchParams } from 'react-router';
import type { Layout, Me, PaneView, TreeNode } from '../api';
import { api, rawUrl } from '../api';
import { ErrorPanel, Pane } from '../components/Pane';
import { RepoLayout } from '../components/RepoLayout';
import { Blankslate, Button, Icon, LinkButton, Loading } from '../components/ui';
import { blobUrl, docsUrl } from '../routes';
import { usePageTitle } from '../title';
import { useAsync } from '../useAsync';
import { lineFromHash } from './RepoPage';

/**
 * What the Documentation panel shows, by extension. Text documents render through the same
 * /view the Project panel uses; PDF is the one format a browser draws natively, embedded from
 * /raw; the Office formats are downloads, because converting them server-side was rejected
 * (PLAN.md, "The product shape" item 4). The server's allowlist is Documents.cs — a kind added
 * here without a server rule there downloads nothing.
 */
export function docKind(path: string): 'text' | 'pdf' | 'download' | null {
  const dot = path.lastIndexOf('.');
  if (dot < 0 || path.lastIndexOf('/') > dot) {
    return null;
  }
  const kinds: Record<string, 'text' | 'pdf' | 'download'> = {
    md: 'text', markdown: 'text', txt: 'text', csv: 'text',
    pdf: 'pdf',
    docx: 'download', doc: 'download', xlsx: 'download', xls: 'download',
    pptx: 'download', ppt: 'download', odt: 'download', ods: 'download', odp: 'download', rtf: 'download',
  };
  return kinds[path.slice(dot + 1).toLowerCase()] ?? null;
}

interface Doc {
  node: TreeNode;
  kind: 'text' | 'pdf' | 'download';
}

/** Every document in the tree, wherever it is — a manual beside a project export counts. */
function collect(nodes: TreeNode[], out: Doc[]): Doc[] {
  for (const n of nodes) {
    const kind = n.kind === 'file' ? docKind(n.path) : null;
    if (kind) {
      out.push({ node: n, kind });
    }
    collect(n.children, out);
  }
  return out;
}

const folderOf = (path: string) => (path.includes('/') ? path.slice(0, path.lastIndexOf('/')) : '');
const nameOf = (path: string) => path.slice(path.lastIndexOf('/') + 1);

/** /owner/repo/docs/ref[?path=…]: every document in the repository, under its path. */
export function DocsPage({ me }: { me: Me | null }) {
  const { owner = '', repo = '', ref: refParam } = useParams();
  const [search, setSearch] = useSearchParams();
  const location = useLocation();
  const navigate = useNavigate();
  const picked = search.get('path');
  const [listOpen, setListOpen] = useState(false);
  const view: PaneView = search.get('view') === 'raw' ? 'raw' : 'rendered';
  const layout: Layout = search.get('layout') === 'split' ? 'split' : 'unified';

  const info = useAsync((s) => api.repo(owner, repo, s), [owner, repo]);
  const ref = refParam ?? (info.state === 'ok' ? info.value.defaultBranch : null);
  const tree = useAsync((s) => (ref ? api.tree(owner, repo, ref, s) : new Promise<never>(() => {})), [owner, repo, ref]);

  const docs = tree.state === 'ok' ? collect(tree.value.nodes, []) : [];
  const pickedKind = picked ? docKind(picked) : null;
  const pane = useAsync(
    (s) => (ref && picked && pickedKind === 'text' ? api.view(owner, repo, ref, picked, view, layout, s) : new Promise<never>(() => {})),
    [owner, repo, ref, picked, pickedKind, view, layout],
  );

  const defaultBranch = info.state === 'ok' ? info.value.defaultBranch : 'main';
  const signIn = me?.signInAvailable && !me.signedIn ? api.signInUrl(location.pathname + location.search) : null;
  usePageTitle(picked ? nameOf(picked) : 'Docs', `${owner}/${repo}`);

  function setParam(key: string, value: string, fallback: string) {
    const next = new URLSearchParams(search);
    if (value === fallback) {
      next.delete(key);
    } else {
      next.set(key, value);
    }
    setSearch(next);
  }

  // Grouped under their folders, the root first: a repository's documents are organised by
  // where they live, and the panel keeps that rather than flattening it to one pile.
  const groups = new Map<string, Doc[]>();
  for (const d of docs) {
    const folder = folderOf(d.node.path);
    groups.set(folder, [...(groups.get(folder) ?? []), d]);
  }
  const folders = [...groups.keys()].sort((a, b) => (a === '' ? -1 : b === '' ? 1 : a.localeCompare(b)));

  return (
    <RepoLayout me={me} owner={owner} repo={repo} nav="docs" refName={ref ?? defaultBranch} wide>
      {/* The list is a drawer at phone width, as the Code view's tree is; this page had no way to
          open it there (review, 2026-10-24). */}
      <div className="code-toolbar">
        <Button className="tree-toggle-button" leadingIcon="three-bars" size="small" onClick={() => setListOpen(!listOpen)} aria-label="Show or hide the documents" />
      </div>
      <div className={`code-view${listOpen ? ' sidebar-open' : ''}`}>
        <aside className="code-sidebar">
          <div className="code-sidebar-head">
            <Icon name="book" className="text-muted" />
            <span>Documentation</span>
          </div>
          <div className="code-sidebar-body">
            {tree.state === 'loading' && <Loading />}
            {tree.state === 'ok' && docs.length === 0 && <p className="text-muted pad">No documents in this repository.</p>}
            {folders.map((folder) => (
              <div key={folder || '(root)'}>
                <div className="doc-folder" title={folder}>{folder === '' ? 'Repository root' : folder}</div>
                <ul className="tree-list">
                  {groups.get(folder)!.map(({ node, kind }) => (
                    <li key={node.path}>
                      <button
                        type="button"
                        className={`tree-row${node.path === picked ? ' is-selected' : ''}`}
                        onClick={() => navigate(docsUrl(owner, repo, ref ?? defaultBranch, node.path))}
                        title={node.label}
                      >
                        <span className={`doc-ext doc-ext-${kind}`}>{node.path.slice(node.path.lastIndexOf('.') + 1).toLowerCase()}</span>
                        <span className="tree-name">{node.name}</span>
                      </button>
                    </li>
                  ))}
                </ul>
              </div>
            ))}
          </div>
        </aside>
        <div className="code-main">
          {tree.state === 'error' ? (
            <ErrorPanel error={tree.error} signInUrl={signIn} />
          ) : !picked || !pickedKind || !ref ? (
            <Blankslate icon="book" heading="Pick a document">Text documents are drawn here; a PDF is embedded; the Office formats download.</Blankslate>
          ) : pickedKind === 'text' ? (
            <Pane
              result={pane}
              view={view}
              layout={layout}
              onView={(v) => setParam('view', v, 'rendered')}
              onLayout={(l) => setParam('layout', l, 'unified')}
              onLayer={(layer) => navigate(docsUrl(owner, repo, ref, layer))}
              onOpenPath={(p) => (docKind(p) ? navigate(docsUrl(owner, repo, ref, p)) : navigate(blobUrl(owner, repo, ref, p)))}
              line={lineFromHash(location.hash)}
              onLine={(n) => navigate({ hash: `L${n}`, search: search.toString() }, { replace: true })}
              comparing={false}
              signInUrl={signIn}
            />
          ) : pickedKind === 'pdf' ? (
            <section className="pane pane-doc">
              <header className="pane-head">
                <div className="pane-title" title={picked}>
                  <span className="pane-label">Document</span>
                  <h1>{nameOf(picked)}</h1>
                  {folderOf(picked) && <span className="pane-folder">{folderOf(picked)}</span>}
                </div>
              </header>
              {/* The browser's own PDF viewer, same origin. The response says application/pdf and
                  the site says nosniff, so a file lying about being a PDF fails to draw rather
                  than becoming markup (Documents.cs). */}
              <iframe className="doc-frame" src={rawUrl(owner, repo, ref, picked)} title={nameOf(picked)} />
            </section>
          ) : (
            <section className="pane">
              <header className="pane-head">
                <div className="pane-title" title={picked}>
                  <span className="pane-label">Document</span>
                  <h1>{nameOf(picked)}</h1>
                  {folderOf(picked) && <span className="pane-folder">{folderOf(picked)}</span>}
                </div>
              </header>
              <p className="text-muted">A browser cannot draw this format; download it and open it in its own application.</p>
              <p><LinkButton href={rawUrl(owner, repo, ref, picked)} variant="primary">Download {nameOf(picked)}</LinkButton></p>
            </section>
          )}
        </div>
      </div>
    </RepoLayout>
  );
}
