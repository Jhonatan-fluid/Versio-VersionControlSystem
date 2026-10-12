import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import assets from '../../src/Versio.Core/Iec/Ladder/LadderPageAssets.cs?raw';

// The ladder's navigation script is Core's LadderPageAssets.Script, served as /assets/ladder.js.
// It is read here from the C# source itself, so this runs the very text the server sends, not a
// copy that could drift. The web viewer runs it once per placed fragment into one document, which
// the desktop never does. Found in review: every run left another document F7 listener behind,
// each keeping its replaced page alive, and a second run over one page stacked a second set of
// handlers. Each set kept its own position, so the screen looked right; registrations are what
// show it, and they are what these count.

const script = (() => {
  const start = assets.indexOf('public const string Script = """');
  const body = assets.indexOf('"""', start) + 3;
  return assets.slice(body, assets.indexOf('""";', body));
})();

function run() {
  new Function(script)();
}

function place(changes: number) {
  const rows = Array.from({ length: changes }, (_, i) => `<div class="ld-row" id="r${i}" data-change="changed"></div>`).join('');
  document.body.innerHTML =
    `<div class="ld-page" data-changes-only="false"><button type="button" data-action="next">v</button>` +
    `<label><input type="checkbox" data-action="changes-only"> Changes only</label>${rows}</div>`;
}

function current() {
  return document.querySelector('.ld-current')?.id;
}

function f7() {
  document.dispatchEvent(new KeyboardEvent('keydown', { key: 'F7' }));
}

/** How many listeners of <type> were added to <target> while <act> ran. */
function added(target: EventTarget | (() => EventTarget | null), type: string, act: () => void) {
  const spy = vi.spyOn(EventTarget.prototype, 'addEventListener');
  act();
  const on = typeof target === 'function' ? target() : target;
  const count = spy.mock.calls.filter((call, i) => call[0] === type && spy.mock.contexts[i] === on).length;
  spy.mockRestore();
  return count;
}

describe('the ladder script, run once per placed fragment', () => {
  beforeAll(() => {
    Element.prototype.scrollIntoView = () => {};
  });

  afterEach(() => vi.restoreAllMocks());

  it('is found in the C# source', () => {
    expect(script).toContain("querySelector('.ld-page')");
  });

  it('run twice over one page, sets the page up once', () => {
    place(3);
    const clicks = added(() => document.querySelector('.ld-page'), 'click', () => {
      run();
      run();
    });
    expect(clicks).toBe(1);
    (document.querySelector('[data-action="next"]') as HTMLElement).click();
    expect(current()).toBe('r0');
  });

  it('leaves at most one keyboard listener on the document however many pages it ran over', () => {
    const keys = added(document, 'keydown', () => {
      for (let n = 3; n < 8; n++) {
        place(n);
        run();
      }
    });
    expect(keys).toBeLessThanOrEqual(1);
  });

  it('F7 steps the page in the document now, after earlier pages were replaced', () => {
    place(3);
    run();
    place(5);
    run();
    f7();
    expect(current()).toBe('r0');
    f7();
    expect(current()).toBe('r1');
  });

  it('the "Changes only" switch still applies', () => {
    place(2);
    run();
    const only = document.querySelector('[data-action="changes-only"]') as HTMLInputElement;
    only.checked = true;
    only.dispatchEvent(new Event('change'));
    expect(document.querySelector('.ld-page')?.classList.contains('ld-changes-only')).toBe(true);
  });
});
