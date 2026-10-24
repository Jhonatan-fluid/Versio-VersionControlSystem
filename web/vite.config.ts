import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// The page builds into the server's wwwroot, which serves it. In development, `npm run dev`
// serves it from Vite and forwards the API, sign-in and the renderers' assets to a server
// started on port 5099 — or the port in VERSIO_WEB_API_PORT, which is how the page is worked
// on against a fixture or pull-request preview already serving on 5199 — so the page is never
// pointed at anything but its own origin.
// Read without Node's types: the config is checked by the page's tsc, which has only the DOM's.
const apiPort = (globalThis as { process?: { env?: Record<string, string | undefined> } }).process?.env?.VERSIO_WEB_API_PORT ?? '5099';
const api = `http://127.0.0.1:${apiPort}`;

export default defineConfig({
  plugins: [react()],
  build: {
    outDir: '../src/Versio.Web/wwwroot',
    emptyOutDir: true,
    // No inline script or style in the output: the server's CSP allows script and style from
    // 'self' only, and an inlined chunk would be refused by it.
    assetsInlineLimit: 0,
    modulePreload: { polyfill: false },
  },
  server: {
    // The page's own folder, plus the folders of the files tests read as written so they cannot
    // drift from the source: the one C# file whose script ladderScript.test.ts runs, the icon's
    // reference drawing VersioMark.test.tsx holds the header's mark to, and the desktop palette
    // tokens.test.ts holds the page's colours to. Vite refused a file named alone; folders, and
    // never the repository.
    fs: { allow: ['.', '../src/Versio.Core/Iec/Ladder', '../assets', '../src/Versio.App/Theme/tokens'] },
    proxy: {
      '/api': api,
      '/auth': api,
      '/assets/code.css': api,
      '/assets/ladder.css': api,
      '/assets/ladder.js': api,
      '/assets/mermaid.js': api,
    },
  },
  test: {
    environment: 'jsdom',
  },
} as never);
