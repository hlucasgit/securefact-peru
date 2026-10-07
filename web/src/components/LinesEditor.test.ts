import { emptyLine, lineFrom, toRequestLine } from './LinesEditor'
import { emptyFishing, emptyGuest } from './LineDetailEditors'
import { copyTransport, emptyLeg } from './TransportEditor'

describe('toRequestLine', () => {
  it('sends the amounts as typed and one bag per unit when bags are on', () => {
    const line = { ...emptyLine(), description: ' Bebida ', quantity: '4', unitValue: '3.5', bags: true }

    expect(toRequestLine(line, false)).toEqual({
      description: 'Bebida',
      unitCode: 'NIU',
      productCode: null,
      tax: { quantity: 4, unitValue: 3.5, igvAffectationCode: '10', plasticBagCount: 4 },
    })
  })

  it('turns the ad valorem ISC percentage into a fraction and keeps a fixed amount as it is', () => {
    const percent = toRequestLine({ ...emptyLine(), quantity: '3', unitValue: '100', isc: 'AdValorem', iscValue: '10' }, false)
    const fixed = toRequestLine({ ...emptyLine(), quantity: '3', unitValue: '100', isc: 'FixedAmount', iscValue: '1.5' }, false)

    expect(percent.tax.isc).toEqual({ system: 'AdValorem', rateOrUnitAmount: 0.1 })
    expect(fixed.tax.isc).toEqual({ system: 'FixedAmount', rateOrUnitAmount: 1.5 })
  })

  it('sends the reference value, and no unit value, for a free operation', () => {
    const line = toRequestLine({ ...emptyLine(), affectation: '11', unitValue: '99', referenceValue: '30' }, true)

    expect(line.tax).toMatchObject({ unitValue: 0, referenceUnitValue: 30 })
  })

  it('omits the discount when it is empty', () => {
    expect(toRequestLine(emptyLine(), false).tax).not.toHaveProperty('discountAffectingBase')
    expect(toRequestLine({ ...emptyLine(), discount: '5' }, false).tax).toHaveProperty('discountAffectingBase', 5)
  })
})

describe('lineFrom', () => {
  it('rebuilds the editable line of a document line with its ISC and bags', () => {
    const line = lineFrom({
      lineNumber: 1,
      description: 'Bebida',
      unitCode: 'NIU',
      productCode: 'B1',
      quantity: 3,
      lineExtensionAmount: 300,
      taxCode: '1000',
      totalTaxAmount: 90.9,
      unitPriceIncludingTaxes: 130.3,
      unitValue: 100,
      igvAffectationCode: '10',
      isc: { system: 'AdValorem', rateOrUnitAmount: 0.1 },
      plasticBagCount: 3,
    })

    expect(line).toMatchObject({ quantity: '3', unitValue: '100', isc: 'AdValorem', iscValue: '10', bags: true, productCode: 'B1' })
  })
})

describe('the cargo transport of a line', () => {
  const transport = {
    originUbigeo: ' 150101 ',
    originAddress: ' Av. Argentina 123, Callao ',
    destinationUbigeo: '040101',
    destinationAddress: 'Parque Industrial, Arequipa',
    tripDetail: 'Carga seca Lima - Arequipa',
    serviceReferenceValue: '1500',
    effectiveLoadReferenceValue: '1.5',
    nominalLoadReferenceValue: '2.25',
    legs: [] as ReturnType<typeof emptyLeg>[],
  }

  it('is sent only when the detraction is of cargo transport', () => {
    const line = { ...emptyLine(), description: 'Flete', unitValue: '100', transport }

    expect(toRequestLine(line, false)).not.toHaveProperty('transport')
    expect(toRequestLine(line, false, 'transport').transport).toMatchObject({
      originUbigeo: '150101',
      originAddress: 'Av. Argentina 123, Callao',
      destinationUbigeo: '040101',
      serviceReferenceValue: 1500,
      effectiveLoadReferenceValue: 1.5,
      nominalLoadReferenceValue: 2.25,
    })
    expect(toRequestLine(line, false, 'transport').transport).not.toHaveProperty('legs')
  })

  it('sends the legs with their vehicles and leaves out what was not typed', () => {
    const leg = { ...emptyLeg(), originUbigeo: '150101', destinationUbigeo: '040101', vehicleConfiguration: ' T3S3 ', usefulLoadTonnes: '28', description: '', effectiveLoadTonnes: '20.5', returnEmpty: true }
    const sent = toRequestLine({ ...emptyLine(), transport: { ...transport, legs: [leg] } }, false, 'transport').transport

    expect(sent?.legs).toEqual([{ originUbigeo: '150101', destinationUbigeo: '040101', vehicleConfiguration: 'T3S3', usefulLoadTonnes: 28, effectiveLoadTonnes: 20.5, returnEmpty: true }])
  })

  it('copies the transport of a line to another with legs of their own', () => {
    const first = { ...transport, legs: [{ ...emptyLeg(), vehicleConfiguration: 'T3S3' }] }
    const copy = copyTransport(first)

    expect(copy).toEqual({ ...first, legs: [{ ...first.legs[0], key: copy.legs[0].key }] })
    expect(copy.legs[0].key).not.toBe(first.legs[0].key)
  })
})

describe('the fishing resource and the guest of a line', () => {
  it('sends the fishing data only in a detraction 004, with the quantity as a number', () => {
    const fishing = { ...emptyFishing(), vesselRegistration: ' CE-1234-PM ', vesselName: 'Don Pepe', speciesType: 'Anchoveta', unloadingPlace: 'Chimbote', unloadingDate: '2026-10-01', speciesQuantity: '12.5' }
    const line = { ...emptyLine(), description: 'Anchoveta', unitValue: '1000', fishing }

    expect(toRequestLine(line, false)).not.toHaveProperty('fishing')
    expect(toRequestLine(line, false, 'fishing').fishing).toEqual({ vesselRegistration: 'CE-1234-PM', vesselName: 'Don Pepe', speciesType: 'Anchoveta', unloadingPlace: 'Chimbote', unloadingDate: '2026-10-01', speciesQuantity: 12.5 })
  })

  it('sends the guest of a tourist package alone and the guest of a lodging with the data of the stay', () => {
    const guest = {
      ...emptyGuest(),
      name: ' John Smith ',
      documentNumber: ' P1234567 ',
      passportCountryCode: 'us',
      residenceCountryCode: 'us',
      countryEntryDate: '2026-09-28',
      checkInDate: '2026-09-29',
      checkOutDate: '2026-10-02',
      consumptionDate: '2026-10-02',
      stayDays: '3',
    }
    const line = { ...emptyLine(), guest }

    expect(toRequestLine(line, false, 'package').guest).toEqual({ name: 'John Smith', documentTypeCode: '7', documentNumber: 'P1234567', passportCountryCode: 'US' })
    expect(toRequestLine(line, false, 'lodging').guest).toEqual({
      name: 'John Smith',
      documentTypeCode: '7',
      documentNumber: 'P1234567',
      passportCountryCode: 'US',
      residenceCountryCode: 'US',
      countryEntryDate: '2026-09-28',
      checkInDate: '2026-09-29',
      checkOutDate: '2026-10-02',
      consumptionDate: '2026-10-02',
      stayDays: 3,
    })
    expect(toRequestLine(line, false)).not.toHaveProperty('guest')
  })
})
