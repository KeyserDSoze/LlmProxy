#!/usr/bin/env node
// Runs the `visual` Playwright project inside the Playwright image that matches the
// pinned @playwright/test version, so screenshots are pixel-identical on Windows,
// macOS, Linux and CI. Extra arguments go to `playwright test`, for example:
//   npm run test:visual:docker
//   npm run test:visual:update            (rewrite changed/missing baselines)
//   node scripts/visual-docker.mjs -g "dashboard"
import { spawnSync } from 'node:child_process'
import { readFileSync } from 'node:fs'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const frontendDir = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const repoRoot = resolve(frontendDir, '..', '..')
const version = JSON.parse(readFileSync(resolve(frontendDir, 'package.json'), 'utf8')).devDependencies?.['@playwright/test']
if (!/^\d+\.\d+\.\d+$/.test(version ?? '')) {
  console.error(`@playwright/test must be pinned to an exact version in tests/frontend/package.json (found "${version}").`)
  process.exit(2)
}
const image = `mcr.microsoft.com/playwright:v${version}-noble`

// node_modules live in named volumes: host installs (e.g. Windows binaries) are never reused.
const inner = `set -u
npm --prefix src/LlmProxy.Admin install --no-audit --no-fund --no-package-lock --loglevel=error || exit $?
npm --prefix tests/frontend install --no-audit --no-fund --no-package-lock --loglevel=error || exit $?
cd tests/frontend
npx playwright test --project=visual "$@"
status=$?
if [ -n "\${HOST_UID:-}" ]; then chown -R "$HOST_UID:$HOST_GID" test-results playwright-report e2e/__screenshots__ 2>/dev/null || true; fi
exit $status`

const owner = typeof process.getuid === 'function' ? ['-e', `HOST_UID=${process.getuid()}`, '-e', `HOST_GID=${process.getgid()}`] : []
const result = spawnSync('docker', [
  'run', '--rm', '--init', '--ipc=host',
  '-v', `${repoRoot}:/work`, '-w', '/work',
  '-v', 'llmproxy-visual-admin-modules:/work/src/LlmProxy.Admin/node_modules',
  '-v', 'llmproxy-visual-test-modules:/work/tests/frontend/node_modules',
  '-e', 'LLMPROXY_VISUAL_COMPARE=1',
  '-e', 'PLAYWRIGHT_USE_SYSTEM_CHROME=0',
  '-e', `CI=${process.env.CI ?? ''}`,
  ...owner,
  image, 'bash', '-c', inner, 'visual', ...process.argv.slice(2)
], { stdio: 'inherit' })

if (result.error) {
  console.error(`Unable to start Docker (${result.error.message}). Docker is required for pixel comparisons.`)
  process.exit(1)
}
process.exit(result.status ?? 1)
