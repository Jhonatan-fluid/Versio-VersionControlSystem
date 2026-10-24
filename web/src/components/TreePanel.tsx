import { useEffect, useMemo, useRef, useState } from 'react';
import type { Tree, TreeNode } from '../api';
import { Icon, type IconName } from './ui';

interface Props {
  tree: Tree;
  selected: string | null;
  onSelect: (node: TreeNode) => void;
  /** The filter box's placeholder: "Go to object or file" in the code view, "Filter changed objects" on a comparison. */
  placeholder?: string;
  /** A comparison: the marks are drawn, and nothing says the repository is plain folders. */
  comparing?: boolean;
}

/** The glyph per kind: a folder, a file, a project, an object; the catalog rows by what they are. */
const icon: Record<TreeNode['kind'], { name: IconName; className: string } | null> = {
  folder: { name: 'file-directory-fill', className: 'tree-icon tree-icon-folder' },
  file: { name: 'file', className: 'tree-icon' },
  project: null,
  object: null,
  library: null,
  task: null,
  device: null,
};

const letter: Partial<Record<TreeNode['kind'], string>> = { project: 'P', object: 'O', library: 'L', task: 'T', device: 'D' };

/** The desktop's one-letter glyph per state (TreeRow.Glyph), blank when unchanged. */
export const glyph: Record<string, string> = { added: 'A', modified: 'M', deleted: 'D', renamed: 'R', copied: 'C', untracked: '?', conflicted: '!' };

function matches(node: TreeNode, needle: string): boolean {
  if (!needle) {
    return true;
  }
  return node.name.toLowerCase().includes(needle) || node.children.some((c) => matches(c, needle));
}

function contains(node: TreeNode, path: string | null): boolean {
  if (!path) {
    return false;
  }
  return node.path === path || node.files.includes(path) || node.children.some((c) => contains(c, path));
}

/**
 * The object tree with a filter: the Project panel's content, in the code view's sidebar and,
 * marked, on a comparison (FINISH.md Step 8). Each row is the desktop's (TreeRow): the vendor's
 * pill on a project, the type or detail beside the name, the one-letter state glyph, a struck
 * name for a deleted object, and on a folder the count of changes below it in the strongest
 * state's colour.
 */
export function TreePanel({ tree, selected, onSelect, placeholder = 'Go to object or file', comparing = false }: Props) {
  const [filter, setFilter] = useState('');
  const needle = filter.trim().toLowerCase();
  const box = useRef<HTMLInputElement>(null);
  return (
    <nav className="tree" aria-label="Objects and files">
      <div className="tree-filter-row">
        <input
          ref={box}
          className="tree-filter form-control"
          type="search"
          placeholder={placeholder}
          value={filter}
          onChange={(e) => setFilter(e.target.value)}
          aria-label="Filter objects and files"
        />
      </div>
      {!comparing && !tree.isVersio && <p className="tree-note">This repository has no PLC project Versio can draw, so it is shown as plain folders.</p>}
      {tree.truncated && <p className="tree-note">GitHub listed only part of this tree; some files are missing.</p>}
      {tree.problems.length > 0 && (
        <details className="tree-note">
          <summary>{tree.problems.length === 1 ? '1 thing could not be read' : `${tree.problems.length} things could not be read`}</summary>
          <ul>
            {tree.problems.map((p) => (
              <li key={p}>{p}</li>
            ))}
          </ul>
        </details>
      )}
      <ul className="tree-list" role="tree">
        {tree.nodes.map((n) => (
          <Node key={n.path + n.kind} node={n} depth={0} filter={needle} selected={selected} onSelect={onSelect} openAll={comparing} />
        ))}
      </ul>
    </nav>
  );
}

function Node({ node, depth, filter, selected, onSelect, openAll }: { node: TreeNode; depth: number; filter: string; selected: string | null; onSelect: (n: TreeNode) => void; openAll: boolean }) {
  const branch = node.children.length > 0;
  const holdsSelection = useMemo(() => contains(node, selected), [node, selected]);
  // A comparison lists only what changed, which is short: every folder opens, as the Project
  // panel opens the way to each change. The code view's whole tree opens one level, as before.
  const [open, setOpen] = useState(depth < 1 || holdsSelection || openAll);
  const row = useRef<HTMLButtonElement>(null);
  const isSelected = selected !== null && (node.files.includes(selected) || node.path === selected) && !branch;

  // Reveal the selection whenever it changes, not only when the tree first drew: a type link
  // clicked in a table selects an object in a folder that may be closed, and the desktop's
  // OpenObjectAsync expands and scrolls to it. Before this, the first render's state stuck
  // (useState's initial value is read once), so the target stayed hidden — found 2026-10-23
  // (docs/web/FINISH.md, C2). Opening only, never closing: a person's own fold is theirs.
  useEffect(() => {
    if (holdsSelection) {
      setOpen(true);
    }
  }, [holdsSelection, selected]);
  useEffect(() => {
    if (isSelected && row.current && typeof row.current.scrollIntoView === 'function') {
      row.current.scrollIntoView({ block: 'nearest' });
    }
  }, [isSelected, selected]);

  if (!matches(node, filter)) {
    return null;
  }

  const shown = open || filter !== '';
  // Anything that stands for files opens one: objects, files, and the library, task and device
  // rows the catalogs graft in (they open their catalog). Folders and projects only expand.
  const opensSomething = node.kind !== 'folder' && node.kind !== 'project' && node.files.length > 0;
  const glyphOf = icon[node.kind];
  const status = node.status ?? 'unmodified';
  const mark = glyph[status] ?? '';
  // The desktop's Annotation: a project's, library's, task's or device's detail; an object's type.
  const annotation = node.kind === 'project' || node.kind === 'library' || node.kind === 'task' || node.kind === 'device' || node.kind === 'folder'
    ? node.detail || ''
    : node.typeName ?? '';
  const tone = status !== 'unmodified' ? status : node.rollUp && node.rollUp !== 'unmodified' ? node.rollUp : '';
  const badge = branch && (node.changedDescendants ?? 0) > 0 ? String(node.changedDescendants) : '';

  return (
    <li role="treeitem" aria-expanded={branch ? shown : undefined} aria-selected={isSelected}>
      <button
        ref={row}
        type="button"
        className={`tree-row kind-${node.kind}${isSelected ? ' is-selected' : ''}${tone ? ` tone-${tone}` : ''}${status === 'deleted' ? ' is-deleted' : ''}`}
        style={{ paddingLeft: `${8 + depth * 14}px` }}
        onClick={() => {
          if (branch) {
            setOpen(!open);
          }
          if (opensSomething) {
            onSelect(node);
          }
        }}
        title={node.label}
      >
        <span className="tree-caret" aria-hidden="true">{branch ? <Icon name={shown ? 'chevron-down' : 'chevron-right'} size={12} /> : ''}</span>
        {glyphOf ? <Icon name={glyphOf.name} className={glyphOf.className} /> : letter[node.kind] && <span className={`tree-kind kind-${node.kind}`} aria-hidden="true">{letter[node.kind]}</span>}
        <span className="tree-name">{node.name}</span>
        {node.toolchain && <span className="tree-pill" title="The toolchain that opens this project">{node.toolchain}</span>}
        {annotation && <span className="tree-annotation">{annotation}</span>}
        {badge && <span className={`tree-badge tone-${tone}`} title={`${badge} changed below`}>{badge}</span>}
        {mark && <span className={`change change-${status}`} aria-label={status}>{mark}</span>}
      </button>
      {branch && shown && (
        <ul role="group">
          {node.children.map((c) => (
            <Node key={c.path + c.kind} node={c} depth={depth + 1} filter={filter} selected={selected} onSelect={onSelect} openAll={openAll} />
          ))}
        </ul>
      )}
    </li>
  );
}
