import { describe, expect, it } from 'vitest';
import dark from '../../../src/Versio.App/Theme/tokens/Palette.Dark.xaml?raw';
import light from '../../../src/Versio.App/Theme/tokens/Palette.Light.xaml?raw';

// tokens.css speaks Primer's names with the desktop palette's values (since 2026-10-24). Two
// things are held here that a browser would never report: the three colour blocks define the
// same keys (a token missing from one theme falls back to the other theme's colour, and only
// in that theme), and the values are the XAML palette's — the file beside the desktop app is
// the one source of Versio's colours, so a change there and not here is a red test, as the
// desktop's ThemeTokenTests makes a change in one palette and not the other. The XAML comes in
// as text through Vite's ?raw (vite.config.ts allows the folder), as ladderScript.test.ts reads Core.

// Vite's ?raw hands back the stylesheet as Vite processed it, and under vitest a stylesheet is
// processed to nothing (test.css is off), so this one is read from disk: Node's fs without
// Node's types, which the page's tsconfig does not have — tsc does not resolve a computed
// specifier, and vitest runs in Node where it resolves fine.
// The path is from the page's folder, where vitest runs (npm test in web/, and the build's --root).
const fs = (await import(/* @vite-ignore */ 'node:' + 'fs')) as { readFileSync(path: string, encoding: string): string };
const css = fs.readFileSync('src/styles/tokens.css', 'utf8');

function block(selector: string): string {
  const start = css.indexOf(selector + ' {');
  expect(start, selector).toBeGreaterThanOrEqual(0);
  // The first brace after the opening one closes the block: nothing nests inside a declaration
  // block, and the comments carry no braces.
  return css.slice(start + selector.length + 2, css.indexOf('}', start));
}

function declarations(text: string): Map<string, string> {
  const out = new Map<string, string>();
  for (const m of text.matchAll(/(--[a-z-]+):\s*([^;]+);/g)) {
    out.set(m[1] ?? '', (m[2] ?? '').trim());
  }
  return out;
}

function palette(xaml: string): Map<string, string> {
  const out = new Map<string, string>();
  for (const m of xaml.matchAll(/x:Key="(Color\.[A-Za-z.]+)">(#[0-9A-Fa-f]{6})</g)) {
    out.set(m[1] ?? '', (m[2] ?? '').toLowerCase());
  }
  expect(out.size).toBeGreaterThan(20);
  return out;
}

const lightTokens = declarations(block(':root'));
const darkBySystem = declarations(block(':root:not([data-color-mode="light"])'));
const darkByChoice = declarations(block(':root[data-color-mode="dark"]'));

describe('tokens.css', () => {
  it('defines the same colour keys in every theme block', () => {
    const colours = (d: Map<string, string>) => [...d.keys()].filter((k) => k.startsWith('--color-')).sort();
    expect(colours(lightTokens).length).toBeGreaterThan(40);
    expect(colours(darkBySystem)).toEqual(colours(lightTokens));
    expect(colours(darkByChoice)).toEqual(colours(lightTokens));
  });

  it('writes the dark theme the same way twice', () => {
    expect([...darkByChoice.entries()]).toEqual([...darkBySystem.entries()]);
  });

  // The page's token on the left, the palette key it must equal on the right. Not every token
  // has a key (the translucent "subtle" fills are derived), and three fills in dark take the
  // light palette's colour on purpose (tokens.css says why); those are not listed.
  const pinned: [token: string, key: string][] = [
    ['--color-canvas-default', 'Color.Surface.Base'],
    ['--color-canvas-inset', 'Color.Surface.Sunken'],
    ['--color-canvas-overlay', 'Color.Surface.Raised'],
    ['--color-canvas-hover', 'Color.Surface.Hover'],
    ['--color-canvas-selected', 'Color.Surface.Selected'],
    ['--color-border-default', 'Color.Border.Strong'],
    ['--color-border-muted', 'Color.Border.Subtle'],
    ['--color-fg-default', 'Color.Text.Primary'],
    ['--color-fg-muted', 'Color.Text.Secondary'],
    ['--color-fg-subtle', 'Color.Text.Disabled'],
    ['--color-fg-on-emphasis', 'Color.Text.OnAccent'],
    ['--color-accent-fg', 'Color.Accent.Default'],
    ['--color-accent-emphasis', 'Color.Accent.Default'],
    ['--color-accent-subtle', 'Color.Accent.Muted'],
    ['--color-success-fg', 'Color.Status.Added'],
    ['--color-success-subtle', 'Color.Diff.AddedBg'],
    ['--color-attention-fg', 'Color.Status.Modified'],
    ['--color-severe-fg', 'Color.Status.Conflict'],
    ['--color-danger-fg', 'Color.Status.Deleted'],
    ['--color-danger-subtle', 'Color.Diff.DeletedBg'],
    ['--color-done-fg', 'Color.Status.Renamed'],
    ['--color-neutral-emphasis', 'Color.Status.Neutral'],
    ['--color-diff-add-line', 'Color.Diff.AddedBg'],
    ['--color-diff-add-word', 'Color.Diff.AddedWord'],
    ['--color-diff-del-line', 'Color.Diff.DeletedBg'],
    ['--color-diff-del-word', 'Color.Diff.DeletedWord'],
    ['--color-diff-hunk', 'Color.Accent.Muted'],
    ['--color-btn-primary-bg', 'Color.Accent.Default'],
    ['--color-btn-primary-hover-bg', 'Color.Accent.Hover'],
    ['--color-btn-hover-bg', 'Color.Surface.Hover'],
    ['--color-btn-border', 'Color.Border.Strong'],
    ['--color-header-bg', 'Color.Surface.Base'],
    ['--color-focus', 'Color.Accent.Default'],
  ];

  it.each([
    ['light', lightTokens, palette(light)],
    ['dark', darkByChoice, palette(dark)],
  ])('takes the desktop %s palette, colour for colour', (_theme, tokens, colours) => {
    const got = Object.fromEntries(pinned.map(([token]) => [token, tokens.get(token)?.toLowerCase()]));
    const want = Object.fromEntries(pinned.map(([token, key]) => [token, colours.get(key)]));
    expect(got).toEqual(want);
  });

  it('is the desktop face too: Segoe UI and Cascadia Mono first', () => {
    expect(lightTokens.get('--font-ui')).toMatch(/^"Segoe UI Variable Text", "Segoe UI"/);
    expect(lightTokens.get('--font-mono')).toMatch(/^"Cascadia Mono", Consolas/);
  });
});
