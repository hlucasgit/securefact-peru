import type { CreateGuideBody, GreAddress, GreParty } from '../api/types'

// The form of a guide of the sender keeps every value as text, as typed. This module turns it into the request of the API; it checks nothing of the regulation (the API does, with the rules of SUNAT).

export interface PartyState {
  documentTypeCode: string
  documentNumber: string
  name: string
}

export interface AddressState {
  ubigeoCode: string
  address: string
  establishmentRuc: string
  establishmentCode: string
}

export interface GoodState {
  key: number
  description: string
  unitCode: string
  quantity: string
  code: string
}

export interface RelatedState {
  key: number
  typeCode: string
  number: string
  issuerRuc: string
}

export interface GuideForm {
  companyId: string
  seriesId: string
  issueDate: string
  motiveCode: string
  motiveDescription: string
  modalityCode: string
  startDate: string
  grossWeight: string
  weightUnit: string
  packageCount: string
  note: string
  recipient: PartyState
  supplier: PartyState
  buyer: PartyState
  origin: AddressState
  destination: AddressState
  carrierRuc: string
  carrierName: string
  carrierMtc: string
  plate: string
  circulationCard: string
  driver: { documentTypeCode: string; documentNumber: string; firstNames: string; lastNames: string; licenseNumber: string }
  goods: GoodState[]
  related: RelatedState[]
}

let counter = 0

export const emptyParty = (): PartyState => ({ documentTypeCode: '6', documentNumber: '', name: '' })
export const emptyAddress = (): AddressState => ({ ubigeoCode: '', address: '', establishmentRuc: '', establishmentCode: '' })
export const emptyGood = (): GoodState => ({ key: ++counter, description: '', unitCode: 'NIU', quantity: '1', code: '' })
export const emptyRelated = (): RelatedState => ({ key: ++counter, typeCode: '01', number: '', issuerRuc: '' })

export const emptyGuide = (today: string): GuideForm => ({
  companyId: '',
  seriesId: '',
  issueDate: today,
  motiveCode: '01',
  motiveDescription: '',
  modalityCode: '02',
  startDate: today,
  grossWeight: '',
  weightUnit: 'KGM',
  packageCount: '',
  note: '',
  recipient: emptyParty(),
  supplier: emptyParty(),
  buyer: emptyParty(),
  origin: emptyAddress(),
  destination: emptyAddress(),
  carrierRuc: '',
  carrierName: '',
  carrierMtc: '',
  plate: '',
  circulationCard: '',
  driver: { documentTypeCode: '1', documentNumber: '', firstNames: '', lastNames: '', licenseNumber: '' },
  goods: [emptyGood()],
  related: [],
})

/** The motives that ask for the party that sells to the sender (supplier) or the one that buys on the recipient's behalf (buyer). */
export const needsSupplier = (motive: string) => motive === '02' || motive === '07' || motive === '13'
export const needsBuyer = (motive: string) => motive === '03' || motive === '13'

const text = (value: string) => value.trim()
const optional = (value: string) => (value.trim() === '' ? null : value.trim())
const party = (state: PartyState): GreParty => ({ documentTypeCode: state.documentTypeCode, documentNumber: text(state.documentNumber), name: text(state.name) })
const address = (state: AddressState): GreAddress => ({
  ubigeoCode: text(state.ubigeoCode),
  address: text(state.address),
  establishmentRuc: optional(state.establishmentRuc),
  establishmentCode: optional(state.establishmentCode),
})

/** Converts a number typed with a decimal point or comma; NaN when it is not a number. */
export const toNumber = (value: string) => Number(value.trim().replace(',', '.'))

export function buildGuide(form: GuideForm): CreateGuideBody {
  const isPrivate = form.modalityCode === '02'
  const supplierFilled = form.supplier.documentNumber.trim() !== ''
  const buyerFilled = form.buyer.documentNumber.trim() !== ''
  return {
    companyId: form.companyId,
    seriesId: form.seriesId,
    issueDate: optional(form.issueDate),
    motiveCode: form.motiveCode,
    motiveDescription: form.motiveCode === '13' ? optional(form.motiveDescription) : null,
    modalityCode: form.modalityCode,
    transferStartDate: isPrivate ? optional(form.startDate) : null,
    handoverDate: isPrivate ? null : optional(form.startDate),
    grossWeight: toNumber(form.grossWeight),
    weightUnit: form.weightUnit,
    packageCount: optional(form.packageCount) === null ? null : Math.trunc(toNumber(form.packageCount)),
    note: optional(form.note),
    recipient: party(form.recipient),
    supplier: needsSupplier(form.motiveCode) && supplierFilled ? party(form.supplier) : null,
    buyer: needsBuyer(form.motiveCode) && buyerFilled ? party(form.buyer) : null,
    origin: address(form.origin),
    destination: address(form.destination),
    carrier: isPrivate ? null : { ruc: text(form.carrierRuc), name: text(form.carrierName), mtcRegistration: optional(form.carrierMtc) },
    vehicle: isPrivate ? { plate: text(form.plate).toUpperCase(), circulationCard: optional(form.circulationCard) } : null,
    driver: isPrivate
      ? {
          documentTypeCode: form.driver.documentTypeCode,
          documentNumber: text(form.driver.documentNumber),
          firstNames: text(form.driver.firstNames),
          lastNames: text(form.driver.lastNames),
          licenseNumber: text(form.driver.licenseNumber).toUpperCase(),
        }
      : null,
    goods: form.goods.map((good) => ({ description: text(good.description), unitCode: text(good.unitCode).toUpperCase(), quantity: toNumber(good.quantity), code: optional(good.code) })),
    relatedDocuments: form.related.filter((item) => item.number.trim() !== '').map((item) => ({ typeCode: item.typeCode, number: text(item.number).toUpperCase(), issuerRuc: optional(item.issuerRuc) })),
  }
}
