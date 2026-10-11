import DOMPurify from 'dompurify';

// The HTML the server rendered with Versio.Core, placed and never built here.
//
// Three walls stand between a file's contents and markup on this page (docs/web/PLAN.md,
// security rule 5): Core escapes every character from a file; a test feeds random input through
// every renderer and checks only known tags come out; and this, DOMPurify, on the way in. The
// server's CSP stands behind all three: no inline script runs even if all of them failed.
//
// What is kept is what the renderers emit: HTML and SVG, their classes and data-* markers (the
// contract a link to a line, and later a review comment, attaches to), titles, ARIA, and the style
// attribute two renderers use on purpose. What goes: script, style elements, event handlers,
// javascript: URLs, forms, frames, objects.

export function sanitize(html: string): string {
  return DOMPurify.sanitize(html, {
    USE_PROFILES: { html: true, svg: true },
    ALLOW_DATA_ATTR: true,
    FORBID_TAGS: ['style', 'script', 'iframe', 'object', 'embed', 'form', 'input', 'textarea', 'select', 'link', 'meta', 'base'],
    FORBID_ATTR: ['srcset', 'action', 'formaction', 'xlink:href'],
  });
}

/** Whether a placed fragment holds a ladder, whose navigation script must run over it. */
export function hasLadder(html: string): boolean {
  return html.includes('class="ld-page"');
}

/**
 * Where a Markdown link may go. Core writes links into data-href, never href, so nothing in a
 * document can navigate by itself; this decides what a click does. Same-repository relative
 * paths stay in the viewer; http(s) opens a new tab with no opener and no referrer; anything else
 * (javascript:, data:, file:) goes nowhere.
 */
export function linkTarget(href: string, currentPath: string): { kind: 'internal'; path: string } | { kind: 'external'; url: string } | null {
  const value = href.trim();
  if (/^https?:\/\//i.test(value)) {
    return { kind: 'external', url: value };
  }
  if (/^[a-z][a-z0-9+.-]*:/i.test(value) || value.startsWith('//')) {
    return null;
  }
  const withoutAnchor = value.split('#')[0] ?? '';
  if (withoutAnchor === '') {
    return null;
  }
  const base = currentPath.includes('/') ? currentPath.slice(0, currentPath.lastIndexOf('/') + 1) : '';
  const parts: string[] = [];
  for (const part of (withoutAnchor.startsWith('/') ? withoutAnchor.slice(1) : base + withoutAnchor).split('/')) {
    if (part === '' || part === '.') {
      continue;
    }
    if (part === '..') {
      if (parts.length === 0) {
        return null;
      }
      parts.pop();
      continue;
    }
    parts.push(part);
  }
  return parts.length ? { kind: 'internal', path: parts.join('/') } : null;
}
