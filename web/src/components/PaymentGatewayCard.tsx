import { useState } from 'react'
import { useGatewaySettings, usePaymentAttempts, useResolveAttempt, useSetGateway } from '../api/queries'
import type { GatewaySettings, PaymentGatewayProvider } from '../api/types'
import { dateTime, money } from '../lib/format'
import { Badge, Empty, ErrorAlert, Loading, SelectField, TextField, useToast } from './ui'

const ENVIRONMENTS: Record<GatewaySettings['environment'], string> = { test: 'pruebas', live: 'producción', sandbox: 'simulador' }

/** The gateway that takes the card payments of the charges (ADR-073) and the payments that need a person: platform staff only. The secret key is written here and never shown again. */
export function PaymentGatewayCard({ canManage }: { canManage: boolean }) {
  const settings = useGatewaySettings()
  return (
    <div className="card">
      <h2>Pago con tarjeta</h2>
      <ErrorAlert error={settings.error} />
      {settings.isPending ? <Loading /> : settings.data && <GatewayForm settings={settings.data} canManage={canManage} />}
      <Attempts canManage={canManage} />
    </div>
  )
}

function GatewayForm({ settings, canManage }: { settings: GatewaySettings; canManage: boolean }) {
  const save = useSetGateway()
  const toast = useToast()
  const [value, setValue] = useState({ provider: settings.provider as PaymentGatewayProvider, publicKey: settings.publicKey ?? '', secretKey: '', enabled: settings.enabled })
  const culqi = value.provider === 'Culqi'

  return (
    <form
      className="stack"
      onSubmit={(event) => {
        event.preventDefault()
        save.mutate(
          { provider: value.provider, publicKey: culqi ? value.publicKey.trim() || null : null, secretKey: culqi ? value.secretKey.trim() || null : null, enabled: value.enabled },
          { onSuccess: () => { toast.ok('Pasarela guardada.'); setValue((current) => ({ ...current, secretKey: '' })) } },
        )
      }}
    >
      <p className="hint" style={{ margin: 0 }}>
        Las cuentas pagan sus cargos con tarjeta en el formulario seguro de la pasarela: la tarjeta no pasa por esta plataforma. {settings.updatedAt > '2000' ? `Estado: ${settings.enabled ? 'activa' : 'apagada'} (${ENVIRONMENTS[settings.environment]}).` : 'Aún sin configurar.'}
      </p>
      <ErrorAlert error={save.error} />
      <SelectField label="Pasarela" value={value.provider} disabled={!canManage} onChange={(event) => setValue({ ...value, provider: event.target.value as PaymentGatewayProvider })}>
        <option value="Culqi">Culqi</option>
        <option value="Sandbox">Simulador (solo desarrollo)</option>
      </SelectField>
      {culqi && (
        <>
          <TextField label="Llave pública" hint="pk_test_… o pk_live_…" maxLength={80} disabled={!canManage} value={value.publicKey} onChange={(event) => setValue({ ...value, publicKey: event.target.value })} />
          <TextField
            label="Llave secreta"
            type="password"
            autoComplete="off"
            hint={settings.hasSecretKey ? 'ya hay una guardada; déjela vacía para conservarla' : 'sk_test_… o sk_live_…; se guarda cifrada y no se vuelve a mostrar'}
            maxLength={80}
            disabled={!canManage}
            value={value.secretKey}
            onChange={(event) => setValue({ ...value, secretKey: event.target.value })}
          />
        </>
      )}
      <label className="row" style={{ gap: 8 }}>
        <input type="checkbox" checked={value.enabled} disabled={!canManage} onChange={(event) => setValue({ ...value, enabled: event.target.checked })} />
        Activar el pago con tarjeta
      </label>
      {canManage && (
        <div className="actions">
          <button className="btn primary" type="submit" disabled={save.isPending}>
            Guardar pasarela
          </button>
        </div>
      )}
    </form>
  )
}

/** The attempts whose outcome nobody knows: the answer of the gateway was lost, or a payment that was made could not be applied. The person is told not to pay again, so these need a look. */
function Attempts({ canManage }: { canManage: boolean }) {
  const attempts = usePaymentAttempts()
  const resolve = useResolveAttempt()
  const toast = useToast()
  const [closing, setClosing] = useState<string | null>(null)
  const [note, setNote] = useState('')

  return (
    <div style={{ marginTop: 18 }}>
      <h3>Pagos por revisar</h3>
      <ErrorAlert error={attempts.error ?? resolve.error} />
      {attempts.isPending ? (
        <Loading />
      ) : attempts.data && attempts.data.length > 0 ? (
        <div className="table-wrap">
          <table className="table">
            <thead>
              <tr>
                <th>Cuenta</th>
                <th>Mes</th>
                <th className="right">Monto</th>
                <th>Referencia de la pasarela</th>
                <th>Qué pasó</th>
                <th>Desde</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {attempts.data.map((attempt) => (
                <tr key={attempt.id}>
                  <td>{attempt.tenantName}</td>
                  <td>{attempt.period}</td>
                  <td className="right">{money(attempt.amount, 'PEN')}</td>
                  <td className="mono">{attempt.gatewayChargeId ?? '—'}</td>
                  <td>{attempt.message ?? <Badge tone="warn">Sin terminar</Badge>}</td>
                  <td>{dateTime(attempt.createdAt)}</td>
                  <td className="right tight">
                    {canManage &&
                      (closing === attempt.id ? (
                        <form
                          className="row"
                          onSubmit={(event) => {
                            event.preventDefault()
                            resolve.mutate({ id: attempt.id, note: note.trim() }, { onSuccess: () => { toast.ok('Intento cerrado.'); setClosing(null); setNote('') } })
                          }}
                        >
                          <input className="input" aria-label="Qué se revisó y qué se hizo" placeholder="Qué se revisó y qué se hizo" maxLength={300} value={note} onChange={(event) => setNote(event.target.value)} />
                          <button className="btn small primary" type="submit" disabled={note.trim().length < 3 || resolve.isPending}>
                            Cerrar
                          </button>
                        </form>
                      ) : (
                        <button className="btn small" type="button" aria-label={`Revisar el pago de ${attempt.tenantName}`} onClick={() => setClosing(attempt.id)}>
                          Revisado
                        </button>
                      ))}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : (
        <Empty>No hay pagos por revisar.</Empty>
      )}
      <p className="hint">Revise el cobro en el panel de la pasarela. Si el dinero llegó, registre el pago del cargo a mano; luego cierre el intento con una nota. Mientras esté abierto, la cuenta no puede volver a pagar ese cargo.</p>
    </div>
  )
}
