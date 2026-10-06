import { adminCredentials, API_URL, login } from './helpers/api.ts'

/** Fails at once, with the reason, when the API under test is not ready: a hundred red tests would not say it. */
export default async function globalSetup(): Promise<void> {
  const health = await fetch(`${API_URL}/health/ready`).catch(() => null)
  if (!health?.ok) {
    throw new Error(`The API is not ready at ${API_URL} (SF_E2E_API_URL). Start it with Sunat__Environment=Sandbox; see web/README.md.`)
  }
  await login(adminCredentials())
}
