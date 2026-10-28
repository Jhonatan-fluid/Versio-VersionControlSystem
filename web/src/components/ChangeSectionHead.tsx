import type { ReactNode } from 'react';
import type { Layout, PaneView } from '../api';
import { Icon } from './ui';

/**
 * The desktop's one-letter glyph per change, blank when unchanged. GitHub's words (removed) and
 * the tree's (deleted) both land here, so a header can take either side's status as it is.
 */
export const changeMark: Record<string, string> = { added: 'A', modified: 'M', deleted: 'D', removed: 'D', renamed: 'R', copied: 'C', changed: 'M' };

/** The tree's word for a change, for the colour class: GitHub says "removed" where the tree says "deleted". */
export function changeTone(status: string): string {
  return status === 'removed' ? 'deleted' : status === 'changed' || status === 'copied' ? 'modified' : status;
}

interface Props {
  open: boolean;
  onToggle: () => void;
  /** The change: the tree's word or GitHub's. */
  status: string;
  /** The object's name, or the file's. */
  name: string;
  /** The full path for display, spelled by the server; the tooltip, and where the folder comes from. */
  label: string;
  /** The object's type as the tree's chip says it (FINISH.md Step 8); nothing for a plain file. */
  typeName?: string | null;
  /** The vendor's pill, as the tree draws it on the project; nothing for a plain file. */
  toolchain?: string | null;
  /** The old path's label when the commit renamed the file. */
  renamedFrom?: string | null;
  additions?: number | null;
  deletions?: number | null;
  view: PaneView;
  onView: (v: PaneView) => void;
  layout: Layout;
  onLayout: (l: Layout) => void;
  full: boolean;
  onFull: (on: boolean) => void;
  /** Tools a page adds before the view switches: the review's Viewed box. */
  extraTools?: ReactNode;
}

/**
 * The sticky header of one stacked change section — GitHub's files-changed header with the
 * desktop's row inside it: the chevron, the change glyph, the object's name with its type chip
 * and its project's vendor pill, where it lives, "renamed from", +N −M, then the view switches.
 * Shared by the commit page and the review's Files changed, which used to carry two copies of
 * it that had already drifted (one folded with a link-button and said "Fold", the other with a
 * button and said "Collapse"); each page keeps its own body, because what a fold does to the
 * drawing underneath differs between them.
 */
export function ChangeSectionHead({ open, onToggle, status, name, label, typeName, toolchain, renamedFrom, additions, deletions, view, onView, layout, onLayout, full, onFull, extraTools }: Props) {
  // The folder from the SPELLED label (security rule 9), never the raw path, which may hold the
  // very characters the label spells out; a caret in a spelled label is a spelled character's.
  const segments = label.split('/');
  const folder = segments.slice(0, -1).join(' › ').replace(/\^/g, '');
  const tone = changeTone(status);
  return (
    <div className="pane-head">
      <div className="pane-title" title={label}>
        <button type="button" className="btn btn-invisible btn-small file-chevron" aria-expanded={open} aria-label={open ? 'Collapse this file' : 'Expand this file'} onClick={onToggle}>
          <Icon name={open ? 'chevron-down' : 'chevron-right'} />
        </button>
        <span className={`change change-${tone}`} title={tone}>{changeMark[status] ?? ''}</span>
        <h2 className="review-file">{name}</h2>
        {typeName && <span className="tree-annotation">{typeName}</span>}
        {toolchain && <span className="tree-pill" title="The toolchain that opens this project">{toolchain}</span>}
        {folder && <span className="pane-folder" title={label}>{folder}</span>}
        {renamedFrom && <span className="text-muted text-small">renamed from {renamedFrom}</span>}
        {additions !== undefined && additions !== null && deletions !== undefined && deletions !== null && (
          <span className="diffstat"><span className="diffstat-add">+{additions}</span> <span className="diffstat-del">−{deletions}</span></span>
        )}
      </div>
      <div className="pane-tools">
        {extraTools}
        <div className="segmented" role="tablist" aria-label="View">
          <button type="button" role="tab" aria-selected={view === 'rendered'} onClick={() => onView('rendered')}>Rendered</button>
          <button type="button" role="tab" aria-selected={view === 'raw'} onClick={() => onView('raw')}>Raw</button>
        </div>
        <label className="full-file-label" title="Every line, on both tabs; clear, the drawing is folded to the lines around each change">
          <input type="checkbox" checked={full} onChange={(e) => onFull(e.target.checked)} /> Show full file
        </label>
        <div className="segmented" role="group" aria-label="Layout">
          <button type="button" aria-pressed={layout === 'unified'} onClick={() => onLayout('unified')}>Unified</button>
          <button type="button" aria-pressed={layout === 'split'} onClick={() => onLayout('split')}>Side by side</button>
        </div>
      </div>
    </div>
  );
}
