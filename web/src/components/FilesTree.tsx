import { useMemo, useState } from 'react';
import type { ReviewFile } from '../api';
import { Icon } from './ui';

/**
 * GitHub's file tree beside the diffs (docs/web/FINISH.md, B6), built from the review's inventory:
 * the path's folders, then the object, with the files an object owns under it — a method's
 * `FB_Template.fb.st^/MoveHome.meth.st` sits under `FB_Template.fb.st`, which is how a CODESYS
 * File-Based Storage project names a block's members and how the desktop's tree nests them. A
 * project's export folder is a folder like any other here; the project card's grouping is
 * the project tree's (Step 8), not the diff's.
 */

export interface FileLeaf {
  kind: 'file';
  name: string;
  file: ReviewFile;
}

export interface FileFolder {
  kind: 'folder';
  name: string;
  path: string;
  children: FileNode[];
  /** How many changed files sit below. */
  count: number;
}

export type FileNode = FileLeaf | FileFolder;

/** The segments of a path, with an object's own-file segment (`X.fb.st^`) kept as a folder of its members. */
export function segmentsOf(path: string): string[] {
  return path.split('/').filter((s) => s.length > 0);
}

export function buildFileTree(files: ReviewFile[]): FileNode[] {
  const root: FileFolder = { kind: 'folder', name: '', path: '', children: [], count: 0 };
  for (const f of files) {
    const segments = segmentsOf(f.path);
    let at = root;
    for (let i = 0; i < segments.length - 1; i++) {
      const name = segments[i]!.replace(/\^$/, '');
      const path = segments.slice(0, i + 1).join('/');
      let next = at.children.find((c): c is FileFolder => c.kind === 'folder' && c.path === path);
      if (!next) {
        next = { kind: 'folder', name, path, children: [], count: 0 };
        at.children.push(next);
      }
      next.count++;
      at = next;
    }
    at.children.push({ kind: 'file', name: f.object ?? segments[segments.length - 1] ?? f.path, file: f });
  }
  // Folders first, as GitHub orders them; files in the server's order (ordinal by path).
  const sort = (nodes: FileNode[]) => {
    nodes.sort((a, b) => (a.kind === b.kind ? 0 : a.kind === 'folder' ? -1 : 1));
    for (const n of nodes) {
      if (n.kind === 'folder') {
        sort(n.children);
      }
    }
  };
  sort(root.children);
  return collapseChains(root.children);
}

/** A folder holding only one folder is drawn as one row, "a/b/c", as GitHub's tree does. */
function collapseChains(nodes: FileNode[]): FileNode[] {
  return nodes.map((n) => {
    if (n.kind !== 'folder') {
      return n;
    }
    let folder = n;
    while (folder.children.length === 1 && folder.children[0]!.kind === 'folder') {
      const only = folder.children[0] as FileFolder;
      folder = { ...only, name: `${folder.name}/${only.name}` };
    }
    return { ...folder, children: collapseChains(folder.children) };
  });
}

const mark: Record<ReviewFile['change'], string> = { added: 'A', modified: 'M', deleted: 'D', renamed: 'R' };

interface Props {
  files: ReviewFile[];
  /** The file the page is on: its row is lit. */
  active: string | null;
  onPick: (path: string) => void;
  /** Review threads per path, for the count beside a file. */
  threadCount: (path: string) => number;
  /** The viewer's viewed marks, and the writer's checkbox; null when the viewer cannot mark. */
  viewed: Set<string>;
  onViewed: ((path: string, on: boolean) => void) | null;
  truncated?: boolean;
  total?: number;
}

function matches(node: FileNode, needle: string): boolean {
  if (!needle) {
    return true;
  }
  if (node.kind === 'file') {
    return node.name.toLowerCase().includes(needle) || node.file.path.toLowerCase().includes(needle);
  }
  return node.children.some((c) => matches(c, needle));
}

export function FilesTree({ files, active, onPick, threadCount, viewed, onViewed, truncated, total }: Props) {
  const [filter, setFilter] = useState('');
  const needle = filter.trim().toLowerCase();
  const tree = useMemo(() => buildFileTree(files), [files]);
  const viewedCount = files.filter((f) => viewed.has(f.path)).length;
  return (
    <nav className="files-tree-nav" aria-label="Changed files">
      <div className="files-tree-head">
        <input className="form-control tree-filter" type="search" placeholder="Filter changed files" value={filter} onChange={(e) => setFilter(e.target.value)} aria-label="Filter changed files" />
        {onViewed && files.length > 0 && (
          <div className="files-progress" title={`${viewedCount} of ${files.length} files viewed`}>
            <span className="text-muted text-small">{viewedCount} / {files.length} files viewed</span>
            <span className="files-progress-bar" aria-hidden="true"><span style={{ width: `${(100 * viewedCount) / files.length}%` }} /></span>
          </div>
        )}
      </div>
      {truncated && total !== undefined && <p className="text-muted pad text-small">The first {files.length} of {total} files are listed.</p>}
      {files.length === 0 && <p className="text-muted pad">This pull request changes no files.</p>}
      <ul className="changes tree-list" role="tree">
        {tree.map((n) => <Node key={n.kind === 'folder' ? `d:${n.path}` : `f:${n.file.path}`} node={n} depth={0} needle={needle} active={active} onPick={onPick} threadCount={threadCount} viewed={viewed} onViewed={onViewed} />)}
      </ul>
    </nav>
  );
}

function Node({ node, depth, needle, active, onPick, threadCount, viewed, onViewed }: { node: FileNode; depth: number; needle: string } & Omit<Props, 'files' | 'truncated' | 'total'>) {
  const [open, setOpen] = useState(true);
  if (!matches(node, needle)) {
    return null;
  }
  if (node.kind === 'folder') {
    const shown = open || needle !== '';
    return (
      <li role="treeitem" aria-expanded={shown}>
        <button type="button" className="tree-row tree-folder" style={{ paddingLeft: `${8 + depth * 14}px` }} onClick={() => setOpen(!open)} title={node.path}>
          <span className="tree-caret" aria-hidden="true"><Icon name={shown ? 'chevron-down' : 'chevron-right'} size={12} /></span>
          <Icon name="file-directory-fill" className="tree-icon tree-icon-folder" />
          <span className="tree-name">{node.name}</span>
          <span className="text-muted text-small tree-count">{node.count}</span>
        </button>
        {shown && (
          <ul role="group">
            {node.children.map((c) => <Node key={c.kind === 'folder' ? `d:${c.path}` : `f:${c.file.path}`} node={c} depth={depth + 1} needle={needle} active={active} onPick={onPick} threadCount={threadCount} viewed={viewed} onViewed={onViewed} />)}
          </ul>
        )}
      </li>
    );
  }
  const f = node.file;
  const count = threadCount(f.path);
  const isViewed = viewed.has(f.path);
  return (
    <li role="treeitem" aria-selected={f.path === active} className={isViewed ? 'is-viewed' : undefined}>
      <button type="button" className={`tree-row${f.path === active ? ' is-selected' : ''}`} style={{ paddingLeft: `${8 + depth * 14}px` }} onClick={() => onPick(f.path)} title={f.label}>
        <span className={`change change-${f.change}`} title={f.change}>{mark[f.change]}</span>
        <span className="tree-name">{node.name}</span>
        {f.additions !== undefined && f.additions !== null && f.deletions !== undefined && f.deletions !== null && (
          <span className="diffstat tree-stat"><span className="diffstat-add">+{f.additions}</span> <span className="diffstat-del">−{f.deletions}</span></span>
        )}
        {!f.inline && <span className="text-muted review-flag" title="Comments on this file go on the whole file, not a line">file only</span>}
        {count > 0 && <span className="review-count" title={`${count} review thread${count === 1 ? '' : 's'}`}><Icon name="comment" size={12} /> {count}</span>}
      </button>
      {onViewed && (
        <input type="checkbox" className="review-viewed" checked={isViewed} onChange={(e) => onViewed(f.path, e.target.checked)} aria-label={`Viewed ${f.label}`} title="Viewed" />
      )}
    </li>
  );
}
