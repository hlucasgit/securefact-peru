# Changelog

Formato [Keep a Changelog](https://keepachangelog.com/es-ES/1.1.0/); versionado semántico cuando haya releases.

## [Unreleased]
### Added
- Identity/RBAC (login, sesiones con rotación y detección de reutilización, MFA TOTP, recuperación de contraseña, usuarios y roles con anti-escalada), módulo Audit (cadena de hashes append-only con verificación), módulo Organizations (empresas y establecimientos), OpenTelemetry y logging estructurado sin datos sensibles, `SecretProtector` con cifrado de envoltura, stack completo en `docker compose` con migraciones y bootstrap.
- Tenancy: `SecureFact.Platform` (ámbito de datos, RLS), módulo `Tenancy` con registro de tenants, migración inicial con RLS forzado y 21 pruebas de seguridad cross-tenant contra PostgreSQL real.
- Fase 0: baseline normativo, matriz, C4, ERD, ADR-001…010, documentación PSE, roadmap.
