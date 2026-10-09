# ADR-043: Revendedores

- Estado: Aceptada · Fecha: 2026-10-06
- Completa: ADR-003 (jerarquía Plataforma → Reseller → Tenant), ADR-041 (administración de plataforma), ADR-042 (planes)

## Contexto
La jerarquía de ADR-003 ya nombraba al revendedor, y el rol `ResellerAdmin` existía en el catálogo de roles, pero no había nada detrás: sin entidad, sin usuarios que pudieran ingresar (su token no identificaba un ámbito válido) y sin pantallas. Un revendedor trae clientes a la plataforma y los administra; este ADR cubre lo mínimo para que lo haga **sin ver nada que no sea suyo**.

## Decisión

### Modelo
- **`tenancy.reseller`** (`id`, `name`, `is_active`): datos de la plataforma, sin `tenant_id`. RLS **solo ámbito de plataforma** (`RlsSql.EnablePlatformOnly`): una cuenta no puede leer ni escribir revendedores.
- **`tenancy.tenant.reseller_id`** (ya existía, ahora con clave foránea): **la relación explícita** entre una cuenta y su revendedor. ADR-003 hablaba de concesiones `reseller_grant`; no hace falta una tabla aparte mientras una cuenta tenga **un solo** revendedor y el acceso de este sea el que enumera este ADR. Si un día hay varios revendedores por cuenta o permisos distintos por cuenta, esa tabla se justifica.
- **`tenancy.plan.reseller_id`** (nulo = catálogo público): un plan con revendedor es una **oferta privada** de ese revendedor (ADR-042).
- **`identity.app_user.reseller_id`**: los usuarios de un revendedor (rol `ResellerAdmin`) no pertenecen a ninguna cuenta y pertenecen a **exactamente un** revendedor. El token lleva el claim `rid`; `ICurrentUser.ResellerId` solo lo devuelve si el usuario tiene un rol de nivel revendedor.
- Los usuarios de un revendedor los crea **solo el personal de plataforma** (`POST /api/v1/users` con `roles: ["ResellerAdmin"]` y `resellerId`). El `resellerId` solo vale para ese rol, y debe nombrar un revendedor que exista y esté activo.

### Qué ve y qué hace un revendedor
Su rol tiene **tres permisos propios** y ninguno de los de plataforma ni de cuenta: `reseller.tenants.read`, `reseller.tenants.create`, `reseller.tenants.manage`.

| Ruta | Permiso | Efecto |
|---|---|---|
| `GET /api/v1/reseller` | read | su propio registro |
| `GET /api/v1/reseller/tenants`, `…/{id}`, `…/{id}/usage` | read | solo las cuentas con su `reseller_id` |
| `POST /api/v1/reseller/tenants` | create | abre una cuenta suya, con un plan que puede asignar |
| `POST /api/v1/reseller/tenants/{id}/owner` | create | crea el propietario, **una sola vez**: mientras la cuenta no tenga usuarios activos; después la cuenta administra los suyos |
| `GET /api/v1/reseller/plans` | read | planes activos del catálogo público y los privados suyos |
| `POST /api/v1/reseller/tenants/{id}/plan` | manage | cambia el plan de una cuenta suya a uno permitido |

Un revendedor **no** cierra cuentas, no lee sus usuarios, documentos ni auditoría, no edita planes y no mueve cuentas entre revendedores (eso es de la plataforma). Suspender y reactivar sus cuentas sí puede, con las reglas del ADR-045.

### Aislamiento
- El usuario de un revendedor trabaja en **ámbito de plataforma** a nivel de base de datos (necesita leer las filas de las cuentas de sus clientes). Eso **no** se confía a las rutas: lo que lo contiene es (1) su rol solo tiene permisos de revendedor, así que **toda ruta de plataforma o de cuenta le contesta 403**; y (2) `IResellerAdministration` toma el revendedor **del token**, nunca de la solicitud, y **filtra cada lectura y escritura por él**.
- Una cuenta de otro revendedor, una sin revendedor y una inexistente contestan **lo mismo: 404**, para que un revendedor no pueda sondear qué cuentas existen. Igual con un plan privado ajeno (`SF-PLAN-002`).
- Un revendedor **desactivado** no puede ingresar ni renovar (403 `SF-TEN-002`, como una cuenta suspendida) y su token deja de servir en la siguiente solicitud. Las cuentas de sus clientes **siguen funcionando**.
- Auditoría: `tenancy.reseller.created/updated`, `tenancy.tenant.reseller_changed` (con el anterior), y lo que hace el revendedor queda en la auditoría **de la cuenta** (`tenancy.tenant.created` con su `resellerId`; `plan_changed` con `byReseller`).

### Plataforma
`GET/POST /api/v1/platform/resellers`, `PUT /api/v1/platform/resellers/{id}` (renombrar o desactivar), `POST /api/v1/platform/tenants/{id}/reseller` (asignar, mover o quitar). Leer es de `tenants.read` (el soporte puede); cambiar es de `tenants.manage` (superadministrador). La lista trae cuántas cuentas tiene cada uno.

### Interfaz (`web/`)
- **Plataforma**: *Revendedores* (crear, renombrar, desactivar, agregar su administrador), una tarjeta *Revendedor* en el detalle de la cuenta (asignar o dejar directa) y la oferta pública o privada en el formulario de *Planes*.
- **Revendedor**: su menú es solo *Mis cuentas* y *Seguridad*. Lista y búsqueda, *Nueva cuenta* (nombre, entorno, plan, propietario en dos llamadas, como en la plataforma) y el detalle con el plan y el consumo, el cambio de plan y *Crear propietario*.

## Verificación
- 8 pruebas de API (`ResellersApiTests`): quién administra revendedores, el `resellerId` y el rol, abrir cuentas con su propietario (y que lo cree una sola vez), que cada revendedor vea solo las suyas (otro, ninguno e inexistente dan 404 en lectura, consumo, plan y propietario), **que no alcance nada de plataforma ni de cuenta** (11 lecturas y 5 escrituras de plataforma contestan 403), los planes públicos y privados (el ajeno y el retirado no), mover una cuenta entre revendedores con su auditoría, desactivar a un revendedor y reactivarlo, y la auditoría de lo que hace.
- 3 recorridos de extremo a extremo (`resellers.spec.ts`): crear un revendedor y su administrador que ingresa y ve solo *Mis cuentas*; abrir una cuenta con un plan propio y el propietario que ingresa y ve su plan, el propietario que no se repite y otro revendedor que no ve la cuenta ni por su dirección; mover una cuenta y apagar al revendedor sin afectar a su cliente. Accesibilidad (axe) de sus pantallas.

## Límites (P)
- ~~Sin facturación ni comisiones~~: resuelto en ADR-062 y ADR-063 (la plataforma cobra al cliente final y el revendedor gana una comisión recurrente).
- La **marca blanca** (nombre, color, logotipo y dominio del portal) es del ADR-044; siguen pendientes los correos y las plantillas por revendedor.
- Un revendedor no ve los usuarios ni la actividad de sus cuentas, y no entra «como» el cliente. Suspender es del ADR-045.
- Una cuenta tiene un solo revendedor, y un revendedor desactivado no puede transferir sus cuentas: la plataforma las mueve.
- El revendedor opera en ámbito de plataforma a nivel de base de datos; el filtro por revendedor es de aplicación (probado), no una política RLS propia. Un ámbito de revendedor con su propia política sería una defensa más profunda, a cambio de tocar el ámbito de datos en todos los módulos.
- Los usuarios de un revendedor no se listan en la interfaz (la API de usuarios no filtra por revendedor).
