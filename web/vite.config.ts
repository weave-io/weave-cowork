import { resolve } from 'node:path'
import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'
import tailwindcss from '@tailwindcss/vite'

export default defineConfig({
  plugins: [vue(), tailwindcss()],
  resolve: { alias: { '@': resolve(import.meta.dirname, './src') } },
  build: { outDir: '../src/CoWork.Server/wwwroot', emptyOutDir: true },
  server: {
    port: 5173,
    proxy: {
      '/api': 'http://localhost:5880',
      '/hubs': { target: 'http://localhost:5880', ws: true },
    },
  },
  test: { environment: 'node' },
})
