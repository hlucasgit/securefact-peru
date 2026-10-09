import { buildGuide, emptyContainer, emptyGood, emptyGuide, emptyRelated, isCustomsMotive, relatedTypesFor, toNumber } from './guide'

const sale = () => {
  const form = emptyGuide('2026-10-08')
  form.companyId = 'company-1'
  form.seriesId = 'series-1'
  form.grossWeight = '12,5'
  form.recipient = { documentTypeCode: '6', documentNumber: ' 20100070970 ', name: ' CLIENTE DEMO SAC ' }
  form.origin = { ubigeoCode: '150101', address: 'Av. Argentina 123', establishmentRuc: '', establishmentCode: '' }
  form.destination = { ubigeoCode: '150122', address: 'Calle Los Pinos 456', establishmentRuc: '', establishmentCode: '' }
  form.plate = 'abc123'
  form.driver = { documentTypeCode: '1', documentNumber: '12345678', firstNames: 'JUAN', lastNames: 'PEREZ', licenseNumber: 'q12345678' }
  form.goods = [{ ...emptyGood(), description: ' Caja de repuestos ', quantity: '3' }]
  return form
}

describe('buildGuide', () => {
  it('sends the vehicle and the driver in private transport, with the start of the transfer', () => {
    const body = buildGuide(sale())

    expect(body.modalityCode).toBe('02')
    expect(body.transferStartDate).toBe('2026-10-08')
    expect(body.handoverDate).toBeNull()
    expect(body.vehicle).toEqual({ plate: 'ABC123', circulationCard: null })
    expect(body.driver?.licenseNumber).toBe('Q12345678')
    expect(body.carrier).toBeNull()
    expect(body.grossWeight).toBe(12.5)
    expect(body.recipient).toEqual({ documentTypeCode: '6', documentNumber: '20100070970', name: 'CLIENTE DEMO SAC' })
    expect(body.goods).toEqual([{ description: 'Caja de repuestos', unitCode: 'NIU', quantity: 3, code: null, customs: null }])
  })

  it('sends the carrier and the handover day in public transport, and no vehicle', () => {
    const form = sale()
    form.modalityCode = '01'
    form.carrierRuc = '20601030013'
    form.carrierName = 'TRANSPORTES RAPIDOS SAC'

    const body = buildGuide(form)

    expect(body.carrier).toEqual({ ruc: '20601030013', name: 'TRANSPORTES RAPIDOS SAC', mtcRegistration: null })
    expect(body.vehicle).toBeNull()
    expect(body.driver).toBeNull()
    expect(body.handoverDate).toBe('2026-10-08')
    expect(body.transferStartDate).toBeNull()
  })

  it('keeps the supplier and the buyer only for the motives that use them, and the description only for «Otros»', () => {
    const form = sale()
    form.supplier = { documentTypeCode: '6', documentNumber: '20100066603', name: 'PROVEEDOR SAC' }
    form.buyer = { documentTypeCode: '6', documentNumber: '20100070970', name: 'COMPRADOR SAC' }
    form.motiveDescription = 'Muestras'

    expect(buildGuide(form)).toMatchObject({ supplier: null, buyer: null, motiveDescription: null })

    form.motiveCode = '02'
    expect(buildGuide(form)).toMatchObject({ supplier: { documentNumber: '20100066603' }, buyer: null })

    form.motiveCode = '13'
    expect(buildGuide(form)).toMatchObject({ supplier: { documentNumber: '20100066603' }, buyer: { documentNumber: '20100070970' }, motiveDescription: 'Muestras' })
  })

  it('leaves out the related documents without a number', () => {
    const form = sale()
    form.related = [{ ...emptyRelated(), number: ' f001-123 ', issuerRuc: '20100066603' }, emptyRelated()]

    expect(buildGuide(form).relatedDocuments).toEqual([{ typeCode: '01', number: 'F001-123', issuerRuc: '20100066603' }])
  })
})

describe('toNumber', () => {
  it('reads a decimal point or a comma', () => {
    expect(toNumber('1.5')).toBe(1.5)
    expect(toNumber(' 1,5 ')).toBe(1.5)
    expect(toNumber('abc')).toBeNaN()
  })
})

describe('guides with customs documents and the itinerant issuer', () => {
  it('sends no point of arrival for the itinerant issuer and no customs data for the other motives', () => {
    const form = sale()
    form.motiveCode = '18'
    form.portCode = 'CLL'
    form.wholeTransfer = true

    const body = buildGuide(form)

    expect(body.destination).toBeNull()
    expect(body.customs).toBeNull()
    expect(buildGuide(sale()).destination).toMatchObject({ ubigeoCode: '150122' })
  })

  it('sends the port, the weights and the containers of an export, with the properties of each good', () => {
    const form = sale()
    form.motiveCode = '09'
    form.portType = '1'
    form.portCode = 'cll'
    form.portName = 'Callao'
    form.netWeight = '1450,5'
    form.weightNote = 'incluye sacos'
    form.containers = [{ ...emptyContainer(), number: 'msku1234567', seal: 'seal001' }, emptyContainer()]
    form.goods = [{ ...emptyGood(), description: 'Café', unitCode: '2u', declarationNumber: '118-2026-40-654321', declarationSeries: '1' }, { ...emptyGood(), description: '' }]

    const body = buildGuide(form)

    expect(body.customs).toEqual({
      portCode: 'CLL',
      portType: '1',
      portName: 'Callao',
      wholeTransfer: false,
      manifestContainers: false,
      netWeight: 1450.5,
      weightNote: 'incluye sacos',
      containers: [{ number: 'MSKU1234567', seal: 'SEAL001' }],
    })
    expect(body.goods).toHaveLength(1)
    expect(body.goods[0]).toMatchObject({ unitCode: '2U', customs: { declarationNumber: '118-2026-40-654321', declarationSeries: '1', emptyContainer: null } })
  })

  it('reads the empty-container indicator of a manifest line', () => {
    const form = sale()
    form.motiveCode = '19'
    form.goods = [{ ...emptyGood(), description: 'Contenedor', transportDocument: 'hbl-998', transportDetail: '1', manifestContainer: 'msku1234567', emptyContainer: '1' }]

    expect(buildGuide(form).goods[0].customs).toMatchObject({ transportDocument: 'HBL-998', manifestContainer: 'MSKU1234567', emptyContainer: true })
  })

  it('offers the customs documents only to the motives that carry them', () => {
    const all = ['01', '09', '50', '52', '91', '92']

    expect(relatedTypesFor('01', all)).toEqual(['01', '09'])
    expect(relatedTypesFor('08', all)).toEqual(['09', '50', '52'])
    expect(relatedTypesFor('09', all)).toEqual(['09', '50', '52'])
    expect(relatedTypesFor('19', all)).toEqual(['50', '52', '91', '92'])
    expect(['08', '09', '19', '18', '01'].map(isCustomsMotive)).toEqual([true, true, true, false, false])
  })
})
