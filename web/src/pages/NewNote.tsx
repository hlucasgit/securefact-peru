import { useMemo, useState, type FormEvent } from 'react'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { useCatalog, useDocument, useIssueNote, useSeries } from '../api/queries'
import { lineFrom, lineIsFree, LinesEditor, toRequestLine, type LineState } from '../components/LinesEditor'
import { ErrorAlert, Loading, PageHeader, SelectField, TextAreaField, TextField, useToast } from '../components/ui'
import { documentName, todayInLima } from '../lib/format'

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

  // The lines start as the ones of the original document until the user edits them.
  const lines = edited ?? original.data?.lines.map(lineFrom) ?? null
  const seriesId = noteSeries.some((item) => item.id === seriesChoice) ? seriesChoice : (noteSeries[0]?.id ?? '')

  if (!original.data || lines === null) return original.error ? <ErrorAlert error={original.error} /> : <Loading />
  const doc = original.data
  const noteLines = lines
  const reasons = kind === '07' ? credit.data : debit.data

  function submit(event: FormEvent) {
    event.preventDefault()
    issue.mutate(
      {
        seriesId,
        referencedDocumentId: doc.id,
        issueDate,
        reasonCode,
        reason: reason.trim(),
        lines: noteLines.map((line) => toRequestLine(line, lineIsFree(line.affectation, affectations.data))),
      },
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
        <div className="actions" style={{ justifyContent: 'flex-end' }}>
          <Link className="btn" to={`/documentos/${doc.id}`}>Cancelar</Link>
          <button className="btn primary" type="submit" disabled={issue.isPending || !seriesId}>{issue.isPending ? 'Emitiendo…' : 'Emitir nota'}</button>
        </div>
      </form>
    </>
  )
}
