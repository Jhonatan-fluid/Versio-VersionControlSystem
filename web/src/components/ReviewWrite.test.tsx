import { act } from 'react';
import { createRoot } from 'react-dom/client';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { ReviewSnapshot } from '../api';
import { ActionButton } from './ReviewWrite';

// The one-click write's key rules (review, 2026-10-24): a held key belongs to one path and one
// payload. When the page flips the payload under it (Resolve became Unresolve after a poll), the
// key is let go rather than sent again with the opposite payload.

(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

const snap = { token: 'T1', writable: true, number: 12 } as unknown as ReviewSnapshot;

describe('ActionButton', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    document.body.innerHTML = '';
  });

  it('keeps its key across a retry of the same write and drops it when the payload flips', async () => {
    const bodies: Record<string, unknown>[] = [];
    vi.stubGlobal('fetch', vi.fn(async (_url: string, init?: RequestInit) => {
      bodies.push(JSON.parse(init?.body as string) as Record<string, unknown>);
      return new Response(JSON.stringify({ outcome: 'unknown' }), { status: 202, headers: { 'Content-Type': 'application/json' } });
    }));
    const host = document.createElement('div');
    document.body.appendChild(host);
    const root = createRoot(host);
    const draw = (resolved: boolean) => act(() => root.render(
      <ActionButton owner="acme" repo="plant" snap={snap} path="/threads/T1/resolve" payload={{ resolved }} label={resolved ? 'Resolve' : 'Unresolve'} onDone={() => {}} />,
    ));
    const click = async () => {
      await act(async () => {
        host.querySelector('button')!.click();
        await new Promise((r) => setTimeout(r, 10));
      });
    };

    draw(true);
    await click();
    await click();
    expect(bodies).toHaveLength(2);
    expect(bodies[1]!.opKey).toBe(bodies[0]!.opKey);
    expect(bodies[1]).toMatchObject({ resolved: true });

    // The poll says it is resolved now: the button offers the opposite write, under a new key.
    draw(false);
    await click();
    expect(bodies).toHaveLength(3);
    expect(bodies[2]).toMatchObject({ resolved: false });
    expect(bodies[2]!.opKey).not.toBe(bodies[0]!.opKey);
    act(() => root.unmount());
  });
});
