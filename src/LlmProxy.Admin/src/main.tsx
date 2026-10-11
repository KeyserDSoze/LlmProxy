import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import App from './App'
import UserPortal from './UserPortal'
import './styles.css'
import './requestAudit.css'

const userRoute = window.location.pathname.replace(/\/+$/, '').endsWith('/admin/me')

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    {userRoute
      ? <UserPortal />
      : <App />}
  </StrictMode>
)
