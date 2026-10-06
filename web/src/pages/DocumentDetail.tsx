import { useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { fetchBlob } from '../api/http'
import { useArchive, useCompany, useCreateVoid, useDocument, useElectronic, useEvents, usePoll, usePrepare, useRecover, useRetry, useSend } from '../api/queries'
import type { Document, ElectronicDocument } from '../api/types'
import { useSession } from '../auth/session'
import { Badge, Empty, ErrorAlert, KeyValues, Loading, Modal, PageHeader, StateBadge, TextAreaField, useToast } from '../components/ui'
import { BILLING_ROLES, date, dateTime, documentName, money } from '../lib/format'

function save(blob: Blob, name: string, open = false) {
  const url = URL.createObjectURL(blob)
  if (open) {
    window.open(url, '_blank', 'noopener')
  } else {
    const link = document.createElement('a')
    link.href = url
    link.download = name
    link.click()
  }
  window.setTimeout(() => URL.revokeObjectURL(url), 60_000)
}

export function DocumentDetail() {
  const { id = '' } = useParams()
  const doc = useDocument(id)
  const electronic = useElectronic(id)

  if (doc.isPending) return <Loading />
  if (!doc.data) return <ErrorAlert error={doc.error} />
  return <Detail document={doc.data} electronic={electronic.data ?? null} loadingElectronic={electronic.isPending} />
}

function Detail({ document: doc, electronic, loadingElectronic }: { document: Document; electronic: ElectronicDocument | null; loadingElectronic: boolean }) {
  const { hasRole } = useSession()
  const canIssue = hasRole(...BILLING_ROLES)
  const company = useCompany(doc.companyId)
  const prepare = usePrepare(doc.id)
  const send = useSend(doc.id)
  const poll = usePoll(doc.id)
  const retry = useRetry(doc.id)
  const recover = useRecover(doc.id)
  const toast = useToast()
  const events = useEvents(electronic?.id)
  const archive = useArchive(electronic?.id, electronic !== null && (electronic.state === 'Accepted' || electronic.state === 'AcceptedWithObservations'))
  const [voiding, setVoiding] = useState(false)
  const [downloadError, setDownloadError] = useState<unknown>(null)

  const accepted = electronic?.state === 'Accepted' || electronic?.state === 'AcceptedWithObservations'
  const actionError = prepare.error ?? send.error ?? poll.error ?? retry.error ?? recover.error

  async function download(kind: 'pdf' | 'xml' | 'cdr') {
    if (!electronic) return
    setDownloadError(null)
    try {
      const blob = await fetchBlob(`/api/v1/electronic-documents/${electronic.id}/${kind}`)
      if (kind === 'pdf') save(blob, `${electronic.fileBaseName}.pdf`, true)
      else save(blob, kind === 'xml' ? `${electronic.fileBaseName}.xml` : `R-${electronic.fileBaseName}.zip`)
    } catch (error) {
      setDownloadError(error)
    }
  }

  const done = (message: string) => ({ onSuccess: () => toast.ok(message) })

  return (
    <>
      <PageHeader title={`${documentName(doc.documentTypeCode)} ${doc.series}-${doc.number}`} subtitle={`${company.data?.legalName ?? ''} · ${date(doc.issueDate)}`}>
        {electronic && <StateBadge state={electronic.state} voided={electronic.voided} />}
      </PageHeader>
      <ErrorAlert error={actionError ?? downloadError} />

      <div className="card">
        <h2>Envío a SUNAT</h2>
        {loadingElectronic ? (
          <Loading />
        ) : !electronic ? (
          <div className="stack">
            <p className="muted">El comprobante está emitido pero aún no se generó su XML firmado.</p>
            {canIssue && (
              <div>
                <button className="btn primary" type="button" disabled={prepare.isPending} onClick={() => prepare.mutate(undefined, done('XML generado y firmado.'))}>
                  {prepare.isPending ? 'Generando…' : 'Generar y firmar XML'}
                </button>
              </div>
            )}
          </div>
        ) : (
          <div className="stack">
            <div className="actions">
              {canIssue && electronic.state === 'ReadyToSend' && (
                <button className="btn primary" type="button" disabled={send.isPending} onClick={() => send.mutate(electronic.id, done('Enviado a SUNAT.'))}>
                  {send.isPending ? 'Enviando…' : 'Enviar a SUNAT'}
                </button>
              )}
              {canIssue && electronic.state === 'AwaitingTicket' && (
                <button className="btn primary" type="button" disabled={poll.isPending} onClick={() => poll.mutate(electronic.id, done('Consulta realizada.'))}>
                  Consultar respuesta
                </button>
              )}
              {canIssue && electronic.state === 'Failed' && (
                <button className="btn primary" type="button" disabled={retry.isPending} onClick={() => retry.mutate(electronic.id, done('Reintento programado.'))}>
                  Reintentar envío
                </button>
              )}
              {canIssue && electronic.state === 'Sending' && (
                <button className="btn" type="button" disabled={recover.isPending} onClick={() => recover.mutate(electronic.id, done('Recuperación realizada.'))}>
                  Recuperar envío
                </button>
              )}
              <button className="btn" type="button" onClick={() => void download('pdf')}>Ver PDF</button>
              <button className="btn" type="button" onClick={() => void download('xml')}>Descargar XML</button>
              {accepted && <button className="btn" type="button" onClick={() => void download('cdr')}>Descargar CDR</button>}
              {canIssue && accepted && !electronic.voided && (
                <button className="btn danger" type="button" onClick={() => setVoiding(true)}>Dar de baja</button>
              )}
              {canIssue && accepted && !electronic.voided && doc.documentTypeCode !== '07' && doc.documentTypeCode !== '08' && (
                <Link className="btn" to={`/documentos/${doc.id}/nota`}>Emitir nota</Link>
              )}
            </div>
            {electronic.cdrDescription && (
              <div className={`alert ${accepted ? (electronic.state === 'Accepted' ? 'ok' : 'warn') : 'bad'}`}>
                {electronic.cdrDescription}
                {electronic.cdrObservations.length > 0 && (
                  <ul style={{ margin: '6px 0 0', paddingLeft: 18 }}>
                    {electronic.cdrObservations.map((observation) => <li key={observation.code}><strong>{observation.code}</strong> {observation.message}</li>)}
                  </ul>
                )}
              </div>
            )}
            {electronic.lastErrorMessage && !accepted && (
              <div className="alert bad">
                {electronic.lastErrorCode && <strong>{electronic.lastErrorCode} · </strong>}
                {electronic.lastErrorMessage}
                {electronic.nextAttemptAt && <div>Próximo intento: {dateTime(electronic.nextAttemptAt)}</div>}
              </div>
            )}
            <KeyValues items={[['Archivo', <span className="mono" key="f">{electronic.fileBaseName}</span>], ['Intentos', electronic.attempts], ['Enviado', dateTime(electronic.sentAt)], ['Procesado', dateTime(electronic.processedAt)], ...(electronic.ticket ? ([['Ticket', <span className="mono" key="t">{electronic.ticket}</span>]] as [string, React.ReactNode][]) : [])]} />
          </div>
        )}
      </div>

      <div className="grid cols-2">
        <div className="card">
          <h2>Cliente</h2>
          <KeyValues items={[['Documento', `${doc.buyer.documentTypeCode} · ${doc.buyer.documentNumber}`], ['Nombre', doc.buyer.name], ['Dirección', doc.buyer.address ?? '—'], ['Correo', doc.buyer.email ?? '—']]} />
          {doc.note && (
            <p className="muted" style={{ marginTop: 12 }}>
              Modifica <Link to={`/documentos/${doc.note.referencedDocumentId}`}>{doc.note.referencedSeries}-{doc.note.referencedNumber}</Link> · motivo {doc.note.reasonCode}: {doc.note.reason}
            </p>
          )}
        </div>
        <div className="card">
          <h2>Totales</h2>
          <div className="totals">
            <dl className="kv">
              {doc.totals.totalTaxableGravado > 0 && <><dt>Op. gravadas</dt><dd>{money(doc.totals.totalTaxableGravado, doc.currency)}</dd></>}
              {doc.totals.totalExempt > 0 && <><dt>Op. exoneradas</dt><dd>{money(doc.totals.totalExempt, doc.currency)}</dd></>}
              {doc.totals.totalUnaffected > 0 && <><dt>Op. inafectas</dt><dd>{money(doc.totals.totalUnaffected, doc.currency)}</dd></>}
              {doc.totals.totalFree > 0 && <><dt>Op. gratuitas</dt><dd>{money(doc.totals.totalFree, doc.currency)}</dd></>}
              {doc.totals.totalExport > 0 && <><dt>Exportación</dt><dd>{money(doc.totals.totalExport, doc.currency)}</dd></>}
              {doc.totals.totalIsc > 0 && <><dt>ISC</dt><dd>{money(doc.totals.totalIsc, doc.currency)}</dd></>}
              <dt>IGV</dt><dd>{money(doc.totals.totalIgv, doc.currency)}</dd>
              {doc.totals.totalIvap > 0 && <><dt>IVAP</dt><dd>{money(doc.totals.totalIvap, doc.currency)}</dd></>}
              {doc.totals.totalIcbper > 0 && <><dt>ICBPER</dt><dd>{money(doc.totals.totalIcbper, doc.currency)}</dd></>}
              <dt className="grand">Importe total</dt><dd className="grand">{money(doc.totals.payableAmount, doc.currency)}</dd>
            </dl>
          </div>
        </div>
      </div>

      <div className="card">
        <h2>Ítems</h2>
        <div className="table-wrap">
          <table className="table">
            <thead><tr><th>#</th><th>Descripción</th><th className="num">Cantidad</th><th className="num">Valor unitario</th><th className="num">Valor de venta</th><th className="num">Impuestos</th></tr></thead>
            <tbody>
              {doc.lines.map((line) => (
                <tr key={line.lineNumber}>
                  <td>{line.lineNumber}</td>
                  <td>
                    {line.description}
                    <div className="row" style={{ gap: 6 }}>
                      <span className="muted">Afectación {line.igvAffectationCode}</span>
                      {line.isc && <Badge tone="info">ISC {line.isc.system === 'AdValorem' ? `${Math.round(line.isc.rateOrUnitAmount * 10000) / 100} %` : money(line.isc.rateOrUnitAmount, doc.currency)}</Badge>}
                      {line.plasticBagCount > 0 && <Badge tone="info">{line.plasticBagCount} bolsas</Badge>}
                    </div>
                  </td>
                  <td className="num">{line.quantity} {line.unitCode}</td>
                  <td className="num">{money(line.unitValue, doc.currency)}</td>
                  <td className="num">{money(line.lineExtensionAmount, doc.currency)}</td>
                  <td className="num">{money(line.totalTaxAmount, doc.currency)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        {doc.installments && doc.installments.length > 0 && (
          <p className="muted" style={{ marginTop: 12 }}>
            Venta al crédito: {doc.installments.map((item) => `${money(item.amount, doc.currency)} el ${date(item.dueDate)}`).join(' · ')}
          </p>
        )}
      </div>

      {electronic && (
        <div className="grid cols-2">
          <div className="card">
            <h2>Historial</h2>
            {events.isPending ? <Loading /> : events.data && events.data.length > 0 ? (
              <div className="table-wrap">
                <table className="table">
                  <thead><tr><th>Fecha</th><th>Evento</th><th>Detalle</th></tr></thead>
                  <tbody>
                    {events.data.map((event) => (
                      <tr key={event.id}>
                        <td className="tight">{dateTime(event.occurredAt)}</td>
                        <td>{event.event}</td>
                        <td>{event.detail ?? '—'}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            ) : <Empty>Sin eventos.</Empty>}
          </div>
          <div className="card">
            <h2>Archivo conservado</h2>
            {!accepted ? <p className="muted">El XML firmado y el CDR se archivan al aceptar el comprobante.</p> : archive.isPending ? <Loading /> : archive.data && archive.data.length > 0 ? (
              <div className="stack">
                {archive.data.map((file) => (
                  <div key={file.kind}>
                    <a href={file.downloadUrl} target="_blank" rel="noopener noreferrer">{file.kind === 'signed-xml' ? 'XML firmado' : file.kind === 'cdr-zip' ? 'CDR' : file.kind}</a>
                    <div className="muted">{file.sizeBytes} bytes · SHA-256 <span className="mono">{file.sha256.slice(0, 16)}…</span> · enlace válido hasta {dateTime(file.downloadExpiresAt)}</div>
                  </div>
                ))}
              </div>
            ) : <p className="muted">Todavía no se archivó (se hace en segundo plano).</p>}
          </div>
        </div>
      )}

      {voiding && <VoidModal document={doc} onClose={() => setVoiding(false)} />}
    </>
  )
}

function VoidModal({ document: doc, onClose }: { document: Document; onClose: () => void }) {
  const voidDocument = useCreateVoid(doc.id)
  const toast = useToast()
  const [reason, setReason] = useState('')
  return (
    <Modal title="Dar de baja el comprobante" onClose={onClose}>
      <form
        className="stack"
        onSubmit={(event) => {
          event.preventDefault()
          voidDocument.mutate({ companyId: doc.companyId, items: [{ documentId: doc.id, reason: reason.trim() }] }, { onSuccess: () => { toast.ok('Comunicación de baja generada.'); onClose() } })
        }}
      >
        <p>
          Se comunicará a SUNAT la baja de <strong>{doc.series}-{doc.number}</strong>. Esta acción no se puede deshacer.
        </p>
        <ErrorAlert error={voidDocument.error} />
        <TextAreaField label="Motivo" required maxLength={500} value={reason} onChange={(event) => setReason(event.target.value)} />
        <div className="actions">
          <button className="btn" type="button" onClick={onClose}>Cancelar</button>
          <button className="btn danger" type="submit" disabled={voidDocument.isPending}>Dar de baja</button>
        </div>
      </form>
    </Modal>
  )
}
