import type { CreateCarrierGuideBody, GreFreightPayer } from '../api/types'
import { emptyAddress, emptyGood, emptyParty, optional, party, text, toNumber, type AddressState, type GoodState, type PartyState, type RelatedState } from './guide'

// The form of a guide of the carrier (type 31) keeps every value as text, as typed. This module turns it into the request of the API; the API checks it against the rules of SUNAT.

export interface VehicleState {
  key: number
  plate: string
  circulationCard: string
}

export interface DriverState {
  key: number
  documentTypeCode: string
  documentNumber: string
  firstNames: string
  lastNames: string
  licenseNumber: string
}

export interface CarrierGuideForm {
  companyId: string
  seriesId: string
  issueDate: string
  startDate: string
  grossWeight: string
  weightUnit: string
  packageCount: string
  note: string
  mtcRegistration: string
  sender: PartyState
  recipient: PartyState
  origin: AddressState
  destination: AddressState
  vehicle: VehicleState
  secondaryVehicles: VehicleState[]
  driver: DriverState
  secondaryDrivers: DriverState[]
  /** The guide of the sender (09) that lists the goods, as «T001-45»; empty when the goods are listed here. */
  senderGuide: string
  goods: GoodState[]
  related: RelatedState[]
  freightPayer: GreFreightPayer
  thirdPartyPayer: PartyState
  subcontracted: boolean
  subcontractor: PartyState
  plannedTransshipment: boolean
  returnWithEmptyPackaging: boolean
  returnEmptyVehicle: boolean
  /** All the goods of the related voucher travel, so they are not listed; the note says what they are when SUNAT does not have them. */
  wholeTransfer: boolean
  wholeTransferNote: string
}

let counter = 0

export const emptyVehicle = (): VehicleState => ({ key: ++counter, plate: '', circulationCard: '' })
export const emptyDriver = (): DriverState => ({ key: ++counter, documentTypeCode: '1', documentNumber: '', firstNames: '', lastNames: '', licenseNumber: '' })

export const emptyCarrierGuide = (today: string): CarrierGuideForm => ({
  companyId: '',
  seriesId: '',
  issueDate: today,
  startDate: today,
  grossWeight: '',
  weightUnit: 'KGM',
  packageCount: '',
  note: '',
  mtcRegistration: '',
  sender: emptyParty(),
  recipient: emptyParty(),
  origin: emptyAddress(),
  destination: emptyAddress(),
  vehicle: emptyVehicle(),
  secondaryVehicles: [],
  driver: emptyDriver(),
  secondaryDrivers: [],
  senderGuide: '',
  goods: [emptyGood()],
  related: [],
  freightPayer: 'Sender',
  thirdPartyPayer: emptyParty(),
  subcontracted: false,
  subcontractor: emptyParty(),
  plannedTransshipment: false,
  returnWithEmptyPackaging: false,
  returnEmptyVehicle: false,
  wholeTransfer: false,
  wholeTransferNote: '',
})

const vehicle = (state: VehicleState) => ({ plate: text(state.plate).toUpperCase(), circulationCard: optional(state.circulationCard) })
const driver = (state: DriverState) => ({
  documentTypeCode: state.documentTypeCode,
  documentNumber: text(state.documentNumber),
  firstNames: text(state.firstNames),
  lastNames: text(state.lastNames),
  licenseNumber: text(state.licenseNumber).toUpperCase(),
})

/**
 * The request of the guide of the carrier. The guide of the sender that lists the goods goes as a related document whose issuer is the sender, and then no goods are sent; the other related
 * documents are sent as typed.
 */
export function buildCarrierGuide(form: CarrierGuideForm): CreateCarrierGuideBody {
  const senderGuide = text(form.senderGuide).toUpperCase()
  const related = form.related
    .filter((item) => item.number.trim() !== '')
    .map((item) => ({ typeCode: item.typeCode, number: text(item.number).toUpperCase(), issuerRuc: optional(item.issuerRuc) }))
  if (senderGuide !== '') related.unshift({ typeCode: '09', number: senderGuide, issuerRuc: form.sender.documentTypeCode === '6' ? optional(form.sender.documentNumber) : null })
  return {
    companyId: form.companyId,
    seriesId: form.seriesId,
    issueDate: optional(form.issueDate),
    transferStartDate: form.startDate,
    grossWeight: toNumber(form.grossWeight),
    weightUnit: form.weightUnit,
    packageCount: optional(form.packageCount) === null ? null : Math.trunc(toNumber(form.packageCount)),
    note: optional(form.note),
    mtcRegistration: optional(form.mtcRegistration)?.toUpperCase() ?? null,
    sender: party(form.sender),
    recipient: party(form.recipient),
    origin: { ubigeoCode: text(form.origin.ubigeoCode), address: text(form.origin.address) },
    destination: { ubigeoCode: text(form.destination.ubigeoCode), address: text(form.destination.address) },
    vehicle: vehicle(form.vehicle),
    secondaryVehicles: form.secondaryVehicles.filter((item) => item.plate.trim() !== '').map(vehicle),
    driver: driver(form.driver),
    secondaryDrivers: form.secondaryDrivers.filter((item) => item.documentNumber.trim() !== '').map(driver),
    goods: senderGuide !== '' ? null : form.goods.filter((good) => !form.wholeTransfer || good.description.trim() !== '').map((good) => ({ description: text(good.description), unitCode: text(good.unitCode).toUpperCase(), quantity: toNumber(good.quantity), code: optional(good.code) })),
    relatedDocuments: related,
    freightPayer: form.freightPayer,
    thirdPartyPayer: form.freightPayer === 'ThirdParty' ? party(form.thirdPartyPayer) : null,
    subcontracted: form.subcontracted,
    subcontractor: form.subcontracted ? { documentTypeCode: '6', documentNumber: text(form.subcontractor.documentNumber), name: text(form.subcontractor.name) } : null,
    plannedTransshipment: form.plannedTransshipment,
    returnWithEmptyPackaging: form.returnWithEmptyPackaging,
    returnEmptyVehicle: form.returnEmptyVehicle,
    wholeTransfer: form.wholeTransfer,
    wholeTransferNote: form.wholeTransfer ? optional(form.wholeTransferNote) : null,
  }
}
