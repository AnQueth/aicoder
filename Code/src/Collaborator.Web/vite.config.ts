import { defineConfig } from 'vitest/config'
import vue from '@vitejs/plugin-vue'

export default defineConfig({
  plugins: [vue()],
  build: {
    outDir: '../Collaborator.Api/wwwroot',
    emptyOutDir: true,
  },
  server: {
    proxy: {
      '/api': { target: process.env.COLLABORATOR_API_URL ?? 'http://localhost:5076' },
    },
  },
  test: {
    environment: 'jsdom',
    clearMocks: true,
  },
})
