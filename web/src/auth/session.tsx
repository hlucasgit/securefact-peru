import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { api, setTokenSource } from '../api/http'

interface Tokens {
  accessToken: string
  refreshToken: string
}

interface LoginResponse extends Tokens {
  expiresInSeconds: number
}

export interface Principal {
  userId: string
  roles: string[]
}

interface SessionValue {
  principal: Principal | null
  /** True while the stored refresh token is being exchanged on load. */
  restoring: boolean
  login(email: string, password: string, totpCode?: string): Promise<void>
  logout(): Promise<void>
  hasRole(...roles: string[]): boolean
}

const REFRESH_KEY = 'sf.refresh'
const Session = createContext<SessionValue | null>(null)

/** Reads the claims of a JWT for display and menu decisions only: the API decides what is allowed. */
export function decodeToken(token: string): Principal | null {
  try {
    const payload = token.split('.')[1]
    const json = JSON.parse(atob(payload.replaceAll('-', '+').replaceAll('_', '/'))) as { sub?: string; role?: string | string[] }
    const roles = json.role === undefined ? [] : Array.isArray(json.role) ? json.role : [json.role]
    return json.sub ? { userId: json.sub, roles } : null
  } catch {
    return null
  }
}

function storedRefresh(): string | null {
  try {
    return sessionStorage.getItem(REFRESH_KEY)
  } catch {
    return null
  }
}

function storeRefresh(token: string | null): void {
  try {
    if (token) sessionStorage.setItem(REFRESH_KEY, token)
    else sessionStorage.removeItem(REFRESH_KEY)
  } catch {
    // storage blocked: the session lasts until the page is reloaded
  }
}

export function SessionProvider({ children }: { children: ReactNode }) {
  // The access token lives in memory only; the refresh token survives a reload in sessionStorage (cleared when the tab closes).
  const access = useRef<string | null>(null)
  const refreshToken = useRef<string | null>(storedRefresh())
  const pending = useRef<Promise<boolean> | null>(null)
  const [principal, setPrincipal] = useState<Principal | null>(null)
  const [restoring, setRestoring] = useState(() => storedRefresh() !== null)

  const accept = useCallback((tokens: Tokens) => {
    access.current = tokens.accessToken
    refreshToken.current = tokens.refreshToken
    storeRefresh(tokens.refreshToken)
    setPrincipal(decodeToken(tokens.accessToken))
  }, [])

  const clear = useCallback(() => {
    access.current = null
    refreshToken.current = null
    storeRefresh(null)
    setPrincipal(null)
  }, [])

  // One refresh at a time: the refresh token rotates, so concurrent calls would invalidate each other.
  const refresh = useCallback((): Promise<boolean> => {
    if (!refreshToken.current) return Promise.resolve(false)
    pending.current ??= api<LoginResponse>('POST', '/api/v1/auth/refresh', { body: { refreshToken: refreshToken.current }, anonymous: true })
      .then((tokens) => {
        accept(tokens)
        return true
      })
      .catch(() => false)
      .finally(() => {
        pending.current = null
      })
    return pending.current
  }, [accept])

  useEffect(() => {
    setTokenSource({ accessToken: () => access.current, refresh, expired: clear })
  }, [refresh, clear])

  useEffect(() => {
    if (refreshToken.current === null) return
    let active = true
    void refresh().then((ok) => {
      if (!ok) clear()
      if (active) setRestoring(false)
    })
    return () => {
      active = false
    }
  }, [refresh, clear])

  const value = useMemo<SessionValue>(
    () => ({
      principal,
      restoring,
      async login(email, password, totpCode) {
        accept(await api<LoginResponse>('POST', '/api/v1/auth/login', { body: { email, password, totpCode: totpCode || null }, anonymous: true }))
      },
      async logout() {
        try {
          await api('POST', '/api/v1/auth/logout')
        } catch {
          // the session is dropped locally in any case
        }
        clear()
      },
      hasRole: (...roles) => principal?.roles.some((r) => roles.includes(r)) ?? false,
    }),
    [principal, restoring, accept, clear],
  )

  return <Session.Provider value={value}>{children}</Session.Provider>
}

export function useSession(): SessionValue {
  const value = useContext(Session)
  if (!value) throw new Error('useSession needs a SessionProvider')
  return value
}
