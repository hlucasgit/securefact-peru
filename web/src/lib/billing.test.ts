import { buildPayment, monthClosed, buildPolicy, buildPrice, buildSchedule, firstOfNextMonth, monthKey, monthLabel, percent, rateFromPercent } from './billing'

describe('billing helpers', () => {
  it('starts the earliest price on the first day of the next month', () => {
    expect(firstOfNextMonth('2026-10-09')).toBe('2026-11-01')
    expect(firstOfNextMonth('2026-12-31')).toBe('2027-01-01')
    expect(firstOfNextMonth('2026-01-01')).toBe('2026-02-01')
  })

  it('names a month for the person and for the path', () => {
    expect(monthKey('2026-11-01')).toBe('2026-11')
    expect(monthLabel('2026-11-01')).toBe('noviembre de 2026')
    expect(monthLabel('2027-01')).toBe('enero de 2027')
  })

  it('knows when a month is closed', () => {
    expect(monthClosed('2026-10-01', '2026-11-02')).toBe(true)
    expect(monthClosed('2026-11-01', '2026-11-30')).toBe(false)
    expect(monthClosed('2026-12-01', '2026-11-30')).toBe(false)
  })

  it('shows a rate as a percentage', () => {
    expect(percent(0.2)).toBe('20 %')
    expect(percent(0.125)).toBe('12.5 %')
    expect(percent(0.3)).toBe('30 %')
  })

  it('turns a percentage typed by a person into the exact share', () => {
    expect(rateFromPercent('20')).toBe(0.2)
    expect(rateFromPercent('12.5')).toBe(0.125)
    expect(rateFromPercent('0.07')).toBe(0.0007)
    expect(rateFromPercent('33.33')).toBe(0.3333)
  })

  it('sends the overage fields only for a plan that charges the overage', () => {
    const form = { effectiveFrom: '2026-11-01', monthlyFee: '99.90', includedDocuments: '200', overageUnitPrice: '0.15', note: ' Lanzamiento ' }

    expect(buildPrice(form, true)).toEqual({ effectiveFrom: '2026-11-01', monthlyFee: 99.9, includedDocuments: 200, overageUnitPrice: 0.15, note: 'Lanzamiento' })
    expect(buildPrice(form, false)).toEqual({ effectiveFrom: '2026-11-01', monthlyFee: 99.9, includedDocuments: null, overageUnitPrice: null, note: 'Lanzamiento' })
    expect(buildPrice({ ...form, includedDocuments: '', overageUnitPrice: '', note: '' }, true)).toMatchObject({ includedDocuments: null, overageUnitPrice: null, note: null })
  })

  it('builds the policy with an empty grace as never suspend', () => {
    expect(buildPolicy({ effectiveFrom: '2027-01-01', dueDays: '7', suspendAfterDays: '', note: '' })).toEqual({ effectiveFrom: '2027-01-01', dueDays: 7, suspendAfterDays: null, note: null })
    expect(buildPolicy({ effectiveFrom: '2027-01-01', dueDays: '0', suspendAfterDays: '0', note: 'x' })).toMatchObject({ dueDays: 0, suspendAfterDays: 0 })
  })

  it('builds the commission terms with the tiers as shares', () => {
    const body = buildSchedule('2027-01-01', [{ key: 'a', minAccounts: '0', percent: '15' }, { key: 'b', minAccounts: '20', percent: '22.5' }], ' ')

    expect(body).toEqual({ effectiveFrom: '2027-01-01', tiers: [{ minAccounts: 0, rate: 0.15 }, { minAccounts: 20, rate: 0.225 }], note: null })
  })

  it('builds a payment with the optional texts empty as null', () => {
    expect(buildPayment({ amount: '118.00', method: 'Transfer', paidOn: '2026-12-03', reference: ' Op. 4521 ', note: '' })).toEqual({
      amount: 118,
      method: 'Transfer',
      paidOn: '2026-12-03',
      reference: 'Op. 4521',
      note: null,
    })
  })
})
