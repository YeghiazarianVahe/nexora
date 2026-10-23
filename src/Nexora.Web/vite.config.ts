import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// The production build lands in the API's wwwroot, which HTTP.sys serves.
export default defineConfig({
  plugins: [react()],
  build: {
    outDir: '../Nexora.Api/wwwroot',
    emptyOutDir: true,
    sourcemap: false,
  },
  server: {
    port: 5173,
    proxy: {
      '/api': { target: 'http://localhost:5090', changeOrigin: false },
      '/hubs': { target: 'http://localhost:5090', ws: true, changeOrigin: false },
    },
  },
});
