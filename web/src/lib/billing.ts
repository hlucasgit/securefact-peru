import type { BillingPolicyInput, ChargeStatus, CommissionScheduleInput, PaymentInput, PaymentMethod, PlanPriceInput } from '../api/types'
import type { Tone } from './format'

export const CHARGE_LABELS: Record<ChargeStatus, string> = {
  Pending: 'Por pagar',
  Partial: 'Pago parcial',
  Overdue: 'Vencido',
  Paid: 'Pagado',
  Void: 'Anulado',
}

export const CHARGE_TONES: Record<ChargeStatus, Tone> = {
  Pending: 'info',
  Partial: 'warn',
  Overdue: 'bad',
  Paid: 'ok',
  Void: 'neutral',
}

export const PAYMENT_METHODS: Record<PaymentMethod, string> = {
  Transfer: 'Transferencia',
  Deposit: 'Depósito',
  Cash: 'Efectivo',
  Card: 'Tarjeta',
  Other: 'Otro',
}

/** The first day of the month after the one that `today` (yyyy-MM-dd) is in: the earliest date a price, a policy or commission terms can start. */
export function firstOfNextMonth(today: string): string {
  const [year, month] = today.split('-').map(Number)
  return month === 12 ? `${year + 1}-01-01` : `${year}-${String(month + 1).padStart(2, '0')}-01`
}

/** A month (yyyy-MM-01 or yyyy-MM) as the text the API takes in a path. */
export const monthKey = (value: string) => value.slice(0, 7)

/** A month is closed once the current month of Lima is a later one. */
export const monthClosed = (month: string, today: string) => monthKey(month) < today.slice(0, 7)

/** «noviembre de 2026» for a month. */
export function monthLabel(value: string): string {
  const [year, month] = value.split('-').map(Number)
  return new Intl.DateTimeFormat('es-PE', { month: 'long', year: 'numeric', timeZone: 'UTC' }).format(new Date(Date.UTC(year, month - 1, 1)))
}

/** 0.2 as «20 %», 0.125 as «12.5 %». */
export function percent(rate: number): string {
  return `${new Intl.NumberFormat('es-PE', { maximumFractionDigits: 2 }).format(Math.round(rate * 10000) / 100)} %`
}

const optionalNumber = (text: string) => (text.trim() === '' ? null : Number(text))
const optionalText = (text: string) => (text.trim() === '' ? null : text.trim())

export interface PriceForm {
  effectiveFrom: string
  monthlyFee: string
  includedDocuments: string
  overageUnitPrice: string
  note: string
}

/** The overage fields go only for a plan that charges the overage: the others must not send them. */
export function buildPrice(form: PriceForm, allowsOverage: boolean): PlanPriceInput {
  return {
    effectiveFrom: form.effectiveFrom,
    monthlyFee: Number(form.monthlyFee),
    includedDocuments: allowsOverage ? optionalNumber(form.includedDocuments) : null,
    overageUnitPrice: allowsOverage ? optionalNumber(form.overageUnitPrice) : null,
    note: optionalText(form.note),
  }
}

export interface PolicyForm {
  effectiveFrom: string
  dueDays: string
  suspendAfterDays: string
  reminderDays: string
  note: string
}

export function buildPolicy(form: PolicyForm): BillingPolicyInput {
  return { effectiveFrom: form.effectiveFrom, dueDays: Number(form.dueDays), suspendAfterDays: optionalNumber(form.suspendAfterDays), reminderDays: Number(form.reminderDays), note: optionalText(form.note) }
}

export interface TierForm {
  key: string
  minAccounts: string
  percent: string
}

/** The percentage as the person types it (20, 12.5) becomes the share the API takes (0.2, 0.125) without the errors of floating point. */
export function rateFromPercent(text: string): number {
  return Math.round(Number(text) * 100) / 10000
}

export function buildSchedule(effectiveFrom: string, tiers: TierForm[], note: string): CommissionScheduleInput {
  return {
    effectiveFrom,
    tiers: tiers.map((tier) => ({ minAccounts: Number(tier.minAccounts), rate: rateFromPercent(tier.percent) })),
    note: optionalText(note),
  }
}

export interface PaymentForm {
  amount: string
  method: PaymentMethod
  paidOn: string
  reference: string
  note: string
}

export function buildPayment(form: PaymentForm): PaymentInput {
  return { amount: Number(form.amount), method: form.method, paidOn: form.paidOn, reference: optionalText(form.reference), note: optionalText(form.note) }
}
