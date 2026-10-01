# Changelog

Formato [Keep a Changelog](https://keepachangelog.com/es-ES/1.1.0/); versionado semántico cuando haya releases.

## [Unreleased]
### Added
- Rules (reglas con vigencia servidas por `IRuleProvider`; Billing deja de aceptar tasas del cliente), Customers y Products (datos maestros con catálogos oficiales), referencia de cliente en documentos, y generador UBL 2.1 sin firmar de factura/boleta validado contra el XSD oficial y las etiquetas obligatorias del libro de reglas.
- Catalogs (42 catálogos oficiales importados del libro de reglas con vigencias y versiones, API de lectura, pruebas anti-deriva con TaxEngine/Billing) y CpeEngine (contenido del QR según el Anexo N.° 6). Activos regulatorios oficiales versionados con hashes.
- TaxEngine (cálculo puro con `decimal`, derivado de las reglas oficiales de factura; 40 pruebas incl. 3 000 documentos aleatorios) y Billing (series por tipo, numeración atómica sin huecos, creación idempotente de facturas/boletas, documentos insert-only, endpoints `/api/v1/series` y `/api/v1/documents`).
- Identity/RBAC (login, sesiones con rotación y detección de reutilización, MFA TOTP, recuperación de contraseña, usuarios y roles con anti-escalada), módulo Audit (cadena de hashes append-only con verificación), módulo Organizations (empresas y establecimientos), OpenTelemetry y logging estructurado sin datos sensibles, `SecretProtector` con cifrado de envoltura, stack completo en `docker compose` con migraciones y bootstrap.
- Tenancy: `SecureFact.Platform` (ámbito de datos, RLS), módulo `Tenancy` con registro de tenants, migración inicial con RLS forzado y 21 pruebas de seguridad cross-tenant contra PostgreSQL real.
- Fase 0: baseline normativo, matriz, C4, ERD, ADR-001…010, documentación PSE, roadmap.
