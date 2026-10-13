/** The checkout of Culqi (ADR-073): it opens in the page, takes the card and gives back a token; the card never reaches this platform. */
const SCRIPT = 'https://js.culqi.com/checkout-js'

interface CulqiInstance {
  token?: { id: string }
  error?: { user_message?: string; merchant_message?: string }
  culqi: () => void
  open(): void
  close(): void
}

type CulqiConstructor = new (publicKey: string, config: unknown) => CulqiInstance

declare global {
  interface Window {
    CulqiCheckout?: CulqiConstructor
  }
}

let loading: Promise<CulqiConstructor> | null = null

/** Loads the script of Culqi once, when a person is about to pay and not before. */
export function loadCulqi(): Promise<CulqiConstructor> {
  if (window.CulqiCheckout) return Promise.resolve(window.CulqiCheckout)
  loading ??= new Promise<CulqiConstructor>((resolve, reject) => {
    const script = document.createElement('script')
    script.src = SCRIPT
    script.async = true
    script.onload = () => (window.CulqiCheckout ? resolve(window.CulqiCheckout) : reject(new Error('El pago con tarjeta no se pudo cargar.')))
    script.onerror = () => {
      loading = null
      reject(new Error('No se pudo conectar con la pasarela de pago. Revise su conexión e intente de nuevo.'))
    }
    document.head.append(script)
  })
  return loading
}

/** The amount in céntimos, exact: 118.00 soles are 11800. */
export function cents(amount: number): number {
  return Math.round(amount * 100)
}

/** The tokens of the simulator (ADR-073): the first characters say what the simulated bank does. Only offered when the platform uses the simulator. */
export const SANDBOX_CARDS: { code: string; label: string }[] = [
  { code: 'OK', label: 'Tarjeta aprobada' },
  { code: 'NOFONDOS', label: 'Sin fondos' },
  { code: '3DS', label: 'Pide autenticación 3D Secure' },
  { code: 'CAIDA', label: 'La pasarela no responde' },
  { code: 'ERROR', label: 'La pasarela rechaza la solicitud' },
]

export const sandboxToken = (code: string) => `tkn_test_${code.padEnd(16, '0')}`

/** Opens the checkout of Culqi for one card payment and gives back the token that it returns. */
export async function askCulqiToken(options: { publicKey: string; amountCents: number; title: string; email: string }): Promise<string> {
  const Checkout = await loadCulqi()
  return new Promise<string>((resolve, reject) => {
    const checkout = new Checkout(options.publicKey, {
      settings: { currency: 'PEN', amount: options.amountCents, title: options.title },
      client: { email: options.email },
      options: { lang: 'es', modal: true, paymentMethods: { tarjeta: true, yape: false, billetera: false, bancaMovil: false, agente: false, cuotealo: false } },
    })
    checkout.culqi = () => {
      if (checkout.token) {
        const id = checkout.token.id
        checkout.close()
        resolve(id)
      } else {
        reject(new Error(checkout.error?.user_message ?? 'No se pudo leer la tarjeta. Intente de nuevo.'))
      }
    }
    checkout.open()
  })
}
