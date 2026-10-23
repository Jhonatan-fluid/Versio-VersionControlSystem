// The colour mode: the system's, or the person's choice, kept in this browser only. Browser
// storage can be absent or refused (private windows, blocked site data), and the page must
// draw the same without it, so every read and write is guarded.

export type ColorMode = 'system' | 'light' | 'dark';

const Key = 'versio-color-mode';

export function readColorMode(): ColorMode {
  try {
    const v = localStorage.getItem(Key);
    return v === 'light' || v === 'dark' ? v : 'system';
  } catch {
    return 'system';
  }
}

/** Applies a mode to the document and remembers it; "system" clears both. */
export function applyColorMode(mode: ColorMode): void {
  const root = document.documentElement;
  if (mode === 'system') {
    root.removeAttribute('data-color-mode');
  } else {
    root.setAttribute('data-color-mode', mode);
  }
  try {
    if (mode === 'system') {
      localStorage.removeItem(Key);
    } else {
      localStorage.setItem(Key, mode);
    }
  } catch {
    // Nothing to remember into; the attribute still applies for this page.
  }
}

/** What the page is drawn in right now, whichever way it was chosen. */
export function effectiveColorMode(): 'light' | 'dark' {
  const chosen = document.documentElement.getAttribute('data-color-mode');
  if (chosen === 'light' || chosen === 'dark') {
    return chosen;
  }
  return typeof matchMedia === 'function' && matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
}
