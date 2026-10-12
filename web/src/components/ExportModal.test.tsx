import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, vi } from 'vitest'
import type { Company } from '../api/types'
import { ExportModal, exportPath, monthStart } from './ExportModal'

const COMPANY = { id: 'c1', ruc: '20100066603', legalName: 'Emisora SAC' } as Company

afterEach(() => {
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
})

describe('the export of a period', () => {
  it('asks for the period of the company and only names the type when one is chosen', () => {
    expect(monthStart('2026-10-11')).toBe('2026-10-01')
    expect(exportPath('c1', '2026-10-01', '2026-10-11', '')).toBe('/api/v1/electronic-documents/export?companyId=c1&from=2026-10-01&to=2026-10-11')
    expect(exportPath('c1', '2026-10-01', '2026-10-11', '01')).toContain('&documentTypeCode=01')
  })

  it('downloads the zip of the company with the period and the type that the person chose', async () => {
    const user = userEvent.setup()
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response(new Blob(['zip']), { status: 200, headers: { 'Content-Type': 'application/zip' } }))
    vi.stubGlobal('URL', Object.assign(URL, { createObjectURL: vi.fn(() => 'blob:zip'), revokeObjectURL: vi.fn() }))
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined)
    const onClose = vi.fn()
    render(<ExportModal companies={[COMPANY]} onClose={onClose} />)

    await user.clear(screen.getByLabelText('Desde'))
    await user.type(screen.getByLabelText('Desde'), '2026-09-01')
    await user.selectOptions(screen.getByLabelText('Tipo'), '03')
    await user.click(screen.getByRole('button', { name: 'Descargar ZIP' }))

    await waitFor(() => expect(onClose).toHaveBeenCalled())
    const url = String(fetchMock.mock.calls[0][0])
    expect(url).toContain('companyId=c1')
    expect(url).toContain('from=2026-09-01')
    expect(url).toContain('documentTypeCode=03')
    expect(click).toHaveBeenCalledTimes(1)
  })

  it('says why the API refused it and stays open', async () => {
    const user = userEvent.setup()
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(
      new Response(JSON.stringify({ code: 'SF-CPE-013', title: 'Demasiados comprobantes', detail: 'El período tiene más de 500 comprobantes: pida un período más corto o un solo tipo.' }), {
        status: 422,
        headers: { 'Content-Type': 'application/json' },
      }),
    )
    const onClose = vi.fn()
    render(<ExportModal companies={[COMPANY]} onClose={onClose} />)

    await user.click(screen.getByRole('button', { name: 'Descargar ZIP' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('más de 500 comprobantes')
    expect(onClose).not.toHaveBeenCalled()
  })
})
