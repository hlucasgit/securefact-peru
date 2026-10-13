import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, vi } from 'vitest'
import { ACCOUNT_EXPORT_PATH, AccountData } from './AccountData'

afterEach(() => {
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
})

describe('the data of the account', () => {
  it('downloads the zip of the account and tells what it brings and what it never does', async () => {
    const user = userEvent.setup()
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response(new Blob(['zip']), { status: 200, headers: { 'Content-Type': 'application/zip' } }))
    vi.stubGlobal('URL', Object.assign(URL, { createObjectURL: vi.fn(() => 'blob:zip'), revokeObjectURL: vi.fn() }))
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined)
    render(<AccountData />)

    expect(screen.getByText(/No trae contraseñas, certificados digitales/)).toBeVisible()
    expect(screen.getByText(/XML firmado y el CDR/)).toBeVisible()
    await user.click(screen.getByRole('button', { name: 'Descargar los datos de la cuenta' }))

    await waitFor(() => expect(click).toHaveBeenCalledTimes(1))
    expect(String(fetchMock.mock.calls[0][0])).toBe(ACCOUNT_EXPORT_PATH)
    expect(screen.getByRole('button', { name: 'Descargar los datos de la cuenta' })).toBeEnabled()
  })

  it('says why the API refused it and lets the person try again', async () => {
    const user = userEvent.setup()
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(
      new Response(JSON.stringify({ code: 'SF-AUTH-002', title: 'Prohibido', detail: 'No tiene permiso para exportar los datos de la cuenta.' }), { status: 403, headers: { 'Content-Type': 'application/json' } }),
    )
    render(<AccountData />)

    await user.click(screen.getByRole('button', { name: 'Descargar los datos de la cuenta' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('No tiene permiso')
    expect(screen.getByRole('button', { name: 'Descargar los datos de la cuenta' })).toBeEnabled()
  })
})
