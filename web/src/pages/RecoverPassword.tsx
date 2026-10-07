import { useState, type FormEvent } from 'react'
import { Link } from 'react-router-dom'
import { api, errorMessage } from '../api/http'
import { BrandMark, useBrand } from '../branding/BrandingProvider'
import { ErrorAlert, TextField } from '../components/ui'

/** Asks for the e-mail with the link to choose a new password. The answer is the same whether the address has an account or not (the API never says). */
export function RecoverPassword() {
  const brand = useBrand()
  const [email, setEmail] = useState('')
  const [busy, setBusy] = useState(false)
  const [sent, setSent] = useState(false)
  const [error, setError] = useState<unknown>(null)

  async function submit(event: FormEvent) {
    event.preventDefault()
    setBusy(true)
    setError(null)
    try {
      await api<void>('POST', '/api/v1/auth/password-reset/request', { body: { email: email.trim() }, anonymous: true })
      setSent(true)
    } catch (failure) {
      setError(failure instanceof Error ? new Error(errorMessage(failure)) : failure)
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
          <p className="muted">Recupere el acceso a su cuenta.</p>
        </div>
        {sent ? (
          <div className="stack" role="status">
            <p>Si la dirección tiene una cuenta activa, le enviamos un correo con el enlace para elegir una contraseña nueva. El enlace vale por tiempo limitado y se usa una sola vez.</p>
            <p className="muted">Si no llega en unos minutos, revise la carpeta de correo no deseado.</p>
          </div>
        ) : (
          <>
            <ErrorAlert error={error} />
            <TextField label="Correo electrónico" type="email" autoComplete="username" required value={email} onChange={(event) => setEmail(event.target.value)} autoFocus />
            <button className="btn primary" type="submit" disabled={busy}>
              {busy ? 'Enviando…' : 'Enviar el enlace'}
            </button>
          </>
        )}
        <p>
          <Link to="/ingresar">Volver a ingresar</Link>
        </p>
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
