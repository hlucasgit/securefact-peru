import { useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { useAudit, useDeadEmails, useDeadMessages, useRequeue, useRequeueEmail, useTenants, useVerifyAudit } from '../api/queries'
import type { AuditRecord } from '../api/types'
import { useSession } from '../auth/session'
import { Badge, Empty, ErrorAlert, Loading, PageHeader, SelectField, TextField, useToast } from '../components/ui'
import { dateTime } from '../lib/format'

const PAGE = 50

/** Values are stored as JSON text: shown as they are, in a box that opens. */
function Values({ record }: { record: AuditRecord }) {
  if (!record.oldValues && !record.newValues) return <span className="muted">—</span>
  return (
    <details>
      <summary>Ver</summary>
      {record.oldValues && (
        <div>
          <span className="muted">Antes:</span> <code className="mono">{record.oldValues}</code>
        </div>
      )}
      {record.newValues && (
        <div>
          <span className="muted">Después:</span> <code className="mono">{record.newValues}</code>
        </div>
      )}
    </details>
  )
}

export function Audit() {
  const { hasRole } = useSession()
  const platform = hasRole('PlatformSuperAdmin', 'PlatformSupport')
  const [params, setParams] = useSearchParams()
  const [action, setAction] = useState('')
  const [entityType, setEntityType] = useState('')
  const [page, setPage] = useState(0)
  const tenantId = params.get('tenantId') ?? ''
  const tenants = useTenants('', '', platform)
  const audit = useAudit({ tenantId, action, entityType }, page * PAGE)
  const verify = useVerifyAudit()
  const toast = useToast()

  return (
    <>
      <PageHeader title="Auditoría" subtitle="Registro de hechos de la cuenta, con cadena de huellas contra la alteración">
        <button className="btn" type="button" disabled={verify.isPending || (platform && !tenantId)} onClick={() => verify.mutate(tenantId, { onSuccess: (result) => (result.isIntact ? toast.ok(`Cadena íntegra (${result.eventsChecked} eventos).`) : toast.fail(new Error(`Cadena rota en el evento ${result.firstBrokenSequence}: ${result.reason}`))) })}>
          {verify.isPending ? 'Verificando…' : 'Verificar integridad'}
        </button>
      </PageHeader>
      {verify.data && (
        <div className={`alert ${verify.data.isIntact ? 'ok' : 'bad'}`} role="status">
          {verify.data.isIntact ? `La cadena de ${verify.data.eventsChecked} eventos es íntegra.` : `La cadena está rota en el evento ${verify.data.firstBrokenSequence}: ${verify.data.reason}`}
        </div>
      )}
      <div className="card">
        <div className="form-grid" style={{ marginBottom: 12 }}>
          {platform && (
            <SelectField
              label="Cuenta"
              value={tenantId}
              onChange={(event) => {
                setParams(event.target.value ? { tenantId: event.target.value } : {})
                setPage(0)
              }}
            >
              <option value="">Todas</option>
              {tenants.data?.map((tenant) => (
                <option key={tenant.id} value={tenant.id}>
                  {tenant.name}
                </option>
              ))}
            </SelectField>
          )}
          <TextField label="Acción" hint="ej. tenancy.tenant.suspended" value={action} onChange={(event) => { setAction(event.target.value); setPage(0) }} />
          <TextField label="Tipo de entidad" hint="ej. tenant, user, company" value={entityType} onChange={(event) => { setEntityType(event.target.value); setPage(0) }} />
        </div>
        <ErrorAlert error={audit.error ?? verify.error} />
        {audit.isPending ? (
          <Loading />
        ) : audit.data && audit.data.length > 0 ? (
          <div className="table-wrap">
            <table className="table">
              <thead>
                <tr>
                  <th>#</th>
                  <th>Fecha</th>
                  <th>Acción</th>
                  <th>Entidad</th>
                  <th>Quién</th>
                  <th>Detalle</th>
                </tr>
              </thead>
              <tbody>
                {audit.data.map((record) => (
                  <tr key={record.id}>
                    <td className="num">{record.sequence}</td>
                    <td className="tight">{dateTime(record.occurredAt)}</td>
                    <td className="mono">{record.action}</td>
                    <td>
                      {record.entityType}
                      {record.entityId && <div className="muted mono">{record.entityId.slice(0, 8)}…</div>}
                    </td>
                    <td>{record.actorUserId ? <span className="mono">{record.actorUserId.slice(0, 8)}…</span> : <Badge tone="neutral">{record.actorType}</Badge>}</td>
                    <td>
                      <Values record={record} />
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : (
          <Empty>No hay eventos que coincidan.</Empty>
        )}
        <div className="row spread" style={{ marginTop: 12 }}>
          <button className="btn small" type="button" disabled={page === 0} onClick={() => setPage(page - 1)}>
            ← Más recientes
          </button>
          <span className="muted">Página {page + 1}</span>
          <button className="btn small" type="button" disabled={(audit.data?.length ?? 0) < PAGE} onClick={() => setPage(page + 1)}>
            Más antiguos →
          </button>
        </div>
      </div>
    </>
  )
}

export function DeadMessages() {
  const { data, isPending, error } = useDeadMessages()
  const requeue = useRequeue()
  const toast = useToast()
  return (
    <>
      <PageHeader title="Mensajes fallidos" subtitle="Eventos internos que agotaron sus reintentos (por ejemplo, el archivo de un documento)" />
      <div className="card">
        <ErrorAlert error={error ?? requeue.error} />
        {isPending ? (
          <Loading />
        ) : data && data.length > 0 ? (
          <div className="table-wrap">
            <table className="table">
              <thead>
                <tr>
                  <th>Evento</th>
                  <th>Origen</th>
                  <th className="num">Intentos</th>
                  <th>Último error</th>
                  <th>Creado</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {data.map((message) => (
                  <tr key={message.id}>
                    <td className="mono">{message.eventType}</td>
                    <td>{message.source}</td>
                    <td className="num">{message.attempts}</td>
                    <td>{message.lastError ?? '—'}</td>
                    <td className="tight">{dateTime(message.createdAt)}</td>
                    <td className="right">
                      <button className="btn small" type="button" disabled={requeue.isPending} onClick={() => requeue.mutate(message, { onSuccess: () => toast.ok('Mensaje devuelto a la cola.') })}>
                        Reencolar
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : (
          <Empty>No hay mensajes fallidos.</Empty>
        )}
      </div>
    </>
  )
}

/** The e-mails that the platform could not send after all their attempts (ADR-054). Only platform staff see them: the queue is the platform's. */
export function DeadEmails() {
  const { data, isPending, error } = useDeadEmails()
  const requeue = useRequeueEmail()
  const toast = useToast()
  return (
    <>
      <PageHeader title="Correos fallidos" subtitle="Avisos que agotaron sus reintentos de envío (servidor de correo caído o dirección rechazada)" />
      <div className="card">
        <ErrorAlert error={error ?? requeue.error} />
        {isPending ? (
          <Loading />
        ) : data && data.length > 0 ? (
          <div className="table-wrap">
            <table className="table">
              <thead>
                <tr>
                  <th>Destinatario</th>
                  <th>Asunto</th>
                  <th className="num">Intentos</th>
                  <th>Último error</th>
                  <th>Murió</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {data.map((email) => (
                  <tr key={email.id}>
                    <td>{email.toAddress}</td>
                    <td>{email.subject}</td>
                    <td className="num">{email.attempts}</td>
                    <td>{email.lastError ?? '—'}</td>
                    <td className="tight">{dateTime(email.deadAt)}</td>
                    <td className="right">
                      <button className="btn small" type="button" disabled={requeue.isPending} aria-label={`Reenviar a ${email.toAddress}: ${email.subject}`} onClick={() => requeue.mutate(email, { onSuccess: () => toast.ok('Correo devuelto a la cola.') })}>
                        Reenviar
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : (
          <Empty>No hay correos fallidos.</Empty>
        )}
      </div>
    </>
  )
}
