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

/**
 * The glyph per kind, the desktop's kind icons (TreeRow, FINISH.md Step 9): a folder, a file, a
 * project, an object, and the catalog rows by what they are.
 */
const icon: Record<TreeNode['kind'], { name: IconName; className: string; title: string }> = {
  folder: { name: 'file-directory-fill', className: 'tree-icon tree-icon-folder', title: 'Folder' },
  file: { name: 'file', className: 'tree-icon', title: 'File' },
  project: { name: 'repo', className: 'tree-icon tree-icon-project', title: 'Project' },
  object: { name: 'code', className: 'tree-icon', title: 'Object' },
  library: { name: 'book', className: 'tree-icon', title: 'Library' },
  task: { name: 'clock', className: 'tree-icon', title: 'Task' },
  device: { name: 'device-desktop', className: 'tree-icon', title: 'Device' },
};

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
  // A comparison lists only what changed, which is short: every folder opens. The code view's
  // whole tree opens its roots and its project rows, nothing else: the desktop's Project panel
  // opens the project row alone since 2026-10-30 (it used to open the way to each change, which
  // on a real project unfolded everything), and the dot on every row with a change below it is
  // what says where to open.
  const [open, setOpen] = useState(depth < 1 || node.kind === 'project' || holdsSelection || openAll);
  const row = useRef<HTMLButtonElement>(null);
  // A branch that opens something (a block with methods under it) is selected like a leaf; the
  // first version lit no branch, so an open block with members was never shown as the current
  // one and never scrolled to (review, 2026-10-24).
  const isSelected = selected !== null && (node.files.includes(selected) || node.path === selected) && (!branch || node.files.length > 0);

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
  // The name takes a colour only for the row's OWN change. It used to take the roll-up too, so
  // every folder on the way down to one edited block was orange, and the owner asked for a
  // tree where a parent carries one small mark instead (2026-10-30): a dot on every row with
  // a change somewhere below it, open or shut, in the strongest state's colour below. It was
  // drawn on a collapsed row only for a few hours; once the tree stopped opening the way to
  // each change, the owner wanted it at every level, so the dot is what says where to open.
  const tone = status !== 'unmodified' ? status : '';
  const below = branch && (node.changedDescendants ?? 0) > 0;
  const belowTone = node.rollUp && node.rollUp !== 'unmodified' ? node.rollUp : 'modified';

  return (
    <li role="treeitem" aria-expanded={branch ? shown : undefined} aria-selected={isSelected}>
      <button
        ref={row}
        type="button"
        className={`tree-row kind-${node.kind}${isSelected ? ' is-selected' : ''}${tone ? ` tone-${tone}` : ''}${status === 'deleted' ? ' is-deleted' : ''}`}
        style={{ paddingLeft: `${8 + depth * 14}px` }}
        onClick={() => {
          // A folder toggles. A block with members opens its members on the click that opens it,
          // and toggles them only once it is the current one — re-clicking the open block folded
          // its methods away while opening it (review, 2026-10-24).
          if (branch) {
            setOpen(opensSomething && !isSelected ? true : !open);
          }
          if (opensSomething) {
            onSelect(node);
          }
        }}
        title={node.label}
      >
        <span className="tree-caret" aria-hidden="true">{branch ? <Icon name={shown ? 'chevron-down' : 'chevron-right'} size={12} /> : ''}</span>
        <Icon name={glyphOf.name} className={glyphOf.className} label={glyphOf.title} />
        <span className="tree-name">{node.name}</span>
        {below && <span className={`tree-dot tone-${belowTone}`} role="img" aria-label="Changes below" title="Changes below" />}
        {node.toolchain && <span className="tree-pill" title="The toolchain that opens this project">{node.toolchain}</span>}
        {annotation && <span className="tree-annotation">{annotation}</span>}
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
