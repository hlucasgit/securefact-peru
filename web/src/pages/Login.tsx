import { useState, type FormEvent } from 'react'
import { Navigate, useLocation } from 'react-router-dom'
import { ApiError, errorMessage } from '../api/http'
import { useSession } from '../auth/session'
import { BrandMark, useBrand } from '../branding/BrandingProvider'
import { ErrorAlert, Loading, TextField } from '../components/ui'

export function Login() {
  const { principal, restoring, login } = useSession()
  const brand = useBrand()
  const location = useLocation()
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [code, setCode] = useState('')
  const [needsCode, setNeedsCode] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<unknown>(null)

  if (restoring) return <Loading label="Restaurando la sesión…" />
  if (principal) return <Navigate to={(location.state as { from?: string } | null)?.from ?? '/'} replace />

  async function submit(event: FormEvent) {
    event.preventDefault()
    setBusy(true)
    setError(null)
    try {
      await login(email.trim(), password, code.trim())
    } catch (failure) {
      if (failure instanceof ApiError && failure.code === 'SF-AUTH-005') {
        setNeedsCode(true)
        setError(null)
      } else {
        setError(failure instanceof Error ? new Error(errorMessage(failure)) : failure)
      }
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="login-wrap">
      <form className="card login-card stack" onSubmit={(event) => void submit(event)}>
        <div>
          <h1>
            <BrandMark />
          </h1>
          <p className="muted">Ingrese a su cuenta.</p>
        </div>
        <ErrorAlert error={error} />
        <TextField label="Correo electrónico" type="email" autoComplete="username" required value={email} onChange={(event) => setEmail(event.target.value)} autoFocus />
        <TextField label="Contraseña" type="password" autoComplete="current-password" required value={password} onChange={(event) => setPassword(event.target.value)} />
        {needsCode && (
          <TextField label="Código de verificación" hint="de su aplicación de autenticación" inputMode="numeric" autoComplete="one-time-code" maxLength={6} required value={code} onChange={(event) => setCode(event.target.value)} autoFocus />
        )}
        <button className="btn primary" type="submit" disabled={busy}>
          {busy ? 'Ingresando…' : 'Ingresar'}
        </button>
        {brand?.supportEmail && (
          <p className="muted">
            ¿Necesita ayuda? <a href={`mailto:${brand.supportEmail}`}>{brand.supportEmail}</a>
          </p>
        )}
        {brand && <p className="muted powered">Con tecnología SecureFact</p>}
      </form>
    </div>
  )
}
