import { useState, type FormEvent } from 'react'
import {
  useBillingPolicies,
  useChargeDetail,
  useCharges,
  useCommissionSchedules,
  useMyCommissions,
  useMyCommissionStatement,
  useMyTerms,
  usePlanPrices,
  usePlans,
  usePublishPolicy,
  usePublishPrice,
  usePublishSchedule,
  useRecordPayment,
  useReversePayment,
  useRunCollection,
  useSettleCommission,
  useTenantTerms,
  useVoidCharge,
  useResellerCommissions,
  useCommissionStatement,
} from '../api/queries'
import type { Charge, ChargeStatus, CommissionMonth, CommissionStatement, PaymentMethod, PlanRow, ResellerRow, TenantTerms } from '../api/types'
import { useSession } from '../auth/session'
import { Badge, Empty, ErrorAlert, KeyValues, Loading, Modal, PageHeader, SelectField, Tabs, TextField, useToast } from '../components/ui'
import {
  buildPayment,
  buildPolicy,
  buildPrice,
  buildSchedule,
  CHARGE_LABELS,
  CHARGE_TONES,
  firstOfNextMonth,
  monthClosed,
  monthKey,
  monthLabel,
  PAYMENT_METHODS,
  percent,
  type TierForm,
} from '../lib/billing'
import { date, money, todayInLima } from '../lib/format'

let tierCounter = 0
const newTier = (minAccounts = '', pct = ''): TierForm => ({ key: `tier-${++tierCounter}`, minAccounts, percent: pct })

export function ChargeBadge({ status }: { status: ChargeStatus }) {
  return <Badge tone={CHARGE_TONES[status]}>{CHARGE_LABELS[status]}</Badge>
}

// ---------- charges ----------

/** The charges of an account (or of all, for platform staff). Opening one shows what it is made of and its payments. */
export function ChargesTable({ charges, withTenant, onOpen }: { charges: Charge[]; withTenant: boolean; onOpen: (charge: Charge) => void }) {
  return (
    <div className="table-wrap">
      <table className="table">
        <thead>
          <tr>
            <th>Mes</th>
            {withTenant && <th>Cuenta</th>}
            <th>Plan</th>
            <th className="right">Total</th>
            <th className="right">Saldo</th>
            <th>Vence</th>
            <th>Estado</th>
            <th />
          </tr>
        </thead>
        <tbody>
          {charges.map((charge) => (
            <tr key={charge.id}>
              <td>{monthLabel(charge.period)}</td>
              {withTenant && <td>{charge.tenantName}</td>}
              <td>{charge.planName}</td>
              <td className="right">{money(charge.totalAmount, charge.currency)}</td>
              <td className="right">{money(charge.balance, charge.currency)}</td>
              <td>{date(charge.dueOn)}</td>
              <td>
                <ChargeBadge status={charge.status} />
              </td>
              <td className="right tight">
                <button className="btn small" type="button" aria-label={`Ver cargo de ${monthLabel(charge.period)}${withTenant ? ` de ${charge.tenantName}` : ''}`} onClick={() => onOpen(charge)}>
                  Ver
                </button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

/** One charge: its parts, its payments and, for the super administrator, the actions on them. */
export function ChargeModal({ id, own, canManage, onClose }: { id: string; own: boolean; canManage: boolean; onClose: () => void }) {
  const { data, isPending, error } = useChargeDetail(id, own)
  const charge = data?.charge
  return (
    <Modal title={charge ? `Cargo de ${monthLabel(charge.period)}` : 'Cargo'} onClose={onClose}>
      <ErrorAlert error={error} />
      {isPending ? (
        <Loading />
      ) : (
        charge &&
        data && (
          <div className="stack">
            <KeyValues
              items={[
                ['Cuenta', charge.tenantName],
                ['Plan', `${charge.planName} (${charge.planCode})`],
                ['Cuota mensual', money(charge.monthlyFee, charge.currency)],
                ['Comprobantes del mes', charge.includedDocuments === null ? `${charge.documentsIssued}` : `${charge.documentsIssued} (incluye ${charge.includedDocuments})`],
                ...(charge.overageUnitPrice !== null
                  ? ([['Excedente', `${charge.overageDocuments} × ${money(charge.overageUnitPrice, charge.currency)} = ${money(charge.overageAmount, charge.currency)}`]] as [string, string][])
                  : []),
                ['Subtotal', money(charge.netAmount, charge.currency)],
                [`IGV (${percent(charge.taxRate)})`, money(charge.taxAmount, charge.currency)],
                ['Total', <strong key="total">{money(charge.totalAmount, charge.currency)}</strong>],
                ['Pagado', money(charge.paidAmount, charge.currency)],
                ['Saldo', <strong key="balance">{money(charge.balance, charge.currency)}</strong>],
                ['Emitido', date(charge.issuedOn)],
                ['Vence', date(charge.dueOn)],
                ['Suspensión por mora desde', charge.suspendOn ? date(charge.suspendOn) : 'No suspende'],
                ['Estado', <ChargeBadge key="status" status={charge.status} />],
                ...(charge.voidReason ? ([['Motivo de la anulación', charge.voidReason]] as [string, string][]) : []),
              ]}
            />
            <PaymentList charge={charge} payments={data.payments} canManage={canManage} />
            {canManage && charge.status !== 'Void' && <ChargeActions charge={charge} />}
          </div>
        )
      )}
    </Modal>
  )
}

function PaymentList({ charge, payments, canManage }: { charge: Charge; payments: { id: string; amount: number; method: PaymentMethod; reference: string | null; paidOn: string; note: string | null; reversesPaymentId: string | null }[]; canManage: boolean }) {
  const reverse = useReversePayment()
  const toast = useToast()
  const [reversing, setReversing] = useState<string | null>(null)
  const [reason, setReason] = useState('')
  const reversed = new Set(payments.filter((payment) => payment.reversesPaymentId).map((payment) => payment.reversesPaymentId))

  if (payments.length === 0) return <p className="hint">Aún no hay pagos.</p>
  return (
    <div>
      <h3>Pagos</h3>
      <ErrorAlert error={reverse.error} />
      <div className="table-wrap">
        <table className="table">
          <thead>
            <tr>
              <th>Fecha</th>
              <th>Medio</th>
              <th>Referencia</th>
              <th className="right">Monto</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {payments.map((payment) => (
              <tr key={payment.id}>
                <td>{date(payment.paidOn)}</td>
                <td>{PAYMENT_METHODS[payment.method]}</td>
                <td>{payment.reversesPaymentId ? `Reversa: ${payment.note ?? ''}` : (payment.reference ?? '—')}</td>
                <td className="right">{money(payment.amount, charge.currency)}</td>
                <td className="right tight">
                  {canManage && payment.amount > 0 && !reversed.has(payment.id) && charge.status !== 'Void' && (
                    <button className="btn small" type="button" aria-label={`Revertir el pago de ${money(payment.amount, charge.currency)}`} onClick={() => { setReversing(payment.id); setReason('') }}>
                      Revertir
                    </button>
                  )}
                  {reversed.has(payment.id) && <Badge tone="neutral">Revertido</Badge>}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {reversing && (
        <form
          className="row"
          style={{ alignItems: 'flex-end', marginTop: 10 }}
          onSubmit={(event) => {
            event.preventDefault()
            reverse.mutate({ id: reversing, reason }, { onSuccess: () => { toast.ok('Pago revertido.'); setReversing(null) } })
          }}
        >
          <TextField label="Motivo de la reversa" required minLength={3} maxLength={300} value={reason} onChange={(event) => setReason(event.target.value)} />
          <button className="btn primary" type="submit" disabled={reverse.isPending}>
            Confirmar reversa
          </button>
          <button className="btn" type="button" onClick={() => setReversing(null)}>
            Cancelar
          </button>
        </form>
      )}
    </div>
  )
}

function ChargeActions({ charge }: { charge: Charge }) {
  const record = useRecordPayment(charge.id)
  const voidCharge = useVoidCharge(charge.id)
  const toast = useToast()
  const [form, setForm] = useState({ amount: charge.balance > 0 ? charge.balance.toFixed(2) : '', method: 'Transfer' as PaymentMethod, paidOn: todayInLima(), reference: '', note: '' })
  const [voiding, setVoiding] = useState(false)
  const [reason, setReason] = useState('')

  function pay(event: FormEvent) {
    event.preventDefault()
    record.mutate(buildPayment(form), { onSuccess: () => { toast.ok('Pago registrado.'); setForm({ ...form, amount: '', reference: '', note: '' }) } })
  }

  return (
    <div className="stack">
      {charge.balance > 0 && (
        <form className="stack" onSubmit={pay}>
          <h3>Registrar un pago</h3>
          <ErrorAlert error={record.error} />
          <div className="grid-2">
            <TextField label="Monto (S/ con IGV)" required type="number" step="0.01" min="0.01" max={charge.balance} inputMode="decimal" value={form.amount} onChange={(event) => setForm({ ...form, amount: event.target.value })} />
            <SelectField label="Medio de pago" value={form.method} onChange={(event) => setForm({ ...form, method: event.target.value as PaymentMethod })}>
              {Object.entries(PAYMENT_METHODS).map(([code, label]) => (
                <option key={code} value={code}>
                  {label}
                </option>
              ))}
            </SelectField>
            <TextField label="Fecha del pago" required type="date" max={todayInLima()} min={charge.issuedOn} value={form.paidOn} onChange={(event) => setForm({ ...form, paidOn: event.target.value })} />
            <TextField label="Referencia" maxLength={100} hint="número de operación" value={form.reference} onChange={(event) => setForm({ ...form, reference: event.target.value })} />
          </div>
          <TextField label="Nota" maxLength={300} value={form.note} onChange={(event) => setForm({ ...form, note: event.target.value })} />
          <div className="actions">
            <button className="btn primary" type="submit" disabled={record.isPending}>
              Registrar pago
            </button>
          </div>
        </form>
      )}
      {charge.paidAmount === 0 &&
        (voiding ? (
          <form
            className="row"
            style={{ alignItems: 'flex-end' }}
            onSubmit={(event) => {
              event.preventDefault()
              voidCharge.mutate(reason, { onSuccess: () => toast.ok('Cargo anulado.') })
            }}
          >
            <ErrorAlert error={voidCharge.error} />
            <TextField label="Motivo de la anulación" required minLength={3} maxLength={300} value={reason} onChange={(event) => setReason(event.target.value)} />
            <button className="btn danger" type="submit" disabled={voidCharge.isPending}>
              Anular cargo
            </button>
            <button className="btn" type="button" onClick={() => setVoiding(false)}>
              Cancelar
            </button>
          </form>
        ) : (
          <div>
            <button className="btn small danger" type="button" onClick={() => setVoiding(true)}>
              Anular este cargo…
            </button>
          </div>
        ))}
    </div>
  )
}

const STATUS_FILTERS: ChargeStatus[] = ['Pending', 'Partial', 'Overdue', 'Paid', 'Void']

/** Platform staff: every charge, with the collection pass that makes them. */
export function Collections() {
  const { hasRole } = useSession()
  const canManage = hasRole('PlatformSuperAdmin')
  const [status, setStatus] = useState<ChargeStatus | ''>('')
  const [period, setPeriod] = useState('')
  const [open, setOpen] = useState<string | null>(null)
  const { data, isPending, error } = useCharges({ status, period })
  const run = useRunCollection()
  const toast = useToast()

  return (
    <>
      <PageHeader title="Cobranza" subtitle="Cargos mensuales de las cuentas y sus pagos">
        {canManage && (
          <button
            className="btn"
            type="button"
            disabled={run.isPending}
            onClick={() =>
              run.mutate(undefined, {
                onSuccess: (result) => toast.ok(`Cobranza ejecutada: ${result.chargesCreated} cargos nuevos, ${result.tenantsSuspended} cuentas suspendidas, ${result.tenantsReactivated} reactivadas.`),
              })
            }
          >
            Ejecutar la cobranza ahora
          </button>
        )}
      </PageHeader>
      <div className="card">
        <ErrorAlert error={error ?? run.error} />
        <div className="row" style={{ alignItems: 'flex-end', marginBottom: 12 }}>
          <SelectField label="Estado" value={status} onChange={(event) => setStatus(event.target.value as ChargeStatus | '')}>
            <option value="">Todos</option>
            {STATUS_FILTERS.map((code) => (
              <option key={code} value={code}>
                {CHARGE_LABELS[code]}
              </option>
            ))}
          </SelectField>
          <TextField label="Mes" type="month" value={period} onChange={(event) => setPeriod(event.target.value)} />
        </div>
        {isPending ? <Loading /> : data && data.length > 0 ? <ChargesTable charges={data} withTenant onOpen={(charge) => setOpen(charge.id)} /> : <Empty>No hay cargos con esos filtros.</Empty>}
      </div>
      {open && <ChargeModal id={open} own={false} canManage={canManage} onClose={() => setOpen(null)} />}
    </>
  )
}

// ---------- the price and the charges of one account ----------

function TermsSummary({ terms }: { terms: TenantTerms }) {
  if (!terms.price) return <p>Esta cuenta no se cobra: su plan no tiene precio.</p>
  const price = terms.price
  return (
    <>
      <KeyValues
        items={[
          ['Plan', `${terms.planName} (${terms.planCode})`],
          ['Cuota mensual', `${money(price.monthlyFee)} más IGV`],
          ...(price.overageUnitPrice !== null
            ? ([
                ['Comprobantes incluidos', `${price.includedDocuments ?? 0} por mes`],
                ['Cada comprobante adicional', `${money(price.overageUnitPrice)} más IGV`],
              ] as [string, string][])
            : []),
          ['Se cobra desde', terms.firstChargePeriod ? monthLabel(terms.firstChargePeriod) : '—'],
          ['Versión del precio', `${price.version}, vigente desde ${date(price.effectiveFrom)}`],
        ]}
      />
      <p className="hint">El precio es el que regía el día en que la cuenta tomó su plan: las versiones nuevas no lo cambian.</p>
    </>
  )
}

/** The price and the charges of the own account. */
export function MyCharges() {
  const terms = useMyTerms()
  const charges = useCharges({}, true)
  const [open, setOpen] = useState<string | null>(null)
  return (
    <div className="card">
      <h2>Precio y cargos</h2>
      <ErrorAlert error={terms.error ?? charges.error} />
      {terms.isPending ? <Loading /> : terms.data && <TermsSummary terms={terms.data} />}
      {charges.isPending ? <Loading /> : charges.data && charges.data.length > 0 ? <ChargesTable charges={charges.data} withTenant={false} onOpen={(charge) => setOpen(charge.id)} /> : <Empty>Aún no tiene cargos.</Empty>}
      {open && <ChargeModal id={open} own canManage={false} onClose={() => setOpen(null)} />}
    </div>
  )
}

/** The price and the charges of one account, for platform staff. */
export function TenantBillingCard({ tenantId }: { tenantId: string }) {
  const { hasRole } = useSession()
  const terms = useTenantTerms(tenantId)
  const charges = useCharges({ tenantId })
  const [open, setOpen] = useState<string | null>(null)
  return (
    <div className="card">
      <h2>Precio y cargos</h2>
      <ErrorAlert error={terms.error ?? charges.error} />
      {terms.isPending ? <Loading /> : terms.data && <TermsSummary terms={terms.data} />}
      {charges.isPending ? <Loading /> : charges.data && charges.data.length > 0 ? <ChargesTable charges={charges.data} withTenant={false} onOpen={(charge) => setOpen(charge.id)} /> : <Empty>Aún no tiene cargos.</Empty>}
      {open && <ChargeModal id={open} own={false} canManage={hasRole('PlatformSuperAdmin')} onClose={() => setOpen(null)} />}
    </div>
  )
}

// ---------- prices, billing policy and commission terms (platform) ----------

type PricingTab = 'precios' | 'politica' | 'comisiones'

/** Platform staff: the versions of the prices, of the billing policy and of the commission terms. Versions are only added. */
export function Pricing() {
  const [tab, setTab] = useState<PricingTab>('precios')
  return (
    <>
      <PageHeader title="Precios y comisiones" subtitle="Cada versión rige desde una fecha futura y nunca cambia lo que ya se cobró" />
      <Tabs
        tabs={[
          { id: 'precios', label: 'Precios por plan' },
          { id: 'politica', label: 'Política de cobranza' },
          { id: 'comisiones', label: 'Comisiones' },
        ]}
        value={tab}
        onChange={setTab}
      />
      {tab === 'precios' && <PricesTab />}
      {tab === 'politica' && <PolicyTab />}
      {tab === 'comisiones' && <SchedulesTab />}
    </>
  )
}

function PricesTab() {
  const { hasRole } = useSession()
  const canManage = hasRole('PlatformSuperAdmin')
  const plans = usePlans()
  const [planId, setPlanId] = useState('')
  const prices = usePlanPrices(planId)
  const [adding, setAdding] = useState(false)
  const plan = (plans.data ?? []).find((candidate) => candidate.id === planId)

  return (
    <div className="card">
      <ErrorAlert error={plans.error ?? prices.error} />
      <div className="row spread" style={{ alignItems: 'flex-end', marginBottom: 12 }}>
        <SelectField label="Plan" value={planId} onChange={(event) => setPlanId(event.target.value)}>
          <option value="">Elija un plan</option>
          {(plans.data ?? []).map((candidate) => (
            <option key={candidate.id} value={candidate.id}>
              {candidate.name} ({candidate.code})
            </option>
          ))}
        </SelectField>
        {canManage && plan && (
          <button className="btn primary" type="button" onClick={() => setAdding(true)}>
            Publicar una versión
          </button>
        )}
      </div>
      {!planId ? (
        <Empty>Elija un plan para ver sus precios.</Empty>
      ) : prices.isPending ? (
        <Loading />
      ) : prices.data && prices.data.length > 0 ? (
        <div className="table-wrap">
          <table className="table">
            <thead>
              <tr>
                <th>Versión</th>
                <th>Rige desde</th>
                <th className="right">Cuota mensual</th>
                {plan?.allowsOverage && <th className="right">Incluye</th>}
                {plan?.allowsOverage && <th className="right">Cada adicional</th>}
                <th>Nota</th>
              </tr>
            </thead>
            <tbody>
              {prices.data.map((price) => (
                <tr key={price.id}>
                  <td>{price.version}</td>
                  <td>{date(price.effectiveFrom)}</td>
                  <td className="right">{money(price.monthlyFee)}</td>
                  {plan?.allowsOverage && <td className="right">{price.includedDocuments}</td>}
                  {plan?.allowsOverage && <td className="right">{price.overageUnitPrice === null ? '—' : money(price.overageUnitPrice)}</td>}
                  <td>{price.note ?? ''}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : (
        <Empty>Este plan aún no tiene precio: sus cuentas no se cobran.</Empty>
      )}
      {adding && plan && <PriceModal plan={plan} onClose={() => setAdding(false)} />}
    </div>
  )
}

function PriceModal({ plan, onClose }: { plan: PlanRow; onClose: () => void }) {
  const publish = usePublishPrice(plan.id)
  const toast = useToast()
  const [form, setForm] = useState({ effectiveFrom: firstOfNextMonth(todayInLima()), monthlyFee: '', includedDocuments: '', overageUnitPrice: '', note: '' })
  return (
    <Modal title={`Nueva versión del precio de ${plan.name}`} onClose={onClose}>
      <form
        className="stack"
        onSubmit={(event) => {
          event.preventDefault()
          publish.mutate(buildPrice(form, plan.allowsOverage), { onSuccess: () => { toast.ok('Precio publicado.'); onClose() } })
        }}
      >
        <ErrorAlert error={publish.error} />
        <TextField label="Rige desde" required type="date" min={firstOfNextMonth(todayInLima())} hint="el primer día de un mes futuro" value={form.effectiveFrom} onChange={(event) => setForm({ ...form, effectiveFrom: event.target.value })} />
        <TextField label="Cuota mensual (S/ sin IGV)" required type="number" step="0.01" min="0" inputMode="decimal" value={form.monthlyFee} onChange={(event) => setForm({ ...form, monthlyFee: event.target.value })} />
        {plan.allowsOverage && (
          <>
            <TextField label="Comprobantes incluidos por mes" required type="number" min="0" inputMode="numeric" value={form.includedDocuments} onChange={(event) => setForm({ ...form, includedDocuments: event.target.value })} />
            <TextField label="Precio de cada comprobante adicional (S/ sin IGV)" required type="number" step="0.0001" min="0.0001" inputMode="decimal" value={form.overageUnitPrice} onChange={(event) => setForm({ ...form, overageUnitPrice: event.target.value })} />
          </>
        )}
        <TextField label="Nota" maxLength={300} value={form.note} onChange={(event) => setForm({ ...form, note: event.target.value })} />
        <p className="hint">Solo alcanza a las cuentas que tomen el plan desde esa fecha: quienes ya lo tienen conservan su precio.</p>
        <div className="actions">
          <button className="btn" type="button" onClick={onClose}>
            Cancelar
          </button>
          <button className="btn primary" type="submit" disabled={publish.isPending}>
            Publicar precio
          </button>
        </div>
      </form>
    </Modal>
  )
}

function PolicyTab() {
  const { hasRole } = useSession()
  const { data, isPending, error } = useBillingPolicies()
  const [adding, setAdding] = useState(false)
  return (
    <div className="card">
      <ErrorAlert error={error} />
      {hasRole('PlatformSuperAdmin') && (
        <div className="row" style={{ marginBottom: 12 }}>
          <button className="btn primary" type="button" onClick={() => setAdding(true)}>
            Publicar una política
          </button>
        </div>
      )}
      {isPending ? (
        <Loading />
      ) : data && data.length > 0 ? (
        <div className="table-wrap">
          <table className="table">
            <thead>
              <tr>
                <th>Versión</th>
                <th>Rige desde</th>
                <th className="right">Plazo de pago</th>
                <th className="right">Gracia antes de suspender</th>
                <th>Nota</th>
              </tr>
            </thead>
            <tbody>
              {data.map((policy) => (
                <tr key={policy.id}>
                  <td>{policy.version}</td>
                  <td>{date(policy.effectiveFrom)}</td>
                  <td className="right">{policy.dueDays} días</td>
                  <td className="right">{policy.suspendAfterDays === null ? 'No suspende' : `${policy.suspendAfterDays} días`}</td>
                  <td>{policy.note ?? ''}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : (
        <Empty>No hay políticas.</Empty>
      )}
      <p className="hint">La política rige para los cargos que se emitan desde su fecha; cada cargo guarda las fechas con que se emitió.</p>
      {adding && <PolicyModal onClose={() => setAdding(false)} />}
    </div>
  )
}

function PolicyModal({ onClose }: { onClose: () => void }) {
  const publish = usePublishPolicy()
  const toast = useToast()
  const [form, setForm] = useState({ effectiveFrom: '', dueDays: '10', suspendAfterDays: '15', note: '' })
  return (
    <Modal title="Nueva política de cobranza" onClose={onClose}>
      <form
        className="stack"
        onSubmit={(event) => {
          event.preventDefault()
          publish.mutate(buildPolicy(form), { onSuccess: () => { toast.ok('Política publicada.'); onClose() } })
        }}
      >
        <ErrorAlert error={publish.error} />
        <TextField label="Rige desde" required type="date" min={todayInLima()} hint="un día futuro" value={form.effectiveFrom} onChange={(event) => setForm({ ...form, effectiveFrom: event.target.value })} />
        <TextField label="Plazo de pago (días)" required type="number" min="0" max="90" inputMode="numeric" value={form.dueDays} onChange={(event) => setForm({ ...form, dueDays: event.target.value })} />
        <TextField label="Gracia antes de suspender (días)" type="number" min="0" max="365" inputMode="numeric" hint="vacío: no suspende por mora" value={form.suspendAfterDays} onChange={(event) => setForm({ ...form, suspendAfterDays: event.target.value })} />
        <TextField label="Nota" maxLength={300} value={form.note} onChange={(event) => setForm({ ...form, note: event.target.value })} />
        <div className="actions">
          <button className="btn" type="button" onClick={onClose}>
            Cancelar
          </button>
          <button className="btn primary" type="submit" disabled={publish.isPending}>
            Publicar política
          </button>
        </div>
      </form>
    </Modal>
  )
}

function SchedulesTab() {
  const { hasRole } = useSession()
  const { data, isPending, error } = useCommissionSchedules()
  const [adding, setAdding] = useState(false)
  return (
    <div className="card">
      <ErrorAlert error={error} />
      {hasRole('PlatformSuperAdmin') && (
        <div className="row" style={{ marginBottom: 12 }}>
          <button className="btn primary" type="button" onClick={() => setAdding(true)}>
            Publicar términos
          </button>
        </div>
      )}
      {isPending ? (
        <Loading />
      ) : data && data.length > 0 ? (
        <div className="table-wrap">
          <table className="table">
            <thead>
              <tr>
                <th>Versión</th>
                <th>Rige desde</th>
                <th>Tramos (cuentas activas del revendedor → porcentaje)</th>
                <th>Nota</th>
              </tr>
            </thead>
            <tbody>
              {data.map((schedule) => (
                <tr key={schedule.id}>
                  <td>{schedule.version}</td>
                  <td>{date(schedule.effectiveFrom)}</td>
                  <td>{schedule.tiers.map((tier) => `desde ${tier.minAccounts}: ${percent(tier.rate)}`).join(' · ')}</td>
                  <td>{schedule.note ?? ''}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : (
        <Empty>No hay términos de comisión.</Empty>
      )}
      <p className="hint">La comisión es un porcentaje de lo que paga la cuenta sin IGV. Cada cuenta conserva los términos que regían el día que quedó bajo su revendedor.</p>
      {adding && <ScheduleModal onClose={() => setAdding(false)} />}
    </div>
  )
}

function ScheduleModal({ onClose }: { onClose: () => void }) {
  const publish = usePublishSchedule()
  const toast = useToast()
  const [effectiveFrom, setEffectiveFrom] = useState('')
  const [note, setNote] = useState('')
  const [tiers, setTiers] = useState<TierForm[]>([newTier('0', '20'), newTier('10', '25'), newTier('25', '30')])
  const update = (key: string, patch: Partial<TierForm>) => setTiers(tiers.map((tier) => (tier.key === key ? { ...tier, ...patch } : tier)))
  return (
    <Modal title="Nuevos términos de comisión" onClose={onClose}>
      <form
        className="stack"
        onSubmit={(event) => {
          event.preventDefault()
          publish.mutate(buildSchedule(effectiveFrom, tiers, note), { onSuccess: () => { toast.ok('Términos publicados.'); onClose() } })
        }}
      >
        <ErrorAlert error={publish.error} />
        <TextField label="Rige desde" required type="date" min={todayInLima()} hint="un día futuro" value={effectiveFrom} onChange={(event) => setEffectiveFrom(event.target.value)} />
        {tiers.map((tier, index) => (
          <div className="row" key={tier.key} style={{ alignItems: 'flex-end' }}>
            <TextField label={`Tramo ${index + 1}: desde cuentas activas`} required type="number" min="0" inputMode="numeric" disabled={index === 0} value={tier.minAccounts} onChange={(event) => update(tier.key, { minAccounts: event.target.value })} />
            <TextField label={`Tramo ${index + 1}: porcentaje`} required type="number" step="0.01" min="0" max="100" inputMode="decimal" value={tier.percent} onChange={(event) => update(tier.key, { percent: event.target.value })} />
            {index > 0 && (
              <button className="btn small danger" type="button" onClick={() => setTiers(tiers.filter((candidate) => candidate.key !== tier.key))}>
                Quitar tramo {index + 1}
              </button>
            )}
          </div>
        ))}
        <div>
          <button className="btn small" type="button" onClick={() => setTiers([...tiers, newTier()])}>
            Agregar tramo
          </button>
        </div>
        <TextField label="Nota" maxLength={300} value={note} onChange={(event) => setNote(event.target.value)} />
        <div className="actions">
          <button className="btn" type="button" onClick={onClose}>
            Cancelar
          </button>
          <button className="btn primary" type="submit" disabled={publish.isPending}>
            Publicar términos
          </button>
        </div>
      </form>
    </Modal>
  )
}

// ---------- commissions ----------

function StatementTable({ statement }: { statement: CommissionStatement }) {
  if (statement.items.length === 0) return <Empty>No hay comisiones en este mes.</Empty>
  return (
    <div className="table-wrap">
      <table className="table">
        <thead>
          <tr>
            <th>Cuenta</th>
            <th>Cargo de</th>
            <th className="right">Base sin IGV</th>
            <th className="right">Porcentaje</th>
            <th className="right">Comisión</th>
          </tr>
        </thead>
        <tbody>
          {statement.items.map((item) => (
            <tr key={item.id}>
              <td>{item.tenantName}</td>
              <td>{monthLabel(item.chargePeriod)}</td>
              <td className="right">{money(item.baseAmount)}</td>
              <td className="right">{percent(item.rate)}</td>
              <td className="right">{money(item.amount)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

function MonthsTable({ months, selected, onSelect }: { months: CommissionMonth[]; selected: string | null; onSelect: (month: string) => void }) {
  if (months.length === 0) return <Empty>Aún no hay comisiones.</Empty>
  return (
    <div className="table-wrap">
      <table className="table">
        <thead>
          <tr>
            <th>Mes</th>
            <th className="right">Asientos</th>
            <th className="right">Comisión</th>
            <th>Liquidación</th>
            <th />
          </tr>
        </thead>
        <tbody>
          {months.map((month) => (
            <tr key={month.month}>
              <td>{monthLabel(month.month)}</td>
              <td className="right">{month.entries}</td>
              <td className="right">{money(month.total)}</td>
              <td>{month.settlement ? <Badge tone="ok">{`Liquidado el ${date(month.settlement.settledOn)}`}</Badge> : <Badge tone="neutral">Pendiente</Badge>}</td>
              <td className="right tight">
                <button className="btn small" type="button" aria-pressed={selected === monthKey(month.month)} aria-label={`Ver ${monthLabel(month.month)}`} onClick={() => onSelect(monthKey(month.month))}>
                  Ver
                </button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

/** What the reseller earned, month by month. */
export function ResellerCommissions() {
  const { data, isPending, error } = useMyCommissions()
  const [month, setMonth] = useState<string | null>(null)
  const statement = useMyCommissionStatement(month)
  return (
    <>
      <PageHeader title="Comisiones" subtitle="Lo que ganan sus cuentas cuando pagan" />
      <div className="card">
        <ErrorAlert error={error} />
        {isPending ? (
          <Loading />
        ) : (
          data && (
            <>
              <KeyValues
                items={[
                  ['Cuentas activas', String(data.activeAccounts)],
                  ['Porcentaje actual', data.currentRate === null ? '—' : percent(data.currentRate)],
                  ...(data.scheduleForNewAccounts
                    ? ([['Tramos para las cuentas nuevas', data.scheduleForNewAccounts.tiers.map((tier) => `desde ${tier.minAccounts}: ${percent(tier.rate)}`).join(' · ')]] as [string, string][])
                    : []),
                ]}
              />
              <p className="hint">La comisión es un porcentaje de lo que paga cada cuenta sin IGV, desde que el pago se registra. Cada cuenta conserva los términos que regían el día que quedó bajo su cuenta de revendedor.</p>
              <MonthsTable months={data.months} selected={month} onSelect={setMonth} />
            </>
          )
        )}
      </div>
      {month && (
        <div className="card">
          <h2>Comisiones de {monthLabel(month)}</h2>
          <ErrorAlert error={statement.error} />
          {statement.isPending ? <Loading /> : statement.data && <StatementTable statement={statement.data} />}
        </div>
      )}
    </>
  )
}

/** Platform staff: the commissions of one reseller and the settlement of its closed months. */
export function CommissionsModal({ reseller, onClose }: { reseller: ResellerRow; onClose: () => void }) {
  const { hasRole } = useSession()
  const overview = useResellerCommissions(reseller.id)
  const [month, setMonth] = useState<string | null>(null)
  const statement = useCommissionStatement(reseller.id, month)
  const settle = useSettleCommission(reseller.id)
  const toast = useToast()
  const [form, setForm] = useState({ settledOn: todayInLima(), reference: '', note: '' })
  const current = statement.data?.month
  const canSettle = hasRole('PlatformSuperAdmin') && !!current && !current.settlement && current.entries > 0 && !!month && monthClosed(month, todayInLima())

  return (
    <Modal title={`Comisiones de ${reseller.name}`} onClose={onClose}>
      <div className="stack">
        <ErrorAlert error={overview.error ?? statement.error ?? settle.error} />
        {overview.isPending ? (
          <Loading />
        ) : (
          overview.data && (
            <>
              <KeyValues items={[['Cuentas activas', String(overview.data.activeAccounts)], ['Porcentaje actual', overview.data.currentRate === null ? '—' : percent(overview.data.currentRate)]]} />
              <MonthsTable months={overview.data.months} selected={month} onSelect={setMonth} />
            </>
          )
        )}
        {month && (
          <>
            <h3>{monthLabel(month)}</h3>
            {statement.isPending ? <Loading /> : statement.data && <StatementTable statement={statement.data} />}
            {current?.settlement && <p>Liquidado el {date(current.settlement.settledOn)} por {money(current.settlement.total)}{current.settlement.reference ? ` (${current.settlement.reference})` : ''}.</p>}
            {canSettle && (
              <form
                className="stack"
                onSubmit={(event) => {
                  event.preventDefault()
                  settle.mutate({ month, settledOn: form.settledOn, reference: form.reference.trim() || null, note: form.note.trim() || null }, { onSuccess: () => toast.ok('Mes liquidado.') })
                }}
              >
                <h3>Liquidar el mes</h3>
                <TextField label="Fecha de la liquidación" required type="date" max={todayInLima()} value={form.settledOn} onChange={(event) => setForm({ ...form, settledOn: event.target.value })} />
                <TextField label="Referencia" maxLength={100} hint="número de la transferencia" value={form.reference} onChange={(event) => setForm({ ...form, reference: event.target.value })} />
                <TextField label="Nota" maxLength={300} value={form.note} onChange={(event) => setForm({ ...form, note: event.target.value })} />
                <div className="actions">
                  <button className="btn primary" type="submit" disabled={settle.isPending}>
                    Registrar liquidación
                  </button>
                </div>
              </form>
            )}
          </>
        )}
      </div>
    </Modal>
  )
}
