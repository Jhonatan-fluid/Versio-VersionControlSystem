import { useEffect, useMemo, useRef } from 'react';
import { hasLadder, linkTarget, sanitize } from '../fragment';

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

    const gutter = element.closest<HTMLElement>('.cd-num');
    const row = gutter?.parentElement;
    const number = row?.dataset.line;
    if (number) {
      onLine(Number(number));
    }
  }

  return <div ref={host} className="fragment" onClick={onClick} dangerouslySetInnerHTML={{ __html: clean }} />;
}
