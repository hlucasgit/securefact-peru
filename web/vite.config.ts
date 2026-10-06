/// <reference types="vitest/config" />
import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// The API is on another origin in development: the proxy keeps the browser on one origin (no CORS) and the production build is expected behind the same gateway as the API.
const apiTarget = process.env.SF_API_URL ?? 'http://localhost:5180'
const proxy = { '/api': { target: apiTarget, changeOrigin: false } }

export default defineConfig({
  plugins: [react()],
  server: { port: 5173, proxy },
  // The end-to-end tests serve the production build with `vite preview`: the same bundle that ships, and a server that stands many parallel browsers.
  preview: { port: 5173, strictPort: true, proxy },
  // The Playwright specs in e2e/ run with their own runner.
  test: { environment: 'jsdom', setupFiles: ['./src/test/setup.ts'], globals: true, exclude: ['e2e/**', 'node_modules/**'] },
})
