import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, vi } from 'vitest'
import { ToastProvider } from '../components/ui'
import { Collections, CommissionsModal, MyCharges, Pricing, ResellerCommissions } from './Billing'

const mocks = vi.hoisted(() => ({ roles: ['PlatformSuperAdmin'] as string[] }))

vi.mock('../auth/session', () => ({ useSession: () => ({ hasRole: (...roles: string[]) => roles.some((role) => mocks.roles.includes(role)) }) }))

const json = (status: number, body: unknown) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })

const CHARGE = {
  id: 'c1',
  tenantId: 't1',
  tenantName: 'Cliente Uno SAC',
  period: '2026-11-01',
  planCode: 'pro',
  planName: 'Profesional',
  monthlyFee: 100,
  includedDocuments: 200,
  documentsIssued: 230,
  overageDocuments: 30,
  overageUnitPrice: 0.5,
  overageAmount: 15,
  netAmount: 115,
  taxRate: 0.18,
  taxAmount: 20.7,
  totalAmount: 135.7,
  currency: 'PEN',
  issuedOn: '2026-12-02',
  dueOn: '2026-12-12',
  suspendOn: '2026-12-27',
  paidAmount: 35.7,
  balance: 100,
  status: 'Partial',
  voidReason: null,
  createdAt: '2026-12-02T10:00:00Z',
}

const DETAIL = {
  charge: CHARGE,
  payments: [{ id: 'p1', chargeId: 'c1', amount: 35.7, method: 'Transfer', reference: 'Op. 4521', paidOn: '2026-12-03', note: null, reversesPaymentId: null, recordedAt: '2026-12-03T10:00:00Z' }],
}

function renderPage(page: React.ReactNode) {
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })}>
      <MemoryRouter>
        <ToastProvider>{page}</ToastProvider>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

afterEach(() => {
  vi.useRealTimers()
  vi.restoreAllMocks()
  mocks.roles = ['PlatformSuperAdmin']
})

describe('Collections', () => {
  it('lists the charges, opens one with its parts and registers a payment for the balance', async () => {
    const user = userEvent.setup()
    // The payment cannot be older than the charge nor in the future: the day of the test is after the issue.
    vi.useFakeTimers({ toFake: ['Date'], now: new Date('2026-12-10T15:00:00Z') })
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockImplementation((input, init) => {
      const url = String(input)
      if (init?.method === 'POST') return Promise.resolve(json(201, { id: 'p2' }))
      if (url.includes('/api/v1/platform/charges/c1')) return Promise.resolve(json(200, DETAIL))
      return Promise.resolve(json(200, [CHARGE]))
    })
    renderPage(<Collections />)

    expect(await screen.findByText('Cliente Uno SAC')).toBeVisible()
    expect(within(screen.getByRole('table')).getByText('Pago parcial')).toBeVisible()
    await user.click(screen.getByRole('button', { name: /Ver cargo de noviembre de 2026 de Cliente Uno SAC/ }))

    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).getByText('30 × S/ 0.50 = S/ 15.00')).toBeVisible()
    expect(within(dialog).getByText('230 (incluye 200)')).toBeVisible()
    expect(within(dialog).getByText('IGV (18 %)')).toBeVisible()
    expect(within(dialog).getByLabelText(/Monto/)).toHaveValue(100)

    await user.click(within(dialog).getByRole('button', { name: 'Registrar pago' }))

    expect(await screen.findByText('Pago registrado.')).toBeVisible()
    const post = fetchMock.mock.calls.find(([, init]) => init?.method === 'POST')
    expect(post?.[0]).toBe('/api/v1/platform/charges/c1/payments')
    expect(JSON.parse(String(post?.[1]?.body))).toMatchObject({ amount: 100, method: 'Transfer', reference: null })
  })

  it('reverses a payment with its reason', async () => {
    const user = userEvent.setup()
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockImplementation((input, init) => {
      const url = String(input)
      if (init?.method === 'POST') return Promise.resolve(json(201, { id: 'p3' }))
      if (url.includes('/api/v1/platform/charges/c1')) return Promise.resolve(json(200, DETAIL))
      return Promise.resolve(json(200, [CHARGE]))
    })
    renderPage(<Collections />)
    await user.click(await screen.findByRole('button', { name: /Ver cargo de noviembre/ }))
    const dialog = await screen.findByRole('dialog')

    await user.click(await within(dialog).findByRole('button', { name: /Revertir el pago de S\/ 35.70/ }))
    await user.type(within(dialog).getByLabelText('Motivo de la reversa'), 'Cuenta equivocada')
    await user.click(within(dialog).getByRole('button', { name: 'Confirmar reversa' }))

    expect(await screen.findByText('Pago revertido.')).toBeVisible()
    const post = fetchMock.mock.calls.find(([, init]) => init?.method === 'POST')
    expect(post?.[0]).toBe('/api/v1/platform/payments/p1/reverse')
    expect(JSON.parse(String(post?.[1]?.body))).toEqual({ reason: 'Cuenta equivocada' })
  })

  it('says so when there are no charges and hides the actions from support', async () => {
    mocks.roles = ['PlatformSupport']
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(json(200, []))
    renderPage(<Collections />)

    expect(await screen.findByText('No hay cargos con esos filtros.')).toBeVisible()
    expect(screen.queryByRole('button', { name: 'Ejecutar la cobranza ahora' })).toBeNull()
  })

  it('runs the collection and says what it did', async () => {
    const user = userEvent.setup()
    vi.spyOn(globalThis, 'fetch').mockImplementation((_input, init) =>
      Promise.resolve(init?.method === 'POST' ? json(200, { chargesCreated: 3, tenantsSuspended: 1, tenantsReactivated: 0 }) : json(200, [])),
    )
    renderPage(<Collections />)

    await user.click(await screen.findByRole('button', { name: 'Ejecutar la cobranza ahora' }))

    expect(await screen.findByText('Cobranza ejecutada: 3 cargos nuevos, 1 cuentas suspendidas, 0 reactivadas.')).toBeVisible()
  })
})

describe('MyCharges', () => {
  it('shows the price the account keeps and its charges, without actions on them', async () => {
    const user = userEvent.setup()
    vi.spyOn(globalThis, 'fetch').mockImplementation((input) => {
      const url = String(input)
      if (url.endsWith('/api/v1/subscription')) {
        return Promise.resolve(
          json(200, {
            tenantId: 't1',
            planId: 'p',
            planCode: 'pro',
            planName: 'Profesional',
            allowsOverage: true,
            planAssignedAt: '2026-10-09T10:00:00Z',
            price: { id: 'x', planId: 'p', version: 2, effectiveFrom: '2026-09-01', monthlyFee: 100, includedDocuments: 200, overageUnitPrice: 0.5, note: null },
            firstChargePeriod: '2026-11-01',
          }),
        )
      }
      if (url.includes('/api/v1/charges/c1')) return Promise.resolve(json(200, DETAIL))
      return Promise.resolve(json(200, [CHARGE]))
    })
    renderPage(<MyCharges />)

    expect(await screen.findByText('S/ 100.00 más IGV')).toBeVisible()
    expect(screen.getByText('200 por mes')).toBeVisible()
    expect(screen.getByText('noviembre de 2026', { selector: 'dd' })).toBeVisible()
    await user.click(await screen.findByRole('button', { name: /Ver cargo de noviembre de 2026/ }))
    const dialog = await screen.findByRole('dialog')
    expect(await within(dialog).findByText('Op. 4521')).toBeVisible()
    expect(within(dialog).queryByRole('button', { name: 'Registrar pago' })).toBeNull()
    expect(within(dialog).queryByRole('button', { name: /Revertir/ })).toBeNull()
  })

  it('says that a plan without price does not charge', async () => {
    vi.spyOn(globalThis, 'fetch').mockImplementation((input) =>
      Promise.resolve(String(input).endsWith('/api/v1/subscription') ? json(200, { tenantId: 't', planId: 'p', planCode: 'pilot', planName: 'Piloto', allowsOverage: false, planAssignedAt: '2026-10-09T10:00:00Z', price: null, firstChargePeriod: null }) : json(200, [])),
    )
    renderPage(<MyCharges />)

    expect(await screen.findByText(/no tiene precio/)).toBeVisible()
    expect(await screen.findByText('Aún no tiene cargos.')).toBeVisible()
  })
})

describe('Pricing', () => {
  const PLANS = [
    { id: 'plan-a', code: 'basico', name: 'Básico', maxCompanies: null, maxUsers: null, maxDocumentsPerMonth: null, isActive: true, resellerId: null, allowsOverage: false },
    { id: 'plan-b', code: 'medido', name: 'Medido', maxCompanies: null, maxUsers: null, maxDocumentsPerMonth: null, isActive: true, resellerId: null, allowsOverage: true },
  ]

  it('publishes a price version and asks for the overage only on a plan that charges it', async () => {
    const user = userEvent.setup()
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockImplementation((input, init) => {
      const url = String(input)
      if (init?.method === 'POST') return Promise.resolve(json(201, {}))
      if (url.endsWith('/prices')) return Promise.resolve(json(200, [{ id: 'v1', planId: 'plan-b', version: 1, effectiveFrom: '2026-11-01', monthlyFee: 100, includedDocuments: 200, overageUnitPrice: 0.5, note: 'Lanzamiento' }]))
      return Promise.resolve(json(200, PLANS))
    })
    renderPage(<Pricing />)

    await screen.findByRole('option', { name: 'Medido (medido)' })
    await user.selectOptions(screen.getByLabelText('Plan'), 'plan-b')
    expect(await screen.findByText('Lanzamiento')).toBeVisible()
    await user.click(screen.getByRole('button', { name: 'Publicar una versión' }))
    const dialog = await screen.findByRole('dialog')
    await user.clear(within(dialog).getByLabelText(/Rige desde/))
    await user.type(within(dialog).getByLabelText(/Rige desde/), '2027-01-01')
    await user.type(within(dialog).getByLabelText(/Cuota mensual/), '120')
    await user.type(within(dialog).getByLabelText(/Comprobantes incluidos/), '300')
    await user.type(within(dialog).getByLabelText(/Precio de cada comprobante adicional/), '0.4')
    await user.click(within(dialog).getByRole('button', { name: 'Publicar precio' }))

    expect(await screen.findByText('Precio publicado.')).toBeVisible()
    const post = fetchMock.mock.calls.find(([, init]) => init?.method === 'POST')
    expect(post?.[0]).toBe('/api/v1/platform/plans/plan-b/prices')
    expect(JSON.parse(String(post?.[1]?.body))).toEqual({ effectiveFrom: '2027-01-01', monthlyFee: 120, includedDocuments: 300, overageUnitPrice: 0.4, note: null })
  })

  it('does not ask for the overage on a plan that refuses documents over its cap', async () => {
    const user = userEvent.setup()
    vi.spyOn(globalThis, 'fetch').mockImplementation((input) => Promise.resolve(String(input).endsWith('/prices') ? json(200, []) : json(200, PLANS)))
    renderPage(<Pricing />)

    await screen.findByRole('option', { name: 'Básico (basico)' })
    await user.selectOptions(screen.getByLabelText('Plan'), 'plan-a')
    expect(await screen.findByText(/aún no tiene precio/)).toBeVisible()
    await user.click(screen.getByRole('button', { name: 'Publicar una versión' }))

    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).queryByLabelText(/Comprobantes incluidos/)).toBeNull()
  })

  it('publishes a billing policy that never suspends and commission terms with their tiers', async () => {
    const user = userEvent.setup()
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockImplementation((input, init) => {
      const url = String(input)
      if (init?.method === 'POST') return Promise.resolve(json(201, {}))
      if (url.endsWith('/billing-policies')) return Promise.resolve(json(200, [{ id: 'bp', version: 1, effectiveFrom: '2026-01-01', dueDays: 10, suspendAfterDays: 15, note: null }]))
      if (url.endsWith('/commission-schedules')) return Promise.resolve(json(200, [{ id: 'cs', version: 1, effectiveFrom: '2026-01-01', tiers: [{ minAccounts: 0, rate: 0.2 }, { minAccounts: 10, rate: 0.25 }], note: null }]))
      return Promise.resolve(json(200, PLANS))
    })
    renderPage(<Pricing />)

    await user.click(screen.getByRole('tab', { name: 'Política de cobranza' }))
    expect(await screen.findByText('15 días')).toBeVisible()
    await user.click(screen.getByRole('button', { name: 'Publicar una política' }))
    let dialog = await screen.findByRole('dialog')
    await user.type(within(dialog).getByLabelText(/Rige desde/), '2027-03-01')
    await user.clear(within(dialog).getByLabelText(/Gracia antes de suspender/))
    await user.click(within(dialog).getByRole('button', { name: 'Publicar política' }))
    expect(await screen.findByText('Política publicada.')).toBeVisible()
    expect(JSON.parse(String(fetchMock.mock.calls.find(([url, init]) => init?.method === 'POST' && String(url).endsWith('billing-policies'))?.[1]?.body))).toEqual({
      effectiveFrom: '2027-03-01',
      dueDays: 10,
      suspendAfterDays: null,
      note: null,
    })

    await user.click(screen.getByRole('tab', { name: 'Comisiones' }))
    expect(await screen.findByText('desde 0: 20 % · desde 10: 25 %')).toBeVisible()
    await user.click(screen.getByRole('button', { name: 'Publicar términos' }))
    dialog = await screen.findByRole('dialog')
    await user.type(within(dialog).getByLabelText(/Rige desde/), '2027-03-01')
    await user.click(within(dialog).getByRole('button', { name: 'Quitar tramo 3' }))
    await user.click(within(dialog).getByRole('button', { name: 'Publicar términos' }))
    expect(await screen.findByText('Términos publicados.')).toBeVisible()
    expect(JSON.parse(String(fetchMock.mock.calls.find(([url, init]) => init?.method === 'POST' && String(url).endsWith('commission-schedules'))?.[1]?.body))).toEqual({
      effectiveFrom: '2027-03-01',
      tiers: [{ minAccounts: 0, rate: 0.2 }, { minAccounts: 10, rate: 0.25 }],
      note: null,
    })
  })
})

const ENTRY = { id: 'e1', tenantId: 't1', tenantName: 'Cliente Uno SAC', chargePeriod: '2026-11-01', paymentId: 'p1', month: '2026-12-01', baseAmount: 100, rate: 0.2, amount: 20 }

const OVERVIEW = {
  resellerId: 'r1',
  activeAccounts: 3,
  scheduleForNewAccounts: { id: 'cs', version: 1, effectiveFrom: '2026-01-01', tiers: [{ minAccounts: 0, rate: 0.2 }, { minAccounts: 10, rate: 0.25 }], note: null },
  currentRate: 0.2,
  months: [{ month: '2026-12-01', total: 20, entries: 1, settlement: null }],
}

describe('Commissions', () => {
  it('shows the reseller its rate, its months and the entries of one month', async () => {
    const user = userEvent.setup()
    vi.spyOn(globalThis, 'fetch').mockImplementation((input) => {
      const url = String(input)
      if (url.endsWith('/api/v1/reseller/commissions/2026-12')) return Promise.resolve(json(200, { resellerId: 'r1', month: OVERVIEW.months[0], items: [ENTRY] }))
      return Promise.resolve(json(200, OVERVIEW))
    })
    renderPage(<ResellerCommissions />)

    expect(await screen.findByText('Porcentaje actual')).toBeVisible()
    expect(screen.getByText('desde 0: 20 % · desde 10: 25 %')).toBeVisible()
    await user.click(screen.getByRole('button', { name: 'Ver diciembre de 2026' }))

    expect(await screen.findByText('Comisiones de diciembre de 2026')).toBeVisible()
    expect(await screen.findByText('Cliente Uno SAC')).toBeVisible()
    expect(screen.getAllByText('S/ 20.00').length).toBeGreaterThan(0)
  })

  it('says so when the reseller has not earned anything yet', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(json(200, { ...OVERVIEW, months: [], currentRate: null, scheduleForNewAccounts: null }))
    renderPage(<ResellerCommissions />)

    expect(await screen.findByText('Aún no hay comisiones.')).toBeVisible()
  })

  it('lets the super administrator settle a closed month and shows a settled one without the form', async () => {
    const user = userEvent.setup()
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockImplementation((input, init) => {
      const url = String(input)
      if (init?.method === 'POST') return Promise.resolve(json(200, {}))
      if (url.endsWith('/commissions/2026-12')) return Promise.resolve(json(200, { resellerId: 'r1', month: OVERVIEW.months[0], items: [ENTRY] }))
      if (url.endsWith('/commissions/2026-11')) {
        return Promise.resolve(
          json(200, { resellerId: 'r1', month: { month: '2026-11-01', total: 10, entries: 1, settlement: { id: 's', resellerId: 'r1', month: '2026-11-01', total: 10, entries: 1, settledOn: '2026-12-05', reference: 'Transferencia 77', note: null } }, items: [] }),
        )
      }
      return Promise.resolve(json(200, { ...OVERVIEW, months: [...OVERVIEW.months, { month: '2026-11-01', total: 10, entries: 1, settlement: null }] }))
    })
    vi.useFakeTimers({ toFake: ['Date'], now: new Date('2027-01-10T12:00:00Z') })
    try {
      renderPage(<CommissionsModal reseller={{ id: 'r1', name: 'Canal Uno', isActive: true, tenantCount: 3, createdAt: '2026-01-01T00:00:00Z' }} onClose={() => undefined} />)

      await user.click(await screen.findByRole('button', { name: 'Ver diciembre de 2026' }))
      await user.type(await screen.findByLabelText(/Referencia/), 'Transferencia 889')
      await user.click(screen.getByRole('button', { name: 'Registrar liquidación' }))

      expect(await screen.findByText('Mes liquidado.')).toBeVisible()
      const post = fetchMock.mock.calls.find(([, init]) => init?.method === 'POST')
      expect(post?.[0]).toBe('/api/v1/platform/resellers/r1/commissions/2026-12/settle')
      expect(JSON.parse(String(post?.[1]?.body))).toMatchObject({ settledOn: '2027-01-10', reference: 'Transferencia 889', note: null })

      await user.click(screen.getByRole('button', { name: 'Ver noviembre de 2026' }))
      expect(await screen.findByText(/Liquidado el 05\/12\/2026 por S\/ 10.00 \(Transferencia 77\)/)).toBeVisible()
      expect(screen.queryByRole('button', { name: 'Registrar liquidación' })).toBeNull()
    } finally {
      vi.useRealTimers()
    }
  })
})
