import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import App from './App'
import GovernancePage from './GovernancePage'
import './styles.css'

const governanceRoute = window.location.pathname.replace(/\/+$/, '').endsWith('/admin/governance')

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    {governanceRoute ? <GovernancePage /> : <App />}
  </StrictMode>
)
