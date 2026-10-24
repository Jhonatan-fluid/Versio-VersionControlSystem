import type { ReactNode } from 'react';
import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { hasLadder, linkTarget, mermaidBlocks, objectLink, sanitize } from '../fragment';

let ladderRun = 0;

/**
 * A row's own text: the code element of the cell the gutter sits in (a Raw row, a split cell,
 * a drawn source line all put theirs in `.cd-code`), or failing that the row less its numbers
 * and its diff marker. What a suggested change starts from, so the person edits the line rather
 * than retypes it.
 */
function rowText(row: HTMLElement, gutter: HTMLElement): string {
  const cell = gutter.closest<HTMLElement>('.cd-cell') ?? row;
  const code = cell.querySelector<HTMLElement>('.cd-code');
  if (code) {
    return code.textContent ?? '';
  }
  const copy = cell.cloneNode(true) as HTMLElement;
  copy.querySelectorAll('.cd-num, .cd-marker, .thread-slot, .thread-slot-row').forEach((e) => e.remove());
  return copy.textContent ?? '';
}

/**
 * Something the page draws BETWEEN the fragment's rows — a review thread under its line — placed
 * after the row that `selector` finds. The selector is built by the page from the renderers'
 * data-* markers, never from text a fragment carries.
 */
export interface Annotation {
  key: string;
  selector: string;
  content: ReactNode;
}

interface Props {
  html: string;
  /** The file shown, so a relative Markdown link resolves against it. */
  path: string;
  /** Opens a file of the same repository and commit. */
  onOpenPath: (path: string) => void;
  /** The line to scroll to and mark, from the URL's #L12. */
  line: number | null;
  /** Called with a line number when its gutter is clicked, to put it in the URL. */
  onLine: (line: number) => void;
  /** Drawn after the rows they name; see {@link Annotation}. */
  annotations?: Annotation[];
  /** Which annotations found their row, so the page can list the rest rather than lose them. */
  onPlaced?: (keys: string[]) => void;
  /**
   * A gutter click on a diff row, with both of its file lines: the new side's (data-line) and the
   * old side's (data-old-line). A review comment needs the side; a link to a line does not.
   */
  onRowPick?: (row: { line: number | null; oldLine: number | null; fileLine: number | null; oldFileLine: number | null; srcPath: string | null; shift: boolean; text: string }) => void;
  /** A folded stretch ("N unchanged lines") was clicked: the page asks for the whole file. */
  onUnfold?: () => void;
  /**
   * A click on a drawn element a review comment can sit on — a member's name, a network's heading —
   * with the anchor ids it stands for (its <c>data-anchors</c>). Only those two targets, so a click
   * on a type link or a ladder control still does what it always did.
   */
  onAnchorPick?: (anchors: string[]) => void;
}

/**
 * Places a fragment the server rendered with Versio.Core. React draws nothing inside it: the
 * fragment is sanitised (the third wall), placed, and then only read, through the data-* markers
 * the renderers promise.
 */
export function FragmentHost({ html, path, onOpenPath, line, onLine, annotations, onPlaced, onRowPick, onAnchorPick, onUnfold }: Props) {
  const host = useRef<HTMLDivElement>(null);
  const clean = useMemo(() => sanitize(html), [html]);
  const [slots, setSlots] = useState<Map<string, HTMLElement>>(() => new Map());
  const placedReport = useRef(onPlaced);
  placedReport.current = onPlaced;
  const wanted = annotations ?? [];
  const wantedKey = wanted.map((a) => `${a.key}\u0000${a.selector}`).join('\u0001');

  // Annotations: an empty slot is inserted after each named row, and React draws the content
  // into it through a portal. React still draws nothing INSIDE the fragment — the slots sit
  // between its rows, the renderers' markup is untouched, and the slots go when the fragment
  // does. A selector that finds nothing is reported, never dropped: a thread whose line is not
  // in this drawing must still be shown somewhere (docs/web/PLAN.md, C3's unplaced list).
  useLayoutEffect(() => {
    const root = host.current;
    const made = new Map<string, HTMLElement>();
    if (root) {
      for (const a of wanted) {
        const target = root.querySelector<HTMLElement>(a.selector);
        if (!target) {
          continue;
        }
        const row: Element = target.closest('.cd-drow, .cd-split, .cd-row, tr.mt-row, section.ld-row') ?? target;
        let after: Element = row;
        // Several threads on one row stack in the order given.
        while (after.nextElementSibling?.classList.contains('thread-slot') || after.nextElementSibling?.classList.contains('thread-slot-row')) {
          after = after.nextElementSibling;
        }
        // A table row's slot is a table row of its own, one cell across: a div between rows of a
        // tbody is invalid markup, and browsers move it out of the table, away from its row.
        let slot: HTMLElement;
        const cell = target.closest<HTMLElement>('.cd-split > .cd-cell');
        // Which column the line is in, read from the LINE's row: once a slot row follows it, `after`
        // is that slot row, whose first cell is never the line's — comparing against it put the
        // second thread on a pair of lines under the wrong column (found by the test, 2026-10-24).
        const onLeft = cell !== null && row.querySelector('.cd-cell') === cell;
        if (cell && row.classList.contains('cd-split')) {
          // Side by side (Step 3b): the slot is a split row of its own with the thread under the
          // column the line is in and the other column empty, as GitHub draws it — a slot across
          // both columns read as if the comment were on both sides' lines.
          const slotRow = document.createElement('div');
          slotRow.className = 'cd-split thread-slot-row';
          const left = document.createElement('div');
          left.className = 'cd-cell thread-slot-cell';
          const rule = document.createElement('span');
          rule.className = 'cd-rule';
          const right = document.createElement('div');
          right.className = 'cd-cell thread-slot-cell';
          slotRow.append(left, rule, right);
          after.after(slotRow);
          slot = document.createElement('div');
          slot.className = 'thread-slot';
          (onLeft ? left : right).appendChild(slot);
        } else if (after instanceof HTMLTableRowElement || after.closest('tbody')) {
          const row = document.createElement('tr');
          row.className = 'thread-slot thread-row';
          const cell = document.createElement('td');
          cell.colSpan = Math.max(1, (after as HTMLTableRowElement).cells?.length ?? 1);
          row.appendChild(cell);
          after.after(row);
          slot = cell;
        } else {
          slot = document.createElement('div');
          slot.className = 'thread-slot';
          after.after(slot);
        }
        made.set(a.key, slot);
      }
    }
    setSlots(made);
    placedReport.current?.([...made.keys()]);
    return () => made.forEach((slot) => (slot.closest('tr.thread-row, .thread-slot-row') ?? slot).remove());
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [clean, wantedKey]);

  // Where a drawn line lives (FINISH.md, C2 "origin"): the gutter's tooltip names the layer file
  // and the file line the renderer recorded, so a line under a table says which file it is of —
  // a code body under a table is another file than the table's. Read from the renderers' own
  // markers, never from text; a block naming no file gets no tooltip.
  useEffect(() => {
    const root = host.current;
    if (!root) {
      return;
    }
    for (const block of root.querySelectorAll<HTMLElement>('[data-src-path]')) {
      const file = block.dataset.srcPath;
      if (!file) {
        continue;
      }
      for (const cell of block.querySelectorAll<HTMLElement>('[data-file-line], [data-old-file-line]')) {
        const line = cell.dataset.fileLine ?? cell.dataset.oldFileLine;
        const gutter = cell.querySelector<HTMLElement>('.cd-num');
        if (gutter && line) {
          gutter.title = `${file}:${line}${cell.dataset.fileLine ? '' : ' (as it was)'}`;
        }
      }
    }
  }, [clean]);

  // The ladder's navigation script works over the .ld-page it finds when it runs, and a page
  // like this places fragments long after load. It is re-run per fragment, from this origin, as
  // the CSP allows; the diagram reads without it, so a failure only loses the buttons.
  useEffect(() => {
    if (!hasLadder(clean)) {
      return;
    }
    const script = document.createElement('script');
    script.src = `/assets/ladder.js?run=${++ladderRun}`;
    document.body.appendChild(script);
    return () => script.remove();
  }, [clean]);

  // Mermaid diagrams: each <pre class="mermaid"> stays as readable source until a sandboxed frame
  // (allow-scripts, never allow-same-origin) has drawn it, then the frame takes its place. The
  // frame gets the diagram's text and gives back a height; nothing else crosses. Messages are
  // accepted only from the frame's own window.
  useEffect(() => {
    const root = host.current;
    if (!root) {
      return;
    }
    const dark = window.matchMedia?.('(prefers-color-scheme: dark)').matches ?? false;
    const frames = new Map<Window, { frame: HTMLIFrameElement; pre: HTMLElement; id: number; source: string }>();
    mermaidBlocks(root).forEach(({ element, source }, id) => {
      const frame = document.createElement('iframe');
      frame.src = '/mermaid-frame.html';
      frame.setAttribute('sandbox', 'allow-scripts');
      frame.title = 'Diagram';
      frame.className = 'mermaid-frame';
      // Born zero-height, NOT hidden. The first version used the hidden attribute, which the
      // class's display:block silently overrode, so every frame showed as an empty box; hiding
      // it properly (display:none) then voided the frame's layout, and the diagram measured
      // itself in a zero-size viewport as the 40px floor. At height 0 the frame still lays out
      // at full width, so the diagram's reported height is real (FINDINGS 2026-10-16).
      frame.style.height = '0';
      element.after(frame);
      if (frame.contentWindow) {
        frames.set(frame.contentWindow, { frame, pre: element, id, source });
      }
    });
    if (frames.size === 0) {
      return;
    }

    function onMessage(event: MessageEvent) {
      const entry = frames.get(event.source as Window);
      if (!entry) {
        return;
      }
      const data = event.data as { ready?: boolean; id?: number; height?: number };
      if (data.ready) {
        (event.source as Window).postMessage({ id: entry.id, source: entry.source, dark }, '*');
      } else if (data.id === entry.id && typeof data.height === 'number') {
        entry.frame.style.height = `${Math.min(Math.max(data.height, 40), 4000)}px`;
        entry.pre.hidden = true;
      }
    }

    window.addEventListener('message', onMessage);
    return () => {
      window.removeEventListener('message', onMessage);
      frames.forEach(({ frame, pre }) => {
        frame.remove();
        pre.hidden = false;
      });
    };
  }, [clean]);

  useEffect(() => {
    const root = host.current;
    if (!root || line === null) {
      return;
    }
    root.querySelectorAll('.is-target').forEach((el) => el.classList.remove('is-target'));
    const target = root.querySelector(`[data-line="${line}"]`);
    if (target) {
      target.classList.add('is-target');
      target.scrollIntoView({ block: 'center' });
    }
  }, [clean, line]);

  function onClick(event: React.MouseEvent<HTMLDivElement>) {
    const element = event.target as HTMLElement;
    const link = element.closest<HTMLElement>('[data-href]');
    if (link) {
      event.preventDefault();
      const target = linkTarget(link.dataset.href ?? '', path);
      if (target?.kind === 'internal') {
        onOpenPath(target.path);
      } else if (target?.kind === 'external') {
        window.open(target.url, '_blank', 'noopener,noreferrer');
      }
      return;
    }

    // A member's type linked by the server (MemberTableHtml's data-link): the repository path
    // of the object it names. Opened in the viewer; anything that is not a plain relative path
    // is ignored, the same rule a Markdown link gets.
    const typeLink = element.closest<HTMLElement>('[data-link]');
    if (typeLink) {
      event.preventDefault();
      const target = objectLink(typeLink.dataset.link ?? '');
      if (target !== null) {
        onOpenPath(target);
      }
      return;
    }

    if (onAnchorPick) {
      const target = element.closest<HTMLElement>('td.mt-name, td.mt-mark, .ld-head');
      const anchored = target?.closest<HTMLElement>('[data-anchors]');
      const ids = anchored?.dataset.anchors?.split(' ').filter((id) => id.length > 0) ?? [];
      if (ids.length > 0) {
        onAnchorPick(ids);
        return;
      }
    }

    if (onUnfold && element.closest<HTMLElement>('.cd-fold')) {
      onUnfold();
      return;
    }

    const gutter = element.closest<HTMLElement>('.cd-num');
    const row = gutter?.parentElement;
    if (row && onRowPick) {
      // In a side-by-side diff the gutter sits in a cell, and the cell carries its own side's line.
      // A drawn source block names its file (data-src-path): its rows' file lines are lines of THAT
      // file, which under a member table is not the one on screen.
      const n = (v: string | undefined) => (v && /^\d+$/.test(v) ? Number(v) : null);
      onRowPick({
        line: n(row.dataset.line),
        oldLine: n(row.dataset.oldLine),
        fileLine: n(row.dataset.fileLine),
        oldFileLine: n(row.dataset.oldFileLine),
        srcPath: row.closest<HTMLElement>('[data-src-path]')?.dataset.srcPath || null,
        shift: event.shiftKey,
        // The line's own text, for a suggested change to start from (Step 5): the row without its
        // gutter, and without the diff's leading sign on a Raw row.
        text: rowText(row, gutter),
      });
      return;
    }
    const number = row?.dataset.line;
    if (number) {
      onLine(Number(number));
    }
  }

  return (
    <>
      <div ref={host} className="fragment" onClick={onClick} dangerouslySetInnerHTML={{ __html: clean }} />
      {wanted.map((a) => {
        const slot = slots.get(a.key);
        return slot ? createPortal(a.content, slot, a.key) : null;
      })}
    </>
  );
}
