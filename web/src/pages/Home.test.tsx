import { act } from 'react';
import { createRoot } from 'react-dom/client';
import { MemoryRouter } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { Me } from '../api';
import { Home } from './Home';

// The home page is the product's front door (PLAN.md, "The product shape"): on a scoped server
// it is the repository list, and on a sign-in-only server it is the gate. Rendered over a
// stubbed fetch, as the History page's tests are.

(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

let scoped = true;
let repos: { owner: string; name: string; private: boolean; defaultBranch: string }[] = [];

function me(overrides: Partial<Me>): Me {
  return { signedIn: false, login: null, name: null, avatarUrl: null, signInAvailable: true, anonymousAccess: true, installUrl: null, ...overrides };
}

async function render(who: Me | null) {
  vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({ scoped, repos }), { status: 200, headers: { 'Content-Type': 'application/json' } })));
  const host = document.createElement('div');
  document.body.appendChild(host);
  const root = createRoot(host);
  await act(async () => {
    root.render(
      <MemoryRouter initialEntries={['/']}>
        <Home me={who} />
      </MemoryRouter>,
    );
  });
  for (let i = 0; i < 20 && !host.querySelector('.repo-list, .open-form, .button'); i++) {
    await act(async () => {
      await new Promise((r) => setTimeout(r, 5));
    });
  }
  return { host, root };
}

describe('Home', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    document.body.innerHTML = '';
    scoped = true;
    repos = [];
  });

  it('lists a scoped server’s repositories instead of the open form', async () => {
    repos = [
      { owner: 'acme', name: 'plant', private: false, defaultBranch: 'main' },
      { owner: 'acme', name: 'secret', private: true, defaultBranch: 'main' },
    ];
    const { host, root } = await render(me({ signedIn: true }));

    const rows = Array.from(host.querySelectorAll('.repo-row'));
    expect(rows.map((r) => r.getAttribute('href'))).toEqual(['/acme/plant', '/acme/secret']);
    // The private one says so; the public one says nothing.
    expect(rows[1]?.textContent).toMatch(/private/i);
    expect(rows[0]?.textContent).not.toMatch(/private/i);
    expect(host.querySelector('.open-form')).toBeNull();
    act(() => root.unmount());
  });

  it('says when none of the scope is visible, and offers sign-in', async () => {
    const { host, root } = await render(me({}));

    expect(host.textContent).toContain("None of this server's repositories are visible to you");
    expect(host.querySelector('a[href^="/auth/login"]')).not.toBeNull();
    act(() => root.unmount());
  });

  it('keeps the open form on an unscoped server', async () => {
    scoped = false;
    const { host, root } = await render(me({}));

    expect(host.querySelector('.open-form')).not.toBeNull();
    expect(host.querySelector('.repo-list')).toBeNull();
    act(() => root.unmount());
  });

  it('is only the sign-in gate on a sign-in-only server', async () => {
    const { host, root } = await render(me({ anonymousAccess: false }));

    expect(host.textContent).toContain('signed-in people only');
    expect(host.querySelector('a[href^="/auth/login"]')).not.toBeNull();
    expect(host.querySelector('.open-form')).toBeNull();
    expect(host.querySelector('.repo-list')).toBeNull();
    act(() => root.unmount());
  });
});
