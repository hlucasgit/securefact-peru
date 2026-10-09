import { buildCarrierGuide, emptyCarrierGuide, emptyDriver, emptyVehicle } from './carrierGuide'
import { emptyGood, emptyRelated } from './guide'

const transfer = () => {
  const form = emptyCarrierGuide('2026-10-08')
  form.companyId = 'company-1'
  form.seriesId = 'series-v'
  form.grossWeight = '1500,5'
  form.mtcRegistration = 'mtc123'
  form.sender = { documentTypeCode: '6', documentNumber: ' 20100070970 ', name: ' REMITENTE SAC ' }
  form.recipient = { documentTypeCode: '6', documentNumber: '20100066603', name: 'CLIENTE SAC' }
  form.origin = { ubigeoCode: '150101', address: ' Av. Argentina 123 ', establishmentRuc: '', establishmentCode: '' }
  form.destination = { ubigeoCode: '040101', address: 'Calle Mercaderes 45', establishmentRuc: '', establishmentCode: '' }
  form.vehicle = { ...emptyVehicle(), plate: 'abc123', circulationCard: '1234567890' }
  form.driver = { ...emptyDriver(), documentNumber: '12345678', firstNames: 'JUAN', lastNames: 'PEREZ', licenseNumber: 'q12345678' }
  form.goods = [{ ...emptyGood(), description: ' Cajas ', quantity: '10' }]
  return form
}

describe('buildCarrierGuide', () => {
  it('sends the goods, the vehicle and the driver, and no sender guide', () => {
    const body = buildCarrierGuide(transfer())

    expect(body.grossWeight).toBe(1500.5)
    expect(body.mtcRegistration).toBe('MTC123')
    expect(body.sender).toEqual({ documentTypeCode: '6', documentNumber: '20100070970', name: 'REMITENTE SAC' })
    expect(body.vehicle).toEqual({ plate: 'ABC123', circulationCard: '1234567890' })
    expect(body.driver.licenseNumber).toBe('Q12345678')
    expect(body.goods).toEqual([{ description: 'Cajas', unitCode: 'NIU', quantity: 10, code: null }])
    expect(body.relatedDocuments).toEqual([])
    expect(body.freightPayer).toBe('Sender')
    expect(body.subcontracted).toBe(false)
  })

  it('relates the guide of the sender, with the sender as issuer, and sends no goods', () => {
    const form = transfer()
    form.senderGuide = ' t001-45 '
    form.related = [{ ...emptyRelated(), number: 'f001-9', issuerRuc: '20100070970' }]

    const body = buildCarrierGuide(form)

    expect(body.goods).toBeNull()
    expect(body.relatedDocuments).toEqual([
      { typeCode: '09', number: 'T001-45', issuerRuc: '20100070970' },
      { typeCode: '01', number: 'F001-9', issuerRuc: '20100070970' },
    ])
  })

  it('sends the whole transfer with its note and leaves the unfilled goods out', () => {
    const form = transfer()
    form.wholeTransfer = true
    form.wholeTransferNote = ' Cinco cajas '
    form.goods = [{ ...emptyGood(), description: '' }]
    form.related = [{ ...emptyRelated(), typeCode: '03', number: 'b001-5', issuerRuc: '20100070970' }]

    const body = buildCarrierGuide(form)

    expect(body).toMatchObject({ wholeTransfer: true, wholeTransferNote: 'Cinco cajas', goods: [] })
    expect(buildCarrierGuide({ ...form, wholeTransfer: false })).toMatchObject({ wholeTransfer: false, wholeTransferNote: null })
  })

  it('keeps the payer and the subcontractor only when they are chosen, and drops empty secondary vehicles and drivers', () => {
    const form = transfer()
    form.thirdPartyPayer = { documentTypeCode: '6', documentNumber: '20100066603', name: 'PAGADOR SAC' }
    form.subcontractor = { documentTypeCode: '6', documentNumber: '20601030013', name: 'SUBCONTRATADOR SAC' }
    form.secondaryVehicles = [emptyVehicle()]
    form.secondaryDrivers = [emptyDriver()]

    const without = buildCarrierGuide(form)
    expect(without).toMatchObject({ thirdPartyPayer: null, subcontractor: null, secondaryVehicles: [], secondaryDrivers: [] })

    form.freightPayer = 'ThirdParty'
    form.subcontracted = true
    const withBoth = buildCarrierGuide(form)
    expect(withBoth.thirdPartyPayer).toMatchObject({ documentNumber: '20100066603' })
    expect(withBoth.subcontractor).toEqual({ documentTypeCode: '6', documentNumber: '20601030013', name: 'SUBCONTRATADOR SAC' })
  })
})
