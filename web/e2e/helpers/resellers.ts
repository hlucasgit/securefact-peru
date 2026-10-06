import { randomBytes } from 'node:crypto'
import { adminCredentials, login, newPassword, type Credentials } from './api.ts'

const unique = () => randomBytes(4).toString('hex')

export interface Reseller {
  id: string
  name: string
  admin: Credentials
}

/** A new reseller with one administrator, created by the platform administrator. */
export async function createReseller(label: string): Promise<Reseller> {
  const admin = await login(adminCredentials())
  const name = `Revendedor ${label} ${unique()}`
  const reseller = await admin.post<{ id: string }>('/api/v1/platform/resellers', { name })
  const credentials = { email: `reseller-${unique()}@e2e.test`, password: newPassword() }
  await admin.post('/api/v1/users', { ...credentials, displayName: `Admin ${label}`, roles: ['ResellerAdmin'], resellerId: reseller.id })
  return { id: reseller.id, name, admin: credentials }
}

/** A private plan of one reseller, with a cap of companies. */
export async function createPrivatePlan(resellerId: string, name: string, maxCompanies: number): Promise<{ id: string; code: string; name: string }> {
  const admin = await login(adminCredentials())
  return admin.post('/api/v1/platform/plans', { code: `e2e-${unique()}`, name, maxCompanies, resellerId })
}
