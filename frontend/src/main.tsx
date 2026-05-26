import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './styles/theme.css'
import App from './App'
import { installAuthInterceptor } from './api/auth-interceptor'

// Patches window.fetch — 401 from /api/* auto-redirects to /login.
installAuthInterceptor()

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>
)
