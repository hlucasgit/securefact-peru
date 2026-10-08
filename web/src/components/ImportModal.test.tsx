import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, vi } from 'vitest'
import { ImportModal } from './ImportModal'

const json = (status: number, body: unknown) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })

const PREVIEW = {
  committed: false,
  total: 3,
  ready: 1,
  created: 0,
  existing: 1,
  invalid: 1,
  rows: [
    { line: 2, status: 'Ready', key: '6-20100066603', message: null },
    { line: 3, status: 'Existing', key: '1-45678912', message: 'Ya existe un cliente con ese documento; no se modifica.' },
    { line: 4, status: 'Invalid', key: '6-20123456789', message: 'El RUC no es válido.' },
  ],
}

function renderModal(kind: 'customers' | 'products' = 'customers') {
  const onClose = vi.fn()
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { mutations: { retry: false } } })}>
      <ImportModal kind={kind} onClose={onClose} />
    </QueryClientProvider>,
  )
  return onClose
}

const file = (text: string) => new File([text], 'clientes.csv', { type: 'text/csv' })

afterEach(() => vi.restoreAllMocks())

describe('ImportModal', () => {
  it('previews the file as soon as it is chosen, explains the rows that are left out and imports only on request', async () => {
    const user = userEvent.setup()
    const fetchMock = vi
      .spyOn(globalThis, 'fetch')
      .mockResolvedValueOnce(json(200, PREVIEW))
      .mockResolvedValueOnce(json(200, { ...PREVIEW, committed: true, ready: 0, created: 1 }))
    renderModal()

    await user.upload(screen.getByLabelText('Archivo CSV'), file('tipo_documento,numero_documento,nombre\n6,20100066603,Uno\n'))

    const summary = await screen.findByText('Revisión del archivo.')
    expect(summary).toBeVisible()
    expect(screen.getByText('1 por crear')).toBeVisible()
    expect(screen.getByText('1 ya existían')).toBeVisible()
    expect(screen.getByText('1 con error')).toBeVisible()
    const table = screen.getByRole('table')
    expect(within(table).getByText('El RUC no es válido.')).toBeVisible()
    expect(within(table).getByText('Ya existe un cliente con ese documento; no se modifica.')).toBeVisible()
    expect(within(table).queryByText('6-20100066603')).toBeNull() // a row that is fine is not listed among the problems
    const [firstPath, firstInit] = fetchMock.mock.calls[0]
    expect(firstPath).toBe('/api/v1/customers/import')
    expect(JSON.parse(String(firstInit?.body))).toMatchObject({ commit: false })
    expect(fetchMock).toHaveBeenCalledTimes(1) // nothing is written until the person asks

    await user.click(screen.getByRole('button', { name: 'Importar 1 fila' }))

    expect(await screen.findByText('Importación terminada.')).toBeVisible()
    expect(screen.getByText('1 creados')).toBeVisible()
    expect(JSON.parse(String(fetchMock.mock.calls[1][1]?.body))).toMatchObject({ commit: true })
    expect(screen.queryByRole('button', { name: /Importar \d/ })).toBeNull()
    expect(screen.getByRole('button', { name: 'Cerrar' })).toBeVisible()
  })

  it('shows the reason when the API refuses the whole file and offers no import', async () => {
    const user = userEvent.setup()
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(json(422, { title: 'Archivo inválido', detail: 'Faltan columnas obligatorias: «numero_documento» y «nombre».', code: 'SF-IMP-001' }))
    renderModal()

    await user.upload(screen.getByLabelText('Archivo CSV'), file('a,b\n1,2\n'))

    expect(await screen.findByRole('alert')).toHaveTextContent('Faltan columnas obligatorias')
    expect(screen.queryByRole('button', { name: /Importar \d/ })).toBeNull()
  })

  it('does not send a file that is too big', async () => {
    const user = userEvent.setup()
    const fetchMock = vi.spyOn(globalThis, 'fetch')
    renderModal('products')

    await user.upload(screen.getByLabelText('Archivo CSV'), file('x'.repeat(1_000_001)))

    expect(await screen.findByRole('alert')).toHaveTextContent('demasiado grande')
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('offers the template of the kind and closes', async () => {
    const user = userEvent.setup()
    const onClose = renderModal('products')

    expect(screen.getByText(/afectacion_igv: código del catálogo 07, obligatorio/)).toBeVisible()
    expect(screen.getByRole('button', { name: 'Descargar la plantilla' })).toBeEnabled()
    await user.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(onClose).toHaveBeenCalled()
  })
})
