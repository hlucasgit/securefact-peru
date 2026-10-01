# ADR-006: Abstracción de canales de envío CPE

- Estado: Aceptada · Fecha: 2026-09-30

## Contexto
SecureFact debe operar hoy sin ser PSE y migrar entre proveedores (SUNAT directo, PSE, OSE) y, si procede, a PSE propio, sin cambiar la API de los clientes (contexto §120–121). El transporte oficial SUNAT es SOAP + ZIP + WS-Security (`current-baseline.md` §4); la GRE es REST/OAuth2 (§6).

## Decisión
`ICpeSubmissionChannel` es el único límite entre el dominio y el exterior. Capacidades y contrato mínimo: `SubmitAsync(SignedPackage) → SubmissionResult` (síncrono: CDR o rechazo) y `PollAsync(ticket)` (asíncrono), con resultado tipado que clasifica el error: `Transport` (reintentable), `ProviderException` (1000–1999: corregir y reenviar), `Rejected` (2000–3999: CDR rechazada) y `AcceptedWithObservations`.

Implementaciones: `SandboxChannel` (simulador interno), `DirectSunatChannel`, `ThirdPartyPseChannel`, `ThirdPartyOseChannel`, `FutureSecureFactPseChannel`.

Reglas:
- Selección por `ProviderConfiguration` de la empresa y entorno; el `Router` no contiene lógica tributaria.
- **Seguridad por defecto**: en `Local` y `Test` solo está registrado `SandboxChannel`; los canales reales exigen `Environment ∈ {Staging, Production}` **y** una configuración explícita; el endpoint beta de SUNAT solo se admite en `Staging` con documentos de prueba. Imposible enviar un documento real desde test con la configuración por defecto.
- `FutureSecureFactPseChannel` está tras el flag `OwnPseMode=false` y falla al resolverse mientras no se active.
- Cada adapter debe pasar una **suite de contract tests** común (aceptado, rechazado, timeout, lento, caída de conexión, CDR inválido, duplicado, ticket pendiente) contra el simulador.
- Resiliencia por adapter: timeout, retry con backoff+jitter solo para `Transport`, circuit breaker, bulkhead, métricas.

## Consecuencias
+ Cambiar de proveedor no cambia datos ni API. Una caída de SUNAT no tumba SecureFact.
− Hay que normalizar semánticas distintas (SOAP SUNAT vs. REST de un PSE); la normalización vive en el adapter.
