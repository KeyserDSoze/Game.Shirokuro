import { fileURLToPath, URL } from 'node:url'
import tailwindcss from '@tailwindcss/vite'
import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: {
    proxy: {
      '/api': { target: 'http://127.0.0.1:5186', changeOrigin: true },
      '/hubs': { target: 'http://127.0.0.1:5186', changeOrigin: true, ws: true },
    },
  },

  resolve: {
    alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) },
  },
})


