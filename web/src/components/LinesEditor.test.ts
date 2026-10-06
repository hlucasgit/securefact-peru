import { emptyLine, lineFrom, toRequestLine } from './LinesEditor'

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
