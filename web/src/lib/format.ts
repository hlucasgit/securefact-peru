import type { EDocumentState } from '../api/types'

const CURRENCY_SYMBOLS: Record<string, string> = { PEN: 'S/', USD: 'US$', EUR: '€' }

export function money(amount: number, currency = 'PEN'): string {
  const number = new Intl.NumberFormat('es-PE', { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(amount)
  return `${CURRENCY_SYMBOLS[currency] ?? currency} ${number}`
}

export function date(value: string | null | undefined): string {
  if (!value) return '—'
  const day = value.length === 10 ? new Date(`${value}T00:00:00`) : new Date(value)
  return new Intl.DateTimeFormat('es-PE', { day: '2-digit', month: '2-digit', year: 'numeric' }).format(day)
}

export function dateTime(value: string | null | undefined): string {
  if (!value) return '—'
  return new Intl.DateTimeFormat('es-PE', { day: '2-digit', month: '2-digit', year: 'numeric', hour: '2-digit', minute: '2-digit' }).format(new Date(value))
}

/** Today in Lima as yyyy-MM-dd: the issue date is a civil date of the issuer, not of the browser. */
export function todayInLima(now = new Date()): string {
  return new Intl.DateTimeFormat('en-CA', { timeZone: 'America/Lima', year: 'numeric', month: '2-digit', day: '2-digit' }).format(now)
}

export const DOCUMENT_TYPES: Record<string, string> = {
  '01': 'Factura',
  '03': 'Boleta de venta',
  '07': 'Nota de crédito',
  '08': 'Nota de débito',
}

export function documentName(code: string): string {
  return DOCUMENT_TYPES[code] ?? code
}

export const STATE_LABELS: Record<EDocumentState, string> = {
  Pending: 'Pendiente',
  ReadyToSend: 'Listo para enviar',
  Sending: 'Enviando',
  AwaitingTicket: 'Esperando respuesta',
  Accepted: 'Aceptado',
  AcceptedWithObservations: 'Aceptado con observaciones',
  Rejected: 'Rechazado',
  Failed: 'Falló',
}

export type Tone = 'ok' | 'warn' | 'bad' | 'info' | 'neutral'

export const STATE_TONES: Record<EDocumentState, Tone> = {
  Pending: 'neutral',
  ReadyToSend: 'info',
  Sending: 'info',
  AwaitingTicket: 'info',
  Accepted: 'ok',
  AcceptedWithObservations: 'warn',
  Rejected: 'bad',
  Failed: 'bad',
}

export const ROLE_LABELS: Record<string, string> = {
  PlatformSuperAdmin: 'Superadministrador de plataforma',
  PlatformSupport: 'Soporte de plataforma',
  ResellerAdmin: 'Administrador de revendedor',
  TenantOwner: 'Propietario',
  TenantAdmin: 'Administrador',
  BillingAdmin: 'Facturación',
  Accountant: 'Contador',
  Sales: 'Ventas',
  Developer: 'Desarrollador',
  Auditor: 'Auditor',
  ReadOnly: 'Solo lectura',
}

export const TENANT_ROLES = ['TenantAdmin', 'BillingAdmin', 'Accountant', 'Sales', 'Developer', 'Auditor', 'ReadOnly'] as const

/** Roles that can issue and manage documents. */
export const BILLING_ROLES = ['TenantOwner', 'TenantAdmin', 'BillingAdmin', 'Sales']
export const ADMIN_ROLES = ['TenantOwner', 'TenantAdmin', 'PlatformSuperAdmin']
/** Platform staff belong to no tenant: they administer the platform and have no companies or documents of their own. */
export const PLATFORM_ROLES = ['PlatformSuperAdmin', 'PlatformSupport']
/** Roles with the permission to read the audit trail and to requeue the messages that failed. */
export const AUDIT_ROLES = ['TenantOwner', 'Auditor', 'PlatformSuperAdmin', 'PlatformSupport']
/** Who reads the plan and the consumption of the account (permission tenants.read, without the roles that only read documents). */
export const PLAN_ROLES = ['TenantOwner', 'TenantAdmin', 'BillingAdmin']
export const QUEUE_ROLES = ['TenantOwner', 'TenantAdmin', 'BillingAdmin']
