import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import App from './App'
import UserPortal from './UserPortal'
import './styles.css'

const route = window.location.pathname.replace(/\/+$/, '')
const governanceRoute = route.endsWith('/admin/governance')
const releasesRoute = route.endsWith('/admin/releases')
const usersRoute = route.endsWith('/admin/users')
const userRoute = route.endsWith('/admin/me')

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    {userRoute
      ? <UserPortal />
      : <App initialView={releasesRoute ? 'releases' : governanceRoute ? 'governance' : usersRoute ? 'users' : 'dashboard'} />}
  </StrictMode>
)
