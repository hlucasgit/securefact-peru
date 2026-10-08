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
  apiClientId: string | null
  hasApiSecret: boolean
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

export type ImportRowStatus = 'Ready' | 'Created' | 'Existing' | 'Invalid'

export interface ImportRow {
  line: number
  status: ImportRowStatus
  key: string | null
  message: string | null
}

export interface ImportResult {
  committed: boolean
  total: number
  ready: number
  created: number
  existing: number
  invalid: number
  rows: ImportRow[]
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
  transport?: CargoTransport | null
  fishing?: { vesselRegistration: string; vesselName: string; speciesType: string; unloadingPlace: string; unloadingDate: string; speciesQuantity: number } | null
  guest?: { name: string; documentTypeCode: string; documentNumber: string; passportCountryCode: string; residenceCountryCode: string | null; checkInDate: string | null; checkOutDate: string | null; stayDays: number | null } | null
}

export interface CargoTransport {
  originUbigeo: string
  originAddress: string
  destinationUbigeo: string
  destinationAddress: string
  tripDetail: string
  serviceReferenceValue: number
  effectiveLoadReferenceValue: number
  nominalLoadReferenceValue: number
  legs: { originUbigeo: string; destinationUbigeo: string; vehicleConfiguration: string; usefulLoadTonnes: number; description: string | null; returnEmpty: boolean }[] | null
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
  initialPayment?: number | null
  detraction: { goodsOrServiceCode: string; percentage: number; amount: number; accountNumber: string | null } | null
  retention: { percentage: number; baseAmount: number; amount: number } | null
  usageCountryCode: string | null
  legendCodes: string[] | null
}

/** What issuing a document would calculate, with the amounts of a detraction or of a withholding when their percentage was given. */
export interface DocumentPreview {
  totals: Totals
  detractionAmount: number | null
  retentionAmount: number | null
  /** What is left to pay: the payable amount less the detraction or the withholding and the initial payment. The installments of a credit sale add up to it. */
  netPendingAmount: number
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
  /** Who suspended the account, while it is suspended. */
  suspendedBy: 'Platform' | 'Reseller' | null
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

/** The brand of a reseller as its editor sees it; every field but the id and the name can be empty. */
export interface BrandSettings {
  resellerId: string
  resellerName: string
  brandName: string | null
  primaryColor: string | null
  supportEmail: string | null
  host: string | null
  hostStatus: DomainStatus
  logoUrl: string | null
}

export type DomainStatus = 'None' | 'Pending' | 'Verified' | 'Unreachable'

/** The domain of the portal of a reseller and what is left for it to work (ADR-051). */
export interface DomainInfo {
  resellerId: string
  host: string | null
  status: DomainStatus
  txtName: string | null
  txtValue: string | null
  cnameTarget: string | null
  edgeAddresses: string[]
  verifiedAt: string | null
  checkedAt: string | null
  error: string | null
}

export interface BrandInput {
  brandName: string | null
  primaryColor: string | null
  supportEmail: string | null
}

/** The reseller edits its own brand; platform staff edit one by its id. */
export type BrandScope = { kind: 'own' } | { kind: 'platform'; resellerId: string }

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

export interface DeadEmail {
  id: string
  toAddress: string
  subject: string
  attempts: number
  lastError: string | null
  createdAt: string
  deadAt: string
}

export interface DeadMessage {
  id: string
  source: string
  eventType: string
  attempts: number
  lastError: string | null
  createdAt: string
}

export type GreState = 'Prepared' | 'Pending' | 'Accepted' | 'AcceptedWithObservations' | 'Rejected' | 'Failed'

export interface GreSeries {
  id: string
  companyId: string
  code: string
  lastNumber: number
  isActive: boolean
  createdAt: string
}

export interface GreObservation {
  code: string
  message: string
}

export interface Guide {
  id: string
  companyId: string
  series: string
  number: number
  name: string
  issueDate: string
  motiveCode: string
  modalityCode: string
  state: GreState
  recipientDocument: string
  recipientName: string
  ticket: string | null
  attempts: number
  createdAt: string
  sentAt: string | null
  processedAt: string | null
  cdrResponseCode: number | null
  cdrDescription: string | null
  observations: GreObservation[]
  errorCode: string | null
  errorMessage: string | null
}

export interface GreParty {
  documentTypeCode: string
  documentNumber: string
  name: string
}

export interface GreAddress {
  ubigeoCode: string
  address: string
  establishmentRuc?: string | null
  establishmentCode?: string | null
}

export interface GreGoodInput {
  description: string
  unitCode: string
  quantity: number
  code?: string | null
}

/** What the form sends; the API checks it against the rules of the validation workbook of SUNAT. */
export interface CreateGuideBody {
  companyId: string
  seriesId: string
  issueDate?: string | null
  motiveCode: string
  motiveDescription?: string | null
  modalityCode: string
  transferStartDate?: string | null
  handoverDate?: string | null
  grossWeight: number
  weightUnit: string
  packageCount?: number | null
  note?: string | null
  recipient: GreParty
  supplier?: GreParty | null
  buyer?: GreParty | null
  origin: GreAddress
  destination: GreAddress
  carrier?: { ruc: string; name: string; mtcRegistration?: string | null } | null
  vehicle?: { plate: string; circulationCard?: string | null } | null
  driver?: { documentTypeCode: string; documentNumber: string; firstNames: string; lastNames: string; licenseNumber: string } | null
  goods: GreGoodInput[]
  relatedDocuments?: { typeCode: string; number: string; issuerRuc?: string | null }[] | null
}
