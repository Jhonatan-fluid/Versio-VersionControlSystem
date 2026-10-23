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
    // The page's own folder, plus the folder of the one C# file whose script ladderScript.test.ts
    // runs as written, so the test cannot drift from what the server serves. Vite refused the file
    // named alone; one folder of Core, not the repository.
    fs: { allow: ['.', '../src/Versio.Core/Iec/Ladder'] },
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
