import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { api, setTokenSource } from '../api/http'

/** What the API returns in the cookie mode: the access token only. The refresh token is in an HttpOnly cookie that the page cannot read. */
interface LoginResponse {
  accessToken: string
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

/** A hint, not a secret: it says that this browser had a session, so the page tries to restore it. Without it a visitor is not asked for a refresh that the server would refuse. */
const SESSION_HINT = 'sf.session'
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

function hasSessionHint(): boolean {
  try {
    return localStorage.getItem(SESSION_HINT) !== null
  } catch {
    return false
  }
}

function setSessionHint(on: boolean): void {
  try {
    if (on) localStorage.setItem(SESSION_HINT, '1')
    else localStorage.removeItem(SESSION_HINT)
  } catch {
    // storage blocked: the session is restored only while the page is not reloaded
  }
}

/**
 * The refresh token rotates and its cookie is shared by every tab of the browser, so two tabs that renew at the same moment would spend the same token twice and the server would take it
 * for a theft. The browser's lock serialises the renewals of all the tabs: the second one goes on with the cookie that the first one left.
 */
function exclusively<T>(work: () => Promise<T>): Promise<T> {
  return typeof navigator !== 'undefined' && navigator.locks ? navigator.locks.request('sf-refresh', work) : work()
}

export function SessionProvider({ children }: { children: ReactNode }) {
  // The access token lives in memory only. The refresh token is an HttpOnly cookie that the page cannot read, so a script injected in the page cannot take it (ADR-050).
  const access = useRef<string | null>(null)
  const pending = useRef<Promise<boolean> | null>(null)
  const [principal, setPrincipal] = useState<Principal | null>(null)
  const [restoring, setRestoring] = useState(hasSessionHint)

  const accept = useCallback((tokens: LoginResponse) => {
    access.current = tokens.accessToken
    setSessionHint(true)
    setPrincipal(decodeToken(tokens.accessToken))
  }, [])

  const clear = useCallback(() => {
    access.current = null
    setSessionHint(false)
    setPrincipal(null)
  }, [])

  // One refresh at a time in this tab, and one at a time across the tabs (exclusively).
  const refresh = useCallback((): Promise<boolean> => {
    pending.current ??= exclusively(() => api<LoginResponse>('POST', '/api/v1/auth/refresh', { anonymous: true, cookieSession: true }))
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
    if (!hasSessionHint()) return
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
        accept(await api<LoginResponse>('POST', '/api/v1/auth/login', { body: { email, password, totpCode: totpCode || null }, anonymous: true, cookieSession: true }))
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
