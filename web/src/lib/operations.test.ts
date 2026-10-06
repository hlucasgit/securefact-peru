import { buildDocumentRequest, buyerMustBeForeign, isExport, needsUsageCountry, noDeduction, type DeductionInput } from './operations'

const base = {
  seriesId: 's1',
  issueDate: '2026-10-06',
  currency: 'PEN',
  buyer: { documentTypeCode: '6', documentNumber: '20100066603', name: 'Cliente SAC' },
  lines: [{ description: 'Servicio' }],
  operation: '0101',
  usageCountry: '',
  deduction: noDeduction(),
}

const detraction = (patch: Partial<DeductionInput> = {}): DeductionInput => ({ ...noDeduction(), kind: 'detraction', goodsOrServiceCode: '037', percentage: '12', amount: '28.32', account: ' 00012345678 ', ...patch })

describe('buildDocumentRequest', () => {
  it('sends a plain sale without operation type, detraction or withholding', () => {
    const body = buildDocumentRequest(base)

    for (const absent of ['operationTypeCode', 'usageCountryCode', 'detraction', 'retention', 'installments']) expect(body).not.toHaveProperty(absent)
  })

  it('sends a detraction with its numbers and never an operation type (it follows from the code)', () => {
    const body = buildDocumentRequest({ ...base, deduction: detraction() })

    expect(body.detraction).toEqual({ goodsOrServiceCode: '037', percentage: 12, amount: 28.32, accountNumber: '00012345678' })
    expect(body).not.toHaveProperty('operationTypeCode')
    expect(body).not.toHaveProperty('retention')
  })

  it('leaves the account to the one of the company when it is empty', () => {
    expect(buildDocumentRequest({ ...base, deduction: detraction({ account: '  ' }) }).detraction?.accountNumber).toBeNull()
  })

  it('sends a withholding as its percentage only, the API gives the amount', () => {
    const body = buildDocumentRequest({ ...base, deduction: { ...noDeduction(), kind: 'retention', retentionPercentage: '3' } })

    expect(body.retention).toEqual({ percentage: 3 })
    expect(body).not.toHaveProperty('detraction')
  })

  it('names the operation type of an export and drops what an export cannot carry', () => {
    const body = buildDocumentRequest({ ...base, operation: '0200', deduction: detraction() })

    expect(body.operationTypeCode).toBe('0200')
    for (const absent of ['detraction', 'retention', 'usageCountryCode']) expect(body).not.toHaveProperty(absent)
  })

  it('carries the country of use, in capitals, only in the exports that ask for it', () => {
    expect(buildDocumentRequest({ ...base, operation: '0201', usageCountry: ' us ' }).usageCountryCode).toBe('US')
    expect(buildDocumentRequest({ ...base, operation: '0208', usageCountry: 'br' }).usageCountryCode).toBe('BR')
    expect(buildDocumentRequest({ ...base, operation: '0203', usageCountry: 'US' })).not.toHaveProperty('usageCountryCode')
  })

  it('keeps the installments of a credit sale', () => {
    expect(buildDocumentRequest({ ...base, installments: [{ amount: 100, dueDate: '2026-11-06' }] }).installments).toHaveLength(1)
  })
})

describe('the export types', () => {
  it('knows which are exports and which need the country of use', () => {
    expect(['0200', '0201', '0203', '0208'].every(isExport)).toBe(true)
    expect(['0101', '1001', '0202', '0205'].some(isExport)).toBe(false) // lodging and tourist package are issued through the API
    expect(['0201', '0208'].every(needsUsageCountry)).toBe(true)
    expect(['0200', '0203', '0206'].some(needsUsageCountry)).toBe(false)
  })

  it('says when the buyer cannot have a RUC', () => {
    expect(buyerMustBeForeign('01', '0200')).toBe(true) // goods abroad
    expect(buyerMustBeForeign('01', '0203')).toBe(false) // shipping lines may sell to a company with RUC
    expect(buyerMustBeForeign('03', '0203')).toBe(true) // a receipt of any export never goes to a RUC
    expect(buyerMustBeForeign('01', '0101')).toBe(false)
  })
})

describe('legends of exoneration', () => {
  it('sends the legends chosen, and nothing when none was chosen', () => {
    expect(buildDocumentRequest({ ...base, legends: ['2001', '2008'] }).legendCodes).toEqual(['2001', '2008'])
    expect(buildDocumentRequest({ ...base, legends: [] })).not.toHaveProperty('legendCodes')
    expect(buildDocumentRequest(base)).not.toHaveProperty('legendCodes')
  })

  it('never sends a legend in an export, whose total cannot be exonerated', () => {
    expect(buildDocumentRequest({ ...base, operation: '0200', legends: ['2001'] })).not.toHaveProperty('legendCodes')
  })
})
