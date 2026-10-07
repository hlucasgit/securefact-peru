import { useEffect, useState, type FormEvent } from 'react'
import { Link, useLocation, useNavigate } from 'react-router-dom'
import { api, ApiError, errorMessage } from '../api/http'
import { BrandMark, useBrand } from '../branding/BrandingProvider'
import { ErrorAlert, TextField } from '../components/ui'

const MIN_LENGTH = 12

/** The token travels in the fragment of the link (`#token=…`), which a browser never sends to a server; it is read once and the fragment is removed from the address bar. */
function tokenOf(hash: string): string {
  return new URLSearchParams(hash.replace(/^#/, '')).get('token') ?? ''
}

export function ResetPassword() {
  const brand = useBrand()
  const location = useLocation()
  const navigate = useNavigate()
  const [token] = useState(() => tokenOf(location.hash))
  const [password, setPassword] = useState('')
  const [confirmation, setConfirmation] = useState('')
  const [busy, setBusy] = useState(false)
  const [done, setDone] = useState(false)
  const [invalidLink, setInvalidLink] = useState(false)
  const [error, setError] = useState<unknown>(null)

  useEffect(() => {
    if (location.hash) navigate({ pathname: location.pathname, search: location.search }, { replace: true })
  }, [location.hash, location.pathname, location.search, navigate])

  async function submit(event: FormEvent) {
    event.preventDefault()
    setError(null)
    if (password !== confirmation) {
      setError(new Error('Las dos contraseñas no coinciden.'))
      return
    }
    setBusy(true)
    try {
      await api<void>('POST', '/api/v1/auth/password-reset/confirm', { body: { token, newPassword: password }, anonymous: true })
      setDone(true)
    } catch (failure) {
      if (failure instanceof ApiError && failure.code === 'SF-AUTH-009') {
        setInvalidLink(true)
      } else {
        setError(failure instanceof Error ? new Error(errorMessage(failure)) : failure)
      }
    } finally {
      setBusy(false)
    }
  }

  const unusable = !token || invalidLink

  return (
    <div className="login-wrap">
      <form className="card login-card stack" onSubmit={(event) => void submit(event)}>
        <div>
          <h1>
            <BrandMark />
          </h1>
          <p className="muted">Elija una contraseña nueva.</p>
        </div>
        {done ? (
          <div className="stack" role="status">
            <p>Su contraseña cambió. Por seguridad se cerraron todas sus sesiones.</p>
            <Link className="btn primary" to="/ingresar">
              Ingresar
            </Link>
          </div>
        ) : unusable ? (
          <div className="stack" role="alert">
            <p>El enlace no es válido o ya venció. Pida uno nuevo.</p>
            <Link className="btn primary" to="/recuperar">
              Pedir un enlace nuevo
            </Link>
          </div>
        ) : (
          <>
            <ErrorAlert error={error} />
            <TextField
              label="Contraseña nueva"
              type="password"
              autoComplete="new-password"
              required
              minLength={MIN_LENGTH}
              hint={`mínimo ${MIN_LENGTH} caracteres`}
              value={password}
              onChange={(event) => setPassword(event.target.value)}
              autoFocus
            />
            <TextField label="Repita la contraseña" type="password" autoComplete="new-password" required minLength={MIN_LENGTH} value={confirmation} onChange={(event) => setConfirmation(event.target.value)} />
            <button className="btn primary" type="submit" disabled={busy}>
              {busy ? 'Guardando…' : 'Cambiar la contraseña'}
            </button>
          </>
        )}
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
