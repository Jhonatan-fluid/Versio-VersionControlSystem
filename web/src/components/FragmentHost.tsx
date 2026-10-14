import { useEffect, useMemo, useRef } from 'react';
import { hasLadder, linkTarget, mermaidBlocks, sanitize } from '../fragment';

let ladderRun = 0;

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
}

/**
 * Places a fragment the server rendered with Versio.Core. React draws nothing inside it: the
 * fragment is sanitised (the third wall), placed, and then only read, through the data-* markers
 * the renderers promise.
 */
export function FragmentHost({ html, path, onOpenPath, line, onLine }: Props) {
  const host = useRef<HTMLDivElement>(null);
  const clean = useMemo(() => sanitize(html), [html]);

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
      frame.hidden = true;
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
        entry.frame.hidden = false;
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

  return <div ref={host} className="fragment" onClick={onClick} dangerouslySetInnerHTML={{ __html: clean }} />;
}
