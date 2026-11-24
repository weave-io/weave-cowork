import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

export default defineConfig({
  plugins: [vue()],
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
