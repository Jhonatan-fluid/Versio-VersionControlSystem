import { describe, expect, it } from 'vitest';
import { hasLadder, linkTarget, mermaidBlocks, sanitize } from './fragment';
import { blobUrl, compareUrl, parseRange, parseRepoInput } from './routes';
import { query } from './api';

describe('sanitize: the third wall', () => {
  it('keeps the renderers markup, classes and line markers', () => {
    const html =
      '<div class="cd-row cd-added" data-line="3" data-file-line="7"><span class="cd-num">3</span>' +
      '<code class="cd-code"><span class="cd-t-keyword">IF</span> <mark class="cd-em">x</mark></code></div>';
    expect(sanitize(html)).toBe(html);
  });

  it('keeps ladder SVG and its width variable', () => {
    const html = '<svg class="ld-net" viewBox="0 0 10 10" style="--w:120px" role="img" aria-label="Network 1"><line x1="0" y1="0" x2="1" y2="1"></line></svg>';
    const out = sanitize(html);
    expect(out).toContain('<svg');
    expect(out).toContain('--w:120px');
    expect(out).toContain('aria-label="Network 1"');
  });

  it('removes script, handlers, style elements and javascript: URLs', () => {
    const out = sanitize(
      '<p>ok</p><script>alert(1)</script><img src="x" onerror="alert(2)"><style>*{}</style>' +
        '<a href="javascript:alert(3)">x</a><svg><a xlink:href="javascript:alert(4)"><text>t</text></a></svg>',
    );
    expect(out).not.toMatch(/script|onerror|<style|javascript:/i);
    expect(out).toContain('<p>ok</p>');
  });

  it('keeps the ladder "Changes only" checkbox, and no other input', () => {
    // Found in review: every input was forbidden, so the ladder's switch reached the page as a
    // bare label and did nothing.
    const out = sanitize(
      '<label><input type="checkbox" data-action="changes-only"> Changes only</label>' +
        '<input type="text" value="x"><input name="q" form="f" type="checkbox"><input>' +
        '<button type="button" data-action="next" title="Next change (F7)">v</button>',
    );
    expect(out).toContain('<input type="checkbox" data-action="changes-only">');
    expect(out).toContain('<button type="button" data-action="next"');
    expect(out).not.toMatch(/type="text"|name=|form=/);
    expect(out.match(/<input/g)).toHaveLength(2);
  });

  it('keeps a hidden-character marker as text, never the character', () => {
    const html = '<span class="cd-hidden" data-char="U+202E" title="Right-to-left override">U+202E</span>';
    expect(sanitize(html)).toBe(html);
  });
});

describe('Mermaid blocks', () => {
  it('are found with their source text, which the frame draws', () => {
    const root = document.createElement('div');
    root.innerHTML = sanitize('<div class="md-page"><p>x</p><pre class="mermaid">graph TD\n  A --&gt; B</pre></div>');
    const blocks = mermaidBlocks(root);
    expect(blocks).toHaveLength(1);
    expect(blocks[0]!.source).toBe('graph TD\n  A --> B');
  });
});

describe('Markdown links', () => {
  it('stay in the viewer for relative paths to other Markdown documents and resolve ..', () => {
    expect(linkTarget('docs/STATUS.md', 'README.md')).toEqual({ kind: 'internal', path: 'docs/STATUS.md' });
    expect(linkTarget('../README.md#top', 'docs/web/PLAN.md')).toEqual({ kind: 'internal', path: 'docs/README.md' });
    expect(linkTarget('/docs/Guide.MARKDOWN', 'docs/x.md')).toEqual({ kind: 'internal', path: 'docs/Guide.MARKDOWN' });
    // The desktop's rule (FINISH.md D2): a source file has its own row to click, so a link to one goes nowhere.
    expect(linkTarget('/src/a.st', 'docs/x.md')).toBeNull();
  });

  it('open http(s) elsewhere and refuse every other scheme', () => {
    expect(linkTarget('https://example.com/a', 'README.md')).toEqual({ kind: 'external', url: 'https://example.com/a' });
    expect(linkTarget('javascript:alert(1)', 'README.md')).toBeNull();
    expect(linkTarget('JaVaScRiPt:alert(1)', 'README.md')).toBeNull();
    expect(linkTarget('data:text/html,x', 'README.md')).toBeNull();
    expect(linkTarget('//evil.example/x', 'README.md')).toBeNull();
    expect(linkTarget('../../outside', 'README.md')).toBeNull();
  });
});

describe('URLs', () => {
  it('escape every segment and keep the path slashes', () => {
    expect(blobUrl('acme', 'plant', 'feature/x', 'Device/Plc Logic/A#B.st')).toBe('/acme/plant/blob/feature%2Fx/Device/Plc%20Logic/A%23B.st');
  });

  it('round-trip a compare range', () => {
    const url = compareUrl('acme', 'plant', 'main', 'next', 'a.st');
    expect(url).toBe('/acme/plant/compare/main...next?path=a.st');
    expect(parseRange('main...next')).toEqual({ base: 'main', head: 'next' });
    expect(parseRange('main..next')).toBeNull();
  });

  it('read owner/repo or a GitHub URL, and nothing else', () => {
    expect(parseRepoInput('acme/plant')).toEqual({ owner: 'acme', repo: 'plant' });
    expect(parseRepoInput('https://github.com/acme/plant.git')).toEqual({ owner: 'acme', repo: 'plant' });
    expect(parseRepoInput('https://github.com/acme/plant/tree/main/src')).toEqual({ owner: 'acme', repo: 'plant' });
    expect(parseRepoInput('https://evil.example/acme/plant')).toBeNull();
    expect(parseRepoInput('acme/..')).toBeNull();
    expect(parseRepoInput('a b/c')).toBeNull();
  });

  it('build a query from the values given, escaped', () => {
    expect(query({ ref: 'main', path: 'a b&c', view: undefined })).toBe('?ref=main&path=a%20b%26c');
  });

  it('spot a ladder fragment', () => {
    expect(hasLadder('<div class="ld-page" data-block="x">')).toBe(true);
    expect(hasLadder('<div class="cd-page">')).toBe(false);
  });
});
