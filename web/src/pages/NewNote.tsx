import { useMemo, useState, type FormEvent } from 'react'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { useCatalog, useCompany, useDocument, useIssueNote, usePreviewNote, useSeries } from '../api/queries'
import { DetractionFields } from '../components/DetractionFields'
import { lineFrom, lineIsFree, LinesEditor, toRequestLine, type LineState } from '../components/LinesEditor'
import { ErrorAlert, Loading, PageHeader, SelectField, TextAreaField, TextField, useToast } from '../components/ui'
import { documentName, money, todayInLima } from '../lib/format'
import { noDeduction, type DeductionInput } from '../lib/operations'

export function NewNote() {
  const { id = '' } = useParams()
  const original = useDocument(id)
  const navigate = useNavigate()
  const issue = useIssueNote()
  const toast = useToast()
  const affectations = useCatalog('07')
  const [kind, setKind] = useState<'07' | '08'>('07')
  const credit = useCatalog('09')
  const debit = useCatalog('10')
  const series = useSeries(original.data?.companyId ?? null)
  const noteSeries = useMemo(() => series.data?.filter((item) => item.isActive && item.documentTypeCode === kind) ?? [], [series.data, kind])
  const [seriesChoice, setSeriesChoice] = useState('')
  const [reasonCode, setReasonCode] = useState('01')
  const [reason, setReason] = useState('')
  const [issueDate, setIssueDate] = useState(todayInLima())
  const [edited, setEdited] = useState<LineState[] | null>(null)
  const [withDetraction, setWithDetraction] = useState(false)
  const [deduction, setDeduction] = useState<DeductionInput>({ ...noDeduction(), kind: 'detraction' })
  const [calculation, setCalculation] = useState<{ key: string; payable: number; detractionAmount: number } | null>(null)
  const preview = usePreviewNote()
  const company = useCompany(original.data?.companyId ?? '')

  // The lines start as the ones of the original document until the user edits them.
  const lines = edited ?? original.data?.lines.map(lineFrom) ?? null
  const seriesId = noteSeries.some((item) => item.id === seriesChoice) ? seriesChoice : (noteSeries[0]?.id ?? '')

  if (!original.data || lines === null) return original.error ? <ErrorAlert error={original.error} /> : <Loading />
  const doc = original.data
  const noteLines = lines
  const reasons = kind === '07' ? credit.data : debit.data
  // A detraction is of a debit note on an invoice in soles: the sheet of the credit note has none, and receipts have no detraction.
  const canDetract = kind === '08' && doc.documentTypeCode === '01' && doc.currency === 'PEN'
  const detracting = canDetract && withDetraction

  const noteBody = (amount: string) => ({
    seriesId,
    referencedDocumentId: doc.id,
    issueDate,
    reasonCode,
    reason: reason.trim(),
    lines: noteLines.map((line) => toRequestLine(line, lineIsFree(line.affectation, affectations.data))),
    ...(detracting
      ? { detraction: { goodsOrServiceCode: deduction.goodsOrServiceCode, percentage: Number(deduction.percentage), amount: Number(amount), accountNumber: deduction.account.trim() || null } }
      : {}),
  })
  // The amount of the detraction is what the preview gives, so it is not part of what is previewed.
  const previewBody = { note: noteBody('0'), detractionPercentage: detracting ? Number(deduction.percentage) || null : null }
  const previewKey = JSON.stringify(previewBody)
  const shown = calculation && calculation.key === previewKey ? calculation : null

  function calculate() {
    preview.mutate(previewBody, {
      onSuccess: (result) => {
        if (result.detractionAmount === null) return
        setCalculation({ key: previewKey, payable: result.totals.payableAmount, detractionAmount: result.detractionAmount })
        setDeduction((current) => ({ ...current, amount: String(result.detractionAmount) }))
      },
    })
  }

  function submit(event: FormEvent) {
    event.preventDefault()
    issue.mutate(
      noteBody(deduction.amount),
      {
        onSuccess: (note) => {
          toast.ok(`${documentName(note.documentTypeCode)} ${note.series}-${note.number} emitida.`)
          void navigate(`/documentos/${note.id}`)
        },
      },
    )
  }

  return (
    <>
      <PageHeader title="Emitir nota" subtitle={`Modifica ${documentName(doc.documentTypeCode)} ${doc.series}-${doc.number}`} />
      <form className="stack" onSubmit={submit}>
        <ErrorAlert error={issue.error} />
        <div className="card">
          <h2>Nota</h2>
          <div className="form-grid">
            <SelectField label="Tipo de nota" value={kind} onChange={(event) => { setKind(event.target.value as '07' | '08'); setReasonCode('01') }}>
              <option value="07">Nota de crédito</option>
              <option value="08">Nota de débito</option>
            </SelectField>
            <SelectField label="Serie" required value={seriesId} onChange={(event) => setSeriesChoice(event.target.value)} error={noteSeries.length === 0 && !series.isPending ? 'No hay series de este tipo: cree una en la empresa.' : undefined}>
              {noteSeries.map((item) => <option key={item.id} value={item.id}>{item.code}</option>)}
            </SelectField>
            <SelectField label="Motivo" value={reasonCode} onChange={(event) => setReasonCode(event.target.value)}>
              {(reasons ?? []).map((entry) => <option key={entry.code} value={entry.code}>{entry.code} · {entry.description}</option>)}
            </SelectField>
            <TextField label="Fecha de emisión" type="date" required min={doc.issueDate} max={todayInLima()} value={issueDate} onChange={(event) => setIssueDate(event.target.value)} />
          </div>
          <div style={{ marginTop: 14 }}>
            <TextAreaField label="Sustento" required maxLength={500} value={reason} onChange={(event) => setReason(event.target.value)} />
          </div>
        </div>
        <div className="card">
          <h2>Ítems de la nota</h2>
          <p className="muted">Parten del documento original; ajústelos a lo que corrige la nota. Repita el ISC y las bolsas de plástico si el original los tiene.</p>
          <LinesEditor lines={lines} onChange={setEdited} />
        </div>
        {canDetract && (
          <div className="card stack">
            <h2>Detracción</h2>
            <label className="checkbox">
              <input type="checkbox" checked={withDetraction} onChange={(event) => setWithDetraction(event.target.checked)} />
              <span>Esta nota de débito está sujeta a detracción</span>
            </label>
            {withDetraction && (
              <>
                <DetractionFields value={deduction} onChange={setDeduction} companyAccount={company.data?.detractionAccount ?? null} />
                <p className="muted">La detracción de una nota es de su propio importe, no del de la factura. SUNAT revisa su estructura, no el porcentaje ni el monto: son datos del emisor.</p>
                <div className="actions">
                  <button className="btn" type="button" disabled={!seriesId || !(Number(deduction.percentage) > 0) || preview.isPending} onClick={calculate}>
                    {preview.isPending ? 'Calculando…' : 'Calcular el monto'}
                  </button>
                </div>
                <ErrorAlert error={preview.error} />
                {shown && (
                  <div className="alert info" role="status">
                    Importe total de la nota: <strong>{money(shown.payable, 'PEN')}</strong> · detracción: <strong>{money(shown.detractionAmount, 'PEN')}</strong>. Puede ajustar el monto dentro de lo que SUNAT acepta.
                  </div>
                )}
              </>
            )}
          </div>
        )}
        <div className="actions" style={{ justifyContent: 'flex-end' }}>
          <Link className="btn" to={`/documentos/${doc.id}`}>Cancelar</Link>
          <button className="btn primary" type="submit" disabled={issue.isPending || !seriesId}>{issue.isPending ? 'Emitiendo…' : 'Emitir nota'}</button>
        </div>
      </form>
    </>
  )
}
