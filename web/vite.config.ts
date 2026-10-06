/// <reference types="vitest/config" />
import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// The API is on another origin in development: the proxy keeps the browser on one origin (no CORS) and the production build is expected behind the same gateway as the API.
const apiTarget = process.env.SF_API_URL ?? 'http://localhost:5180'

export default defineConfig({
  plugins: [react()],
  server: { port: 5173, proxy: { '/api': { target: apiTarget, changeOrigin: false } } },
  test: { environment: 'jsdom', setupFiles: ['./src/test/setup.ts'], globals: true },
})
