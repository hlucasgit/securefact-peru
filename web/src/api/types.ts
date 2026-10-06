// Response shapes of the SecureFact API (camelCase JSON, enums as strings). The request shapes live next to the forms that send them.

export type EDocumentState =
  | 'Pending'
  | 'ReadyToSend'
  | 'Sending'
  | 'AwaitingTicket'
  | 'Accepted'
  | 'AcceptedWithObservations'
  | 'Rejected'
  | 'Failed'

export interface Company {
  id: string
  tenantId: string
  ruc: string
  legalName: string
  tradeName: string | null
  fiscalAddress: string
  ubigeo: string
  taxRegime: string | null
  contactEmail: string | null
  timeZone: string
  defaultCurrency: string
  status: 'Active' | 'Inactive'
  createdAt: string
  detractionAccount: string | null
}

export interface CompanyDetails {
  legalName: string
  tradeName: string | null
  fiscalAddress: string
  ubigeo: string
  taxRegime: string | null
  contactEmail: string | null
  timeZone: string
  defaultCurrency: string
  detractionAccount: string | null
}

export interface Establishment {
  id: string
  companyId: string
  code: string
  name: string
  address: string
  ubigeo: string
  isActive: boolean
  createdAt: string
}

export interface Series {
  id: string
  tenantId: string
  companyId: string
  establishmentId: string | null
  documentTypeCode: string
  code: string
  lastNumber: number
  isActive: boolean
  createdAt: string
}

export interface Certificate {
  id: string
  companyId: string
  subject: string
  thumbprint: string
  serialNumber: string
  notBefore: string
  notAfter: string
  isActive: boolean
  rucInSubject: boolean
  createdAt: string
  deactivatedAt: string | null
}

export interface SolCredential {
  companyId: string
  solUser: string
  hasPassword: boolean
  updatedAt: string
}

export interface Customer {
  id: string
  documentTypeCode: string
  documentNumber: string
  name: string
  address: string | null
  email: string | null
  phone: string | null
  isActive: boolean
  createdAt: string
}

export type CustomerDetails = Pick<Customer, 'documentTypeCode' | 'documentNumber' | 'name' | 'address' | 'email' | 'phone'>

export interface Product {
  id: string
  internalCode: string
  description: string
  kind: 'Goods' | 'Service'
  unitCode: string
  unitValue: number
  igvAffectationCode: string
  sunatProductCode: string | null
  category: string | null
  isActive: boolean
  createdAt: string
}

export type ProductDetails = Pick<Product, 'internalCode' | 'description' | 'kind' | 'unitCode' | 'unitValue' | 'igvAffectationCode' | 'sunatProductCode' | 'category'>

export interface AppUser {
  id: string
  tenantId: string | null
  email: string
  displayName: string
  roles: string[]
  isActive: boolean
  mfaEnabled: boolean
  createdAt: string
}

export interface Tenant {
  id: string
  name: string
  status: string
  environment: 'Sandbox' | 'Production'
  createdAt: string
}

export type IscSystem = 'AdValorem' | 'FixedAmount'

export interface IscInput {
  system: IscSystem
  rateOrUnitAmount: number
}

export interface DocumentLine {
  lineNumber: number
  description: string
  unitCode: string
  productCode: string | null
  quantity: number
  lineExtensionAmount: number
  taxCode: string
  totalTaxAmount: number
  unitPriceIncludingTaxes: number | null
  unitValue: number
  igvAffectationCode: string
  isc: IscInput | null
  plasticBagCount: number
}

export interface Totals {
  totalTaxableGravado: number
  totalExempt: number
  totalUnaffected: number
  totalExport: number
  totalFree: number
  totalIgv: number
  totalIvap: number
  totalIsc: number
  totalIcbper: number
  totalTaxAmount: number
  totalLineExtensionAmount: number
  totalTaxInclusiveAmount: number
  totalAllowances: number
  totalCharges: number
  payableAmount: number
}

export interface Buyer {
  documentTypeCode: string
  documentNumber: string
  name: string
  address?: string | null
  email?: string | null
}

export interface NoteInfo {
  reasonCode: string
  reason: string
  referencedDocumentId: string
  referencedDocumentTypeCode: string
  referencedSeries: string
  referencedNumber: number
}

export interface Document {
  id: string
  companyId: string
  documentTypeCode: string
  series: string
  number: number
  issueDate: string
  currency: string
  buyer: Buyer
  status: string
  lines: DocumentLine[]
  totals: Totals
  createdAt: string
  note: NoteInfo | null
  installments: { amount: number; dueDate: string }[] | null
  operationTypeCode: string
}

export interface CdrObservation {
  code: string
  message: string
}

export interface ElectronicDocument {
  id: string
  documentId: string
  companyId: string
  documentTypeCode: string
  series: string
  number: number
  fileBaseName: string
  state: EDocumentState
  attempts: number
  digestValue: string
  ticket: string | null
  cdrResponseCode: number | null
  cdrDescription: string | null
  cdrObservations: CdrObservation[]
  lastErrorCode: string | null
  lastErrorMessage: string | null
  nextAttemptAt: string | null
  createdAt: string
  updatedAt: string
  sentAt: string | null
  processedAt: string | null
  issueDate: string
  voided: boolean
}

export interface ElectronicDocumentEvent {
  id: string
  from: EDocumentState
  to: EDocumentState
  event: string
  attempt: number
  detail: string | null
  occurredAt: string
}

export interface ArchivedFile {
  kind: string
  contentType: string
  sizeBytes: number
  sha256: string
  storedAt: string
  downloadUrl: string
  downloadExpiresAt: string
}

export interface SummaryResult {
  document: ElectronicDocument
  referenceDate: string
  electronicDocumentIds: string[]
}

export interface CatalogEntry {
  catalogNumber: string
  code: string
  description: string
  effectiveFrom: string
  effectiveTo: string | null
  metadata: Record<string, string>
}

export interface RuleVersion {
  code: string
  version: number
  effectiveFrom: string
  effectiveTo: string | null
  source: string
  verification: 'Verified' | 'Pending'
  configurationJson: string
}

export type TenantStatus = 'Active' | 'Suspended' | 'Closed'

export interface TenantRow {
  id: string
  name: string
  status: TenantStatus
  environment: 'Sandbox' | 'Production'
  resellerId: string | null
  createdAt: string
  planId: string
}

/** What a plan allows; a null limit is unlimited. */
export interface PlanRow {
  id: string
  code: string
  name: string
  maxCompanies: number | null
  maxUsers: number | null
  maxDocumentsPerMonth: number | null
  isActive: boolean
  /** Set for a private offer of one reseller. */
  resellerId: string | null
}

export interface ResellerRow {
  id: string
  name: string
  isActive: boolean
  tenantCount: number
  createdAt: string
}

export interface PlanInput {
  code: string
  name: string
  maxCompanies: number | null
  maxUsers: number | null
  maxDocumentsPerMonth: number | null
  isActive: boolean
  resellerId: string | null
}

export interface UsageItem {
  used: number
  limit: number | null
}

export interface TenantUsage {
  plan: PlanRow
  period: string
  companies: UsageItem
  users: UsageItem
  documentsThisMonth: UsageItem
}

export interface AuditRecord {
  id: string
  tenantId: string | null
  sequence: number
  occurredAt: string
  actorType: string
  actorUserId: string | null
  action: string
  entityType: string
  entityId: string | null
  oldValues: string | null
  newValues: string | null
  ipAddress: string | null
  correlationId: string | null
}

export interface AuditVerification {
  isIntact: boolean
  eventsChecked: number
  firstBrokenSequence: number | null
  reason: string | null
}

export interface DeadMessage {
  id: string
  source: string
  eventType: string
  attempts: number
  lastError: string | null
  createdAt: string
}
