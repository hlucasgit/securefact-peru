// Operations of an invoice that go beyond a plain domestic sale: exports, detraction and IGV withholding. What the API checks is not repeated here beyond what shapes the form
// (which choices to offer); the API refuses the rest. Nothing here calculates an amount: the totals, the detraction and the withholding come from the preview of the API.

/** Catalogue 51 export types that the form issues. 0202 (lodging) and 0205 (tourist package) need data of the guest of each line and are issued through the API. */
export const EXPORT_TYPES = [
  { code: '0200', usageCountry: false, foreignBuyerOnly: true },
  { code: '0201', usageCountry: true, foreignBuyerOnly: true },
  { code: '0203', usageCountry: false, foreignBuyerOnly: false },
  { code: '0204', usageCountry: false, foreignBuyerOnly: true },
  { code: '0206', usageCountry: false, foreignBuyerOnly: false },
  { code: '0207', usageCountry: false, foreignBuyerOnly: false },
  { code: '0208', usageCountry: true, foreignBuyerOnly: false },
] as const

/** Affectation of the lines of an export (catalogue 07, tax 9995). */
export const EXPORT_AFFECTATION = '40'

/** Identity documents of a buyer abroad (catalogue 06): no document, foreigner card, passport, diplomatic ID. */
export const FOREIGN_IDENTITY = ['0', '4', '7', 'A']

/** Catalogue 54 codes whose operation needs data on every line (vessel and species; vehicle and trip): they are issued through the API. */
export const DETRACTION_NEEDS_LINE_DETAILS = ['004', '027']

export const isExport = (code: string) => EXPORT_TYPES.some((type) => type.code === code)

export const needsUsageCountry = (code: string) => EXPORT_TYPES.some((type) => type.code === code && type.usageCountry)

/** True when the buyer of this export cannot have a RUC: always in a receipt, and in the types that sell abroad by nature. */
export function buyerMustBeForeign(documentType: '01' | '03', operation: string): boolean {
  return isExport(operation) && (documentType === '03' || (EXPORT_TYPES.find((type) => type.code === operation)?.foreignBuyerOnly ?? false))
}

export type Deduction = 'none' | 'detraction' | 'retention'

export interface DeductionInput {
  kind: Deduction
  goodsOrServiceCode: string
  percentage: string
  amount: string
  account: string
  retentionPercentage: string
}

export const noDeduction = (): DeductionInput => ({ kind: 'none', goodsOrServiceCode: '', percentage: '', amount: '', account: '', retentionPercentage: '' })

interface RequestInput {
  seriesId: string
  issueDate: string
  currency: string
  buyer: object
  lines: object[]
  installments?: { amount: number; dueDate: string }[]
  operation: string
  usageCountry: string
  deduction: DeductionInput
}

/**
 * The body of a document, in the form the API takes. A domestic sale sends no operation type (the API assumes 0101) and a detraction never sends one (it follows from its code); only an
 * export names its type, and only 0201 and 0208 carry the country of use. A detraction or a withholding goes only when it was chosen, and an export carries neither.
 */
export function buildDocumentRequest(input: RequestInput) {
  const exporting = isExport(input.operation)
  const detraction =
    !exporting && input.deduction.kind === 'detraction'
      ? {
          goodsOrServiceCode: input.deduction.goodsOrServiceCode,
          percentage: Number(input.deduction.percentage),
          amount: Number(input.deduction.amount),
          accountNumber: input.deduction.account.trim() || null,
        }
      : undefined
  const retention = !exporting && input.deduction.kind === 'retention' ? { percentage: Number(input.deduction.retentionPercentage) } : undefined
  return {
    seriesId: input.seriesId,
    issueDate: input.issueDate,
    currency: input.currency,
    buyer: input.buyer,
    lines: input.lines,
    ...(input.installments ? { installments: input.installments } : {}),
    ...(exporting ? { operationTypeCode: input.operation } : {}),
    ...(exporting && needsUsageCountry(input.operation) ? { usageCountryCode: input.usageCountry.trim().toUpperCase() } : {}),
    ...(detraction ? { detraction } : {}),
    ...(retention ? { retention } : {}),
  }
}
