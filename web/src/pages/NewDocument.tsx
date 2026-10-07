import { useMemo, useState, type FormEvent } from 'react'
import { useNavigate } from 'react-router-dom'
import { useCatalog, useCompanies, useCustomers, useIssueDocument, usePreviewDocument, useSeries } from '../api/queries'
import { emptyLine, lineIsFree, LinesEditor, toRequestLine, type LineState } from '../components/LinesEditor'
import { ErrorAlert, Loading, PageHeader, SelectField, TextField, useToast } from '../components/ui'
import { DOCUMENT_TYPES, money, todayInLima } from '../lib/format'
import {
  buildDocumentRequest,
  buyerMustBeForeign,
  EXEMPT_TAX_CODE,
  EXEMPTION_LEGENDS,
  EXPORT_AFFECTATION,
  exportsFor,
  FOREIGN_IDENTITY,
  isExport,
  lineDetailOf,
  needsUsageCountry,
  noDeduction,
  type DeductionInput,
} from '../lib/operations'

const SUPPORTED_IDENTITY = ['0', '1', '4', '6', '7', 'A']

interface BuyerState {
  documentTypeCode: string
  documentNumber: string
  name: string
  address: string
  email: string
}

const emptyBuyer = (code: string): BuyerState => ({ documentTypeCode: code, documentNumber: '', name: '', address: '', email: '' })

/** What the API calculated for the document as it stands, with the percentages it was asked about. It is shown only while the form still says the same. */
interface Calculation {
  key: string
  payable: number
  detractionAmount: number | null
  retentionAmount: number | null
}

export function NewDocument() {
  const companies = useCompanies()
  const navigate = useNavigate()
  const issue = useIssueDocument()
  const preview = usePreviewDocument()
  const toast = useToast()
  const identity = useCatalog('06')
  const affectations = useCatalog('07')
  const operations = useCatalog('51')
  const detractionCodes = useCatalog('54')
  const legendCatalog = useCatalog('52')
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
  const [operation, setOperation] = useState('0101')
  const [usageCountry, setUsageCountry] = useState('')
  const [deduction, setDeduction] = useState<DeductionInput>(noDeduction())
  const [calculation, setCalculation] = useState<Calculation | null>(null)
  const [legends, setLegends] = useState<string[]>([])

  const activeCompanies = useMemo(() => companies.data?.filter((company) => company.status === 'Active') ?? [], [companies.data])
  const selectedCompany = activeCompanies.find((company) => company.id === companyId) ?? activeCompanies[0]
  const effectiveCompanyId = selectedCompany?.id ?? ''
  const series = useSeries(effectiveCompanyId || null)
  const typeSeries = useMemo(() => series.data?.filter((item) => item.isActive && item.documentTypeCode === type) ?? [], [series.data, type])

  // What the user did not choose is derived: the currency of the company and the first series of the type.
  const currency = currencyChoice ?? selectedCompany?.defaultCurrency ?? 'PEN'
  const seriesId = typeSeries.some((item) => item.id === seriesChoice) ? seriesChoice : (typeSeries[0]?.id ?? '')
  const exporting = isExport(operation)
  // Detraction and withholding are of an invoice in soles, and an export has neither.
  const canDeduct = type === '01' && currency === 'PEN' && !exporting
  const activeDeduction: DeductionInput = canDeduct ? deduction : noDeduction()

  function changeType(next: '01' | '03') {
    setType(next)
    if (next === '03' && !exportsFor('03').some((entry) => entry.code === operation)) setOperation('0101')
    setBuyer((current) => (next === '01' && current.documentTypeCode !== '6' ? emptyBuyer('6') : next === '03' && current.documentTypeCode === '6' ? emptyBuyer('1') : current))
    if (next === '03' && buyerMustBeForeign('03', operation)) setBuyer(emptyBuyer('7'))
  }

  function changeOperation(next: string) {
    setOperation(next)
    setUsageCountry('')
    if (buyerMustBeForeign(type, next)) setBuyer((current) => (FOREIGN_IDENTITY.includes(current.documentTypeCode) ? current : emptyBuyer('7')))
    else if (!isExport(next)) setBuyer((current) => (type === '01' && current.documentTypeCode !== '6' ? emptyBuyer('6') : current))
  }

  if (companies.isPending) return <Loading />
  if (activeCompanies.length === 0) return <div className="alert info">Registre una empresa antes de emitir comprobantes.</div>

  const noBuyer = buyer.documentTypeCode === '0'
  const foreignOnly = buyerMustBeForeign(type, operation)

  const detail = lineDetailOf(operation, activeDeduction)
  const requestLines = lines.map((line) => toRequestLine({ ...line, affectation: exporting ? EXPORT_AFFECTATION : line.affectation }, exporting ? false : lineIsFree(line.affectation, affectations.data), detail))
  // A legend of exoneration needs a line that is exonerated: the card appears only then, and the API checks the rest.
  const hasExempt = !exporting && lines.some((line) => affectations.data?.find((entry) => entry.code === line.affectation)?.metadata['Codigo de tributo'] === EXEMPT_TAX_CODE)
  const legendsToSend = hasExempt ? legends : []
  const buyerBody = {
    documentTypeCode: buyer.documentTypeCode,
    documentNumber: noBuyer ? '-' : buyer.documentNumber.trim(),
    name: noBuyer ? 'CLIENTES VARIOS' : buyer.name.trim(),
    address: buyer.address.trim() || null,
    email: buyer.email.trim() || null,
  }
  const installmentsBody = credit && type === '01' ? installments.map((item) => ({ amount: Number(item.amount), dueDate: item.dueDate })) : undefined
  const request = (deductionToSend: DeductionInput) =>
    buildDocumentRequest({ seriesId, issueDate, currency, buyer: buyerBody, lines: requestLines, installments: installmentsBody, operation, usageCountry, deduction: deductionToSend, legends: legendsToSend })

  // The document without its deduction is what the API previews: the amounts of a detraction and of a withholding depend on the total.
  // The amount of the detraction is what the preview gives, so it is not part of what is previewed (nor of what makes the result stale).
  const previewBody = { document: request({ ...activeDeduction, amount: '0' }), detractionPercentage: activeDeduction.kind === 'detraction' ? Number(activeDeduction.percentage) || null : null, retentionPercentage: activeDeduction.kind === 'retention' ? Number(activeDeduction.retentionPercentage) || null : null }
  const previewKey = JSON.stringify(previewBody)
  const shown = calculation && calculation.key === previewKey ? calculation : null
  const canCalculate = Boolean(seriesId) && (previewBody.detractionPercentage !== null || previewBody.retentionPercentage !== null)

  function calculate() {
    preview.mutate(previewBody, {
      onSuccess: (result) => {
        setCalculation({ key: previewKey, payable: result.totals.payableAmount, detractionAmount: result.detractionAmount, retentionAmount: result.retentionAmount })
        if (result.detractionAmount !== null) setDeduction((current) => ({ ...current, amount: String(result.detractionAmount) }))
      },
    })
  }

  function fillFrom(customerId: string) {
    const customer = customers.data?.find((item) => item.id === customerId)
    if (customer) setBuyer({ documentTypeCode: customer.documentTypeCode, documentNumber: customer.documentNumber, name: customer.name, address: customer.address ?? '', email: customer.email ?? '' })
  }

  function submit(event: FormEvent) {
    event.preventDefault()
    issue.mutate(request(activeDeduction), {
      onSuccess: (document) => {
        toast.ok(`${DOCUMENT_TYPES[type]} ${document.series}-${document.number} emitida.`)
        void navigate(`/documentos/${document.id}`)
      },
    })
  }

  const identityOptions = (identity.data ?? []).filter((entry) => SUPPORTED_IDENTITY.includes(entry.code) && (foreignOnly ? FOREIGN_IDENTITY.includes(entry.code) : type === '03' || entry.code !== '0' || exporting))
  const describe = (code: string) => operations.data?.find((entry) => entry.code === code)?.description ?? code

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
            <SelectField label="Tipo de operación" value={operation} onChange={(event) => changeOperation(event.target.value)}>
              <option value="0101">Venta interna</option>
              {exportsFor(type).map((entry) => <option key={entry.code} value={entry.code}>{entry.code} · {describe(entry.code)}</option>)}
            </SelectField>
            {needsUsageCountry(operation) && (
              <TextField label="País del uso o aprovechamiento" hint="código de 2 letras, no PE" required maxLength={2} pattern="[A-Za-z]{2}" value={usageCountry} onChange={(event) => setUsageCountry(event.target.value.toUpperCase())} />
            )}
          </div>
          {exporting && <p className="muted">Una exportación lleva solo líneas con afectación {EXPORT_AFFECTATION} y no está sujeta a detracción ni a retención.</p>}
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
                {identityOptions.map((entry) => <option key={entry.code} value={entry.code}>{entry.description}</option>)}
              </SelectField>
              <TextField label="Número" required={!noBuyer} disabled={noBuyer} value={noBuyer ? '' : buyer.documentNumber} onChange={(event) => setBuyer({ ...buyer, documentNumber: event.target.value })} />
              <TextField label="Nombre o razón social" required={!noBuyer} disabled={noBuyer} value={noBuyer ? 'CLIENTES VARIOS' : buyer.name} onChange={(event) => setBuyer({ ...buyer, name: event.target.value })} />
              <TextField label="Dirección" value={buyer.address} onChange={(event) => setBuyer({ ...buyer, address: event.target.value })} />
              <TextField label="Correo" type="email" value={buyer.email} onChange={(event) => setBuyer({ ...buyer, email: event.target.value })} />
            </div>
            {foreignOnly && <p className="muted">El adquirente de esta operación está en el exterior: no se admite RUC.</p>}
            {type === '03' && noBuyer && !exporting && <p className="muted">Una boleta de más de S/ 700 exige identificar al cliente.</p>}
          </div>
        </div>

        <div className="card">
          <h2>Ítems</h2>
          <LinesEditor lines={lines} onChange={setLines} fixedAffectation={exporting ? EXPORT_AFFECTATION : undefined} detail={detail} />
        </div>

        {hasExempt && (
          <div className="card stack">
            <h2>Leyendas de venta exonerada</h2>
            <p className="muted">Si la venta está exonerada del IGV por la Amazonía o por la zona comercial de Tacna, indique la leyenda que corresponde. Salen impresas y en el XML.</p>
            {EXEMPTION_LEGENDS.map((code) => (
              <label className="checkbox" key={code}>
                <input type="checkbox" checked={legends.includes(code)} onChange={(event) => setLegends(event.target.checked ? [...legends, code] : legends.filter((item) => item !== code))} />
                <span>
                  {code} · {legendCatalog.data?.find((entry) => entry.code === code)?.description ?? code}
                </span>
              </label>
            ))}
          </div>
        )}

        {canDeduct && (
          <div className="card stack">
            <h2>Detracción o retención</h2>
            <SelectField label="Esta factura tiene" value={deduction.kind} onChange={(event) => setDeduction({ ...deduction, kind: event.target.value as DeductionInput['kind'] })}>
              <option value="none">Ninguna</option>
              <option value="detraction">Detracción (SPOT)</option>
              <option value="retention">Retención del IGV</option>
            </SelectField>

            {deduction.kind === 'detraction' && (
              <>
                <div className="form-grid">
                  <SelectField label="Bien o servicio" hint="catálogo 54" required value={deduction.goodsOrServiceCode} onChange={(event) => setDeduction({ ...deduction, goodsOrServiceCode: event.target.value })}>
                    <option value="">Elija…</option>
                    {(detractionCodes.data ?? []).map((entry) => <option key={entry.code} value={entry.code}>{entry.code} · {entry.description}</option>)}
                  </SelectField>
                  <TextField label="Porcentaje" hint="% de la detracción" type="number" min="0" max="100" step="any" required value={deduction.percentage} onChange={(event) => setDeduction({ ...deduction, percentage: event.target.value })} />
                  <TextField label="Monto de la detracción" hint="en soles" type="number" min="0" step="0.01" required value={deduction.amount} onChange={(event) => setDeduction({ ...deduction, amount: event.target.value })} />
                  <TextField label="Cuenta en el Banco de la Nación" hint={selectedCompany?.detractionAccount ? 'vacía: se usa la de la empresa' : 'obligatoria si la empresa no la tiene registrada'} required={!selectedCompany?.detractionAccount} maxLength={100} value={deduction.account} onChange={(event) => setDeduction({ ...deduction, account: event.target.value })} />
                </div>
                <p className="muted">Los porcentajes y los montos de la detracción son datos del emisor: SUNAT revisa su estructura, no su valor. El 004 (recursos hidrobiológicos) pide los datos de la embarcación y de la especie en cada ítem, y el 027 (transporte de carga) los del transporte, más abajo.</p>
              </>
            )}

            {deduction.kind === 'retention' && (
              <>
                <div className="form-grid">
                  <TextField label="Porcentaje de la retención" hint="% del importe total" type="number" min="0" max="99.99999" step="any" required value={deduction.retentionPercentage} onChange={(event) => setDeduction({ ...deduction, retentionPercentage: event.target.value })} />
                </div>
                <p className="muted">El comprador, como agente de retención, retiene ese porcentaje del importe total. Una operación sujeta a detracción queda fuera de la retención: se elige una u otra.</p>
              </>
            )}

            {deduction.kind !== 'none' && (
              <div className="stack">
                <div className="actions">
                  <button className="btn" type="button" disabled={!canCalculate || preview.isPending} onClick={calculate}>
                    {preview.isPending ? 'Calculando…' : deduction.kind === 'detraction' ? 'Calcular el monto' : 'Calcular la retención'}
                  </button>
                </div>
                <ErrorAlert error={preview.error} />
                {shown && (
                  <div className="alert info" role="status">
                    Importe total del comprobante: <strong>{money(shown.payable, currency)}</strong>
                    {shown.detractionAmount !== null && <> · detracción: <strong>{money(shown.detractionAmount, 'PEN')}</strong>. Puede ajustar el monto dentro de lo que SUNAT acepta.</>}
                    {shown.retentionAmount !== null && <> · retención: <strong>{money(shown.retentionAmount, 'PEN')}</strong></>}
                  </div>
                )}
              </div>
            )}
          </div>
        )}

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
                <p className="muted">Las cuotas deben sumar el total del comprobante{activeDeduction.kind === 'none' ? '' : ', menos la detracción o la retención'}.</p>
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
