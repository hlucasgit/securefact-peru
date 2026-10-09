import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { del, get, newKey, post, put } from './http'
import type {
  AppUser,
  ArchivedFile,
  AuditRecord,
  AuditVerification,
  BillingPolicy,
  BillingPolicyInput,
  Charge,
  ChargeDetail,
  ChargeStatus,
  CollectionPassResult,
  CommissionOverview,
  CommissionSchedule,
  CommissionScheduleInput,
  CommissionSettlement,
  CommissionStatement,
  Payment,
  PaymentInput,
  PlanPrice,
  PlanPriceInput,
  TenantTerms,
  DeadEmail,
  DeadMessage,
  BrandInput,
  BrandScope,
  BrandSettings,
  DomainInfo,
  PlanInput,
  PlanRow,
  ResellerRow,
  TenantRow,
  TenantStatus,
  TenantUsage,
  CatalogEntry,
  Certificate,
  Company,
  CompanyDetails,
  Customer,
  CustomerDetails,
  ImportResult,
  Document,
  DocumentPreview,
  ElectronicDocument,
  ElectronicDocumentEvent,
  CreateCarrierGuideBody,
  CreateGuideBody,
  GreSeries,
  GreState,
  Guide,
  Establishment,
  Product,
  ProductDetails,
  RuleVersion,
  Series,
  SolCredential,
  SummaryResult,
  Tenant,
} from './types'

export const keys = {
  companies: ['companies'] as const,
  company: (id: string) => ['companies', id] as const,
  establishments: (id: string) => ['companies', id, 'establishments'] as const,
  series: (companyId: string) => ['series', companyId] as const,
  certificates: (companyId: string) => ['certificates', companyId] as const,
  expiring: ['certificates', 'expiring'] as const,
  sol: (companyId: string) => ['sol', companyId] as const,
  greSeries: (companyId: string) => ['gre', 'series', companyId] as const,
  guides: (companyId: string | null, state: string, skip: number, take: number) => ['gre', 'guides', companyId, state, skip, take] as const,
  guide: (id: string) => ['gre', 'guide', id] as const,
  customers: (search: string) => ['customers', search] as const,
  products: (search: string) => ['products', search] as const,
  documents: (companyId: string | null, skip: number, take: number) => ['documents', companyId, skip, take] as const,
  document: (id: string) => ['document', id] as const,
  electronic: (documentId: string) => ['electronic', documentId] as const,
  events: (id: string) => ['events', id] as const,
  archive: (id: string) => ['archive', id] as const,
  catalog: (number: string) => ['catalog', number] as const,
  users: ['users'] as const,
  tenants: (search: string, status: string) => ['platform', 'tenants', search, status] as const,
  platformTenant: (id: string) => ['platform', 'tenant', id] as const,
  tenantUsers: (id: string) => ['platform', 'tenant', id, 'users'] as const,
  tenantUsage: (id: string) => ['platform', 'tenant', id, 'usage'] as const,
  plans: ['platform', 'plans'] as const,
  resellers: ['platform', 'resellers'] as const,
  resellerTenants: (search: string) => ['reseller', 'tenants', search] as const,
  resellerTenant: (id: string) => ['reseller', 'tenant', id] as const,
  resellerUsage: (id: string) => ['reseller', 'tenant', id, 'usage'] as const,
  resellerPlans: ['reseller', 'plans'] as const,
  myPlan: ['plan'] as const,
  prices: (planId: string) => ['platform', 'plans', planId, 'prices'] as const,
  policies: ['platform', 'billing-policies'] as const,
  schedules: ['platform', 'commission-schedules'] as const,
  charges: (filter: string) => ['charges', filter] as const,
  charge: (id: string) => ['charges', 'one', id] as const,
  terms: (tenantId: string) => ['platform', 'tenant', tenantId, 'terms'] as const,
  myTerms: ['subscription'] as const,
  resellerCommissions: (id: string) => ['platform', 'reseller', id, 'commissions'] as const,
  commissionStatement: (id: string, month: string) => ['platform', 'reseller', id, 'commissions', month] as const,
  myCommissions: ['reseller', 'commissions'] as const,
  myCommissionStatement: (month: string) => ['reseller', 'commissions', month] as const,
  audit: (filters: string) => ['audit', filters] as const,
  dead: ['outbox', 'dead'] as const,
  deadEmails: ['emails', 'dead'] as const,
  tenant: ['tenant'] as const,
  rules: ['rules'] as const,
}

const page = (skip: number, take: number) => `skip=${skip}&take=${take}`

export const useCompanies = () => useQuery({ queryKey: keys.companies, queryFn: () => get<Company[]>(`/api/v1/companies?${page(0, 100)}`) })
export const useCompany = (id: string) => useQuery({ queryKey: keys.company(id), queryFn: () => get<Company>(`/api/v1/companies/${id}`) })
export const useEstablishments = (id: string) => useQuery({ queryKey: keys.establishments(id), queryFn: () => get<Establishment[]>(`/api/v1/companies/${id}/establishments`) })
export const useSeries = (companyId: string | null) =>
  useQuery({ queryKey: keys.series(companyId ?? ''), queryFn: () => get<Series[]>(`/api/v1/series?companyId=${companyId}`), enabled: !!companyId })
export const useCertificates = (companyId: string) => useQuery({ queryKey: keys.certificates(companyId), queryFn: () => get<Certificate[]>(`/api/v1/certificates?companyId=${companyId}`) })
export const useExpiringCertificates = () => useQuery({ queryKey: keys.expiring, queryFn: () => get<Certificate[]>('/api/v1/certificates/expiring?days=30') })
export const useSolCredential = (companyId: string) =>
  useQuery({
    queryKey: keys.sol(companyId),
    queryFn: async () => {
      try {
        return await get<SolCredential>(`/api/v1/sol-credentials/${companyId}`)
      } catch (error) {
        if (error instanceof Error && 'status' in error && error.status === 404) return null
        throw error
      }
    },
  })
export const useCustomers = (search: string) => useQuery({ queryKey: keys.customers(search), queryFn: () => get<Customer[]>(`/api/v1/customers?${page(0, 100)}&search=${encodeURIComponent(search)}`) })
export const useProducts = (search: string) => useQuery({ queryKey: keys.products(search), queryFn: () => get<Product[]>(`/api/v1/products?${page(0, 100)}&search=${encodeURIComponent(search)}`) })
export const useDocuments = (companyId: string | null, skip: number, take: number) =>
  useQuery({
    queryKey: keys.documents(companyId, skip, take),
    queryFn: () => get<Document[]>(`/api/v1/documents?${page(skip, take)}${companyId ? `&companyId=${companyId}` : ''}`),
  })
export const useDocument = (id: string) => useQuery({ queryKey: keys.document(id), queryFn: () => get<Document>(`/api/v1/documents/${id}`) })

/** The electronic document of a document, or null while it was not prepared yet (the API answers 404). */
export const useElectronic = (documentId: string, watchVoid = false) =>
  useQuery({
    queryKey: keys.electronic(documentId),
    // The workers send and read the answers in the background: while the document is on its way (or a voiding was requested and is not confirmed), look again every few seconds.
    refetchInterval: (query) => {
      const data = query.state.data
      if (watchVoid && data?.voided !== true) return 5_000
      return data && (data.state === 'ReadyToSend' || data.state === 'Sending' || data.state === 'AwaitingTicket') ? 5_000 : false
    },
    queryFn: async () => {
      try {
        return await get<ElectronicDocument>(`/api/v1/documents/${documentId}/electronic`)
      } catch (error) {
        if (error instanceof Error && 'status' in error && error.status === 404) return null
        throw error
      }
    },
  })
export const useEvents = (id: string | undefined) =>
  useQuery({ queryKey: keys.events(id ?? ''), queryFn: () => get<ElectronicDocumentEvent[]>(`/api/v1/electronic-documents/${id}/events`), enabled: !!id })
export const useArchive = (id: string | undefined, enabled: boolean) =>
  useQuery({ queryKey: keys.archive(id ?? ''), queryFn: () => get<ArchivedFile[]>(`/api/v1/electronic-documents/${id}/archive`), enabled: !!id && enabled })
export const useCatalog = (number: string) => useQuery({ queryKey: keys.catalog(number), queryFn: () => get<CatalogEntry[]>(`/api/v1/catalogs/${number}`), staleTime: 3_600_000 })
export const useUsers = () => useQuery({ queryKey: keys.users, queryFn: () => get<AppUser[]>(`/api/v1/users?${page(0, 100)}`) })
export const useTenant = () => useQuery({ queryKey: keys.tenant, queryFn: () => get<Tenant>('/api/v1/tenants/current') })
export const useRules = () => useQuery({ queryKey: keys.rules, queryFn: () => get<RuleVersion[]>('/api/v1/rules') })

/** A mutation that refreshes the listed query keys when it succeeds. */
function useAction<TIn, TOut>(run: (input: TIn) => Promise<TOut>, invalidate: (readonly unknown[])[]) {
  const client = useQueryClient()
  return useMutation({
    mutationFn: run,
    onSuccess: async () => {
      await Promise.all(invalidate.map((queryKey) => client.invalidateQueries({ queryKey })))
    },
  })
}

export const useCreateCompany = () =>
  useAction((input: { ruc: string; details: CompanyDetails }) => post<Company>('/api/v1/companies', input), [keys.companies])
export const useUpdateCompany = (id: string) => useAction((details: CompanyDetails) => put<Company>(`/api/v1/companies/${id}`, details), [keys.companies])
export const useCreateEstablishment = (companyId: string) =>
  useAction((input: { code: string; details: { name: string; address: string; ubigeo: string } }) => post(`/api/v1/companies/${companyId}/establishments`, input), [keys.establishments(companyId)])
export const useCreateSeries = (companyId: string) =>
  useAction((input: { documentTypeCode: string; code: string; establishmentId?: string | null }) => post<Series>('/api/v1/series', { companyId, ...input }), [keys.series(companyId)])
export const useDeactivateSeries = (companyId: string) => useAction((id: string) => post(`/api/v1/series/${id}/deactivate`), [keys.series(companyId)])
export const useUploadCertificate = (companyId: string) =>
  useAction((input: { pfxBase64: string; password: string }) => post<Certificate>('/api/v1/certificates', { companyId, ...input }), [keys.certificates(companyId), keys.expiring])
export const useDeactivateCertificate = (companyId: string) => useAction((id: string) => post(`/api/v1/certificates/${id}/deactivate`), [keys.certificates(companyId), keys.expiring])
export const useSetSol = (companyId: string) =>
  useAction((input: { solUser: string; solPassword: string }) => put<SolCredential>('/api/v1/sol-credentials', { companyId, ...input }), [keys.sol(companyId)])
export const useRemoveSol = (companyId: string) => useAction(() => del(`/api/v1/sol-credentials/${companyId}`), [keys.sol(companyId)])
export const useSaveCustomer = () =>
  useAction((input: { id?: string; details: CustomerDetails }) => (input.id ? put<Customer>(`/api/v1/customers/${input.id}`, input.details) : post<Customer>('/api/v1/customers', input.details)), [['customers']])
/** Reads customers or products from a CSV: without `commit` it only reports; with it, it creates (ADR-053). */
export const useImportCsv = (kind: 'customers' | 'products') =>
  useAction((input: { csv: string; commit: boolean }) => post<ImportResult>(`/api/v1/${kind}/import`, input), [[kind]])
export const useDeactivateCustomer = () => useAction((id: string) => post(`/api/v1/customers/${id}/deactivate`), [['customers']])
export const useSaveProduct = () =>
  useAction((input: { id?: string; details: ProductDetails }) => (input.id ? put<Product>(`/api/v1/products/${input.id}`, input.details) : post<Product>('/api/v1/products', input.details)), [['products']])
export const useDeactivateProduct = () => useAction((id: string) => post(`/api/v1/products/${id}/deactivate`), [['products']])
export const useCreateUser = () =>
  useAction((input: { email: string; displayName: string; password: string; roles: string[] }) => post<AppUser>('/api/v1/users', input), [keys.users])
export const useDeactivateUser = () => useAction((id: string) => post(`/api/v1/users/${id}/deactivate`), [keys.users])

export const usePreviewDocument = () => useAction((body: object) => post<DocumentPreview>('/api/v1/documents/preview', body), [])
export const usePreviewNote = () => useAction((body: object) => post<DocumentPreview>('/api/v1/notes/preview', body), [])
export const useIssueDocument = () => useAction((body: object) => post<Document>('/api/v1/documents', body, newKey()), [['documents']])
export const useIssueNote = () => useAction((body: object) => post<Document>('/api/v1/notes', body, newKey()), [['documents']])
export const usePrepare = (documentId: string) => useAction(() => post<ElectronicDocument>(`/api/v1/documents/${documentId}/electronic`), [keys.electronic(documentId), ['documents']])

function useElectronicAction(documentId: string, action: 'send' | 'poll' | 'retry' | 'recover') {
  return useAction((id: string) => post<ElectronicDocument>(`/api/v1/electronic-documents/${id}/${action}`), [keys.electronic(documentId), ['events']])
}
export const useSend = (documentId: string) => useElectronicAction(documentId, 'send')
export const usePoll = (documentId: string) => useElectronicAction(documentId, 'poll')
export const useRetry = (documentId: string) => useElectronicAction(documentId, 'retry')
export const useRecover = (documentId: string) => useElectronicAction(documentId, 'recover')

export const useCreateSummary = () => useAction((input: { companyId: string; referenceDate: string }) => post<SummaryResult[]>('/api/v1/summaries', input), [['documents']])
export const useCreateVoid = (documentId: string) =>
  useAction((input: { companyId: string; items: { documentId: string; reason: string }[] }) => post('/api/v1/voids', input), [keys.electronic(documentId)])

// Platform administration (ADR-041)
export const useTenants = (search: string, status: string, enabled = true) =>
  useQuery({ queryKey: keys.tenants(search, status), enabled, queryFn: () => get<TenantRow[]>(`/api/v1/platform/tenants?${page(0, 100)}&search=${encodeURIComponent(search)}${status ? `&status=${status}` : ''}`) })
export const usePlatformTenant = (id: string) => useQuery({ queryKey: keys.platformTenant(id), queryFn: () => get<TenantRow>(`/api/v1/platform/tenants/${id}`) })
export const useTenantUsers = (id: string) => useQuery({ queryKey: keys.tenantUsers(id), queryFn: () => get<AppUser[]>(`/api/v1/users?${page(0, 100)}&tenantId=${id}`) })
export const useCreateTenant = () =>
  useAction((input: { name: string; environment: 'Sandbox' | 'Production' }) => post<TenantRow>('/api/v1/platform/tenants', input), [['platform', 'tenants']])
export const useChangeTenantStatus = (id: string) =>
  useAction((input: { status: TenantStatus; reason: string }) => post<TenantRow>(`/api/v1/platform/tenants/${id}/status`, input), [keys.platformTenant(id), ['platform', 'tenants'], ['audit']])
export const useCreateTenantUser = (tenantId: string) =>
  useAction((input: { email: string; displayName: string; password: string; roles: string[] }) => post<AppUser>('/api/v1/users', { ...input, tenantId }), [keys.tenantUsers(tenantId)])
export const useRevokeSessions = () => useAction((userId: string) => post(`/api/v1/users/${userId}/sessions/revoke`), [])
export const useDeactivateTenantUser = (tenantId: string) => useAction((userId: string) => post(`/api/v1/users/${userId}/deactivate`), [keys.tenantUsers(tenantId)])

// Plans (ADR-042)
export const usePlans = () => useQuery({ queryKey: keys.plans, queryFn: () => get<PlanRow[]>('/api/v1/platform/plans') })
export const useCreatePlan = () => useAction((input: PlanInput) => post<PlanRow>('/api/v1/platform/plans', input), [keys.plans])
export const useUpdatePlan = (id: string) => useAction((input: PlanInput) => put<PlanRow>(`/api/v1/platform/plans/${id}`, input), [keys.plans, ['platform', 'tenant']])
export const useAssignPlan = (tenantId: string) =>
  useAction((planId: string) => post<TenantRow>(`/api/v1/platform/tenants/${tenantId}/plan`, { planId }), [keys.platformTenant(tenantId), keys.tenantUsage(tenantId), ['audit']])
export const useTenantUsage = (id: string) => useQuery({ queryKey: keys.tenantUsage(id), queryFn: () => get<TenantUsage>(`/api/v1/platform/tenants/${id}/usage`) })
export const useMyPlan = () => useQuery({ queryKey: keys.myPlan, queryFn: () => get<TenantUsage>('/api/v1/plan') })

// Resellers (ADR-043)
export const useResellers = () => useQuery({ queryKey: keys.resellers, queryFn: () => get<ResellerRow[]>('/api/v1/platform/resellers') })
export const useCreateReseller = () => useAction((name: string) => post<ResellerRow>('/api/v1/platform/resellers', { name }), [keys.resellers])
export const useUpdateReseller = (id: string) => useAction((input: { name: string; isActive: boolean }) => put<ResellerRow>(`/api/v1/platform/resellers/${id}`, input), [keys.resellers])
export const useCreateResellerUser = (resellerId: string) =>
  useAction((input: { email: string; displayName: string; password: string }) => post<AppUser>('/api/v1/users', { ...input, roles: ['ResellerAdmin'], resellerId }), [])
export const useAssignReseller = (tenantId: string) =>
  useAction((resellerId: string | null) => post<TenantRow>(`/api/v1/platform/tenants/${tenantId}/reseller`, { resellerId }), [keys.platformTenant(tenantId), keys.resellers, ['audit']])

export const useResellerTenants = (search: string) =>
  useQuery({ queryKey: keys.resellerTenants(search), queryFn: () => get<TenantRow[]>(`/api/v1/reseller/tenants?${page(0, 100)}&search=${encodeURIComponent(search)}`) })
export const useResellerTenant = (id: string) => useQuery({ queryKey: keys.resellerTenant(id), queryFn: () => get<TenantRow>(`/api/v1/reseller/tenants/${id}`) })
export const useResellerUsage = (id: string) => useQuery({ queryKey: keys.resellerUsage(id), queryFn: () => get<TenantUsage>(`/api/v1/reseller/tenants/${id}/usage`) })
export const useResellerPlans = () => useQuery({ queryKey: keys.resellerPlans, queryFn: () => get<PlanRow[]>('/api/v1/reseller/plans') })
export const useOpenTenant = () =>
  useAction((input: { name: string; environment: 'Sandbox' | 'Production'; planId: string | null }) => post<TenantRow>('/api/v1/reseller/tenants', input), [['reseller', 'tenants']])
export const useAddOwner = (tenantId: string) =>
  useAction((input: { email: string; displayName: string; password: string }) => post<AppUser>(`/api/v1/reseller/tenants/${tenantId}/owner`, input), [])
export const useChangeResellerStatus = (tenantId: string) =>
  useAction((input: { status: 'Suspended' | 'Active'; reason: string }) => post<TenantRow>(`/api/v1/reseller/tenants/${tenantId}/status`, input), [keys.resellerTenant(tenantId), ['reseller', 'tenants']])
export const useChangeResellerPlan = (tenantId: string) =>
  useAction((planId: string) => post<TenantRow>(`/api/v1/reseller/tenants/${tenantId}/plan`, { planId }), [keys.resellerTenant(tenantId), keys.resellerUsage(tenantId), ['reseller', 'tenants']])

// White label (ADR-044)
const brandBase = (scope: BrandScope) => (scope.kind === 'own' ? '/api/v1/reseller/branding' : `/api/v1/platform/resellers/${scope.resellerId}/branding`)
const brandKey = (scope: BrandScope) => ['brand-settings', scope.kind === 'own' ? 'own' : scope.resellerId] as const
// The brand that the interface is showing now follows what was just saved.
const brandInvalidations = (scope: BrandScope): (readonly unknown[])[] => [brandKey(scope), ['branding'], ['audit']]

export const useBrandSettings = (scope: BrandScope) => useQuery({ queryKey: brandKey(scope), queryFn: () => get<BrandSettings>(brandBase(scope)) })
export const useSaveBrand = (scope: BrandScope) => useAction((input: BrandInput) => put<BrandSettings>(brandBase(scope), input), brandInvalidations(scope))
export const useSetLogo = (scope: BrandScope) => useAction((dataBase64: string) => put<BrandSettings>(`${brandBase(scope)}/logo`, { dataBase64 }), brandInvalidations(scope))
export const useRemoveLogo = (scope: BrandScope) => useAction(() => del<BrandSettings>(`${brandBase(scope)}/logo`), brandInvalidations(scope))
// Domains (ADR-051): the reseller reads and checks its own; platform staff assign and check any. A domain that is still waiting for its DNS is looked at again by itself.
const domainBase = (scope: BrandScope) => (scope.kind === 'own' ? '/api/v1/reseller/domain' : `/api/v1/platform/resellers/${scope.resellerId}/domain`)
const domainKey = (scope: BrandScope) => ['domain', scope.kind === 'own' ? 'own' : scope.resellerId] as const

export const useDomain = (scope: BrandScope) =>
  useQuery({
    queryKey: domainKey(scope),
    queryFn: () => get<DomainInfo>(domainBase(scope)),
    refetchInterval: (query) => (query.state.data?.status === 'Pending' || query.state.data?.status === 'Unreachable' ? 20_000 : false),
  })
export const useVerifyDomain = (scope: BrandScope) => useAction(() => post<DomainInfo>(`${domainBase(scope)}/verify`), [domainKey(scope), ['branding'], brandKey(scope)])
export const useAssignDomain = (resellerId: string) =>
  useAction((host: string | null) => put<DomainInfo>(`/api/v1/platform/resellers/${resellerId}/host`, { host }), [domainKey({ kind: 'platform', resellerId }), ['branding'], brandKey({ kind: 'platform', resellerId }), ['audit']])

export interface AuditFilters {
  tenantId: string
  action: string
  entityType: string
}

const auditQuery = (filters: AuditFilters, skip: number) =>
  `${page(skip, 50)}${filters.tenantId ? `&tenantId=${filters.tenantId}` : ''}${filters.action ? `&action=${encodeURIComponent(filters.action)}` : ''}${filters.entityType ? `&entityType=${encodeURIComponent(filters.entityType)}` : ''}`

export const useAudit = (filters: AuditFilters, skip: number) =>
  useQuery({ queryKey: keys.audit(`${auditQuery(filters, skip)}`), queryFn: () => get<AuditRecord[]>(`/api/v1/audit?${auditQuery(filters, skip)}`) })
export const useVerifyAudit = () => useAction((tenantId: string) => post<AuditVerification>(`/api/v1/audit/verify${tenantId ? `?tenantId=${tenantId}` : ''}`), [])
export const useDeadMessages = () => useQuery({ queryKey: keys.dead, queryFn: () => get<DeadMessage[]>('/api/v1/outbox/dead') })
export const useDeadEmails = () => useQuery({ queryKey: keys.deadEmails, queryFn: () => get<DeadEmail[]>('/api/v1/platform/emails/dead') })
export const useRequeueEmail = () => useAction((email: DeadEmail) => post(`/api/v1/platform/emails/${email.id}/requeue`), [keys.deadEmails])
export const useRequeue = () => useAction((message: DeadMessage) => post(`/api/v1/outbox/${message.source}/${message.id}/requeue`), [keys.dead])

export const useGreSeries = (companyId: string | null) =>
  useQuery({ queryKey: keys.greSeries(companyId ?? ''), queryFn: () => get<GreSeries[]>(`/api/v1/gre/series?companyId=${companyId}`), enabled: !!companyId })
export const useCreateGreSeries = (companyId: string) => useAction((code: string) => post<GreSeries>('/api/v1/gre/series', { companyId, code }), [keys.greSeries(companyId)])
export const useDeactivateGreSeries = (companyId: string) => useAction((id: string) => post(`/api/v1/gre/series/${id}/deactivate`), [keys.greSeries(companyId)])
export const useSetApiCredentials = (companyId: string) =>
  useAction((input: { clientId: string; clientSecret: string }) => put<SolCredential>(`/api/v1/sol-credentials/${companyId}/api`, input), [keys.sol(companyId)])
export const useRemoveApiCredentials = (companyId: string) => useAction(() => del(`/api/v1/sol-credentials/${companyId}/api`), [keys.sol(companyId)])
export const useGuides = (companyId: string | null, state: GreState | '', skip: number, take: number) =>
  useQuery({
    queryKey: keys.guides(companyId, state, skip, take),
    queryFn: () => get<Guide[]>(`/api/v1/gre/guides?${page(skip, take)}${companyId ? `&companyId=${companyId}` : ''}${state ? `&state=${state}` : ''}`),
    refetchInterval: 15_000,
  })
export const useGuide = (id: string) =>
  useQuery({ queryKey: keys.guide(id), queryFn: () => get<Guide>(`/api/v1/gre/guides/${id}`), refetchInterval: (query) => (query.state.data?.state === 'Pending' ? 5_000 : false) })
export const useCreateGuide = () => useAction((body: CreateGuideBody) => post<Guide>('/api/v1/gre/guides', body), [['gre', 'guides']])
export const useCreateCarrierGuide = () => useAction((body: CreateCarrierGuideBody) => post<Guide>('/api/v1/gre/guides/carrier', body), [['gre', 'guides']])
export const useSubmitGuide = (id: string) => useAction(() => post<Guide>(`/api/v1/gre/guides/${id}/submit`), [keys.guide(id), ['gre', 'guides']])
export const useRefreshGuide = (id: string) => useAction(() => post<Guide>(`/api/v1/gre/guides/${id}/refresh`), [keys.guide(id), ['gre', 'guides']])

// Prices, billing policy, charges, payments and commissions (ADR-062 to ADR-064)
export const usePlanPrices = (planId: string) => useQuery({ queryKey: keys.prices(planId), queryFn: () => get<PlanPrice[]>(`/api/v1/platform/plans/${planId}/prices`), enabled: !!planId })
export const usePublishPrice = (planId: string) =>
  useAction((input: PlanPriceInput) => post<PlanPrice>(`/api/v1/platform/plans/${planId}/prices`, input), [keys.prices(planId), ['audit']])
export const useBillingPolicies = () => useQuery({ queryKey: keys.policies, queryFn: () => get<BillingPolicy[]>('/api/v1/platform/billing-policies') })
export const usePublishPolicy = () => useAction((input: BillingPolicyInput) => post<BillingPolicy>('/api/v1/platform/billing-policies', input), [keys.policies, ['audit']])
export const useCommissionSchedules = () => useQuery({ queryKey: keys.schedules, queryFn: () => get<CommissionSchedule[]>('/api/v1/platform/commission-schedules') })
export const usePublishSchedule = () => useAction((input: CommissionScheduleInput) => post<CommissionSchedule>('/api/v1/platform/commission-schedules', input), [keys.schedules, ['audit']])
export const useTenantTerms = (tenantId: string) => useQuery({ queryKey: keys.terms(tenantId), queryFn: () => get<TenantTerms>(`/api/v1/platform/tenants/${tenantId}/terms`) })
export const useMyTerms = () => useQuery({ queryKey: keys.myTerms, queryFn: () => get<TenantTerms>('/api/v1/subscription') })

export interface ChargeFilters {
  tenantId?: string
  status?: ChargeStatus | ''
  period?: string
}

const chargeQuery = ({ tenantId, status, period }: ChargeFilters) =>
  [tenantId ? `tenantId=${tenantId}` : '', status ? `status=${status}` : '', period ? `period=${period}` : ''].filter(Boolean).join('&')

/** Charges of any account for platform staff, or of the own account when `own` is set. */
export const useCharges = (filters: ChargeFilters, own = false) => {
  const query = chargeQuery(filters)
  return useQuery({
    queryKey: keys.charges(`${own ? 'own' : 'all'}:${query}`),
    queryFn: () => get<Charge[]>(`${own ? '/api/v1/charges' : '/api/v1/platform/charges'}?${page(0, 100)}${query ? `&${query}` : ''}`),
  })
}
export const useChargeDetail = (id: string | null, own = false) =>
  useQuery({ queryKey: keys.charge(`${own ? 'own:' : ''}${id}`), queryFn: () => get<ChargeDetail>(`${own ? '/api/v1/charges' : '/api/v1/platform/charges'}/${id}`), enabled: !!id })
export const useRecordPayment = (chargeId: string) =>
  useAction((input: PaymentInput) => post<Payment>(`/api/v1/platform/charges/${chargeId}/payments`, input), [['charges'], ['platform', 'tenants'], ['platform', 'tenant'], ['audit']])
export const useReversePayment = () =>
  useAction((input: { id: string; reason: string }) => post<Payment>(`/api/v1/platform/payments/${input.id}/reverse`, { reason: input.reason }), [['charges'], ['platform', 'tenant'], ['audit']])
export const useVoidCharge = (chargeId: string) =>
  useAction((reason: string) => post<Charge>(`/api/v1/platform/charges/${chargeId}/void`, { reason }), [['charges'], ['platform', 'tenant'], ['audit']])
export const useRunCollection = () => useAction(() => post<CollectionPassResult>('/api/v1/platform/subscriptions/run'), [['charges'], ['platform', 'tenants'], ['platform', 'tenant']])

export const useResellerCommissions = (resellerId: string) =>
  useQuery({ queryKey: keys.resellerCommissions(resellerId), queryFn: () => get<CommissionOverview>(`/api/v1/platform/resellers/${resellerId}/commissions`) })
export const useCommissionStatement = (resellerId: string, month: string | null) =>
  useQuery({ queryKey: keys.commissionStatement(resellerId, month ?? ''), queryFn: () => get<CommissionStatement>(`/api/v1/platform/resellers/${resellerId}/commissions/${month}`), enabled: !!month })
export const useSettleCommission = (resellerId: string) =>
  useAction(
    (input: { month: string; settledOn: string; reference: string | null; note: string | null }) =>
      post<CommissionSettlement>(`/api/v1/platform/resellers/${resellerId}/commissions/${input.month}/settle`, { settledOn: input.settledOn, reference: input.reference, note: input.note }),
    [keys.resellerCommissions(resellerId), ['platform', 'reseller', resellerId, 'commissions'], ['audit']],
  )
export const useMyCommissions = () => useQuery({ queryKey: keys.myCommissions, queryFn: () => get<CommissionOverview>('/api/v1/reseller/commissions') })
export const useMyCommissionStatement = (month: string | null) =>
  useQuery({ queryKey: keys.myCommissionStatement(month ?? ''), queryFn: () => get<CommissionStatement>(`/api/v1/reseller/commissions/${month}`), enabled: !!month })
