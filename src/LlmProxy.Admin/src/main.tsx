import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import App from './App'
import GovernancePage from './GovernancePage'
import ProductReleaseLauncher from './ProductReleaseLauncher'
import ReleaseNotesPage from './ReleaseNotesPage'
import './styles.css'

const route = window.location.pathname.replace(/\/+$/, '')
const governanceRoute = route.endsWith('/admin/governance')
const releasesRoute = route.endsWith('/admin/releases')

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    {releasesRoute
      ? <ReleaseNotesPage />
      : governanceRoute
        ? <><GovernancePage /><ProductReleaseLauncher /></>
        : <><App /><a className="governanceLauncher" href="/admin/governance">Usage & Governance</a><ProductReleaseLauncher /></>}
  </StrictMode>
)
