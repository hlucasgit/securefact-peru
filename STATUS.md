# STATUS — 2026-10-01

## Estado general
Fase 0 completada. **Fase 1 (Foundation) casi completa**: tenancy con RLS, identidad/RBAC, auditoría inmutable, empresas/establecimientos, observabilidad básica y stack `docker compose` verificado de punta a punta. No hay lógica tributaria todavía (intencional: depende de leer catálogos/XSD oficiales). Dos commits previos ya están en `origin/main`; el trabajo posterior está **sin commitear**.

## Arquitectura definida
Monolito modular .NET 10 + PostgreSQL (RLS, un esquema por módulo) + canales `ICpeSubmissionChannel` + reglas con vigencia. ADR-001…012 en `docs/architecture/decisions/` (nuevos: ADR-011 identidad y sesiones, ADR-012 auditoría con cadena de hashes).

## Módulos creados
| Módulo | Qué contiene |
|--------|--------------|
| `SharedKernel` | `Result`, `Error`, `Ruc`, `Series`, `DocumentNumber`, `TenantId`, `ICurrentUser`, `IRequestContext`, métricas |
| `Platform` | Ámbito de datos (`DataScope`, `Elevate`), RLS (interceptor + DDL), `TenantDbContext`, `ISecretProtector` (cifrado de envoltura AES-GCM) |
| `Tenancy` | Registro de tenants (RLS), creación solo por plataforma |
| `Identity` | Login, sesiones con rotación y detección de reutilización, MFA TOTP, bloqueo, recuperación de contraseña, usuarios, roles/permisos con anti-escalada |
| `Audit` | Eventos append-only con cadena de hashes, verificación, consulta por tenant |
| `Organizations` | Empresas (RUC validado) y establecimientos |
| `Api` | JWT, autorización por permiso, Problem Details, rate limit en `/auth/*`, OpenTelemetry, logging por request sin datos sensibles, comandos `migrate` y `bootstrap-platform-admin` |

## Pruebas (todas pasan en Release con warnings-as-errors)
- **129 en total**: 58 unitarias (incluye vectores RFC 6238), 6 de arquitectura, 6 de integración, **59 de seguridad** contra PostgreSQL real (Testcontainers): aislamiento cross-tenant (con y sin filtro EF, SQL crudo, pool de conexiones), RLS en todas las tablas, autenticación/sesiones/MFA/reset por HTTP, RBAC y escalada de privilegios, auditoría (append-only, detección de manipulación y borrado, concurrencia), organizaciones, y que los logs no contengan contraseñas ni tokens.
- Verificado por mutación: anular el interceptor RLS y la verificación de hash hace fallar las pruebas.
- Bug real hallado por las pruebas y corregido: tras revocar una sesión el token seguía autorizado.
- Cobertura de código **no medida**.
- Smoke test manual del stack en contenedores: migración, bootstrap, login, creación de tenant y lectura de auditoría OK.

## Normativa revisada
`docs/regulatory/` (sources S01–S18, baseline, matriz). Novedades de esta sesión (hoja oficial de reglas de validación 26.08.2026): plazos y tasa de IGV son **parámetros con vigencia** (confirma ADR-008); las boletas pueden enviarse individualmente dentro del plazo y, fuera de él, por Resumen Diario; reglas de serie por tipo de documento; catálogos oficiales en el Anexo N.°8 (47). Hashes SHA-256 de xlsx y XSD registrados.

## Riesgos y desvíos
- MinIO ya no se puede descargar: se usa SeaweedFS en local (ADR-005).
- `docker compose up` verificado; el build de imágenes requería copiar `.editorconfig` (corregido).
- Auditoría escrita después del cambio de negocio, no en la misma transacción (ADR-012); se resolverá con el outbox.
- La validación de sesión consulta la BD en cada request (sin caché).
- Sin proveedor real de correo: la recuperación de contraseña queda inerte hasta el módulo Notifications.
- `UnconfiguredPasswordResetNotifier` y `LocalEnvelopeSecretProtector` son solo de desarrollo; producción exige KMS/Vault (la API lo bloquea en `Production`).
- Rutas inexistentes responden 401 antes que 404 (política por defecto segura).

## Deuda técnica
`AllowedHosts: *`; CSP/HSTS básicos; el CI no se ha ejecutado en GitHub; sin medición de cobertura; sin Redis/RabbitMQ/S3 consumidos por código aún; `dotnet-tools.json` en la raíz.

## Pendientes inmediatos
1. **Decisión del usuario**: autorizar la descarga a `docs/regulatory/assets/` del xlsx de reglas (855 KB), XSD (609 KB) y guías XML (≈5 MB) para versionarlos por hash.
2. Commit y push del trabajo de esta sesión.
3. Paso 9 de Fase 1: outbox + `IMessageBus` (RabbitMQ) + `IObjectStorage` (S3), y ejecutar el CI.
4. Fase 2: módulo `Catalogs` (importando el Anexo N.°8), clientes, productos, series y numeración transaccional, TaxEngine.

## Siguiente fase
Cerrar Fase 1 (outbox/bus/storage) y comenzar Fase 2 (Core Billing) tras leer la Guía XML de factura/boleta (QR, leyendas) y cargar los catálogos.
