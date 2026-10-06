import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { del, get, newKey, post, put } from './http'
import type {
  AppUser,
  ArchivedFile,
  CatalogEntry,
  Certificate,
  Company,
  CompanyDetails,
  Customer,
  CustomerDetails,
  Document,
  ElectronicDocument,
  ElectronicDocumentEvent,
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
  customers: (search: string) => ['customers', search] as const,
  products: (search: string) => ['products', search] as const,
  documents: (companyId: string | null, skip: number, take: number) => ['documents', companyId, skip, take] as const,
  document: (id: string) => ['document', id] as const,
  electronic: (documentId: string) => ['electronic', documentId] as const,
  events: (id: string) => ['events', id] as const,
  archive: (id: string) => ['archive', id] as const,
  catalog: (number: string) => ['catalog', number] as const,
  users: ['users'] as const,
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
export const useElectronic = (documentId: string) =>
  useQuery({
    queryKey: keys.electronic(documentId),
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
export const useDeactivateCustomer = () => useAction((id: string) => post(`/api/v1/customers/${id}/deactivate`), [['customers']])
export const useSaveProduct = () =>
  useAction((input: { id?: string; details: ProductDetails }) => (input.id ? put<Product>(`/api/v1/products/${input.id}`, input.details) : post<Product>('/api/v1/products', input.details)), [['products']])
export const useDeactivateProduct = () => useAction((id: string) => post(`/api/v1/products/${id}/deactivate`), [['products']])
export const useCreateUser = () =>
  useAction((input: { email: string; displayName: string; password: string; roles: string[] }) => post<AppUser>('/api/v1/users', input), [keys.users])
export const useDeactivateUser = () => useAction((id: string) => post(`/api/v1/users/${id}/deactivate`), [keys.users])

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
