import { useMemo, useState } from 'react';
import type { Tree, TreeNode } from '../api';

interface Props {
  tree: Tree;
  selected: string | null;
  onSelect: (node: TreeNode) => void;
}

const icon: Record<TreeNode['kind'], string> = { project: 'P', folder: '', object: 'O', file: 'F', library: 'L', task: 'T', device: 'D' };

/** Whether this node or any below it matches the filter. */
function matches(node: TreeNode, filter: string): boolean {
  return filter === '' || node.name.toLowerCase().includes(filter) || node.children.some((c) => matches(c, filter));
}

function contains(node: TreeNode, path: string | null): boolean {
  return path !== null && (node.files.includes(path) || node.path === path || node.children.some((c) => contains(c, path)));
}

/**
 * The repository as RepoExplorerBuilder built it on the server: folders, and each tracked PLC
 * project grafted in as its object tree. The desktop's explorer, in a browser.
 */
export function TreePanel({ tree, selected, onSelect }: Props) {
  const [filter, setFilter] = useState('');
  const needle = filter.trim().toLowerCase();

  return (
    <nav className="tree" aria-label="Repository">
      <input
        className="tree-filter"
        type="search"
        placeholder="Filter objects and files"
        value={filter}
        onChange={(e) => setFilter(e.target.value)}
        aria-label="Filter the tree"
      />
      {!tree.isVersio && <p className="tree-note">This repository is not set up for Versio, so it is shown as plain folders.</p>}
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
          <Node key={n.path + n.kind} node={n} depth={0} filter={needle} selected={selected} onSelect={onSelect} />
        ))}
      </ul>
    </nav>
  );
}

function Node({ node, depth, filter, selected, onSelect }: { node: TreeNode; depth: number; filter: string; selected: string | null; onSelect: (n: TreeNode) => void }) {
  const branch = node.children.length > 0;
  const startOpen = useMemo(() => depth < 1 || contains(node, selected), [depth, node, selected]);
  const [open, setOpen] = useState(startOpen);
  if (!matches(node, filter)) {
    return null;
  }

  const shown = open || filter !== '';
  const isSelected = selected !== null && (node.files.includes(selected) || node.path === selected) && !branch;
  // Anything that stands for files opens one: objects, files, and the library, task and device
  // rows the catalogs graft in (they open their catalog). Folders and projects only expand.
  const opensSomething = node.kind !== 'folder' && node.kind !== 'project' && node.files.length > 0;

  return (
    <li role="treeitem" aria-expanded={branch ? shown : undefined} aria-selected={isSelected}>
      <button
        type="button"
        className={`tree-row kind-${node.kind}${isSelected ? ' is-selected' : ''}`}
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
        <span className="tree-caret" aria-hidden="true">{branch ? (shown ? '▾' : '▸') : ''}</span>
        {icon[node.kind] && <span className={`tree-kind kind-${node.kind}`} aria-hidden="true">{icon[node.kind]}</span>}
        <span className="tree-name">{node.name}</span>
      </button>
      {branch && shown && (
        <ul role="group">
          {node.children.map((c) => (
            <Node key={c.path + c.kind} node={c} depth={depth + 1} filter={filter} selected={selected} onSelect={onSelect} />
          ))}
        </ul>
      )}
    </li>
  );
}
