import { useEffect } from 'react';

/**
 * The browser tab's title, per page, the way GitHub writes it: the thing first, the place
 * after ("Fix interlock by sam · Pull Request #12 · acme/plant"). It used to be "Versio" on
 * every page, so twenty open tabs read as twenty of the same thing (docs/web/FINISH.md, B1).
 */
export function usePageTitle(...parts: (string | null | undefined)[]): void {
  const title = [...parts.filter((p): p is string => !!p), 'Versio'].join(' · ');
  useEffect(() => {
    const before = document.title;
    document.title = title;
    return () => {
      document.title = before;
    };
  }, [title]);
}
