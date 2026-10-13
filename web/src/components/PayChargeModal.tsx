import { useState } from 'react'
import { usePayCharge } from '../api/queries'
import type { Charge, PayChargeResult, PaymentConfig } from '../api/types'
import { askCulqiToken, cents, SANDBOX_CARDS, sandboxToken } from '../lib/culqi'
import { monthLabel } from '../lib/billing'
import { money } from '../lib/format'
import { ErrorAlert, Modal, SelectField, TextField } from './ui'

const TONES: Record<PayChargeResult['status'], string> = { Paid: 'ok', Declined: 'bad', NeedsAuthentication: 'warn', Failed: 'bad', UnderReview: 'warn' }

/**
 * Pay what is owed of a charge with a card (ADR-073). With Culqi, the checkout of Culqi takes the card and returns a token; with the simulator of development, a person picks what the simulated bank
 * does. Either way the platform receives a token and never the card, and it is the platform that says whether the payment was made.
 */
export function PayChargeModal({ charge, config, onClose }: { charge: Charge; config: PaymentConfig; onClose: () => void }) {
  const pay = usePayCharge(charge.id)
  const [email, setEmail] = useState('')
  const [card, setCard] = useState(SANDBOX_CARDS[0].code)
  const [waiting, setWaiting] = useState(false)
  const [problem, setProblem] = useState<unknown>(null)
  const [result, setResult] = useState<PayChargeResult | null>(null)
  const sandbox = config.provider === 'Sandbox'
  const ready = /^\S+@\S+\.\S+$/.test(email.trim()) && email.trim().length <= 50

  async function submit() {
    setProblem(null)
    try {
      let tokenId: string
      if (sandbox) {
        tokenId = sandboxToken(card)
      } else {
        setWaiting(true)
        tokenId = await askCulqiToken({ publicKey: config.publicKey ?? '', amountCents: cents(charge.balance), title: `Servicio de ${monthLabel(charge.period)}`, email: email.trim() })
      }
      setWaiting(false)
      pay.mutate({ tokenId, email: email.trim() }, { onSuccess: setResult, onError: setProblem })
    } catch (failure) {
      setWaiting(false)
      setProblem(failure)
    }
  }

  return (
    <Modal title={`Pagar el cargo de ${monthLabel(charge.period)}`} onClose={onClose}>
      {result ? (
        <div className="stack">
          <div className={`alert ${TONES[result.status]}`} role="status">
            {result.message}
          </div>
          <div className="actions">
            {result.status === 'Paid' || result.status === 'UnderReview' ? (
              <button className="btn primary" type="button" onClick={onClose}>
                Cerrar
              </button>
            ) : (
              <>
                <button className="btn" type="button" onClick={onClose}>
                  Cerrar
                </button>
                <button className="btn primary" type="button" onClick={() => setResult(null)}>
                  Intentar de nuevo
                </button>
              </>
            )}
          </div>
        </div>
      ) : (
        <form
          className="stack"
          onSubmit={(event) => {
            event.preventDefault()
            void submit()
          }}
        >
          <p>
            Saldo a pagar: <strong>{money(charge.balance, charge.currency)}</strong>. {sandbox ? 'La pasarela de pruebas no cobra nada.' : 'Paga en el formulario seguro de Culqi: su tarjeta no pasa por esta plataforma.'}
          </p>
          <ErrorAlert error={problem} />
          <TextField label="Correo de quien paga" type="email" required maxLength={50} hint="la pasarela envía allí su constancia" value={email} onChange={(event) => setEmail(event.target.value)} />
          {sandbox && (
            <SelectField label="Tarjeta de prueba" value={card} onChange={(event) => setCard(event.target.value)}>
              {SANDBOX_CARDS.map((option) => (
                <option key={option.code} value={option.code}>
                  {option.label}
                </option>
              ))}
            </SelectField>
          )}
          {waiting && (
            <p className="hint" role="status">
              Complete el pago en la ventana de Culqi. Si la cerró, vuelva a pulsar el botón.
            </p>
          )}
          <div className="actions">
            <button className="btn" type="button" onClick={onClose}>
              Cancelar
            </button>
            <button className="btn primary" type="submit" disabled={pay.isPending || !ready}>
              {pay.isPending ? 'Procesando…' : `Pagar ${money(charge.balance, charge.currency)}`}
            </button>
          </div>
        </form>
      )}
    </Modal>
  )
}
