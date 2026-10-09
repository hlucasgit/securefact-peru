import { useState } from 'react'
import {
  useApiKeyRoles,
  useApiKeys,
  useCreateApiKey,
  useCreateWebhook,
  useDeleteWebhook,
  useRedeliver,
  useRevokeApiKey,
  useRotateWebhookSecret,
  useTestWebhook,
  useUpdateWebhook,
  useWebhookDeliveries,
  useWebhookEvents,
  useWebhooks,
} from '../api/queries'
import type { Webhook, WebhookDeliveryState } from '../api/types'
import { Badge, ConfirmButton, Empty, ErrorAlert, Loading, Modal, PageHeader, SelectField, Tabs, TextField, useToast } from '../components/ui'
import { ROLE_LABELS, date, dateTime } from '../lib/format'
import type { Tone } from '../lib/format'

type Tab = 'llaves' | 'webhooks'

/** API keys and webhooks of the account (ADR-066, ADR-067): how a program calls the platform and how the platform tells a program that something happened. */
export function Integrations() {
  const [tab, setTab] = useState<Tab>('llaves')
  return (
    <>
      <PageHeader title="Integraciones" subtitle="Llaves de API para sus programas y webhooks para que la plataforma les avise" />
      <Tabs
        tabs={[
          { id: 'llaves', label: 'Llaves de API' },
          { id: 'webhooks', label: 'Webhooks' },
        ]}
        value={tab}
        onChange={setTab}
      />
      {tab === 'llaves' ? <ApiKeys /> : <Webhooks />}
    </>
  )
}

/** A secret that is shown once. It is not stored anywhere that can be read again, so the person has to copy it now. */
function SecretModal({ title, label, secret, onClose }: { title: string; label: string; secret: string; onClose: () => void }) {
  const [copied, setCopied] = useState(false)
  return (
    <Modal title={title} onClose={onClose}>
      <div className="stack">
        <p>
          {label} <strong>Cópiela ahora: no se vuelve a mostrar.</strong> Guárdela en el gestor de secretos de su sistema.
        </p>
        <pre className="mono" style={{ overflowWrap: 'anywhere', whiteSpace: 'pre-wrap' }} aria-label="Secreto">
          {secret}
        </pre>
        <div className="actions">
          <button
            className="btn"
            type="button"
            onClick={() => {
              void navigator.clipboard?.writeText(secret).then(() => setCopied(true))
            }}
          >
            {copied ? 'Copiado' : 'Copiar'}
          </button>
          <button className="btn primary" type="button" onClick={onClose}>
            Ya la guardé
          </button>
        </div>
      </div>
    </Modal>
  )
}

// ---------- API keys ----------

function ApiKeys() {
  const { data, isPending, error } = useApiKeys()
  const revoke = useRevokeApiKey()
  const toast = useToast()
  const [creating, setCreating] = useState(false)
  const [secret, setSecret] = useState<string | null>(null)

  return (
    <div className="card">
      <div className="row spread" style={{ marginBottom: 12 }}>
        <p className="hint" style={{ margin: 0 }}>
          Una llave deja que un programa use la API con los permisos de un rol de su cuenta. Nunca puede administrar usuarios, llaves ni webhooks.
        </p>
        <button className="btn primary" type="button" onClick={() => setCreating(true)}>
          Nueva llave
        </button>
      </div>
      <ErrorAlert error={error ?? revoke.error} />
      {isPending ? (
        <Loading />
      ) : data && data.length > 0 ? (
        <div className="table-wrap">
          <table className="table">
            <thead>
              <tr>
                <th>Nombre</th>
                <th>Rol</th>
                <th>Llave</th>
                <th>Creada</th>
                <th>Vence</th>
                <th>Último uso</th>
                <th>Estado</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {data.map((key) => (
                <tr key={key.id}>
                  <td>{key.name}</td>
                  <td>{ROLE_LABELS[key.role] ?? key.role}</td>
                  <td className="mono">sfk_…{key.prefix}</td>
                  <td>{date(key.createdAt)}</td>
                  <td>{key.expiresAt ? date(key.expiresAt) : 'No vence'}</td>
                  <td>{key.lastUsedAt ? dateTime(key.lastUsedAt) : 'Nunca'}</td>
                  <td>{key.revokedAt ? <Badge tone="neutral">Revocada</Badge> : key.expiresAt && new Date(key.expiresAt) < new Date() ? <Badge tone="warn">Vencida</Badge> : <Badge tone="ok">Activa</Badge>}</td>
                  <td className="right tight">
                    {!key.revokedAt && (
                      <ConfirmButton label="Revocar" ariaLabel={`Revocar ${key.name}`} message={`¿Revocar la llave «${key.name}»? Los programas que la usan dejarán de funcionar al instante.`} onConfirm={() => revoke.mutate(key.id, { onSuccess: () => toast.ok('Llave revocada.') })} />
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : (
        <Empty>Aún no hay llaves.</Empty>
      )}
      {creating && <ApiKeyModal onClose={() => setCreating(false)} onCreated={setSecret} />}
      {secret && <SecretModal title="Llave creada" label="Esta es su llave de API." secret={secret} onClose={() => setSecret(null)} />}
    </div>
  )
}

function ApiKeyModal({ onClose, onCreated }: { onClose: () => void; onCreated: (secret: string) => void }) {
  const create = useCreateApiKey()
  const roles = useApiKeyRoles()
  const [form, setForm] = useState({ name: '', role: 'Sales', expiresAt: '' })
  return (
    <Modal title="Nueva llave de API" onClose={onClose}>
      <form
        className="stack"
        onSubmit={(event) => {
          event.preventDefault()
          create.mutate(
            { name: form.name.trim(), role: form.role, expiresAt: form.expiresAt ? new Date(`${form.expiresAt}T23:59:59-05:00`).toISOString() : null },
            { onSuccess: (created) => { onClose(); onCreated(created.secret) } },
          )
        }}
      >
        <ErrorAlert error={create.error ?? roles.error} />
        <TextField label="Nombre" required minLength={3} maxLength={60} hint="qué programa la usa" value={form.name} onChange={(event) => setForm({ ...form, name: event.target.value })} />
        <SelectField label="Rol" hint="los permisos de la llave son los de este rol" value={form.role} onChange={(event) => setForm({ ...form, role: event.target.value })}>
          {(roles.data ?? ['Sales']).map((role) => (
            <option key={role} value={role}>
              {ROLE_LABELS[role] ?? role}
            </option>
          ))}
        </SelectField>
        <TextField label="Vence el" type="date" hint="vacío: no vence; dentro de dos años como máximo" value={form.expiresAt} onChange={(event) => setForm({ ...form, expiresAt: event.target.value })} />
        <div className="actions">
          <button className="btn" type="button" onClick={onClose}>
            Cancelar
          </button>
          <button className="btn primary" type="submit" disabled={create.isPending}>
            Crear llave
          </button>
        </div>
      </form>
    </Modal>
  )
}

// ---------- webhooks ----------

const EVENT_LABELS: Record<string, string> = {
  'document.issued': 'Comprobante emitido',
  'document.accepted': 'Aceptado por SUNAT',
  'document.rejected': 'Rechazado por SUNAT',
}

const DELIVERY_TONES: Record<WebhookDeliveryState, Tone> = { Pending: 'info', Delivered: 'ok', Failed: 'warn', Dead: 'bad' }
const DELIVERY_LABELS: Record<WebhookDeliveryState, string> = { Pending: 'En espera', Delivered: 'Entregado', Failed: 'Falló, se reintenta', Dead: 'Agotó los intentos' }

function Webhooks() {
  const { data, isPending, error } = useWebhooks()
  const test = useTestWebhook()
  const rotate = useRotateWebhookSecret()
  const remove = useDeleteWebhook()
  const toast = useToast()
  const [editing, setEditing] = useState<Webhook | 'new' | null>(null)
  const [viewing, setViewing] = useState<Webhook | null>(null)
  const [secret, setSecret] = useState<{ title: string; value: string } | null>(null)

  return (
    <div className="card">
      <div className="row spread" style={{ marginBottom: 12 }}>
        <p className="hint" style={{ margin: 0 }}>
          La plataforma envía cada evento a su dirección, firmado, y lo reintenta si no contesta. Vea la guía de integración para verificar la firma.
        </p>
        <button className="btn primary" type="button" onClick={() => setEditing('new')}>
          Nuevo webhook
        </button>
      </div>
      <ErrorAlert error={error ?? test.error ?? rotate.error ?? remove.error} />
      {isPending ? (
        <Loading />
      ) : data && data.length > 0 ? (
        <div className="table-wrap">
          <table className="table">
            <thead>
              <tr>
                <th>Dirección</th>
                <th>Eventos</th>
                <th>Secreto</th>
                <th>Estado</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {data.map((hook) => (
                <tr key={hook.id}>
                  <td>
                    <span className="mono">{hook.url}</span>
                    {hook.description && <div className="hint">{hook.description}</div>}
                  </td>
                  <td>{hook.events.map((event) => EVENT_LABELS[event] ?? event).join(', ')}</td>
                  <td className="mono">whsec_…{hook.secretHint}</td>
                  <td>
                    {hook.isActive ? <Badge tone={hook.consecutiveFailures > 0 ? 'warn' : 'ok'}>{hook.consecutiveFailures > 0 ? `Activo, ${hook.consecutiveFailures} fallos seguidos` : 'Activo'}</Badge> : <Badge tone="neutral">Apagado</Badge>}
                    {hook.disabledReason && !hook.isActive && <div className="hint">{hook.disabledReason}</div>}
                  </td>
                  <td className="right">
                    <div className="row" style={{ justifyContent: 'flex-end', flexWrap: 'wrap' }}>
                      <button
                        className="btn small"
                        type="button"
                        disabled={!hook.isActive || test.isPending}
                        aria-label={`Probar ${hook.url}`}
                        onClick={() =>
                          test.mutate(hook.id, {
                            onSuccess: (delivery) =>
                              delivery.state === 'Delivered' ? toast.ok(`La dirección contestó ${delivery.lastStatusCode}: funciona.`) : toast.ok(`La prueba falló: ${delivery.lastError ?? `HTTP ${delivery.lastStatusCode}`}.`),
                          })
                        }
                      >
                        Probar
                      </button>
                      <button className="btn small" type="button" aria-label={`Entregas de ${hook.url}`} onClick={() => setViewing(hook)}>
                        Entregas
                      </button>
                      <button className="btn small" type="button" aria-label={`Editar ${hook.url}`} onClick={() => setEditing(hook)}>
                        Editar
                      </button>
                      <ConfirmButton
                        label="Rotar secreto"
                        ariaLabel={`Rotar el secreto de ${hook.url}`}
                        message="¿Rotar el secreto? El anterior deja de firmar al instante: actualice su receptor."
                        className="btn small"
                        onConfirm={() => rotate.mutate(hook.id, { onSuccess: (created) => setSecret({ title: 'Secreto nuevo', value: created.secret }) })}
                      />
                      <ConfirmButton label="Eliminar" ariaLabel={`Eliminar ${hook.url}`} message="¿Eliminar este webhook y sus entregas?" onConfirm={() => remove.mutate(hook.id, { onSuccess: () => toast.ok('Webhook eliminado.') })} />
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : (
        <Empty>Aún no hay webhooks.</Empty>
      )}
      {editing && <WebhookModal hook={editing === 'new' ? null : editing} onClose={() => setEditing(null)} onCreated={(value) => setSecret({ title: 'Webhook creado', value })} />}
      {viewing && <DeliveriesModal hook={viewing} onClose={() => setViewing(null)} />}
      {secret && <SecretModal title={secret.title} label="Este es el secreto con el que se firman las entregas." secret={secret.value} onClose={() => setSecret(null)} />}
    </div>
  )
}

function WebhookModal({ hook, onClose, onCreated }: { hook: Webhook | null; onClose: () => void; onCreated: (secret: string) => void }) {
  const create = useCreateWebhook()
  const update = useUpdateWebhook(hook?.id ?? '')
  const catalogue = useWebhookEvents()
  const toast = useToast()
  const [form, setForm] = useState({ url: hook?.url ?? '', description: hook?.description ?? '', events: hook?.events ?? ['document.accepted', 'document.rejected'], isActive: hook?.isActive ?? true })
  const mutation = hook ? update : create

  function toggle(event: string, on: boolean) {
    setForm({ ...form, events: on ? [...form.events, event] : form.events.filter((candidate) => candidate !== event) })
  }

  return (
    <Modal title={hook ? 'Editar webhook' : 'Nuevo webhook'} onClose={onClose}>
      <form
        className="stack"
        onSubmit={(event) => {
          event.preventDefault()
          const input = { url: form.url.trim(), description: form.description.trim() || null, events: form.events, isActive: form.isActive }
          if (hook) {
            update.mutate(input, { onSuccess: () => { toast.ok('Webhook actualizado.'); onClose() } })
          } else {
            create.mutate(input, { onSuccess: (created) => { onClose(); onCreated(created.secret) } })
          }
        }}
      >
        <ErrorAlert error={mutation.error ?? catalogue.error} />
        <TextField label="Dirección" required type="url" maxLength={500} hint="https y pública; recibe un POST firmado" value={form.url} onChange={(event) => setForm({ ...form, url: event.target.value })} />
        <TextField label="Descripción" maxLength={200} value={form.description} onChange={(event) => setForm({ ...form, description: event.target.value })} />
        <fieldset className="stack">
          <legend>Eventos</legend>
          {(catalogue.data ?? Object.keys(EVENT_LABELS)).map((event) => (
            <label className="checkbox" key={event}>
              <input type="checkbox" checked={form.events.includes(event)} onChange={(change) => toggle(event, change.target.checked)} /> {EVENT_LABELS[event] ?? event} <span className="mono">({event})</span>
            </label>
          ))}
        </fieldset>
        {hook && (
          <label className="checkbox">
            <input type="checkbox" checked={form.isActive} onChange={(event) => setForm({ ...form, isActive: event.target.checked })} /> Activo
          </label>
        )}
        <div className="actions">
          <button className="btn" type="button" onClick={onClose}>
            Cancelar
          </button>
          <button className="btn primary" type="submit" disabled={mutation.isPending || form.events.length === 0}>
            {hook ? 'Guardar' : 'Crear webhook'}
          </button>
        </div>
      </form>
    </Modal>
  )
}

function DeliveriesModal({ hook, onClose }: { hook: Webhook; onClose: () => void }) {
  const [state, setState] = useState<WebhookDeliveryState | ''>('')
  const { data, isPending, error } = useWebhookDeliveries(hook.id, state)
  const redeliver = useRedeliver()
  const toast = useToast()
  return (
    <Modal title={`Entregas de ${hook.url}`} onClose={onClose}>
      <div className="stack">
        <ErrorAlert error={error ?? redeliver.error} />
        <SelectField label="Estado" value={state} onChange={(event) => setState(event.target.value as WebhookDeliveryState | '')}>
          <option value="">Todas</option>
          {(Object.keys(DELIVERY_LABELS) as WebhookDeliveryState[]).map((code) => (
            <option key={code} value={code}>
              {DELIVERY_LABELS[code]}
            </option>
          ))}
        </SelectField>
        {isPending ? (
          <Loading />
        ) : data && data.length > 0 ? (
          <div className="table-wrap">
            <table className="table">
              <thead>
                <tr>
                  <th>Evento</th>
                  <th>Creada</th>
                  <th>Intentos</th>
                  <th>Último resultado</th>
                  <th>Estado</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {data.map((delivery) => (
                  <tr key={delivery.id}>
                    <td className="mono">{delivery.eventType}</td>
                    <td>{dateTime(delivery.createdAt)}</td>
                    <td className="right">{delivery.attempts}</td>
                    <td>{delivery.lastError ?? (delivery.lastStatusCode ? `HTTP ${delivery.lastStatusCode}` : '—')}</td>
                    <td>
                      <Badge tone={DELIVERY_TONES[delivery.state]}>{DELIVERY_LABELS[delivery.state]}</Badge>
                    </td>
                    <td className="right tight">
                      {delivery.state !== 'Pending' && (
                        <button className="btn small" type="button" aria-label={`Reenviar la entrega de ${delivery.eventType} del ${dateTime(delivery.createdAt)}`} onClick={() => redeliver.mutate(delivery.id, { onSuccess: () => toast.ok('Entrega en cola.') })}>
                          Reenviar
                        </button>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : (
          <Empty>No hay entregas con ese estado.</Empty>
        )}
      </div>
    </Modal>
  )
}
