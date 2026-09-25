import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';
import { buildLabel } from './build-label.mjs';

// Dev server proxies the WebSockets to the .NET server on 127.0.0.1:5080.
// In production the .NET server serves dist/ itself, behind Caddy.
export default defineConfig({
  plugins: [react()],
  define: {
    __BUILD__: JSON.stringify(buildLabel()),
  },
  server: {
    port: 5173,
    strictPort: true,
    proxy: {
      '/ws': { target: 'ws://127.0.0.1:5080', ws: true },
      '/health': { target: 'http://127.0.0.1:5080' },
    },
  },
  build: {
    target: 'es2022',
    sourcemap: false,
  },
  test: {
    environment: 'node',
    include: ['src/**/*.test.ts'],
  },
});
