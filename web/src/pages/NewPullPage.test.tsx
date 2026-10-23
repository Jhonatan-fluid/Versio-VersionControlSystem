import { act } from 'react';
import { createRoot } from 'react-dom/client';
import { MemoryRouter, Route, Routes, useLocation, useNavigate } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { NewPullPage } from './NewPullPage';
import { PullsPage } from './PullsPage';

// Opening a pull request (docs/web/PLAN.md, *Pull request review*, slice 5): in the person's own
// name, between two branches of this repository, from the repository's template, and never twice
// for one click — the form's words are the operation's until its outcome is known.

(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

interface Options {
  writable?: boolean;
  template?: string | null;
  /** What the create answers; done with pull request 13 by default. */
  create?: { status: number; body: unknown } | { status: number; body: unknown }[];
}

let calls: { url: string; method: string; headers: Record<string, string>; body: Record<string, unknown> | null }[] = [];

function answer(url: string, o: Options): unknown {
  if (url.includes('/pulls/template')) {
    return { markdown: o.template ?? null, path: o.template ? '.github/pull_request_template.md' : null };
  }
  if (url.endsWith('/pulls')) {
    return { truncated: false, writable: o.writable ?? true, pulls: [] };
  }
  if (url.endsWith('/branches')) {
    return { branches: [{ name: 'main', commitSha: 'a' }, { name: 'feature', commitSha: 'b' }], truncated: false };
  }
  return { owner: 'acme', name: 'plant', private: false, defaultBranch: 'main' };
}

function Where() {
  const location = useLocation();
  return <output data-testid="where">{location.pathname}</output>;
}


/** Lets a test move the router as a link click would, without leaving the page under test. */
let jump: (to: string) => void = () => {};
function Jumper() {
  const navigate = useNavigate();
  jump = navigate;
  return null;
}

async function render(path: string, o: Options = {}) {
  calls = [];
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    const method = init?.method ?? 'GET';
    const body = typeof init?.body === 'string' ? (JSON.parse(init.body) as Record<string, unknown>) : null;
    calls.push({ url, method, headers: (init?.headers ?? {}) as Record<string, string>, body });
    if (method !== 'GET') {
      const reply = Array.isArray(o.create) ? (o.create.length > 1 ? o.create.shift()! : o.create[0]!) : o.create ?? { status: 200, body: { outcome: 'done', id: '13' } };
      return new Response(JSON.stringify(reply.body), { status: reply.status, headers: { 'Content-Type': 'application/json' } });
    }
    return new Response(JSON.stringify(answer(url, o)), { status: 200, headers: { 'Content-Type': 'application/json' } });
  }));
  const host = document.createElement('div');
  document.body.appendChild(host);
  const root = createRoot(host);
  await act(async () => {
    root.render(
      <MemoryRouter initialEntries={[path]}>
        <Jumper />
        <Routes>
          <Route path="/:owner/:repo/pulls/new" element={<NewPullPage me={null} />} />
          <Route path="/:owner/:repo/pulls" element={<PullsPage me={null} />} />
          <Route path="*" element={<Where />} />
        </Routes>
      </MemoryRouter>,
    );
  });
  await settle();
  return { host, root };
}

async function settle() {
  for (let i = 0; i < 30; i++) {
    await act(async () => {
      await new Promise((r) => setTimeout(r, 5));
    });
  }
}

async function fill(element: HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement, value: string) {
  await act(async () => {
    const proto = element instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype : element instanceof HTMLSelectElement ? HTMLSelectElement.prototype : HTMLInputElement.prototype;
    Object.getOwnPropertyDescriptor(proto, 'value')!.set!.call(element, value);
    element.dispatchEvent(new Event(element instanceof HTMLSelectElement ? 'change' : 'input', { bubbles: true }));
  });
}

async function click(element: Element) {
  await act(async () => {
    (element as HTMLElement).click();
  });
  await settle();
}

const button = (host: HTMLElement, label: string) => Array.from(host.querySelectorAll('button')).find((b) => b.textContent === label);
const creates = () => calls.filter((c) => c.method === 'POST');

describe('NewPullPage', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    document.body.innerHTML = '';
    sessionStorage.clear();
  });

  it('offers no form where nothing may be written', async () => {
    const { host, root } = await render('/acme/plant/pulls/new', { writable: false });

    expect(host.querySelector('.new-pull-title')).toBeNull();
    expect(host.textContent).toContain('cannot be opened here');
    act(() => root.unmount());
  });

  it("opens one from the template, with the forgery header and no snapshot, then goes to its review", async () => {
    const { host, root } = await render('/acme/plant/pulls/new?head=feature', { template: '## What changed\n' });
    expect(host.querySelector<HTMLTextAreaElement>('.new-pull-body')!.value).toBe('## What changed\n');

    await fill(host.querySelector<HTMLInputElement>('.new-pull-title')!, 'Add the valve');
    await click(host.querySelector('.new-pull-draft input')!);
    await click(button(host, 'Create draft pull request')!);

    const posted = creates()[0]!;
    expect(posted.url).toBe('/api/repos/acme/plant/pulls');
    expect(posted.headers['X-Versio-Request']).toBe('1');
    expect(posted.headers['X-Versio-Snapshot']).toBeUndefined();
    expect(posted.body).toMatchObject({ base: 'main', head: 'feature', title: 'Add the valve', body: '## What changed\n', draft: true });
    expect(String(posted.body?.opKey)).toMatch(/^\d{13}-[A-Za-z0-9]{16,}$/);
    expect(host.querySelector('[data-testid="where"]')?.textContent).toBe('/acme/plant/pull/13');
    expect(sessionStorage.length).toBe(0);
    act(() => root.unmount());
  });

  it('keeps the form locked after an unknown outcome, and the retry is the same operation', async () => {
    const unknown = { status: 202, body: { outcome: 'unknown', message: 'This may or may not have been posted.' } };
    const { host, root } = await render('/acme/plant/pulls/new?head=feature', { create: unknown });
    await fill(host.querySelector<HTMLInputElement>('.new-pull-title')!, 'Add the valve');
    await click(button(host, 'Create pull request')!);

    expect(host.querySelector<HTMLFieldSetElement>('.new-pull-fields')!.disabled).toBe(true);
    await click(button(host, 'Try again')!);

    const [first, second] = creates();
    expect(second!.body?.opKey).toBe(first!.body?.opKey);
    act(() => root.unmount());
  });

  // Found in review (2026-10-21): only the comment box was tested for a refused RETRY.
  it('a retry refused by the rate limit keeps the key: the first attempt may have opened one', async () => {
    const { host, root } = await render('/acme/plant/pulls/new?head=feature', { create: [{ status: 202, body: { outcome: 'unknown', message: 'This may or may not have been posted.' } }, { status: 429, body: { error: 'rate-limited', message: 'Too many requests: this one was not acted on.' } }, { status: 202, body: { outcome: 'unknown', message: 'This may or may not have been posted.' } }] });
    await fill(host.querySelector<HTMLInputElement>('.new-pull-title')!, 'Add the valve');
    await click(button(host, 'Create pull request')!);
    await click(button(host, 'Try again')!);
    await click(button(host, 'Try again')!);

    expect(new Set(creates().map((c) => c.body?.opKey)).size).toBe(1);
    expect(creates()).toHaveLength(3);
    act(() => root.unmount());
  });

  it('after a refusal nothing was opened, so the form unlocks and the next submission is a new one', async () => {
    const refused = { status: 422, body: { error: 'github-refused', message: 'GitHub refused this: No commits between main and feature' } };
    const { host, root } = await render('/acme/plant/pulls/new?head=feature', { create: refused });
    await fill(host.querySelector<HTMLInputElement>('.new-pull-title')!, 'Add the valve');
    await click(button(host, 'Create pull request')!);

    expect(host.querySelector('.comment-note')?.textContent).toContain('No commits between');
    expect(host.querySelector<HTMLFieldSetElement>('.new-pull-fields')!.disabled).toBe(false);
    await click(button(host, 'Create pull request')!);
    const [first, second] = creates();
    expect(second!.body?.opKey).not.toBe(first!.body?.opKey);
    act(() => root.unmount());
  });

  it('will not open one from a branch into itself, or without a title', async () => {
    const { host, root } = await render('/acme/plant/pulls/new?head=main');

    expect(host.textContent).toContain('Choose two different branches');
    // A title, so the only reason left to refuse is the branch into itself.
    await fill(host.querySelector<HTMLInputElement>('.new-pull-title')!, 'Into itself');
    expect(button(host, 'Create pull request')!.disabled).toBe(true);
    await fill(host.querySelector<HTMLSelectElement>('[aria-label="Branch with the changes"]')!, 'feature');
    await fill(host.querySelector<HTMLInputElement>('.new-pull-title')!, '');
    expect(button(host, 'Create pull request')!.disabled).toBe(true);
    act(() => root.unmount());
  });

  it("keeps what the person wrote when the template arrives or the base changes", async () => {
    sessionStorage.setItem('versio:new-pull:acme/plant', JSON.stringify({ base: 'main', head: 'feature', title: 'Kept', body: 'My own words', draft: false, template: null, opKey: null }));
    const { host, root } = await render('/acme/plant/pulls/new', { template: '## Template\n' });

    expect(host.querySelector<HTMLTextAreaElement>('.new-pull-body')!.value).toBe('My own words');
    expect(host.querySelector<HTMLInputElement>('.new-pull-title')!.value).toBe('Kept');
    act(() => root.unmount());
  });

  it("another repository's form starts empty: one repository's draft is never saved under another's", async () => {
    const { host, root } = await render('/acme/plant/pulls/new?head=feature');
    await fill(host.querySelector<HTMLInputElement>('.new-pull-title')!, 'Plant change');

    await act(async () => {
      jump('/acme/other/pulls/new');
    });
    await settle();

    expect(host.querySelector<HTMLInputElement>('.new-pull-title')!.value).toBe('');
    expect(JSON.parse(sessionStorage.getItem('versio:new-pull:acme/plant')!).title).toBe('Plant change');
    expect(sessionStorage.getItem('versio:new-pull:acme/other') ?? '').not.toContain('Plant change');
    act(() => root.unmount());
  });

  it('the list offers "New pull request" only where one can be opened', async () => {
    const on = await render('/acme/plant/pulls');
    expect(Array.from(on.host.querySelectorAll('a')).find((a) => a.textContent === 'New pull request')?.getAttribute('href')).toBe('/acme/plant/pulls/new');
    act(() => on.root.unmount());
    document.body.innerHTML = '';

    const off = await render('/acme/plant/pulls', { writable: false });
    expect(Array.from(off.host.querySelectorAll('a')).some((a) => a.textContent === 'New pull request')).toBe(false);
    act(() => off.root.unmount());
  });
});
