import { useEffect, useState } from 'react';

export type Async<T> = { state: 'loading' } | { state: 'ok'; value: T } | { state: 'error'; error: Error };

/**
 * Runs <load> whenever <deps> change and keeps only the latest answer: a person clicking down
 * the tree faster than the server answers must see the object they clicked last, never an
 * earlier, slower one landing on top of it (the same race the desktop's DiffView guards against).
 */
export function useAsync<T>(load: (signal: AbortSignal) => Promise<T>, deps: unknown[]): Async<T> {
  const [result, setResult] = useState<Async<T>>({ state: 'loading' });
  useEffect(() => {
    const controller = new AbortController();
    setResult({ state: 'loading' });
    load(controller.signal).then(
      (value) => {
        if (!controller.signal.aborted) {
          setResult({ state: 'ok', value });
        }
      },
      (error: Error) => {
        if (!controller.signal.aborted) {
          setResult({ state: 'error', error });
        }
      },
    );
    return () => controller.abort();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, deps);
  return result;
}
