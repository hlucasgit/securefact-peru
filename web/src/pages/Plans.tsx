import { useState } from 'react'
import { useAssignPlan, useCreatePlan, useMyPlan, usePlans, useResellers, useTenantUsage, useUpdatePlan } from '../api/queries'
import type { PlanInput, PlanRow, TenantUsage, UsageItem } from '../api/types'
import { useSession } from '../auth/session'
import { MyCharges } from './Billing'
import { Badge, Empty, ErrorAlert, Loading, Modal, PageHeader, SelectField, TextField, useToast } from '../components/ui'

const limitText = (limit: number | null) => (limit === null ? 'Ilimitado' : limit.toLocaleString('es-PE'))

/** One consumption against its limit: the numbers are the text, the bar only repeats them. */
function Meter({ label, item }: { label: string; item: UsageItem }) {
  const full = item.limit !== null && item.used >= item.limit
  return (
    <div className="meter">
      <div className="row spread">
        <span>{label}</span>
        <span>
          <strong>{item.used.toLocaleString('es-PE')}</strong> de {limitText(item.limit)}{' '}
          {full && <Badge tone="warn">Límite alcanzado</Badge>}
        </span>
      </div>
      {item.limit !== null && item.limit > 0 && <progress aria-label={`${label}: consumo`} value={Math.min(item.used, item.limit)} max={item.limit} />}
    </div>
  )
}

/** The plan of an account and what it has used this month. */
export function UsagePanel({ usage }: { usage: TenantUsage }) {
  return (
    <>
      <p>
        Plan <strong>{usage.plan.name}</strong> <span className="mono">({usage.plan.code})</span> · periodo {usage.period}
      </p>
      <Meter label="Empresas" item={usage.companies} />
      <Meter label="Usuarios activos" item={usage.users} />
      <Meter label="Comprobantes del mes" item={usage.documentsThisMonth} />
      <p className="hint">Los comprobantes cuentan facturas, boletas y notas emitidas en el mes calendario de Lima, también las dadas de baja. Un límite que se baja nunca quita lo que la cuenta ya tiene.</p>
    </>
  )
}

/** What a tenant sees of its own plan. */
export function MyPlan() {
  const { data, isPending, error } = useMyPlan()
  return (
    <>
      <PageHeader title="Plan y consumo" subtitle="Lo que permite su plan y lo que ha usado" />
      <div className="card">
        <ErrorAlert error={error} />
        {isPending ? <Loading /> : data && <UsagePanel usage={data} />}
      </div>
      <MyCharges />
    </>
  )
}

/** Plan of one tenant for platform staff, with the change (super administrator). */
export function TenantPlanCard({ tenantId, canManage, closed }: { tenantId: string; canManage: boolean; closed: boolean }) {
  const usage = useTenantUsage(tenantId)
  const plans = usePlans()
  const assign = useAssignPlan(tenantId)
  const toast = useToast()
  const [choice, setChoice] = useState('')
  const current = usage.data?.plan.id
  const selectable = (plans.data ?? []).filter((plan) => plan.isActive || plan.id === current)

  return (
    <div className="card">
      <h2>Plan y consumo</h2>
      <ErrorAlert error={usage.error ?? assign.error} />
      {usage.isPending ? <Loading /> : usage.data && <UsagePanel usage={usage.data} />}
      {canManage && !closed && (
        <form
          className="row"
          style={{ marginTop: 14, alignItems: 'flex-end' }}
          onSubmit={(event) => {
            event.preventDefault()
            if (choice) assign.mutate(choice, { onSuccess: () => { toast.ok('Plan cambiado.'); setChoice('') } })
          }}
        >
          <SelectField label="Cambiar a" value={choice} onChange={(event) => setChoice(event.target.value)}>
            <option value="">Elija un plan</option>
            {selectable.filter((plan) => plan.id !== current).map((plan) => (
              <option key={plan.id} value={plan.id}>
                {plan.name} ({plan.code})
              </option>
            ))}
          </SelectField>
          <button className="btn primary" type="submit" disabled={!choice || assign.isPending}>
            Cambiar plan
          </button>
        </form>
      )}
    </div>
  )
}

/** The plan catalogue of the platform. */
export function Plans() {
  const { hasRole } = useSession()
  const { data, isPending, error } = usePlans()
  const resellers = useResellers()
  const [editing, setEditing] = useState<PlanRow | 'new' | null>(null)
  const canManage = hasRole('PlatformSuperAdmin')

  return (
    <>
      <PageHeader title="Planes" subtitle="Qué puede tener y emitir cada cuenta">
        {canManage && (
          <button className="btn primary" type="button" onClick={() => setEditing('new')}>
            Nuevo plan
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
                  <th>Plan</th>
                  <th>Código</th>
                  <th className="right">Empresas</th>
                  <th className="right">Usuarios</th>
                  <th className="right">Comprobantes por mes</th>
                  <th>Excedente</th>
                  <th>Oferta</th>
                  <th>Estado</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {data.map((plan) => (
                  <tr key={plan.id}>
                    <td>{plan.name}</td>
                    <td className="mono">{plan.code}</td>
                    <td className="right">{limitText(plan.maxCompanies)}</td>
                    <td className="right">{limitText(plan.maxUsers)}</td>
                    <td className="right">{limitText(plan.maxDocumentsPerMonth)}</td>
                    <td>{plan.allowsOverage ? 'Se cobra' : 'Se rechaza'}</td>
                    <td>{plan.resellerId ? `Privada de ${(resellers.data ?? []).find((reseller) => reseller.id === plan.resellerId)?.name ?? 'un revendedor'}` : 'Pública'}</td>
                    <td>
                      <Badge tone={plan.isActive ? 'ok' : 'neutral'}>{plan.isActive ? 'Disponible' : 'Retirado'}</Badge>
                    </td>
                    <td className="right tight">
                      {canManage && (
                        <button className="btn small" type="button" aria-label={`Editar ${plan.name}`} onClick={() => setEditing(plan)}>
                          Editar
                        </button>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : (
          <Empty>Aún no hay planes.</Empty>
        )}
      </div>
      {editing && <PlanModal plan={editing === 'new' ? null : editing} onClose={() => setEditing(null)} />}
    </>
  )
}

const toLimit = (text: string) => (text.trim() === '' ? null : Number(text))

function PlanModal({ plan, onClose }: { plan: PlanRow | null; onClose: () => void }) {
  const create = useCreatePlan()
  const update = useUpdatePlan(plan?.id ?? '')
  const toast = useToast()
  const [value, setValue] = useState({
    code: plan?.code ?? '',
    name: plan?.name ?? '',
    maxCompanies: plan?.maxCompanies?.toString() ?? '',
    maxUsers: plan?.maxUsers?.toString() ?? '',
    maxDocumentsPerMonth: plan?.maxDocumentsPerMonth?.toString() ?? '',
    isActive: plan?.isActive ?? true,
    resellerId: plan?.resellerId ?? '',
    allowsOverage: plan?.allowsOverage ?? false,
  })
  const resellers = useResellers()
  const mutation = plan ? update : create

  function submit() {
    const input: PlanInput = {
      code: value.code.trim(),
      name: value.name.trim(),
      maxCompanies: toLimit(value.maxCompanies),
      maxUsers: toLimit(value.maxUsers),
      maxDocumentsPerMonth: toLimit(value.maxDocumentsPerMonth),
      isActive: value.isActive,
      resellerId: value.resellerId || null,
      ...(plan ? {} : { allowsOverage: value.allowsOverage }),
    }
    mutation.mutate(input, { onSuccess: () => { toast.ok(plan ? 'Plan actualizado.' : 'Plan creado.'); onClose() } })
  }

  return (
    <Modal title={plan ? `Editar ${plan.name}` : 'Nuevo plan'} onClose={onClose}>
      <form
        className="stack"
        onSubmit={(event) => {
          event.preventDefault()
          submit()
        }}
      >
        <ErrorAlert error={mutation.error} />
        <TextField label="Código" required={!plan} disabled={plan !== null} pattern="[a-z][a-z0-9\-]{1,39}" hint="minúsculas, dígitos y guiones; no cambia después" value={value.code} onChange={(event) => setValue({ ...value, code: event.target.value })} />
        <TextField label="Nombre" required minLength={2} maxLength={80} value={value.name} onChange={(event) => setValue({ ...value, name: event.target.value })} />
        <TextField label="Máximo de empresas" type="number" min={0} inputMode="numeric" hint="vacío: ilimitado" value={value.maxCompanies} onChange={(event) => setValue({ ...value, maxCompanies: event.target.value })} />
        <TextField label="Máximo de usuarios activos" type="number" min={0} inputMode="numeric" hint="vacío: ilimitado" value={value.maxUsers} onChange={(event) => setValue({ ...value, maxUsers: event.target.value })} />
        <TextField label="Comprobantes por mes" type="number" min={0} inputMode="numeric" hint="vacío: ilimitado" value={value.maxDocumentsPerMonth} onChange={(event) => setValue({ ...value, maxDocumentsPerMonth: event.target.value })} />
        <SelectField label="Oferta" hint="una oferta privada solo la ve y la asigna ese revendedor" value={value.resellerId} onChange={(event) => setValue({ ...value, resellerId: event.target.value })}>
          <option value="">Pública</option>
          {(resellers.data ?? []).map((reseller) => (
            <option key={reseller.id} value={reseller.id}>
              Privada de {reseller.name}
            </option>
          ))}
        </SelectField>
        {plan ? (
          <p className="hint">{plan.allowsOverage ? 'Este plan cobra los comprobantes que pasan de lo incluido en su precio.' : 'Este plan rechaza los comprobantes que pasan de su tope.'} No cambia después de crear el plan.</p>
        ) : (
          <label className="checkbox">
            <input type="checkbox" checked={value.allowsOverage} onChange={(event) => setValue({ ...value, allowsOverage: event.target.checked })} /> Cobrar los comprobantes que pasan de lo incluido, en lugar de rechazarlos
          </label>
        )}
        {plan && (
          <label className="checkbox">
            <input type="checkbox" checked={value.isActive} onChange={(event) => setValue({ ...value, isActive: event.target.checked })} /> Disponible para asignar a cuentas nuevas
          </label>
        )}
        <div className="actions">
          <button className="btn" type="button" onClick={onClose}>
            Cancelar
          </button>
          <button className="btn primary" type="submit" disabled={mutation.isPending}>
            {plan ? 'Guardar' : 'Crear plan'}
          </button>
        </div>
      </form>
    </Modal>
  )
}
