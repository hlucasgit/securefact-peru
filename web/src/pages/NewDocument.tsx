import { useMemo, useState, type FormEvent } from 'react'
import { useNavigate } from 'react-router-dom'
import { useCatalog, useCompanies, useCustomers, useIssueDocument, useSeries } from '../api/queries'
import { emptyLine, lineIsFree, LinesEditor, toRequestLine, type LineState } from '../components/LinesEditor'
import { ErrorAlert, Loading, PageHeader, SelectField, TextField, useToast } from '../components/ui'
import { DOCUMENT_TYPES, todayInLima } from '../lib/format'

const SUPPORTED_IDENTITY = ['0', '1', '4', '6', '7', 'A']

interface BuyerState {
  documentTypeCode: string
  documentNumber: string
  name: string
  address: string
  email: string
}

const emptyBuyer = (code: string): BuyerState => ({ documentTypeCode: code, documentNumber: '', name: '', address: '', email: '' })

export function NewDocument() {
  const companies = useCompanies()
  const navigate = useNavigate()
  const issue = useIssueDocument()
  const toast = useToast()
  const identity = useCatalog('06')
  const affectations = useCatalog('07')
  const customers = useCustomers('')

  const [companyId, setCompanyId] = useState('')
  const [type, setType] = useState<'01' | '03'>('01')
  const [seriesChoice, setSeriesChoice] = useState('')
  const [issueDate, setIssueDate] = useState(todayInLima())
  const [currencyChoice, setCurrencyChoice] = useState<string | null>(null)
  const [buyer, setBuyer] = useState<BuyerState>(emptyBuyer('6'))
  const [lines, setLines] = useState<LineState[]>([emptyLine()])
  const [credit, setCredit] = useState(false)
  const [installments, setInstallments] = useState<{ amount: string; dueDate: string }[]>([{ amount: '', dueDate: '' }])

  const activeCompanies = useMemo(() => companies.data?.filter((company) => company.status === 'Active') ?? [], [companies.data])
  const selectedCompany = activeCompanies.find((company) => company.id === companyId) ?? activeCompanies[0]
  const effectiveCompanyId = selectedCompany?.id ?? ''
  const series = useSeries(effectiveCompanyId || null)
  const typeSeries = useMemo(() => series.data?.filter((item) => item.isActive && item.documentTypeCode === type) ?? [], [series.data, type])

  // What the user did not choose is derived: the currency of the company and the first series of the type.
  const currency = currencyChoice ?? selectedCompany?.defaultCurrency ?? 'PEN'
  const seriesId = typeSeries.some((item) => item.id === seriesChoice) ? seriesChoice : (typeSeries[0]?.id ?? '')

  function changeType(next: '01' | '03') {
    setType(next)
    setBuyer((current) => (next === '01' && current.documentTypeCode !== '6' ? emptyBuyer('6') : next === '03' && current.documentTypeCode === '6' ? emptyBuyer('1') : current))
  }

  if (companies.isPending) return <Loading />
  if (activeCompanies.length === 0) return <div className="alert info">Registre una empresa antes de emitir comprobantes.</div>

  const noBuyer = buyer.documentTypeCode === '0'

  function fillFrom(customerId: string) {
    const customer = customers.data?.find((item) => item.id === customerId)
    if (customer) setBuyer({ documentTypeCode: customer.documentTypeCode, documentNumber: customer.documentNumber, name: customer.name, address: customer.address ?? '', email: customer.email ?? '' })
  }

  function submit(event: FormEvent) {
    event.preventDefault()
    const body = {
      seriesId,
      issueDate,
      currency,
      buyer: {
        documentTypeCode: buyer.documentTypeCode,
        documentNumber: noBuyer ? '-' : buyer.documentNumber.trim(),
        name: noBuyer ? 'CLIENTES VARIOS' : buyer.name.trim(),
        address: buyer.address.trim() || null,
        email: buyer.email.trim() || null,
      },
      lines: lines.map((line) => toRequestLine(line, lineIsFree(line.affectation, affectations.data))),
      ...(credit && type === '01' ? { installments: installments.map((item) => ({ amount: Number(item.amount), dueDate: item.dueDate })) } : {}),
    }
    issue.mutate(body, {
      onSuccess: (document) => {
        toast.ok(`${DOCUMENT_TYPES[type]} ${document.series}-${document.number} emitida.`)
        void navigate(`/documentos/${document.id}`)
      },
    })
  }

  return (
    <>
      <PageHeader title="Emitir comprobante" subtitle="Los importes y los impuestos los calcula el sistema al emitir." />
      <form className="stack" onSubmit={submit}>
        <ErrorAlert error={issue.error} />
        <div className="card">
          <h2>Comprobante</h2>
          <div className="form-grid">
            {activeCompanies.length > 1 && (
              <SelectField label="Empresa" value={effectiveCompanyId} onChange={(event) => setCompanyId(event.target.value)}>
                {activeCompanies.map((company) => <option key={company.id} value={company.id}>{company.ruc} · {company.legalName}</option>)}
              </SelectField>
            )}
            <SelectField label="Tipo" value={type} onChange={(event) => changeType(event.target.value as '01' | '03')}>
              <option value="01">{DOCUMENT_TYPES['01']}</option>
              <option value="03">{DOCUMENT_TYPES['03']}</option>
            </SelectField>
            <SelectField label="Serie" required value={seriesId} onChange={(event) => setSeriesChoice(event.target.value)} error={typeSeries.length === 0 && !series.isPending ? 'No hay series activas de este tipo: cree una en la empresa.' : undefined}>
              {typeSeries.map((item) => <option key={item.id} value={item.id}>{item.code}</option>)}
            </SelectField>
            <TextField label="Fecha de emisión" type="date" required max={todayInLima()} value={issueDate} onChange={(event) => setIssueDate(event.target.value)} />
            <SelectField label="Moneda" value={currency} onChange={(event) => setCurrencyChoice(event.target.value)}>
              <option value="PEN">Soles (PEN)</option>
              <option value="USD">Dólares (USD)</option>
            </SelectField>
          </div>
        </div>

        <div className="card">
          <h2>Cliente</h2>
          <div className="stack">
            {(customers.data?.length ?? 0) > 0 && (
              <SelectField label="Cliente guardado" hint="(opcional)" value="" onChange={(event) => fillFrom(event.target.value)}>
                <option value="">Seleccionar…</option>
                {customers.data?.filter((customer) => customer.isActive).map((customer) => <option key={customer.id} value={customer.id}>{customer.documentNumber} · {customer.name}</option>)}
              </SelectField>
            )}
            <div className="form-grid">
              <SelectField label="Tipo de documento" value={buyer.documentTypeCode} onChange={(event) => setBuyer({ ...buyer, documentTypeCode: event.target.value })}>
                {(identity.data ?? []).filter((entry) => SUPPORTED_IDENTITY.includes(entry.code) && (type === '03' || entry.code !== '0')).map((entry) => <option key={entry.code} value={entry.code}>{entry.description}</option>)}
              </SelectField>
              <TextField label="Número" required={!noBuyer} disabled={noBuyer} value={noBuyer ? '' : buyer.documentNumber} onChange={(event) => setBuyer({ ...buyer, documentNumber: event.target.value })} />
              <TextField label="Nombre o razón social" required={!noBuyer} disabled={noBuyer} value={noBuyer ? 'CLIENTES VARIOS' : buyer.name} onChange={(event) => setBuyer({ ...buyer, name: event.target.value })} />
              <TextField label="Dirección" value={buyer.address} onChange={(event) => setBuyer({ ...buyer, address: event.target.value })} />
              <TextField label="Correo" type="email" value={buyer.email} onChange={(event) => setBuyer({ ...buyer, email: event.target.value })} />
            </div>
            {type === '03' && noBuyer && <p className="muted">Una boleta de más de S/ 700 exige identificar al cliente.</p>}
          </div>
        </div>

        <div className="card">
          <h2>Ítems</h2>
          <LinesEditor lines={lines} onChange={setLines} />
        </div>

        {type === '01' && (
          <div className="card stack">
            <h2>Forma de pago</h2>
            <label className="checkbox">
              <input type="checkbox" checked={credit} onChange={(event) => setCredit(event.target.checked)} />
              <span>Venta al crédito (cuotas)</span>
            </label>
            {credit && (
              <>
                {installments.map((item, index) => (
                  <div className="form-grid" key={index}>
                    <TextField label={`Cuota ${index + 1}: monto`} type="number" min="0" step="0.01" required value={item.amount} onChange={(event) => setInstallments(installments.map((row, i) => (i === index ? { ...row, amount: event.target.value } : row)))} />
                    <TextField label="Vencimiento" type="date" required min={issueDate} value={item.dueDate} onChange={(event) => setInstallments(installments.map((row, i) => (i === index ? { ...row, dueDate: event.target.value } : row)))} />
                  </div>
                ))}
                <div className="actions">
                  <button className="btn" type="button" onClick={() => setInstallments([...installments, { amount: '', dueDate: '' }])}>Agregar cuota</button>
                  {installments.length > 1 && <button className="btn" type="button" onClick={() => setInstallments(installments.slice(0, -1))}>Quitar última</button>}
                </div>
                <p className="muted">Las cuotas deben sumar el total del comprobante.</p>
              </>
            )}
          </div>
        )}

        <div className="actions" style={{ justifyContent: 'flex-end' }}>
          <button className="btn primary" type="submit" disabled={issue.isPending || !seriesId}>
            {issue.isPending ? 'Emitiendo…' : 'Emitir'}
          </button>
        </div>
      </form>
    </>
  )
}
