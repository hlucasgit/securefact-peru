import { useState } from 'react'
import { fetchBlob } from '../api/http'
import type { Company } from '../api/types'
import { DOCUMENT_TYPES, todayInLima } from '../lib/format'
import { save } from '../lib/download'
import { ErrorAlert, Modal, SelectField, TextField } from './ui'

/** The first day of the month of a date as AAAA-MM-DD. */
export function monthStart(day: string): string {
  return `${day.slice(0, 7)}-01`
}

/** The query of the export of a period (ADR-071). */
export function exportPath(companyId: string, from: string, to: string, documentTypeCode: string): string {
  const query = new URLSearchParams({ companyId, from, to })
  if (documentTypeCode) query.set('documentTypeCode', documentTypeCode)
  return `/api/v1/electronic-documents/export?${query.toString()}`
}

/** A period of the documents of a company in one ZIP: each signed XML and each CDR as they are kept, and an index that opens in a spreadsheet (ADR-071). */
export function ExportModal({ companies, onClose }: { companies: Company[]; onClose: () => void }) {
  const today = todayInLima()
  const [form, setForm] = useState({ companyId: companies[0]?.id ?? '', from: monthStart(today), to: today, type: '' })
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<unknown>(null)
  const company = companies.find((item) => item.id === form.companyId)

  async function download() {
    setBusy(true)
    setError(null)
    try {
      const blob = await fetchBlob(exportPath(form.companyId, form.from, form.to, form.type))
      save(blob, `comprobantes-${company?.ruc ?? 'empresa'}-${form.from.replaceAll('-', '')}-${form.to.replaceAll('-', '')}${form.type ? `-${form.type}` : ''}.zip`)
      onClose()
    } catch (failure) {
      setError(failure)
    } finally {
      setBusy(false)
    }
  }

  return (
    <Modal title="Exportar comprobantes" onClose={onClose}>
      <form
        className="stack"
        onSubmit={(event) => {
          event.preventDefault()
          void download()
        }}
      >
        <p className="hint" style={{ margin: 0 }}>
          Un ZIP con el XML firmado y el CDR de SUNAT de cada comprobante del período, tal como se guardan, y un índice (CSV) con su estado y su total. Hasta 500 comprobantes y un año por exportación.
        </p>
        <ErrorAlert error={error} />
        <SelectField label="Empresa" value={form.companyId} onChange={(event) => setForm({ ...form, companyId: event.target.value })}>
          {companies.map((item) => (
            <option key={item.id} value={item.id}>
              {item.ruc} · {item.legalName}
            </option>
          ))}
        </SelectField>
        <div className="row">
          <TextField label="Desde" type="date" required max={form.to} value={form.from} onChange={(event) => setForm({ ...form, from: event.target.value })} />
          <TextField label="Hasta" type="date" required min={form.from} max={today} value={form.to} onChange={(event) => setForm({ ...form, to: event.target.value })} />
        </div>
        <SelectField label="Tipo" value={form.type} onChange={(event) => setForm({ ...form, type: event.target.value })}>
          <option value="">Todos</option>
          {Object.entries(DOCUMENT_TYPES).map(([code, name]) => (
            <option key={code} value={code}>
              {name}
            </option>
          ))}
        </SelectField>
        <div className="actions">
          <button className="btn" type="button" onClick={onClose}>
            Cancelar
          </button>
          <button className="btn primary" type="submit" disabled={busy || !form.companyId || !form.from || !form.to}>
            {busy ? 'Preparando…' : 'Descargar ZIP'}
          </button>
        </div>
      </form>
    </Modal>
  )
}
