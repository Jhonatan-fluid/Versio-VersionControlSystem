import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// The page builds into the server's wwwroot, which serves it. In development, `npm run dev`
// serves it from Vite and forwards the API, sign-in and the renderers' assets to a server
// started on port 5099, so the page is never pointed at anything but its own origin.
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
    proxy: {
      '/api': 'http://127.0.0.1:5099',
      '/auth': 'http://127.0.0.1:5099',
      '/assets/code.css': 'http://127.0.0.1:5099',
      '/assets/ladder.css': 'http://127.0.0.1:5099',
      '/assets/ladder.js': 'http://127.0.0.1:5099',
    },
  },
  test: {
    environment: 'jsdom',
  },
} as never);
