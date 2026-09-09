import react from '@vitejs/plugin-react'
import { defineConfig } from 'vitest/config'

export default defineConfig({
  plugins: [react()],
  resolve: {
    dedupe: ['react', 'react-dom']
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./unit/setup.ts'],
    clearMocks: true,
    restoreMocks: true,
    coverage: {
      reporter: ['text', 'html']
    }
  }
})
