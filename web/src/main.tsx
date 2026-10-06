import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { BrowserRouter } from 'react-router-dom'
import { ApiError } from './api/http'
import { App } from './App'
import { SessionProvider } from './auth/session'
import { ToastProvider } from './components/ui'
import './index.css'

// A request refused for its own reasons (4xx) is not retried; only network trouble and 5xx are.
const client = new QueryClient({
  defaultOptions: {
    queries: { staleTime: 15_000, retry: (count, error) => !(error instanceof ApiError && error.status < 500) && count < 2, refetchOnWindowFocus: false },
  },
})

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <QueryClientProvider client={client}>
      <BrowserRouter>
        <SessionProvider>
          <ToastProvider>
            <App />
          </ToastProvider>
        </SessionProvider>
      </BrowserRouter>
    </QueryClientProvider>
  </StrictMode>,
)
