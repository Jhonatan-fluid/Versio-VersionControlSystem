import type { ReactNode } from 'react';
import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { hasLadder, linkTarget, mermaidBlocks, sanitize } from '../fragment';

let ladderRun = 0;

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
}

/**
 * Places a fragment the server rendered with Versio.Core. React draws nothing inside it: the
 * fragment is sanitised (the third wall), placed, and then only read, through the data-* markers
 * the renderers promise.
 */
export function FragmentHost({ html, path, onOpenPath, line, onLine, annotations, onPlaced }: Props) {
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
        let after: Element = target.closest('.cd-drow, .cd-split, .cd-row') ?? target;
        // Several threads on one row stack in the order given.
        while (after.nextElementSibling?.classList.contains('thread-slot')) {
          after = after.nextElementSibling;
        }
        const slot = document.createElement('div');
        slot.className = 'thread-slot';
        after.after(slot);
        made.set(a.key, slot);
      }
    }
    setSlots(made);
    placedReport.current?.([...made.keys()]);
    return () => made.forEach((slot) => slot.remove());
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [clean, wantedKey]);

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
      const target = linkTarget(typeLink.dataset.link ?? '', '');
      if (target?.kind === 'internal') {
        onOpenPath(target.path);
      }
      return;
    }

    const gutter = element.closest<HTMLElement>('.cd-num');
    const row = gutter?.parentElement;
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
