# ADR-042: Planes, límites y consumo

- Estado: Aceptada · Fecha: 2026-10-06
- Completa: ADR-003 (multitenancy), ADR-041 (administración de plataforma). El modelo de datos de `docs/architecture/erd.md` (`PLAN`) lo anticipaba.

## Contexto
Toda cuenta tenía los mismos derechos: sin tope de empresas, de usuarios ni de comprobantes. Para vender la plataforma hace falta poder dar a cada cuenta lo que contrató y medir lo que usa. Este ADR cubre **qué permite un plan y cómo se hace cumplir**; no cubre precios ni cobro (ver «Límites»).

## Decisión

### Modelo
- **`tenancy.plan`** (datos de plataforma, sin `tenant_id`): `code` único (minúsculas, dígitos y guiones), `name`, tres límites y `is_active`. **Un límite vacío es ilimitado**; cero es un tope real (no se puede tener nada).
  - `max_companies`: empresas de la cuenta.
  - `max_users`: usuarios **activos** (desactivar uno libera su lugar).
  - `max_documents_per_month`: comprobantes emitidos por mes calendario de Lima.
- **`tenancy.tenant.plan_id`** (obligatorio, con clave foránea). La migración crea el plan **`pilot` («Piloto») sin ningún límite** y mueve a él a todas las cuentas existentes: añadir los planes no cambia el comportamiento de nadie hasta que la plataforma mueve una cuenta.
- RLS del catálogo: lectura para cualquier ámbito, escritura solo del ámbito de plataforma (`EnableGlobalReference`). Aun así, la API **no lista el catálogo a una cuenta** (los planes de otras cuentas pueden ser ofertas privadas): una cuenta lee solo el suyo.
- Una cuenta nueva empieza en el plan pedido o en `pilot`. Un plan retirado (`is_active = false`) no se puede asignar a cuentas nuevas, pero quien ya lo tiene lo conserva.

### Cumplimiento
`IPlanLimits` (`Tenancy.Contracts`) lo consultan los módulos que son dueños de lo que se cuenta; Tenancy conoce el plan, no las empresas ni los comprobantes.
- **Empresas** (Organizations) y **usuarios** (Identity): antes de crear, el módulo cuenta lo suyo y pregunta `EnsureCanAddAsync`. Crear un usuario de una cuenta como personal de plataforma cuenta contra la cuenta de destino.
- **Comprobantes** (Billing, `DocumentService.IssueAsync`, que cubre facturas, boletas y notas): dentro de la transacción de emisión, **después de registrar la clave de idempotencia y antes de tomar el número**, si el plan tiene límite se toma un `pg_advisory_xact_lock` por cuenta y se cuenta lo emitido desde el inicio del mes (índice `ix_document_tenant_created`). Así dos solicitudes simultáneas no pueden quedarse con el último lugar, un rechazo no consume número ni deja huella y **un reintento de una solicitud ya aceptada devuelve el original** (la repetición se resuelve antes). Con plan sin límite no hay bloqueo ni conteo.
- Qué cuenta: facturas, boletas y notas emitidas en el mes calendario de Lima (`created_at`), **también las dadas de baja** (la baja no devuelve el lugar). No se cuenta el simulador de forma distinta: el límite es de la cuenta, no del entorno.
- El rechazo es **403 `SF-PLAN-001`** («El plan X permite hasta N …»). Otros códigos: `SF-PLAN-002` plan inexistente, `SF-PLAN-003` plan inválido o inactivo, `SF-PLAN-004` código en uso.
- **Bajar un límite nunca quita lo que la cuenta ya tiene**: solo impide agregar más.

### Consumo (metering)
No hay contadores aparte: el consumo se **calcula de los datos reales** (empresas, usuarios activos, comprobantes del mes) por cada módulo (`CountAsync`, `CountActiveAsync`, `CountIssuedAsync`) y la API lo compone (`TenantUsageReader`). Un contador separado se desfasaría de la realidad; calcularlo no puede.

### API
| Ruta | Permiso | Quién |
|---|---|---|
| `GET /api/v1/platform/plans` | `tenants.read` | solo plataforma (soporte lee) |
| `POST /api/v1/platform/plans`, `PUT /api/v1/platform/plans/{id}` | `tenants.manage` | superadministrador. El código no cambia al editar |
| `POST /api/v1/platform/tenants/{id}/plan` | `tenants.manage` | superadministrador; auditado con el plan anterior |
| `GET /api/v1/platform/tenants/{id}/usage` | `tenants.read` | solo plataforma |
| `GET /api/v1/plan` | `tenants.read` | la cuenta lee su plan y su consumo |
| `POST /api/v1/platform/tenants` | `tenants.create` | acepta `planId` |

Auditoría: `tenancy.plan.created`, `tenancy.plan.updated`, `tenancy.tenant.plan_changed`.

### Interfaz (`web/`)
- **Planes** (plataforma): catálogo, crear y editar (el superadministrador; el soporte solo ve).
- **Detalle del inquilino**: plan y consumo con barras (los números son el texto; la barra solo los repite) y **Cambiar plan** (superadministrador).
- **Plan y consumo** (propietario, administrador y facturación): su plan y lo usado, con «Límite alcanzado» cuando toca. Un rechazo por límite se muestra con su código y el nombre del plan.

## Verificación
- 9 pruebas de API (`PlansApiTests`): plan piloto sin límites, quién puede qué (soporte, propietario, anónimo), validación y unicidad del código y edición, tope de empresas (rechazo y ampliación), tope de usuarios (desactivar libera), tope de comprobantes (rechazo, sin hueco de numeración, reintento idempotente), **8 solicitudes simultáneas con tope de 3 → exactamente 3 emisiones**, lectura aislada del plan y del consumo, auditoría.
- 2 recorridos de extremo a extremo (`plans.spec.ts`): crear un plan, asignarlo, agotarlo y ampliarlo con la vista del propietario y la auditoría; accesibilidad (axe) del catálogo y su formulario, y el propietario sin acceso al catálogo.

## Límites (P)
- **Sin precios, facturación ni cobro**: un plan no tiene tarifa y la plataforma no emite cargos. Los precios y la facturación de la plataforma a sus cuentas son una decisión comercial aún no tomada.
- Solo tres límites. Pendientes: almacenamiento, llamadas a la API, establecimientos, módulos opcionales (GRE, importación masiva).
- El conteo mensual es por `created_at` en el mes calendario de Lima; un plan con periodo de facturación distinto (por ejemplo desde la fecha de alta) pediría otro cálculo.
- El conteo de comprobantes de un mes grande recorre el índice de la cuenta en cada emisión mientras el plan tenga límite; con volúmenes muy altos convendría un contador transaccional (se descartó ahora por el riesgo de desfase).
- Sin cuota blanda ni avisos al acercarse al límite (80 %, 100 %); la interfaz solo marca «Límite alcanzado».
- Un plan retirado sigue sirviendo a quien lo tiene; no hay migración masiva de cuentas entre planes.
