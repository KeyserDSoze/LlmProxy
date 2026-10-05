import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import App from './App'
import UserPortal from './UserPortal'
import './styles.css'
import './requestAudit.css'

const route = window.location.pathname.replace(/\/+$/, '')
const governanceRoute = route.endsWith('/admin/governance')
const releasesRoute = route.endsWith('/admin/releases')
const usersRoute = route.endsWith('/admin/users')
const infrastructureRoute = route.endsWith('/admin/infrastructure') || route.endsWith('/admin/model-management') || route.endsWith('/admin/nodes') || route.endsWith('/admin/hardware')
const userRoute = route.endsWith('/admin/me')

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    {userRoute
      ? <UserPortal />
      : <App initialView={releasesRoute ? 'releases' : governanceRoute ? 'governance' : usersRoute ? 'users' : infrastructureRoute ? 'infrastructure' : 'dashboard'} />}
  </StrictMode>
)
