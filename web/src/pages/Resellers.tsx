import { useState, type FormEvent } from 'react'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { ApiError, api } from '../api/http'
import {
  useAddOwner,
  useAssignReseller,
  useChangeResellerPlan,
  useChangeResellerStatus,
  useCreateReseller,
  useCreateResellerUser,
  useOpenTenant,
  useResellerPlans,
  useResellerTenant,
  useResellerTenants,
  useResellerUsage,
  useResellers,
  useUpdateReseller,
} from '../api/queries'
import type { ResellerRow, TenantRow } from '../api/types'
import { useSession } from '../auth/session'
import { Badge, Empty, ErrorAlert, KeyValues, Loading, Modal, PageHeader, SelectField, TextAreaField, TextField, useToast } from '../components/ui'
import { dateTime } from '../lib/format'
import { BrandEditor } from './Brand'
import { CommissionsModal } from './Billing'
import { UsagePanel } from './Plans'
import { TenantStatusBadge } from './Platform'

const ENVIRONMENTS = { Sandbox: 'Pruebas', Production: 'Producción' } as const

// ---------- platform staff ----------

/** The resellers of the platform. */
export function Resellers() {
  const { hasRole } = useSession()
  const { data, isPending, error } = useResellers()
  const [editing, setEditing] = useState<ResellerRow | 'new' | null>(null)
  const [adding, setAdding] = useState<ResellerRow | null>(null)
  const [branding, setBranding] = useState<ResellerRow | null>(null)
  const [commissions, setCommissions] = useState<ResellerRow | null>(null)
  const canManage = hasRole('PlatformSuperAdmin')

  return (
    <>
      <PageHeader title="Revendedores" subtitle="Quienes traen y administran cuentas de sus clientes">
        {canManage && (
          <button className="btn primary" type="button" onClick={() => setEditing('new')}>
            Nuevo revendedor
          </button>
        )}
      </PageHeader>
      <div className="card">
        <ErrorAlert error={error} />
        {isPending ? (
          <Loading />
        ) : data && data.length > 0 ? (
          <div className="table-wrap">
            <table className="table">
              <thead>
                <tr>
                  <th>Nombre</th>
                  <th className="right">Cuentas</th>
                  <th>Estado</th>
                  <th>Creado</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {data.map((reseller) => (
                  <tr key={reseller.id}>
                    <td>{reseller.name}</td>
                    <td className="right">{reseller.tenantCount}</td>
                    <td>
                      <Badge tone={reseller.isActive ? 'ok' : 'neutral'}>{reseller.isActive ? 'Activo' : 'Desactivado'}</Badge>
                    </td>
                    <td className="tight">{dateTime(reseller.createdAt)}</td>
                    <td className="right tight">
                      {canManage && (
                        <>
                          <button className="btn small" type="button" aria-label={`Editar ${reseller.name}`} onClick={() => setEditing(reseller)}>
                            Editar
                          </button>{' '}
                          <button className="btn small" type="button" aria-label={`Agregar administrador a ${reseller.name}`} onClick={() => setAdding(reseller)}>
                            Agregar administrador
                          </button>{' '}
                        </>
                      )}
                      <button className="btn small" type="button" aria-label={`Comisiones de ${reseller.name}`} onClick={() => setCommissions(reseller)}>
                        Comisiones
                      </button>{' '}
                      <button className="btn small" type="button" aria-label={`Marca de ${reseller.name}`} onClick={() => setBranding(reseller)}>
                        Marca
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : (
          <Empty>Aún no hay revendedores.</Empty>
        )}
      </div>
      {editing && <ResellerModal reseller={editing === 'new' ? null : editing} onClose={() => setEditing(null)} />}
      {adding && <ResellerUserModal reseller={adding} onClose={() => setAdding(null)} />}
      {commissions && <CommissionsModal reseller={commissions} onClose={() => setCommissions(null)} />}
      {branding && (
        <Modal title={`Marca de ${branding.name}`} onClose={() => setBranding(null)}>
          <BrandEditor scope={{ kind: 'platform', resellerId: branding.id }} canSetHost={canManage} />
        </Modal>
      )}
    </>
  )
}

function ResellerModal({ reseller, onClose }: { reseller: ResellerRow | null; onClose: () => void }) {
  const create = useCreateReseller()
  const update = useUpdateReseller(reseller?.id ?? '')
  const toast = useToast()
  const [name, setName] = useState(reseller?.name ?? '')
  const [isActive, setIsActive] = useState(reseller?.isActive ?? true)
  const mutation = reseller ? update : create

  return (
    <Modal title={reseller ? `Editar ${reseller.name}` : 'Nuevo revendedor'} onClose={onClose}>
      <form
        className="stack"
        onSubmit={(event) => {
          event.preventDefault()
          const done = { onSuccess: () => { toast.ok(reseller ? 'Revendedor actualizado.' : 'Revendedor creado.'); onClose() } }
          if (reseller) update.mutate({ name: name.trim(), isActive }, done)
          else create.mutate(name.trim(), done)
        }}
      >
        <ErrorAlert error={mutation.error} />
        <TextField label="Nombre" required minLength={3} maxLength={120} value={name} onChange={(event) => setName(event.target.value)} />
        {reseller && (
          <label className="checkbox">
            <input type="checkbox" checked={isActive} onChange={(event) => setIsActive(event.target.checked)} /> Activo
          </label>
        )}
        {reseller && !isActive && <p className="hint">Un revendedor desactivado no puede ingresar ni actuar. Las cuentas de sus clientes siguen funcionando.</p>}
        <div className="actions">
          <button className="btn" type="button" onClick={onClose}>
            Cancelar
          </button>
          <button className="btn primary" type="submit" disabled={mutation.isPending}>
            {reseller ? 'Guardar' : 'Crear revendedor'}
          </button>
        </div>
      </form>
    </Modal>
  )
}

function ResellerUserModal({ reseller, onClose }: { reseller: ResellerRow; onClose: () => void }) {
  const create = useCreateResellerUser(reseller.id)
  const toast = useToast()
  const [value, setValue] = useState({ displayName: '', email: '', password: '' })
  return (
    <Modal title={`Administrador de ${reseller.name}`} onClose={onClose}>
      <form
        className="stack"
        onSubmit={(event) => {
          event.preventDefault()
          create.mutate({ displayName: value.displayName.trim(), email: value.email.trim(), password: value.password }, { onSuccess: () => { toast.ok('Administrador creado.'); onClose() } })
        }}
      >
        <ErrorAlert error={create.error} />
        <TextField label="Nombre" required value={value.displayName} onChange={(event) => setValue({ ...value, displayName: event.target.value })} />
        <TextField label="Correo electrónico" type="email" required autoComplete="off" value={value.email} onChange={(event) => setValue({ ...value, email: event.target.value })} />
        <TextField label="Contraseña inicial" type="password" required minLength={12} autoComplete="new-password" hint="mínimo 12 caracteres" value={value.password} onChange={(event) => setValue({ ...value, password: event.target.value })} />
        <div className="actions">
          <button className="btn" type="button" onClick={onClose}>
            Cancelar
          </button>
          <button className="btn primary" type="submit" disabled={create.isPending}>
            Crear administrador
          </button>
        </div>
      </form>
    </Modal>
  )
}

/** The reseller of a tenant, for platform staff, with the change (super administrator). */
export function TenantResellerCard({ tenant, canManage }: { tenant: TenantRow; canManage: boolean }) {
  const resellers = useResellers()
  const assign = useAssignReseller(tenant.id)
  const toast = useToast()
  const [choice, setChoice] = useState<string | null>(null)
  const current = (resellers.data ?? []).find((reseller) => reseller.id === tenant.resellerId)
  const selected = choice ?? tenant.resellerId ?? ''

  return (
    <div className="card">
      <h2>Revendedor</h2>
      <ErrorAlert error={resellers.error ?? assign.error} />
      <p>{tenant.resellerId ? <>Esta cuenta la administra <strong>{current?.name ?? 'un revendedor'}</strong>.</> : 'Cuenta directa de la plataforma, sin revendedor.'}</p>
      {canManage && tenant.status !== 'Closed' && (
        <form
          className="row"
          style={{ alignItems: 'flex-end' }}
          onSubmit={(event) => {
            event.preventDefault()
            assign.mutate(selected || null, { onSuccess: () => { toast.ok('Revendedor cambiado.'); setChoice(null) } })
          }}
        >
          <SelectField label="Asignar a" value={selected} onChange={(event) => setChoice(event.target.value)}>
            <option value="">Ninguno (cuenta directa)</option>
            {(resellers.data ?? []).filter((reseller) => reseller.isActive || reseller.id === tenant.resellerId).map((reseller) => (
              <option key={reseller.id} value={reseller.id}>
                {reseller.name}
              </option>
            ))}
          </SelectField>
          <button className="btn primary" type="submit" disabled={assign.isPending || selected === (tenant.resellerId ?? '')}>
            Cambiar revendedor
          </button>
        </form>
      )}
    </div>
  )
}

// ---------- the reseller's own screens ----------

/** The accounts of the reseller. */
export function ResellerAccounts() {
  const [search, setSearch] = useState('')
  const { data, isPending, error } = useResellerTenants(search)
  const [opening, setOpening] = useState(false)
  const navigate = useNavigate()

  return (
    <>
      <PageHeader title="Mis cuentas" subtitle="Las cuentas de sus clientes">
        <button className="btn primary" type="button" onClick={() => setOpening(true)}>
          Nueva cuenta
        </button>
      </PageHeader>
      <div className="card">
        <div className="row" style={{ marginBottom: 12 }}>
          <input className="input" type="search" aria-label="Buscar por nombre" placeholder="Buscar por nombre" style={{ maxWidth: 320 }} value={search} onChange={(event) => setSearch(event.target.value)} />
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
                  <th>Creada</th>
                </tr>
              </thead>
              <tbody>
                {data.map((tenant) => (
                  <tr key={tenant.id} className="clickable" onClick={() => void navigate(`/revendedor/cuentas/${tenant.id}`)}>
                    <td>
                      <Link to={`/revendedor/cuentas/${tenant.id}`}>{tenant.name}</Link>
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
          <Empty>Aún no tiene cuentas. Abra la primera con «Nueva cuenta».</Empty>
        )}
      </div>
      {opening && <OpenAccountModal onClose={() => setOpening(false)} />}
    </>
  )
}

/** Opens the account and then its owner: two calls, so a failure of the second leaves the account ready to receive its owner from its detail. */
function OpenAccountModal({ onClose }: { onClose: () => void }) {
  const open = useOpenTenant()
  const plans = useResellerPlans()
  const navigate = useNavigate()
  const toast = useToast()
  const [value, setValue] = useState({ name: '', environment: 'Sandbox' as 'Sandbox' | 'Production', planId: '', ownerName: '', ownerEmail: '', ownerPassword: '' })
  const [error, setError] = useState<unknown>(null)
  const [busy, setBusy] = useState(false)

  async function submit(event: FormEvent) {
    event.preventDefault()
    setBusy(true)
    setError(null)
    let tenant: TenantRow
    try {
      tenant = await open.mutateAsync({ name: value.name.trim(), environment: value.environment, planId: value.planId || null })
    } catch (failure) {
      setError(failure)
      setBusy(false)
      return
    }
    try {
      await api('POST', `/api/v1/reseller/tenants/${tenant.id}/owner`, { body: { email: value.ownerEmail.trim(), displayName: value.ownerName.trim(), password: value.ownerPassword } })
      toast.ok('Cuenta creada con su propietario.')
    } catch (failure) {
      toast.fail(new Error(`La cuenta se creó, pero no su propietario: ${failure instanceof ApiError ? failure.message : 'error inesperado'}. Créelo desde su detalle.`))
    }
    onClose()
    void navigate(`/revendedor/cuentas/${tenant.id}`)
  }

  return (
    <Modal title="Nueva cuenta" onClose={onClose}>
      <form className="stack" onSubmit={(event) => void submit(event)}>
        <ErrorAlert error={error ?? plans.error} />
        <TextField label="Nombre de la cuenta" required minLength={3} maxLength={120} value={value.name} onChange={(event) => setValue({ ...value, name: event.target.value })} />
        <SelectField label="Entorno" value={value.environment} onChange={(event) => setValue({ ...value, environment: event.target.value as 'Sandbox' | 'Production' })}>
          <option value="Sandbox">Pruebas</option>
          <option value="Production">Producción</option>
        </SelectField>
        <SelectField label="Plan" value={value.planId} onChange={(event) => setValue({ ...value, planId: event.target.value })}>
          <option value="">Piloto (sin límites)</option>
          {(plans.data ?? []).filter((plan) => plan.code !== 'pilot').map((plan) => (
            <option key={plan.id} value={plan.id}>
              {plan.name} ({plan.code})
            </option>
          ))}
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
            {busy ? 'Creando…' : 'Crear cuenta'}
          </button>
        </div>
      </form>
    </Modal>
  )
}

/** One account of the reseller: its state, its plan and consumption, and its owner. */
export function ResellerAccountDetail() {
  const { id = '' } = useParams()
  const tenant = useResellerTenant(id)
  const usage = useResellerUsage(id)
  const plans = useResellerPlans()
  const change = useChangeResellerPlan(id)
  const toast = useToast()
  const [choice, setChoice] = useState('')
  const [owner, setOwner] = useState(false)
  const [statusChange, setStatusChange] = useState<'Suspended' | 'Active' | null>(null)

  if (tenant.isPending) return <Loading />
  if (!tenant.data) return <ErrorAlert error={tenant.error} />
  const t = tenant.data
  const current = usage.data?.plan.id

  return (
    <>
      <PageHeader title={t.name} subtitle={`Entorno ${ENVIRONMENTS[t.environment].toLowerCase()} · creada el ${dateTime(t.createdAt)}`}>
        <TenantStatusBadge status={t.status} />
      </PageHeader>
      {t.status === 'Closed' && (
        <div className="alert bad" role="status">
          Esta cuenta está cerrada por la plataforma y no se puede reactivar.
        </div>
      )}
      {t.status === 'Suspended' && (
        <div className="alert warn" role="status">
          {t.suspendedBy === 'Reseller'
            ? 'Usted suspendió esta cuenta: sus usuarios no pueden ingresar ni usar la API.'
            : 'La plataforma suspendió esta cuenta y solo ella puede reactivarla. Para hacerlo, comuníquese con soporte.'}
        </div>
      )}
      <div className="card">
        <h2>Cuenta</h2>
        <KeyValues items={[['Identificador', <span className="mono" key="i">{t.id}</span>], ['Entorno', ENVIRONMENTS[t.environment]]]} />
        {(t.status === 'Active' || (t.status === 'Suspended' && t.suspendedBy === 'Reseller')) && (
          <div className="actions" style={{ marginTop: 14 }}>
            <button className="btn" type="button" onClick={() => setStatusChange(t.status === 'Active' ? 'Suspended' : 'Active')}>
              {t.status === 'Active' ? 'Suspender' : 'Reactivar'}
            </button>
          </div>
        )}
      </div>

      <div className="card">
        <h2>Plan y consumo</h2>
        <ErrorAlert error={usage.error ?? change.error ?? plans.error} />
        {usage.isPending ? <Loading /> : usage.data && <UsagePanel usage={usage.data} />}
        {t.status !== 'Closed' && (
          <form
            className="row"
            style={{ marginTop: 14, alignItems: 'flex-end' }}
            onSubmit={(event) => {
              event.preventDefault()
              if (choice) change.mutate(choice, { onSuccess: () => { toast.ok('Plan cambiado.'); setChoice('') } })
            }}
          >
            <SelectField label="Cambiar a" value={choice} onChange={(event) => setChoice(event.target.value)}>
              <option value="">Elija un plan</option>
              {(plans.data ?? []).filter((plan) => plan.id !== current).map((plan) => (
                <option key={plan.id} value={plan.id}>
                  {plan.name} ({plan.code})
                </option>
              ))}
            </SelectField>
            <button className="btn primary" type="submit" disabled={!choice || change.isPending}>
              Cambiar plan
            </button>
          </form>
        )}
      </div>

      <div className="card">
        <div className="row spread">
          <h2>Propietario</h2>
          <button className="btn small" type="button" onClick={() => setOwner(true)}>
            Crear propietario
          </button>
        </div>
        <p className="hint">El propietario de la cuenta se crea una sola vez; después, la cuenta administra sus propios usuarios.</p>
      </div>
      {owner && <OwnerModal tenantId={t.id} onClose={() => setOwner(false)} />}
      {statusChange && <StatusModal tenant={t} target={statusChange} onClose={() => setStatusChange(null)} />}
    </>
  )
}

/** Suspends or reactivates an account of the reseller, with the reason that stays in the audit trail. */
function StatusModal({ tenant, target, onClose }: { tenant: TenantRow; target: 'Suspended' | 'Active'; onClose: () => void }) {
  const change = useChangeResellerStatus(tenant.id)
  const toast = useToast()
  const [reason, setReason] = useState('')
  const label = target === 'Suspended' ? 'Suspender' : 'Reactivar'
  return (
    <Modal title={`${label}: ${tenant.name}`} onClose={onClose}>
      <form
        className="stack"
        onSubmit={(event) => {
          event.preventDefault()
          change.mutate({ status: target, reason: reason.trim() }, { onSuccess: () => { toast.ok('Estado cambiado.'); onClose() } })
        }}
      >
        <p>{target === 'Suspended' ? 'Los usuarios de la cuenta no podrán ingresar ni usar la API hasta que usted la reactive. No se pierde nada.' : 'Los usuarios de la cuenta vuelven a poder ingresar y usar la API.'}</p>
        <ErrorAlert error={change.error} />
        <TextAreaField label="Motivo" hint="queda en la auditoría de la cuenta" required minLength={3} maxLength={300} value={reason} onChange={(event) => setReason(event.target.value)} />
        <div className="actions">
          <button className="btn" type="button" onClick={onClose}>
            Cancelar
          </button>
          <button className={`btn ${target === 'Suspended' ? 'danger' : 'primary'}`} type="submit" disabled={change.isPending}>
            {label}
          </button>
        </div>
      </form>
    </Modal>
  )
}

function OwnerModal({ tenantId, onClose }: { tenantId: string; onClose: () => void }) {
  const add = useAddOwner(tenantId)
  const toast = useToast()
  const [value, setValue] = useState({ displayName: '', email: '', password: '' })
  return (
    <Modal title="Crear propietario" onClose={onClose}>
      <form
        className="stack"
        onSubmit={(event) => {
          event.preventDefault()
          add.mutate({ displayName: value.displayName.trim(), email: value.email.trim(), password: value.password }, { onSuccess: () => { toast.ok('Propietario creado.'); onClose() } })
        }}
      >
        <ErrorAlert error={add.error} />
        <TextField label="Nombre del propietario" required value={value.displayName} onChange={(event) => setValue({ ...value, displayName: event.target.value })} />
        <TextField label="Correo del propietario" type="email" required autoComplete="off" value={value.email} onChange={(event) => setValue({ ...value, email: event.target.value })} />
        <TextField label="Contraseña inicial" type="password" required minLength={12} autoComplete="new-password" hint="mínimo 12 caracteres" value={value.password} onChange={(event) => setValue({ ...value, password: event.target.value })} />
        <div className="actions">
          <button className="btn" type="button" onClick={onClose}>
            Cancelar
          </button>
          <button className="btn primary" type="submit" disabled={add.isPending}>
            Crear propietario
          </button>
        </div>
      </form>
    </Modal>
  )
}
