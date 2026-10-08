import { createHmac, randomBytes } from 'node:crypto'
import { execFileSync } from 'node:child_process'
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

// Setup through the API: tests prepare their world with it and exercise the interface for what they check.

export const API_URL = process.env.SF_E2E_API_URL ?? 'http://localhost:5180'

export interface Credentials {
  email: string
  password: string
}

export class ApiFailure extends Error {
  readonly status: number
  readonly body: string

  constructor(status: number, body: string, what: string) {
    super(`${what}: HTTP ${status} ${body.slice(0, 300)}`)
    this.status = status
    this.body = body
  }
}

export class ApiClient {
  private readonly token: string | null

  constructor(token: string | null = null) {
    this.token = token
  }

  async call<T>(method: string, path: string, body?: unknown, headers: Record<string, string> = {}): Promise<T> {
    const response = await fetch(`${API_URL}${path}`, {
      method,
      headers: { 'Content-Type': 'application/json', ...(this.token ? { Authorization: `Bearer ${this.token}` } : {}), ...headers },
      body: body === undefined ? undefined : JSON.stringify(body),
    })
    const text = await response.text()
    if (!response.ok) throw new ApiFailure(response.status, text, `${method} ${path}`)
    return (text ? JSON.parse(text) : undefined) as T
  }

  get = <T>(path: string) => this.call<T>('GET', path)
  post = <T>(path: string, body?: unknown, headers?: Record<string, string>) => this.call<T>('POST', path, body, headers)
  put = <T>(path: string, body: unknown) => this.call<T>('PUT', path, body)
  del = <T>(path: string) => this.call<T>('DELETE', path)
}

export async function login(credentials: Credentials, totpCode?: string): Promise<ApiClient> {
  const tokens = await new ApiClient().post<{ accessToken: string }>('/api/v1/auth/login', { ...credentials, totpCode: totpCode ?? null })
  return new ApiClient(tokens.accessToken)
}

export function adminCredentials(): Credentials {
  const email = process.env.SF_E2E_ADMIN_EMAIL
  const password = process.env.SF_E2E_ADMIN_PASSWORD
  if (!email || !password) {
    throw new Error('Set SF_E2E_ADMIN_EMAIL and SF_E2E_ADMIN_PASSWORD (a platform administrator of the API under test).')
  }
  return { email, password }
}

const unique = () => randomBytes(4).toString('hex')

/** A password that satisfies the policy, different every time. */
export const newPassword = () => `E2e-${randomBytes(6).toString('hex')}-Zz9!`

/** A valid RUC (modulo 11 check digit) that starts with 20, different every time. */
export function newRuc(): string {
  const body = '20' + String(Math.floor(Math.random() * 1e8)).padStart(8, '0')
  const weights = [5, 4, 3, 2, 7, 6, 5, 4, 3, 2]
  const sum = [...body].reduce((total, digit, index) => total + Number(digit) * weights[index], 0)
  return body + String((11 - (sum % 11)) % 10)
}

export interface Tenant {
  id: string
  name: string
  owner: Credentials
  api: ApiClient
}

/** A new tenant with its owner, created by the platform administrator. */
export async function createTenant(label: string): Promise<Tenant> {
  const admin = await login(adminCredentials())
  const name = `E2E ${label} ${unique()}`
  const tenant = await admin.post<{ id: string }>('/api/v1/platform/tenants', { name, environment: 'Sandbox' })
  const owner = { email: `owner-${unique()}@e2e.test`, password: newPassword() }
  await admin.post('/api/v1/users', { ...owner, displayName: `Propietario ${label}`, roles: ['TenantOwner'], tenantId: tenant.id })
  return { id: tenant.id, name, owner, api: await login(owner) }
}

/** A self-signed PFX whose subject carries the RUC, as the certificate of a taxpayer does. Needs the openssl command (OPENSSL overrides its path). */
export function makePfx(ruc: string, password = 'pw'): Buffer {
  const dir = mkdtempSync(join(tmpdir(), 'sf-e2e-'))
  try {
    const openssl = process.env.OPENSSL ?? 'openssl'
    const key = join(dir, 'k.pem')
    const cert = join(dir, 'c.pem')
    const pfx = join(dir, 'c.pfx')
    execFileSync(openssl, ['req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-keyout', key, '-out', cert, '-days', '300', '-subj', `/CN=Representante E2E/OU=${ruc}/O=E2E SAC/C=PE`], { stdio: 'ignore' })
    execFileSync(openssl, ['pkcs12', '-export', '-inkey', key, '-in', cert, '-out', pfx, '-passout', `pass:${password}`], { stdio: 'ignore' })
    return readFileSync(pfx)
  } finally {
    rmSync(dir, { recursive: true, force: true })
  }
}

export interface Company {
  id: string
  ruc: string
  legalName: string
}

export interface SeriesRef {
  id: string
  code: string
}

export interface ReadyCompany {
  company: Company
  series: Record<string, SeriesRef>
}

/** A company ready to issue: certificate, SOL credentials and the series of every document type. */
export async function createReadyCompany(api: ApiClient, legalName = 'Comercial E2E SAC'): Promise<ReadyCompany> {
  const ruc = newRuc()
  const company = await api.post<Company>('/api/v1/companies', {
    ruc,
    details: { legalName, tradeName: null, fiscalAddress: 'Av. Larco 123, Miraflores', ubigeo: '150122', taxRegime: null, contactEmail: null, timeZone: 'America/Lima', defaultCurrency: 'PEN' },
  })
  await api.post('/api/v1/certificates', { companyId: company.id, pfxBase64: makePfx(ruc).toString('base64'), password: 'pw' })
  await api.put('/api/v1/sol-credentials', { companyId: company.id, solUser: 'MODDATOS', solPassword: `Sol-${unique()}-Clave1` })
  const series: Record<string, SeriesRef> = {}
  for (const [type, code] of [['01', 'F001'], ['03', 'B001'], ['07', 'FC01'], ['08', 'FD01']] as const) {
    series[code] = await api.post<SeriesRef>('/api/v1/series', { companyId: company.id, documentTypeCode: type, code })
  }
  return { company, series }
}

/** An invoice or receipt issued through the API (the body of a document request). */
export async function issueDocument(api: ApiClient, body: object): Promise<{ id: string; series: string; number: number }> {
  return api.post('/api/v1/documents', body, { 'Idempotency-Key': randomBytes(16).toString('hex') })
}

export function todayInLima(now = new Date()): string {
  return new Intl.DateTimeFormat('en-CA', { timeZone: 'America/Lima', year: 'numeric', month: '2-digit', day: '2-digit' }).format(now)
}

const BASE32 = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567'

/** The 6-digit TOTP (RFC 6238, SHA-1, 30 s) of a base32 secret, for the second factor of the interface. */
export function totp(secret: string, now = Date.now()): string {
  let bits = ''
  for (const char of secret.replaceAll(' ', '').toUpperCase().replace(/=+$/, '')) bits += BASE32.indexOf(char).toString(2).padStart(5, '0')
  const key = Buffer.from(bits.match(/.{8}/g)!.map((byte) => parseInt(byte, 2)))
  const counter = Buffer.alloc(8)
  counter.writeBigUInt64BE(BigInt(Math.floor(now / 30_000)))
  const hmac = createHmac('sha1', key).update(counter).digest()
  const offset = hmac[hmac.length - 1] & 0x0f
  const code = (hmac.readUInt32BE(offset) & 0x7fffffff) % 1_000_000
  return String(code).padStart(6, '0')
}

export function writeTemp(name: string, content: string): string {
  const dir = mkdtempSync(join(tmpdir(), 'sf-e2e-'))
  const path = join(dir, name)
  writeFileSync(path, content)
  return path
}

export interface IssuedDocument {
  id: string
  series: string
  number: number
  electronicId?: string
}

export const BUYER_RUC = { documentTypeCode: '6', documentNumber: '20100070970', name: 'DISTRIBUIDORA ANDINA SAC' }

/** The line of an invoice with an ISC of 10 % and plastic bags (one per unit). */
export const taxedLine = (description: string, quantity = 3, extra: Record<string, unknown> = {}) => ({
  description,
  unitCode: 'NIU',
  tax: { quantity, unitValue: 100, igvAffectationCode: '10', ...extra },
})

/** An invoice (or receipt) issued through the API, optionally prepared and sent to the sandbox. */
export async function issueInvoice(world: ReadyCompany & { tenant: Tenant }, lines: object[], options: { send?: boolean; receipt?: boolean } = {}): Promise<IssuedDocument> {
  const { api } = world.tenant
  const series = world.series[options.receipt ? 'B001' : 'F001']
  const document = await issueDocument(api, {
    seriesId: series.id,
    issueDate: todayInLima(),
    currency: 'PEN',
    buyer: options.receipt ? { documentTypeCode: '1', documentNumber: '45678912', name: 'MARIA LOPEZ RAMOS' } : BUYER_RUC,
    lines,
  })
  if (!options.send) return document
  const electronic = await api.post<{ id: string }>(`/api/v1/documents/${document.id}/electronic`)
  await api.post(`/api/v1/electronic-documents/${electronic.id}/send`)
  return { ...document, electronicId: electronic.id }
}
