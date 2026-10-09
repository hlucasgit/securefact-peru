import { Link, useParams } from 'react-router-dom'
import { fetchBlob } from '../api/http'
import { useCompany, useGuide, useRefreshGuide, useSubmitGuide } from '../api/queries'
import type { Guide } from '../api/types'
import { useSession } from '../auth/session'
import { GreBadge } from '../components/GreBadge'
import { ErrorAlert, KeyValues, Loading, PageHeader, useToast } from '../components/ui'
import { BILLING_ROLES, date, dateTime } from '../lib/format'

function save(blob: Blob, name: string, tab: Window | null = null) {
  const url = URL.createObjectURL(blob)
  if (tab) {
    // The tab was opened by the click itself (a window opened after the wait for the file is taken for a pop-up and blocked); it is told where to go now, cut from this page.
    tab.opener = null
    tab.location.href = url
  } else {
    const link = document.createElement('a')
    link.href = url
    link.download = name
    link.click()
  }
  window.setTimeout(() => URL.revokeObjectURL(url), 60_000)
}

export const GRE_MOTIVES: Record<string, string> = {
  '01': 'Venta',
  '02': 'Compra',
  '03': 'Venta con entrega a terceros',
  '04': 'Traslado entre establecimientos de la misma empresa',
  '05': 'Consignación',
  '06': 'Devolución',
  '07': 'Recojo de bienes transformados',
  '08': 'Importación',
  '09': 'Exportación',
  '13': 'Otros',
  '14': 'Venta sujeta a confirmación del comprador',
  '17': 'Traslado de bienes para transformación',
  '18': 'Traslado emisor itinerante CP',
  '19': 'Traslado de mercancía extranjera',
}

export function GuideDetail() {
  const { id = '' } = useParams()
  const guide = useGuide(id)
  if (guide.isPending) return <Loading />
  if (!guide.data) return <ErrorAlert error={guide.error} />
  return <Detail guide={guide.data} />
}

function Detail({ guide }: { guide: Guide }) {
  const { hasRole } = useSession()
  const company = useCompany(guide.companyId)
  const submit = useSubmitGuide(guide.id)
  const refresh = useRefreshGuide(guide.id)
  const toast = useToast()
  const canSend = hasRole(...BILLING_ROLES)
  const answered = guide.cdrResponseCode !== null

  async function download(kind: 'xml' | 'cdr' | 'pdf') {
    const tab = kind === 'pdf' ? window.open('', '_blank') : null
    if (kind === 'pdf' && !tab) {
      toast.fail(new Error('El navegador bloqueó la ventana del PDF. Permita las ventanas emergentes de este sitio.'))
      return
    }

    try {
      save(await fetchBlob(`/api/v1/gre/guides/${guide.id}/${kind}`), kind === 'xml' ? `${guide.name}.xml` : `R-${guide.name}.zip`, tab)
    } catch (error) {
      tab?.close()
      toast.fail(error)
    }
  }

  return (
    <>
      <PageHeader title={`Guía de remisión ${guide.documentTypeCode === '31' ? 'del transportista' : 'del remitente'} ${guide.name}`} subtitle={company.data ? `${company.data.ruc} · ${company.data.legalName}` : undefined}>
        <GreBadge state={guide.state} />
      </PageHeader>
      <ErrorAlert error={submit.error ?? refresh.error} />
      <div className="card stack">
        <KeyValues
          items={[
            ['Fecha de emisión', date(guide.issueDate)],
            ...(guide.documentTypeCode === '31'
              ? []
              : ([
                  ['Motivo', `${guide.motiveCode} · ${GRE_MOTIVES[guide.motiveCode ?? ''] ?? ''}`.trim()],
                  ['Modalidad', guide.modalityCode === '01' ? 'Transporte público' : 'Transporte privado'],
                ] as [string, string][])),
            ['Destinatario', `${guide.recipientName} (${guide.recipientDocument})`],
            ['Creada', dateTime(guide.createdAt)],
            ['Enviada', guide.sentAt ? dateTime(guide.sentAt) : '—'],
            ['Respuesta de SUNAT', guide.processedAt ? dateTime(guide.processedAt) : '—'],
            ['Intentos', String(guide.attempts)],
          ]}
        />
        {guide.state === 'Prepared' && (
          <p className="muted">
            La guía está firmada y numerada, pero SUNAT todavía no la recibió.{guide.errorMessage ? ` Último intento: ${guide.errorMessage}` : ''} El envío necesita las credenciales de API de la empresa (
            <Link to={`/empresas/${guide.companyId}`}>Empresa, SOL</Link>).
          </p>
        )}
        {guide.state === 'Pending' && <p className="muted">SUNAT recibió la guía (ticket {guide.ticket}) y la está procesando. Esta pantalla se actualiza sola.</p>}
        {guide.state === 'Failed' && (
          <div className="alert bad" role="alert">
            SUNAT no la procesó{guide.errorCode ? ` (${guide.errorCode})` : ''}: {guide.errorMessage ?? 'sin detalle'}. El número queda consumido: emita una guía nueva.
          </div>
        )}
        {answered && (
          <div className={`alert ${guide.state === 'Rejected' ? 'bad' : guide.state === 'AcceptedWithObservations' ? 'warn' : 'ok'}`} role="status">
            {guide.cdrDescription ?? 'SUNAT respondió.'} (código {guide.cdrResponseCode})
            {guide.observations.length > 0 && (
              <ul>
                {guide.observations.map((observation) => <li key={`${observation.code}-${observation.message}`}>{observation.code} · {observation.message}</li>)}
              </ul>
            )}
          </div>
        )}
        <div className="row">
          {canSend && guide.state === 'Prepared' && (
            <button className="btn primary" type="button" disabled={submit.isPending} onClick={() => submit.mutate(undefined, { onSuccess: () => toast.ok('Guía enviada a SUNAT.') })}>
              {submit.isPending ? 'Enviando…' : 'Enviar a SUNAT'}
            </button>
          )}
          {canSend && guide.state === 'Pending' && (
            <button className="btn" type="button" disabled={refresh.isPending} onClick={() => refresh.mutate(undefined)}>
              {refresh.isPending ? 'Consultando…' : 'Consultar a SUNAT'}
            </button>
          )}
          <button className="btn" type="button" onClick={() => void download('pdf')}>Ver PDF</button>
          <button className="btn" type="button" onClick={() => void download('xml')}>Descargar XML</button>
          {answered && <button className="btn" type="button" onClick={() => void download('cdr')}>Descargar CDR</button>}
        </div>
      </div>
    </>
  )
}
