import { useQuery } from '@tanstack/react-query'
import { createContext, useContext, useEffect, useMemo, type ReactNode } from 'react'
import { api } from '../api/http'
import { useSession } from '../auth/session'

/** What a visitor may see of a reseller's brand (white label, ADR-044). */
export interface Brand {
  brandName: string
  primaryColor: string
  supportEmail: string | null
  logoUrl: string | null
}

const PRODUCT = 'SecureFact Perú'
const Branding = createContext<Brand | null>(null)

/** The brand to show now: null is the default look of the platform. */
export const useBrand = () => useContext(Branding)

/** Public: the sign-in page has no session, so the portal is recognised by the host it is served at. */
const hostBrand = () =>
  api<Brand | undefined>('GET', `/api/v1/branding?host=${encodeURIComponent(window.location.hostname)}`, { anonymous: true }).then((brand) => brand ?? null)

const userBrand = () => api<Brand | undefined>('GET', '/api/v1/branding/current').then((brand) => brand ?? null)

/**
 * Chooses the brand: whoever is signed in sees the brand of their own reseller (or the default look), and a visitor sees the brand of the portal's host. The colour goes on as custom
 * properties of the root element and the title follows the brand; only the light scheme takes the colour, because the reseller chose it to carry white text on a light page.
 */
export function BrandingProvider({ children }: { children: ReactNode }) {
  const { principal, restoring } = useSession()
  const signedIn = principal !== null
  const byHost = useQuery({ queryKey: ['branding', 'host', window.location.hostname], queryFn: hostBrand, staleTime: 5 * 60_000, enabled: !signedIn && !restoring })
  const byUser = useQuery({ queryKey: ['branding', 'current', principal?.userId], queryFn: userBrand, staleTime: 5 * 60_000, enabled: signedIn })
  const brand = signedIn ? (byUser.data ?? null) : (byHost.data ?? null)

  useEffect(() => {
    const root = document.documentElement
    const dark = window.matchMedia('(prefers-color-scheme: dark)')
    const apply = () => {
      if (brand && !dark.matches) {
        root.style.setProperty('--brand', brand.primaryColor)
        root.style.setProperty('--brand-soft', `color-mix(in srgb, ${brand.primaryColor} 12%, white)`)
      } else {
        root.style.removeProperty('--brand')
        root.style.removeProperty('--brand-soft')
      }
    }
    apply()
    dark.addEventListener('change', apply)
    document.title = brand ? brand.brandName : PRODUCT
    return () => dark.removeEventListener('change', apply)
  }, [brand])

  const value = useMemo(() => brand, [brand])
  return <Branding.Provider value={value}>{children}</Branding.Provider>
}

/** The logo and the name of the portal, as the sign-in page and the sidebar show them. */
export function BrandMark({ className }: { className?: string }) {
  const brand = useBrand()
  return (
    <span className={className}>
      {brand?.logoUrl && <img className="brand-logo" src={brand.logoUrl} alt="" />}
      {brand ? brand.brandName : PRODUCT}
    </span>
  )
}
