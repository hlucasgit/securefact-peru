# ADR-041: Administración de plataforma (inquilinos, estado, auditoría)

- Estado: Aceptada · Fecha: 2026-10-06
- Completa: ADR-003 (aislamiento por tenant), ADR-012 (auditoría), ADR-038 (interfaz web)

## Contexto
El personal de la plataforma solo podía crear un inquilino por la API; no podía listarlos, suspenderlos ni cerrarlos, y el `TenantStatus` (`Active`, `Suspended`, `Closed`) existía como columna que nada hacía cumplir. La interfaz no tenía pantallas para ellos y tampoco para la auditoría, que la API ya exponía.

## Decisión

### Backend
- **`GET /api/v1/platform/tenants`** (`tenants.read`, solo ámbito de plataforma): lista por nombre (búsqueda con comodines escapados) y estado, paginada. Un usuario de un inquilino que lo llame recibe 403: nunca ve otro inquilino.
- **`POST /api/v1/platform/tenants/{id}/status`** (`tenants.manage`, solo el superadministrador; el soporte lee pero no cambia): `{status, reason}`. Transiciones: `Active ↔ Suspended` y cualquiera de las dos a `Closed`; **cerrado es definitivo**. El motivo (3 a 300 caracteres) es obligatorio. Cada cambio queda en la auditoría (`tenancy.tenant.suspended`, `.reactivated`, `.closed`) con el estado anterior, el nuevo y el motivo.
- **`GET /api/v1/users?tenantId=`**: el personal de plataforma acota la lista a un inquilino; para un usuario de inquilino el parámetro no tiene efecto (decide el ámbito de datos, no el parámetro).
- **Un inquilino suspendido o cerrado se hace cumplir en tres puntos** (`ITenantStatusReader`, `Tenancy.Contracts`):
  1. **Ingreso**: tras verificar la contraseña (así no se revela el estado a quien no la conoce) la API contesta 403 `SF-TEN-002` «La cuenta … está suspendida o cerrada». La contraseña equivocada sigue pareciéndose a cualquier otra.
  2. **Renovación**: el *refresh token* no se renueva (401); no se revoca la familia, así que al reactivar el inquilino sus usuarios renuevan de nuevo.
  3. **Cada solicitud**: el token se rechaza (401) si el inquilino no está activo. El estado se lee con una caché de **10 segundos** por proceso; el proceso que cambia el estado la invalida al instante y otros procesos lo ven en ≤ 10 s.
- Los usuarios de plataforma no pertenecen a ningún inquilino y no se ven afectados.

### Interfaz (`web/`)
- **El personal de plataforma** entra a **Inquilinos** y su menú es solo de plataforma (Inquilinos, Auditoría, Seguridad, Reglas): no tiene empresas ni documentos propios.
  - Lista con búsqueda y filtro de estado; **Nuevo inquilino** (superadministrador): crea la cuenta y su propietario en dos llamadas; si falla la segunda, el inquilino queda listo para recibir su propietario desde el detalle.
  - **Detalle**: estado, usuarios de la cuenta, **Suspender / Reactivar / Cerrar cuenta** con motivo (el cierre pide además escribir el nombre de la cuenta), agregar usuarios, cerrar sesiones y desactivar. El soporte ve todo y no tiene ninguna acción.
- **Auditoría** (propietarios, auditores y plataforma): eventos con filtros de acción y tipo de entidad (y de cuenta, para la plataforma), valores anteriores y nuevos, y **Verificar integridad** de la cadena de huellas.
- **Mensajes fallidos** (propietario, administrador, facturación): los eventos internos que agotaron sus reintentos, con **Reencolar**.

## Verificación
- 6 pruebas de API (`PlatformTenantsApiTests`): lista, búsqueda y filtro (con comodines como texto), quién puede qué (soporte, propietario, anónimo), el efecto completo de suspender (token, ingreso con la razón, contraseña equivocada indistinguible, renovación) y de reactivar, el cierre definitivo y la validación de los cambios, la auditoría con el motivo y el estado anterior, y la lista de usuarios por inquilino sin filtrar entre cuentas.
- 10 recorridos de extremo a extremo (`platform.spec.ts`): menú de plataforma, crear un inquilino con su propietario (que ingresa), suspender expulsa y reactivar devuelve, con la auditoría y su integridad, cierre con nombre escrito, búsqueda y filtro, usuarios de la cuenta, soporte de solo lectura, vistas del propietario, accesibilidad (axe) de lista, detalle, diálogo y auditoría. Pruebas unitarias de la navegación por rol.

## Límites (P)
- **Los workers no distinguen inquilinos suspendidos**: lo que ya estaba en cola (envíos a SUNAT, resúmenes, archivo) sigue procesándose. Suspender corta el acceso de las personas y de los clientes de la API, no el trabajo en segundo plano ya aceptado. Filtrarlo pide unir el estado del inquilino a las consultas de los workers.
- La caché de estado es de proceso: con varias instancias de la API, la suspensión tarda hasta 10 s en llegar a las demás (el ingreso no usa la caché).
- El cierre no borra datos (los documentos y la auditoría se conservan por obligación legal, pendiente de confirmar en fuente primaria); no hay exportación ni baja definitiva de la cuenta.
- Sin **revendedores** (`ResellerAdmin`) en la interfaz, sin planes ni facturación de la plataforma, sin cuenta suplantada («entrar como») y sin métricas por inquilino (documentos, uso).
- La lista de inquilinos pide una página de 100 sin paginación en pantalla.
