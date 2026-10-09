import { createHmac, timingSafeEqual } from 'node:crypto'
import { createServer, type IncomingMessage } from 'node:http'
import type { AddressInfo } from 'node:net'
import { randomBytes } from 'node:crypto'
import { API_URL, newPassword } from './helpers/api.ts'
import { expect, seriousViolations, signIn, test } from './fixtures.ts'

interface Received {
  headers: IncomingMessage['headers']
  body: string
}

/** A server of the customer, on this machine: the API of the end-to-end stack allows local targets (Webhooks__AllowLocalTargets). */
async function listen(): Promise<{ url: string; requests: Received[]; close: () => Promise<void> }> {
  const requests: Received[] = []
  const server = createServer((request, response) => {
    const chunks: Buffer[] = []
    request.on('data', (chunk: Buffer) => chunks.push(chunk))
    request.on('end', () => {
      requests.push({ headers: request.headers, body: Buffer.concat(chunks).toString('utf8') })
      response.writeHead(200).end('ok')
    })
  })
  await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
  const { port } = server.address() as AddressInfo
  return { url: `http://127.0.0.1:${port}/securefact`, requests, close: () => new Promise((resolve) => server.close(() => resolve())) }
}

test.describe('integraciones', () => {
  test('el propietario crea una llave de API, un programa la usa con su rol y al revocarla deja de servir', async ({ app, world }) => {
    await app.getByRole('link', { name: 'Integraciones' }).click()
    await expect(app.getByRole('heading', { name: 'Integraciones' })).toBeVisible()
    await expect(app.getByText('Aún no hay llaves.')).toBeVisible()

    await app.getByRole('button', { name: 'Nueva llave' }).click()
    const dialog = app.getByRole('dialog')
    await dialog.getByLabel(/Nombre/).fill('Tienda en línea')
    await dialog.getByLabel(/Rol/).selectOption('ReadOnly')
    expect(await seriousViolations(app)).toEqual([])
    await dialog.getByRole('button', { name: 'Crear llave' }).click()

    const secret = (await app.getByLabel('Secreto', { exact: true }).textContent())?.trim() ?? ''
    expect(secret).toMatch(/^sfk_[0-9a-f]{32}_/)
    await expect(app.getByText(/no se vuelve a mostrar/)).toBeVisible()
    await app.getByRole('button', { name: 'Ya la guardé' }).click()
    await expect(app.getByText('Tienda en línea')).toBeVisible()
    await expect(app.getByText(`sfk_…${secret.split('_')[2].slice(0, 6)}`)).toBeVisible()

    // The program: the key reads the companies of the account with the permissions of a reader, and nothing else.
    const asProgram = (path: string, init: RequestInit = {}) => fetch(`${API_URL}${path}`, { ...init, headers: { Authorization: `Bearer ${secret}`, 'Content-Type': 'application/json' } })
    const companies = await asProgram('/api/v1/companies')
    expect(companies.status).toBe(200)
    expect(((await companies.json()) as { id: string }[]).map((company) => company.id)).toContain(world.company.id)
    expect((await asProgram('/api/v1/api-keys')).status).toBe(403)
    expect((await asProgram('/api/v1/companies', { method: 'POST', body: JSON.stringify({ ruc: '20100066603', details: { legalName: 'X SAC', fiscalAddress: 'Av. 1', ubigeo: '150122' } }) })).status).toBe(403)

    // Revoking it ends its use at once.
    app.once('dialog', (confirm) => void confirm.accept())
    await app.getByRole('button', { name: 'Revocar Tienda en línea' }).click()
    await expect(app.getByText('Revocada', { exact: true })).toBeVisible()
    expect((await asProgram('/api/v1/companies')).status).toBe(401)
    expect(await seriousViolations(app)).toEqual([])
  })

  test('el propietario registra un webhook, lo prueba y el servidor recibe el mensaje firmado con su secreto', async ({ app }) => {
    const receiver = await listen()
    try {
      await app.getByRole('link', { name: 'Integraciones' }).click()
      await app.getByRole('tab', { name: 'Webhooks' }).click()
      await expect(app.getByText('Aún no hay webhooks.')).toBeVisible()

      await app.getByRole('button', { name: 'Nuevo webhook' }).click()
      const dialog = app.getByRole('dialog')
      await dialog.getByLabel(/Dirección/).fill(receiver.url)
      await dialog.getByLabel(/Descripción/).fill('Servidor de pruebas')
      expect(await seriousViolations(app)).toEqual([])
      await dialog.getByRole('button', { name: 'Crear webhook' }).click()

      const secret = (await app.getByLabel('Secreto', { exact: true }).textContent())?.trim() ?? ''
      expect(secret).toMatch(/^whsec_/)
      await app.getByRole('button', { name: 'Ya la guardé' }).click()
      await expect(app.getByText('Servidor de pruebas')).toBeVisible()

      await app.getByRole('button', { name: `Probar ${receiver.url}` }).click()
      await expect(app.getByText('La dirección contestó 200: funciona.')).toBeVisible()

      await expect.poll(() => receiver.requests.length).toBe(1)
      const [request] = receiver.requests
      const timestamp = String(request.headers['x-securefact-timestamp'])
      const expected = `v1=${createHmac('sha256', secret).update(`${timestamp}.${request.body}`).digest('hex')}`
      const received = String(request.headers['x-securefact-signature'])
      expect(received.length).toBe(expected.length)
      expect(timingSafeEqual(Buffer.from(received), Buffer.from(expected))).toBe(true)
      expect(request.headers['x-securefact-event']).toBe('webhook.ping')
      expect((JSON.parse(request.body) as { type: string }).type).toBe('webhook.ping')

      // The delivery is in the list, and rotating the secret changes the one that signs.
      await app.getByRole('button', { name: `Entregas de ${receiver.url}` }).click()
      await expect(app.getByRole('dialog').getByRole('cell', { name: 'Entregado' })).toBeVisible()
      await app.keyboard.press('Escape')
      app.once('dialog', (confirm) => void confirm.accept())
      await app.getByRole('button', { name: `Rotar el secreto de ${receiver.url}` }).click()
      const rotated = (await app.getByLabel('Secreto', { exact: true }).textContent())?.trim() ?? ''
      expect(rotated).not.toBe(secret)
      await app.getByRole('button', { name: 'Ya la guardé' }).click()
    } finally {
      await receiver.close()
    }
  })

  test('un lector no ve las integraciones', async ({ world, browser }) => {
    const reader = { email: `reader-${randomBytes(4).toString('hex')}@e2e.test`, password: newPassword() }
    await world.tenant.api.post('/api/v1/users', { ...reader, displayName: 'Lector', roles: ['ReadOnly'] })
    const context = await browser.newContext()
    const page = await context.newPage()
    await signIn(page, reader)
    await expect(page.getByRole('heading', { name: 'Panel' })).toBeVisible()
    await expect(page.getByRole('link', { name: 'Integraciones' })).toHaveCount(0)
    await page.goto('/integraciones')
    await expect(page.getByRole('alert')).toBeVisible()
    await context.close()
  })
})
