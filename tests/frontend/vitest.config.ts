import react from '@vitejs/plugin-react'
import { defineConfig } from 'vitest/config'

export default defineConfig({
  plugins: [react()],
  resolve: {
    dedupe: ['react', 'react-dom']
  },
  test: {
    include: ['unit/**/*.test.{ts,tsx}'],
    environment: 'jsdom',
    setupFiles: ['./unit/setup.ts'],
    clearMocks: true,
    restoreMocks: true
  }
})
