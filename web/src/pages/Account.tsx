import { useState, type FormEvent } from 'react'
import { post } from '../api/http'
import { useCreateUser, useDeactivateUser, useRules, useTenant, useUsers } from '../api/queries'
import { Badge, ConfirmButton, Empty, ErrorAlert, KeyValues, Loading, Modal, PageHeader, SelectField, TextField, useToast } from '../components/ui'
import { ROLE_LABELS, TENANT_ROLES, date } from '../lib/format'

export function Users() {
  const { data, isPending, error } = useUsers()
  const tenant = useTenant()
  const deactivate = useDeactivateUser()
  const [creating, setCreating] = useState(false)

  return (
    <>
      <PageHeader title="Usuarios" subtitle={tenant.data ? `${tenant.data.name} · entorno ${tenant.data.environment === 'Production' ? 'producción' : 'pruebas'}` : undefined}>
        <button className="btn primary" type="button" onClick={() => setCreating(true)}>Nuevo usuario</button>
      </PageHeader>
      <div className="card">
        <ErrorAlert error={error ?? deactivate.error} />
        {isPending ? <Loading /> : data && data.length > 0 ? (
          <div className="table-wrap">
            <table className="table">
              <thead><tr><th>Nombre</th><th>Correo</th><th>Roles</th><th>2FA</th><th>Estado</th><th /></tr></thead>
              <tbody>
                {data.map((user) => (
                  <tr key={user.id}>
                    <td>{user.displayName}</td>
                    <td>{user.email}</td>
                    <td>{user.roles.map((role) => ROLE_LABELS[role] ?? role).join(', ')}</td>
                    <td><Badge tone={user.mfaEnabled ? 'ok' : 'neutral'}>{user.mfaEnabled ? 'Activo' : 'No'}</Badge></td>
                    <td><Badge tone={user.isActive ? 'ok' : 'neutral'}>{user.isActive ? 'Activo' : 'Inactivo'}</Badge></td>
                    <td className="right">{user.isActive && <ConfirmButton label="Desactivar" message={`¿Desactivar a ${user.displayName}? Sus sesiones se cerrarán.`} onConfirm={() => deactivate.mutate(user.id)} />}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : <Empty>No hay usuarios.</Empty>}
      </div>
      {creating && <UserModal onClose={() => setCreating(false)} />}
    </>
  )
}

function UserModal({ onClose }: { onClose: () => void }) {
  const create = useCreateUser()
  const toast = useToast()
  const [value, setValue] = useState({ email: '', displayName: '', password: '', role: 'BillingAdmin' })

  function submit(event: FormEvent) {
    event.preventDefault()
    create.mutate({ email: value.email.trim(), displayName: value.displayName.trim(), password: value.password, roles: [value.role] }, { onSuccess: () => { toast.ok('Usuario creado.'); onClose() } })
  }

  return (
    <Modal title="Nuevo usuario" onClose={onClose}>
      <form className="stack" onSubmit={submit}>
        <ErrorAlert error={create.error} />
        <TextField label="Nombre" required value={value.displayName} onChange={(event) => setValue({ ...value, displayName: event.target.value })} />
        <TextField label="Correo electrónico" type="email" required autoComplete="off" value={value.email} onChange={(event) => setValue({ ...value, email: event.target.value })} />
        <TextField label="Contraseña inicial" type="password" required minLength={12} autoComplete="new-password" hint="mínimo 12 caracteres" value={value.password} onChange={(event) => setValue({ ...value, password: event.target.value })} />
        <SelectField label="Rol" value={value.role} onChange={(event) => setValue({ ...value, role: event.target.value })}>
          {TENANT_ROLES.map((role) => <option key={role} value={role}>{ROLE_LABELS[role]}</option>)}
        </SelectField>
        <div className="actions">
          <button className="btn" type="button" onClick={onClose}>Cancelar</button>
          <button className="btn primary" type="submit" disabled={create.isPending}>Crear usuario</button>
        </div>
      </form>
    </Modal>
  )
}

interface Enrollment {
  secret: string
  otpAuthUri: string
}

export function Security() {
  const toast = useToast()
  const [enrollment, setEnrollment] = useState<Enrollment | null>(null)
  const [code, setCode] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<unknown>(null)
  const [enabled, setEnabled] = useState(false)

  async function begin() {
    setBusy(true)
    setError(null)
    try {
      setEnrollment(await post<Enrollment>('/api/v1/auth/mfa/enroll'))
    } catch (failure) {
      setError(failure)
    } finally {
      setBusy(false)
    }
  }

  async function confirm(event: FormEvent) {
    event.preventDefault()
    setBusy(true)
    setError(null)
    try {
      await post('/api/v1/auth/mfa/confirm', { code: code.trim() })
      setEnabled(true)
      setEnrollment(null)
      setCode('')
      toast.ok('Verificación en dos pasos activada.')
    } catch (failure) {
      setError(failure)
    } finally {
      setBusy(false)
    }
  }

  return (
    <>
      <PageHeader title="Seguridad" subtitle="Proteja su cuenta con un segundo factor" />
      <div className="card stack">
        <h2>Verificación en dos pasos</h2>
        <ErrorAlert error={error} />
        {enabled && <div className="alert ok">Activada. Desde el próximo ingreso se le pedirá el código de su aplicación de autenticación.</div>}
        {!enrollment ? (
          <div>
            <p className="muted">Use una aplicación de autenticación (Google Authenticator, Microsoft Authenticator, Authy…).</p>
            <button className="btn primary" type="button" disabled={busy} onClick={() => void begin()}>Configurar verificación</button>
          </div>
        ) : (
          <form className="stack" onSubmit={(event) => void confirm(event)}>
            <p>Agregue esta cuenta en su aplicación con la clave secreta o el enlace y escriba el código de 6 dígitos que muestra.</p>
            <KeyValues items={[['Clave secreta', <span className="mono" key="s">{enrollment.secret}</span>], ['Enlace', <a key="u" href={enrollment.otpAuthUri}>Abrir en la aplicación</a>]]} />
            <TextField label="Código de 6 dígitos" required inputMode="numeric" maxLength={6} autoComplete="one-time-code" value={code} onChange={(event) => setCode(event.target.value)} />
            <div><button className="btn primary" type="submit" disabled={busy}>Confirmar</button></div>
          </form>
        )}
      </div>
    </>
  )
}

export function Rules() {
  const { data, isPending, error } = useRules()
  return (
    <>
      <PageHeader title="Reglas tributarias" subtitle="Valores vigentes, con su fuente y su estado de verificación" />
      <div className="card">
        <ErrorAlert error={error} />
        {isPending ? <Loading /> : (
          <div className="table-wrap">
            <table className="table">
              <thead><tr><th>Regla</th><th>Vigente desde</th><th>Valor</th><th>Verificación</th><th>Fuente</th></tr></thead>
              <tbody>
                {data?.map((rule) => (
                  <tr key={`${rule.code}-${rule.version}`}>
                    <td className="mono">{rule.code}</td>
                    <td className="tight">{rule.effectiveFrom.startsWith('1900') ? '—' : date(rule.effectiveFrom)}</td>
                    <td className="mono">{rule.configurationJson}</td>
                    <td><Badge tone={rule.verification === 'Verified' ? 'ok' : 'warn'}>{rule.verification === 'Verified' ? 'Verificada' : 'Pendiente'}</Badge></td>
                    <td className="muted">{rule.source}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
      <p className="muted">Una regla «Pendiente» aún no se confirmó en una fuente oficial.</p>
    </>
  )
}
