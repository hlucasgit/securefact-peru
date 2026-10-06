import { TextField } from './ui'

/** A leg (tramo) of a cargo transport and the vehicle that runs it. Every value is text as typed: the API checks and converts them. */
export interface LegState {
  key: number
  originUbigeo: string
  destinationUbigeo: string
  vehicleConfiguration: string
  usefulLoadTonnes: string
  description: string
  effectiveLoadTonnes: string
  effectiveLoadReferenceValue: string
  nominalLoadReferenceValue: string
  returnEmpty: boolean
}

/** The data of the cargo transport that a line of a detraction 027 (operation 1004) states. */
export interface TransportState {
  originUbigeo: string
  originAddress: string
  destinationUbigeo: string
  destinationAddress: string
  tripDetail: string
  serviceReferenceValue: string
  effectiveLoadReferenceValue: string
  nominalLoadReferenceValue: string
  legs: LegState[]
}

let legCounter = 0

export const emptyLeg = (): LegState => ({
  key: ++legCounter,
  originUbigeo: '',
  destinationUbigeo: '',
  vehicleConfiguration: '',
  usefulLoadTonnes: '',
  description: '',
  effectiveLoadTonnes: '',
  effectiveLoadReferenceValue: '',
  nominalLoadReferenceValue: '',
  returnEmpty: false,
})

export const emptyTransport = (): TransportState => ({
  originUbigeo: '',
  originAddress: '',
  destinationUbigeo: '',
  destinationAddress: '',
  tripDetail: '',
  serviceReferenceValue: '',
  effectiveLoadReferenceValue: '',
  nominalLoadReferenceValue: '',
  legs: [],
})

/** The same data for another line: the legs get their own keys. */
export const copyTransport = (transport: TransportState): TransportState => ({ ...transport, legs: transport.legs.map((leg) => ({ ...leg, key: ++legCounter })) })

const num = (text: string) => Number(text)
const optional = (text: string) => (text.trim() === '' ? undefined : Number(text))

/** The `transport` of a line request. Amounts go as typed; the optional values of a leg are left out when empty. */
export function toRequestTransport(transport: TransportState) {
  return {
    originUbigeo: transport.originUbigeo.trim(),
    originAddress: transport.originAddress.trim(),
    destinationUbigeo: transport.destinationUbigeo.trim(),
    destinationAddress: transport.destinationAddress.trim(),
    tripDetail: transport.tripDetail.trim(),
    serviceReferenceValue: num(transport.serviceReferenceValue),
    effectiveLoadReferenceValue: num(transport.effectiveLoadReferenceValue),
    nominalLoadReferenceValue: num(transport.nominalLoadReferenceValue),
    ...(transport.legs.length > 0
      ? {
          legs: transport.legs.map((leg) => ({
            originUbigeo: leg.originUbigeo.trim(),
            destinationUbigeo: leg.destinationUbigeo.trim(),
            vehicleConfiguration: leg.vehicleConfiguration.trim(),
            usefulLoadTonnes: num(leg.usefulLoadTonnes),
            ...(leg.description.trim() ? { description: leg.description.trim() } : {}),
            ...(optional(leg.effectiveLoadTonnes) !== undefined ? { effectiveLoadTonnes: optional(leg.effectiveLoadTonnes) } : {}),
            ...(optional(leg.effectiveLoadReferenceValue) !== undefined ? { effectiveLoadReferenceValue: optional(leg.effectiveLoadReferenceValue) } : {}),
            ...(optional(leg.nominalLoadReferenceValue) !== undefined ? { nominalLoadReferenceValue: optional(leg.nominalLoadReferenceValue) } : {}),
            returnEmpty: leg.returnEmpty,
          })),
        }
      : {}),
  }
}

interface Props {
  transport: TransportState
  onChange: (transport: TransportState) => void
}

const UBIGEO = '[0-9]{6}'

/** The cargo transport data of one line (SUNAT's «Información de Tramo y Vehículo»): origin, destination, trip and reference values, and the legs with their vehicles, which are optional. */
export function TransportEditor({ transport, onChange }: Props) {
  const set = (patch: Partial<TransportState>) => onChange({ ...transport, ...patch })
  const setLeg = (key: number, patch: Partial<LegState>) => set({ legs: transport.legs.map((leg) => (leg.key === key ? { ...leg, ...patch } : leg)) })

  return (
    <fieldset className="transport">
      <legend>Transporte de carga</legend>
      <div className="stack">
        <div className="form-grid">
          <TextField label="Ubigeo de origen" hint="6 dígitos" required inputMode="numeric" maxLength={6} pattern={UBIGEO} value={transport.originUbigeo} onChange={(event) => set({ originUbigeo: event.target.value })} />
          <TextField label="Dirección de origen" required minLength={3} maxLength={200} value={transport.originAddress} onChange={(event) => set({ originAddress: event.target.value })} />
          <TextField label="Ubigeo de destino" hint="6 dígitos" required inputMode="numeric" maxLength={6} pattern={UBIGEO} value={transport.destinationUbigeo} onChange={(event) => set({ destinationUbigeo: event.target.value })} />
          <TextField label="Dirección de destino" required minLength={3} maxLength={200} value={transport.destinationAddress} onChange={(event) => set({ destinationAddress: event.target.value })} />
        </div>
        <TextField label="Detalle del viaje" required minLength={3} maxLength={500} value={transport.tripDetail} onChange={(event) => set({ tripDetail: event.target.value })} />
        <div className="form-grid">
          <TextField label="Valor referencial del servicio" hint="S/" type="number" min="0.01" step="0.01" required value={transport.serviceReferenceValue} onChange={(event) => set({ serviceReferenceValue: event.target.value })} />
          <TextField label="Valor referencial por carga efectiva" hint="S/" type="number" min="0.01" step="0.01" required value={transport.effectiveLoadReferenceValue} onChange={(event) => set({ effectiveLoadReferenceValue: event.target.value })} />
          <TextField label="Valor referencial por carga útil nominal" hint="S/" type="number" min="0.01" step="0.01" required value={transport.nominalLoadReferenceValue} onChange={(event) => set({ nominalLoadReferenceValue: event.target.value })} />
        </div>

        {transport.legs.map((leg, index) => (
          <fieldset key={leg.key} className="leg">
            <legend className="row spread" style={{ width: '100%', padding: 0 }}>
              <strong>Tramo {index + 1}</strong>
              <button className="btn small ghost" type="button" onClick={() => set({ legs: transport.legs.filter((item) => item.key !== leg.key) })}>
                Quitar tramo
              </button>
            </legend>
            <div className="form-grid">
              <TextField label={`Tramo ${index + 1}: ubigeo de origen`} required inputMode="numeric" maxLength={6} pattern={UBIGEO} value={leg.originUbigeo} onChange={(event) => setLeg(leg.key, { originUbigeo: event.target.value })} />
              <TextField label={`Tramo ${index + 1}: ubigeo de destino`} required inputMode="numeric" maxLength={6} pattern={UBIGEO} value={leg.destinationUbigeo} onChange={(event) => setLeg(leg.key, { destinationUbigeo: event.target.value })} />
              <TextField label={`Tramo ${index + 1}: configuración vehicular`} hint="D.S. 058-2003-MTC" required maxLength={15} value={leg.vehicleConfiguration} onChange={(event) => setLeg(leg.key, { vehicleConfiguration: event.target.value })} />
              <TextField label={`Tramo ${index + 1}: carga útil`} hint="toneladas" type="number" min="0.01" step="0.01" required value={leg.usefulLoadTonnes} onChange={(event) => setLeg(leg.key, { usefulLoadTonnes: event.target.value })} />
              <TextField label={`Tramo ${index + 1}: descripción`} hint="opcional" minLength={3} maxLength={100} value={leg.description} onChange={(event) => setLeg(leg.key, { description: event.target.value })} />
              <TextField label={`Tramo ${index + 1}: carga efectiva`} hint="toneladas, opcional" type="number" min="0.01" step="0.01" value={leg.effectiveLoadTonnes} onChange={(event) => setLeg(leg.key, { effectiveLoadTonnes: event.target.value })} />
              <TextField label={`Tramo ${index + 1}: valor por carga efectiva`} hint="S/, opcional" type="number" min="0.01" step="0.01" value={leg.effectiveLoadReferenceValue} onChange={(event) => setLeg(leg.key, { effectiveLoadReferenceValue: event.target.value })} />
              <TextField label={`Tramo ${index + 1}: valor por carga útil nominal`} hint="S/, opcional" type="number" min="0.01" step="0.01" value={leg.nominalLoadReferenceValue} onChange={(event) => setLeg(leg.key, { nominalLoadReferenceValue: event.target.value })} />
              <label className="checkbox" style={{ alignSelf: 'end' }}>
                <input type="checkbox" checked={leg.returnEmpty} onChange={(event) => setLeg(leg.key, { returnEmpty: event.target.checked })} />
                <span>Retorno vacío</span>
              </label>
            </div>
          </fieldset>
        ))}
        {transport.legs.length < 99 && (
          <div>
            <button className="btn small" type="button" onClick={() => set({ legs: [...transport.legs, emptyLeg()] })}>
              Agregar tramo y vehículo
            </button>
          </div>
        )}
      </div>
    </fieldset>
  )
}
