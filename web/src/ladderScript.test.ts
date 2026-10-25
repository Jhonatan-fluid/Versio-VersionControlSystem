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

/**
 * One diagram as LadderHtml draws it, with the row ids every diagram shares (ld-new-N) and a
 * jump to its own second row: the review page stacks one such page per changed file.
 */
function diagram(n: number) {
  const rows = [0, 1, 2].map((i) => `<div class="ld-row" id="ld-new-${i}" data-change="changed"></div>`).join('');
  return `<div class="ld-page" id="p${n}" data-changes-only="false"><button type="button" data-action="next">v</button>` +
    `<label><input type="checkbox" data-action="changes-only"> Changes only</label>` +
    `<a class="ld-count" data-jump="ld-new-1">1 changed</a>${rows}</div>`;
}

function placeTwo() {
  document.body.innerHTML = diagram(1) + diagram(2);
  return { p1: document.getElementById('p1')!, p2: document.getElementById('p2')! };
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
    expect(script).toContain(".ld-page:not(.ld-scripted)");
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

  // The review page stacks one diagram per changed file, and runs the script once per fragment.
  // Only the first diagram was set up — the script took document.querySelector('.ld-page') and
  // returned once that one was scripted — and a jump went through document.getElementById,
  // which every diagram's repeated row ids answer with the FIRST diagram's row (the 2026-10-24
  // review's item 10).
  describe('with two diagrams in one document', () => {
    it('sets up every diagram, and Next on the second marks a row inside the second', () => {
      const { p1, p2 } = placeTwo();
      run();
      run();
      expect(p1.classList.contains('ld-scripted')).toBe(true);
      expect(p2.classList.contains('ld-scripted')).toBe(true);

      (p2.querySelector('[data-action="next"]') as HTMLElement).click();
      expect(p2.querySelector('.ld-current')?.id).toBe('ld-new-0');
      expect(p1.querySelector('.ld-current')).toBeNull();
    });

    it('a jump in the second diagram lands on the second diagram’s row', () => {
      const { p1, p2 } = placeTwo();
      run();
      (p2.querySelector('[data-jump]') as HTMLElement).click();
      expect(p2.querySelector('#ld-new-1')?.classList.contains('ld-current')).toBe(true);
      expect(p1.querySelector('.ld-current')).toBeNull();
    });

    it('the second diagram’s "Changes only" switch changes the second diagram alone', () => {
      const { p1, p2 } = placeTwo();
      run();
      const only = p2.querySelector('[data-action="changes-only"]') as HTMLInputElement;
      only.checked = true;
      only.dispatchEvent(new Event('change'));
      expect(p2.classList.contains('ld-changes-only')).toBe(true);
      expect(p1.classList.contains('ld-changes-only')).toBe(false);
    });

    it('F7 steps the diagram last clicked, and the first until one is', () => {
      const { p1, p2 } = placeTwo();
      run();
      f7();
      expect(p1.querySelector('.ld-current')?.id).toBe('ld-new-0');

      (p2.querySelector('#ld-new-2') as HTMLElement).click();
      f7();
      expect(p2.querySelector('.ld-current')?.id).toBe('ld-new-0');
      f7();
      expect(p2.querySelector('.ld-current')?.id).toBe('ld-new-1');
      // The first diagram kept its own place; nothing stepped it.
      expect(p1.querySelector('.ld-current')?.id).toBe('ld-new-0');
    });
  });
});
