import { useState, type FormEvent } from 'react'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { ApiError } from '../api/http'
import {
  useChangeTenantStatus,
  useCreateTenant,
  useCreateTenantUser,
  useDeactivateTenantUser,
  usePlatformTenant,
  useRevokeSessions,
  useTenants,
  useTenantUsers,
} from '../api/queries'
import { api } from '../api/http'
import type { TenantRow, TenantStatus } from '../api/types'
import { useSession } from '../auth/session'
import { Badge, ConfirmButton, Empty, ErrorAlert, KeyValues, Loading, Modal, PageHeader, SelectField, TextAreaField, TextField, useToast } from '../components/ui'
import { ROLE_LABELS, TENANT_ROLES, dateTime } from '../lib/format'
import { TenantBillingCard } from './Billing'
import { TenantPlanCard } from './Plans'
import { TenantResellerCard } from './Resellers'
import type { Tone } from '../lib/format'

const STATUS_LABELS: Record<TenantStatus, string> = { Active: 'Activo', Suspended: 'Suspendido', Closed: 'Cerrado' }
const STATUS_TONES: Record<TenantStatus, Tone> = { Active: 'ok', Suspended: 'warn', Closed: 'bad' }
const ENVIRONMENTS = { Sandbox: 'Pruebas', Production: 'Producción' } as const

export function TenantStatusBadge({ status }: { status: TenantStatus }) {
  return <Badge tone={STATUS_TONES[status]}>{STATUS_LABELS[status]}</Badge>
}

export function Tenants() {
  const { hasRole } = useSession()
  const [search, setSearch] = useState('')
  const [status, setStatus] = useState('')
  const { data, isPending, error } = useTenants(search, status)
  const [creating, setCreating] = useState(false)
  const navigate = useNavigate()

  return (
    <>
      <PageHeader title="Inquilinos" subtitle="Cuentas de la plataforma">
        {hasRole('PlatformSuperAdmin') && (
          <button className="btn primary" type="button" onClick={() => setCreating(true)}>
            Nuevo inquilino
          </button>
        )}
      </PageHeader>
      <div className="card">
        <div className="row" style={{ marginBottom: 12 }}>
          <input className="input" type="search" aria-label="Buscar por nombre" placeholder="Buscar por nombre" style={{ maxWidth: 320 }} value={search} onChange={(event) => setSearch(event.target.value)} />
          <select className="input" aria-label="Estado" style={{ maxWidth: 200 }} value={status} onChange={(event) => setStatus(event.target.value)}>
            <option value="">Todos los estados</option>
            {Object.entries(STATUS_LABELS).map(([value, label]) => (
              <option key={value} value={value}>
                {label}
              </option>
            ))}
          </select>
        </div>
        <ErrorAlert error={error} />
        {isPending ? (
          <Loading />
        ) : data && data.length > 0 ? (
          <div className="table-wrap">
            <table className="table">
              <thead>
                <tr>
                  <th>Nombre</th>
                  <th>Entorno</th>
                  <th>Estado</th>
                  <th>Creado</th>
                </tr>
              </thead>
              <tbody>
                {data.map((tenant) => (
                  <tr key={tenant.id} className="clickable" onClick={() => void navigate(`/plataforma/inquilinos/${tenant.id}`)}>
                    <td>
                      <Link to={`/plataforma/inquilinos/${tenant.id}`}>{tenant.name}</Link>
                    </td>
                    <td>{ENVIRONMENTS[tenant.environment]}</td>
                    <td>
                      <TenantStatusBadge status={tenant.status} />
                    </td>
                    <td className="tight">{dateTime(tenant.createdAt)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : (
          <Empty>No hay inquilinos que coincidan.</Empty>
        )}
      </div>
      {creating && <CreateTenantModal onClose={() => setCreating(false)} />}
    </>
  )
}

/** Creates the tenant and then its first owner: two calls, so a failure of the second leaves the tenant ready to receive its owner from the detail page. */
function CreateTenantModal({ onClose }: { onClose: () => void }) {
  const createTenant = useCreateTenant()
  const navigate = useNavigate()
  const toast = useToast()
  const [value, setValue] = useState({ name: '', environment: 'Sandbox' as 'Sandbox' | 'Production', ownerName: '', ownerEmail: '', ownerPassword: '' })
  const [error, setError] = useState<unknown>(null)
  const [busy, setBusy] = useState(false)

  async function submit(event: FormEvent) {
    event.preventDefault()
    setBusy(true)
    setError(null)
    let tenant: TenantRow
    try {
      tenant = await createTenant.mutateAsync({ name: value.name.trim(), environment: value.environment })
    } catch (failure) {
      setError(failure)
      setBusy(false)
      return
    }
    try {
      await api('POST', '/api/v1/users', { body: { email: value.ownerEmail.trim(), displayName: value.ownerName.trim(), password: value.ownerPassword, roles: ['TenantOwner'], tenantId: tenant.id } })
      toast.ok('Inquilino creado con su propietario.')
    } catch (failure) {
      toast.fail(new Error(`El inquilino se creó, pero no su propietario: ${failure instanceof ApiError ? failure.message : 'error inesperado'}. Agréguelo desde su detalle.`))
    }
    onClose()
    void navigate(`/plataforma/inquilinos/${tenant.id}`)
  }

  return (
    <Modal title="Nuevo inquilino" onClose={onClose}>
      <form className="stack" onSubmit={(event) => void submit(event)}>
        <ErrorAlert error={error} />
        <TextField label="Nombre de la cuenta" required minLength={3} maxLength={120} value={value.name} onChange={(event) => setValue({ ...value, name: event.target.value })} />
        <SelectField label="Entorno" value={value.environment} onChange={(event) => setValue({ ...value, environment: event.target.value as 'Sandbox' | 'Production' })}>
          <option value="Sandbox">Pruebas</option>
          <option value="Production">Producción</option>
        </SelectField>
        <h3>Propietario</h3>
        <TextField label="Nombre del propietario" required value={value.ownerName} onChange={(event) => setValue({ ...value, ownerName: event.target.value })} />
        <TextField label="Correo del propietario" type="email" required autoComplete="off" value={value.ownerEmail} onChange={(event) => setValue({ ...value, ownerEmail: event.target.value })} />
        <TextField label="Contraseña inicial" type="password" required minLength={12} autoComplete="new-password" hint="mínimo 12 caracteres; el propietario la cambiará" value={value.ownerPassword} onChange={(event) => setValue({ ...value, ownerPassword: event.target.value })} />
        <div className="actions">
          <button className="btn" type="button" onClick={onClose}>
            Cancelar
          </button>
          <button className="btn primary" type="submit" disabled={busy}>
            {busy ? 'Creando…' : 'Crear inquilino'}
          </button>
        </div>
      </form>
    </Modal>
  )
}

const ACTIONS: Record<TenantStatus, { status: TenantStatus; label: string; danger: boolean; text: string }[]> = {
  Active: [
    { status: 'Suspended', label: 'Suspender', danger: false, text: 'Sus usuarios no podrán ingresar ni usar la API hasta que lo reactive. No se pierde nada.' },
    { status: 'Closed', label: 'Cerrar cuenta', danger: true, text: 'El cierre es definitivo: no se puede reactivar. Los documentos y la auditoría se conservan.' },
  ],
  Suspended: [
    { status: 'Active', label: 'Reactivar', danger: false, text: 'Sus usuarios vuelven a poder ingresar y usar la API.' },
    { status: 'Closed', label: 'Cerrar cuenta', danger: true, text: 'El cierre es definitivo: no se puede reactivar. Los documentos y la auditoría se conservan.' },
  ],
  Closed: [],
}

/** What the platform can do with a tenant. A suspension of the reseller can also be taken over, so that the reseller cannot lift it. */
function actionsOf(tenant: TenantRow): (typeof ACTIONS)[TenantStatus] {
  const takeover = { status: 'Suspended' as const, label: 'Suspender por la plataforma', danger: false, text: 'La suspensión pasa a ser de la plataforma: el revendedor ya no podrá reactivar la cuenta.' }
  return tenant.status === 'Suspended' && tenant.suspendedBy === 'Reseller' ? [...ACTIONS.Suspended, takeover] : ACTIONS[tenant.status]
}

export function TenantDetail() {
  const { id = '' } = useParams()
  const { hasRole } = useSession()
  const tenant = usePlatformTenant(id)
  const users = useTenantUsers(id)
  const revoke = useRevokeSessions()
  const deactivate = useDeactivateTenantUser(id)
  const toast = useToast()
  const [change, setChange] = useState<(typeof ACTIONS)[TenantStatus][number] | null>(null)
  const [adding, setAdding] = useState(false)
  const canManage = hasRole('PlatformSuperAdmin')

  if (tenant.isPending) return <Loading />
  if (!tenant.data) return <ErrorAlert error={tenant.error} />
  const t = tenant.data

  return (
    <>
      <PageHeader title={t.name} subtitle={`Entorno ${ENVIRONMENTS[t.environment].toLowerCase()} · creado el ${dateTime(t.createdAt)}`}>
        <TenantStatusBadge status={t.status} />
      </PageHeader>
      {t.status !== 'Active' && (
        <div className={`alert ${t.status === 'Closed' ? 'bad' : 'warn'}`} role="status">
          {t.status === 'Closed'
            ? 'Cuenta cerrada: sus usuarios no pueden ingresar y no se puede reactivar.'
            : t.suspendedBy === 'Reseller'
              ? 'Cuenta suspendida por su revendedor: sus usuarios no pueden ingresar ni usar la API. Usted puede reactivarla, o suspenderla por la plataforma para que el revendedor no pueda levantarla.'
              : 'Cuenta suspendida: sus usuarios no pueden ingresar ni usar la API.'}
        </div>
      )}
      <div className="card">
        <h2>Cuenta</h2>
        <KeyValues items={[['Identificador', <span className="mono" key="i">{t.id}</span>], ['Estado', STATUS_LABELS[t.status]], ['Entorno', ENVIRONMENTS[t.environment]]]} />
        {canManage && actionsOf(t).length > 0 && (
          <div className="actions" style={{ marginTop: 14 }}>
            {actionsOf(t).map((action) => (
              <button key={action.status} className={`btn${action.danger ? ' danger' : ''}`} type="button" onClick={() => setChange(action)}>
                {action.label}
              </button>
            ))}
          </div>
        )}
        <p style={{ marginTop: 14 }}>
          <Link to={`/auditoria?tenantId=${t.id}`}>Ver la auditoría de esta cuenta</Link>
        </p>
      </div>

      <TenantPlanCard tenantId={t.id} canManage={canManage} closed={t.status === 'Closed'} />
      <TenantBillingCard tenantId={t.id} />
      <TenantResellerCard tenant={t} canManage={canManage} />

      <div className="card">
        <div className="row spread" style={{ marginBottom: 8 }}>
          <h2>Usuarios</h2>
          {canManage && t.status !== 'Closed' && (
            <button className="btn small" type="button" onClick={() => setAdding(true)}>
              Agregar usuario
            </button>
          )}
        </div>
        <ErrorAlert error={users.error ?? revoke.error ?? deactivate.error} />
        {users.isPending ? (
          <Loading />
        ) : users.data && users.data.length > 0 ? (
          <div className="table-wrap">
            <table className="table">
              <thead>
                <tr>
                  <th>Nombre</th>
                  <th>Correo</th>
                  <th>Roles</th>
                  <th>2FA</th>
                  <th>Estado</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {users.data.map((user) => (
                  <tr key={user.id}>
                    <td>{user.displayName}</td>
                    <td>{user.email}</td>
                    <td>{user.roles.map((role) => ROLE_LABELS[role] ?? role).join(', ')}</td>
                    <td>
                      <Badge tone={user.mfaEnabled ? 'ok' : 'neutral'}>{user.mfaEnabled ? 'Activo' : 'No'}</Badge>
                    </td>
                    <td>
                      <Badge tone={user.isActive ? 'ok' : 'neutral'}>{user.isActive ? 'Activo' : 'Inactivo'}</Badge>
                    </td>
                    <td className="right tight">
                      {canManage && user.isActive && (
                        <>
                          <ConfirmButton label="Cerrar sesiones" message={`¿Cerrar todas las sesiones de ${user.displayName}?`} onConfirm={() => revoke.mutate(user.id, { onSuccess: () => toast.ok('Sesiones cerradas.') })} />{' '}
                          <ConfirmButton label="Desactivar" message={`¿Desactivar a ${user.displayName}?`} onConfirm={() => deactivate.mutate(user.id)} />
                        </>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : (
          <Empty>Esta cuenta aún no tiene usuarios.</Empty>
        )}
      </div>

      {change && <StatusModal tenant={t} action={change} onClose={() => setChange(null)} />}
      {adding && <AddUserModal tenantId={t.id} onClose={() => setAdding(false)} />}
    </>
  )
}

function StatusModal({ tenant, action, onClose }: { tenant: TenantRow; action: (typeof ACTIONS)[TenantStatus][number]; onClose: () => void }) {
  const change = useChangeTenantStatus(tenant.id)
  const toast = useToast()
  const [reason, setReason] = useState('')
  const [typed, setTyped] = useState('')
  // A closing cannot be undone: the name of the account has to be typed.
  const confirmed = action.status !== 'Closed' || typed.trim() === tenant.name

  return (
    <Modal title={`${action.label}: ${tenant.name}`} onClose={onClose}>
      <form
        className="stack"
        onSubmit={(event) => {
          event.preventDefault()
          change.mutate({ status: action.status, reason: reason.trim() }, { onSuccess: () => { toast.ok('Estado cambiado.'); onClose() } })
        }}
      >
        <p>{action.text}</p>
        <ErrorAlert error={change.error} />
        <TextAreaField label="Motivo" hint="queda en la auditoría" required minLength={3} maxLength={300} value={reason} onChange={(event) => setReason(event.target.value)} />
        {action.status === 'Closed' && <TextField label={`Escriba «${tenant.name}» para confirmar`} autoComplete="off" value={typed} onChange={(event) => setTyped(event.target.value)} />}
        <div className="actions">
          <button className="btn" type="button" onClick={onClose}>
            Cancelar
          </button>
          <button className={`btn ${action.danger ? 'danger' : 'primary'}`} type="submit" disabled={change.isPending || !confirmed}>
            {action.label}
          </button>
        </div>
      </form>
    </Modal>
  )
}

function AddUserModal({ tenantId, onClose }: { tenantId: string; onClose: () => void }) {
  const create = useCreateTenantUser(tenantId)
  const toast = useToast()
  const [value, setValue] = useState({ email: '', displayName: '', password: '', role: 'TenantOwner' })
  return (
    <Modal title="Agregar usuario" onClose={onClose}>
      <form
        className="stack"
        onSubmit={(event) => {
          event.preventDefault()
          create.mutate({ email: value.email.trim(), displayName: value.displayName.trim(), password: value.password, roles: [value.role] }, { onSuccess: () => { toast.ok('Usuario creado.'); onClose() } })
        }}
      >
        <ErrorAlert error={create.error} />
        <TextField label="Nombre" required value={value.displayName} onChange={(event) => setValue({ ...value, displayName: event.target.value })} />
        <TextField label="Correo electrónico" type="email" required autoComplete="off" value={value.email} onChange={(event) => setValue({ ...value, email: event.target.value })} />
        <TextField label="Contraseña inicial" type="password" required minLength={12} autoComplete="new-password" hint="mínimo 12 caracteres" value={value.password} onChange={(event) => setValue({ ...value, password: event.target.value })} />
        <SelectField label="Rol" value={value.role} onChange={(event) => setValue({ ...value, role: event.target.value })}>
          {['TenantOwner', ...TENANT_ROLES].map((role) => (
            <option key={role} value={role}>
              {ROLE_LABELS[role]}
            </option>
          ))}
        </SelectField>
        <div className="actions">
          <button className="btn" type="button" onClick={onClose}>
            Cancelar
          </button>
          <button className="btn primary" type="submit" disabled={create.isPending}>
            Crear usuario
          </button>
        </div>
      </form>
    </Modal>
  )
}
