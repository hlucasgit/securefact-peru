import { useCatalog } from '../api/queries'
import { SelectField, TextField } from './ui'

/** The fishing resource that a line of a detraction 004 (operation 1002) sells. Every value is text as typed; the API checks and converts them. */
export interface FishingState {
  vesselRegistration: string
  vesselName: string
  speciesType: string
  unloadingPlace: string
  unloadingDate: string
  speciesQuantity: string
}

/** The non-domiciled guest of a line of a lodging (0202) or tourist package (0205): the package states only the first four. */
export interface GuestState {
  name: string
  documentTypeCode: string
  documentNumber: string
  passportCountryCode: string
  residenceCountryCode: string
  countryEntryDate: string
  checkInDate: string
  checkOutDate: string
  consumptionDate: string
  stayDays: string
}

export const emptyFishing = (): FishingState => ({ vesselRegistration: '', vesselName: '', speciesType: '', unloadingPlace: '', unloadingDate: '', speciesQuantity: '' })

export const emptyGuest = (): GuestState => ({
  name: '',
  documentTypeCode: '7',
  documentNumber: '',
  passportCountryCode: '',
  residenceCountryCode: '',
  countryEntryDate: '',
  checkInDate: '',
  checkOutDate: '',
  consumptionDate: '',
  stayDays: '',
})

export function toRequestFishing(fishing: FishingState) {
  return {
    vesselRegistration: fishing.vesselRegistration.trim(),
    vesselName: fishing.vesselName.trim(),
    speciesType: fishing.speciesType.trim(),
    unloadingPlace: fishing.unloadingPlace.trim(),
    unloadingDate: fishing.unloadingDate,
    speciesQuantity: Number(fishing.speciesQuantity),
  }
}

/** The `guest` of a line request. A tourist package sends the guest alone, without the data of a stay. */
export function toRequestGuest(guest: GuestState, lodging: boolean) {
  const base = {
    name: guest.name.trim(),
    documentTypeCode: guest.documentTypeCode,
    documentNumber: guest.documentNumber.trim(),
    passportCountryCode: guest.passportCountryCode.trim().toUpperCase(),
  }
  if (!lodging) return base
  return {
    ...base,
    residenceCountryCode: guest.residenceCountryCode.trim().toUpperCase(),
    countryEntryDate: guest.countryEntryDate,
    checkInDate: guest.checkInDate,
    checkOutDate: guest.checkOutDate,
    consumptionDate: guest.consumptionDate,
    stayDays: Number(guest.stayDays),
  }
}

const COUNTRY = '[A-Za-z]{2}'

export function FishingEditor({ fishing, onChange }: { fishing: FishingState; onChange: (fishing: FishingState) => void }) {
  const set = (patch: Partial<FishingState>) => onChange({ ...fishing, ...patch })
  return (
    <fieldset className="transport">
      <legend>Recursos hidrobiológicos</legend>
      <div className="form-grid">
        <TextField label="Matrícula de la embarcación" required maxLength={15} value={fishing.vesselRegistration} onChange={(event) => set({ vesselRegistration: event.target.value })} />
        <TextField label="Nombre de la embarcación" required maxLength={100} value={fishing.vesselName} onChange={(event) => set({ vesselName: event.target.value })} />
        <TextField label="Especie vendida" required maxLength={150} value={fishing.speciesType} onChange={(event) => set({ speciesType: event.target.value })} />
        <TextField label="Lugar de descarga" required maxLength={100} value={fishing.unloadingPlace} onChange={(event) => set({ unloadingPlace: event.target.value })} />
        <TextField label="Fecha de descarga" type="date" required value={fishing.unloadingDate} onChange={(event) => set({ unloadingDate: event.target.value })} />
        <TextField label="Cantidad de la especie" hint="toneladas métricas" type="number" min="0.01" step="0.01" required value={fishing.speciesQuantity} onChange={(event) => set({ speciesQuantity: event.target.value })} />
      </div>
    </fieldset>
  )
}

const SUPPORTED_IDENTITY = ['0', '1', '4', '6', '7', 'A']

/** The guest of a lodging (0202) or of a tourist package (0205); the lodging adds the data of the stay (catalogue 55 codes 4000 to 4009). */
export function GuestEditor({ guest, lodging, onChange }: { guest: GuestState; lodging: boolean; onChange: (guest: GuestState) => void }) {
  const identity = useCatalog('06')
  const set = (patch: Partial<GuestState>) => onChange({ ...guest, ...patch })
  return (
    <fieldset className="transport">
      <legend>{lodging ? 'Huésped no domiciliado y estadía' : 'Huésped no domiciliado'}</legend>
      <div className="stack">
        <div className="form-grid">
          <TextField label="Nombre del huésped" required minLength={3} maxLength={200} value={guest.name} onChange={(event) => set({ name: event.target.value })} />
          <SelectField label="Documento del huésped" value={guest.documentTypeCode} onChange={(event) => set({ documentTypeCode: event.target.value })}>
            {(identity.data ?? []).filter((entry) => SUPPORTED_IDENTITY.includes(entry.code)).map((entry) => <option key={entry.code} value={entry.code}>{entry.description}</option>)}
          </SelectField>
          <TextField label="Número del documento del huésped" required minLength={3} maxLength={20} value={guest.documentNumber} onChange={(event) => set({ documentNumber: event.target.value })} />
          <TextField label="País que emitió el pasaporte" hint="2 letras" required maxLength={2} pattern={COUNTRY} value={guest.passportCountryCode} onChange={(event) => set({ passportCountryCode: event.target.value.toUpperCase() })} />
        </div>
        {lodging && (
          <div className="form-grid">
            <TextField label="País de residencia" hint="2 letras" required maxLength={2} pattern={COUNTRY} value={guest.residenceCountryCode} onChange={(event) => set({ residenceCountryCode: event.target.value.toUpperCase() })} />
            <TextField label="Ingreso al país" type="date" required value={guest.countryEntryDate} onChange={(event) => set({ countryEntryDate: event.target.value })} />
            <TextField label="Ingreso al establecimiento" type="date" required value={guest.checkInDate} onChange={(event) => set({ checkInDate: event.target.value })} />
            <TextField label="Salida del establecimiento" hint="no antes del ingreso" type="date" required min={guest.checkInDate || undefined} value={guest.checkOutDate} onChange={(event) => set({ checkOutDate: event.target.value })} />
            <TextField label="Fecha de consumo" type="date" required value={guest.consumptionDate} onChange={(event) => set({ consumptionDate: event.target.value })} />
            <TextField label="Días de permanencia" type="number" min="0" max="9999" step="1" required value={guest.stayDays} onChange={(event) => set({ stayDays: event.target.value })} />
          </div>
        )}
      </div>
    </fieldset>
  )
}
