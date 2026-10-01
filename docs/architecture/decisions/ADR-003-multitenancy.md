# ADR-003: Estrategia de multitenancy

- Estado: Aceptada · Fecha: 2026-09-30

## Contexto
Miles de organizaciones en una instalación; una fuga entre tenants es una vulnerabilidad crítica.

## Decisión
**Base de datos compartida, esquema por módulo, columna `tenant_id` en toda tabla de negocio**, con defensa en profundidad en tres capas:

1. **Backend**: `ITenantContext` resuelto del token/API key/dominio (nunca de un parámetro del cliente). Filtros globales de consulta en EF Core por `TenantId`.
2. **PostgreSQL RLS**: política `USING (tenant_id = current_setting('app.tenant_id')::uuid)` y `WITH CHECK` en cada tabla, con `FORCE ROW LEVEL SECURITY`. La aplicación se conecta con un rol **sin** `BYPASSRLS` y sin ser dueño de las tablas. Un interceptor de conexión ejecuta `SELECT set_config('app.tenant_id', @id, true)` al comienzo de cada transacción (alcance de transacción, compatible con pooling).
3. **Pruebas automáticas** (bloqueantes en CI) con dos tenants: intentan leer/escribir/listar/contar entre tenants por cada módulo y por la API; verifican que sin `app.tenant_id` no se ve ninguna fila.

Jerarquía: Platform → Reseller → Tenant → Company → Establishment. Las operaciones de plataforma (backoffice, workers de outbox) usan un rol/contexto explícito con auditoría; los workers establecen el tenant del mensaje que procesan.

Los permisos de reseller sobre tenants son concesiones explícitas (`reseller_grant`), no herencia automática de acceso.

## Consecuencias
+ Operación simple y barata; fuga improbable al requerir fallar tres capas.
− Disciplina: toda tabla nueva debe llevar `tenant_id` y política RLS; una prueba de arquitectura/BD lo verifica consultando `pg_policies`.
− Tenants muy grandes (enterprise) podrían requerir aislamiento físico: el diseño no lo impide (se puede mover un tenant a otra BD por contrato de módulo).

## Implementación (2026-09-30)
- `SecureFact.Platform`: `IDataScope`/`DataScope` (Anonymous · Tenant · Platform; se fija explícitamente por request o mensaje), `RlsConnectionInterceptor` (ejecuta `set_config('app.tenant_id'|'app.scope', …, false)` al abrir la conexión y `RESET` justo antes de devolverla al pool; Npgsql añade `DISCARD ALL` como respaldo), `RlsSql` (genera `ENABLE` + `FORCE ROW LEVEL SECURITY` y la política `tenant_isolation` con `USING` y `WITH CHECK`), `TenantDbContext` (filtro global EF, sellado de `tenant_id`, rechazo de escrituras entre tenants o cambio de propietario).
- Sin tenant en el contexto la política evalúa `NULL` y **no devuelve ni acepta filas** (fail-closed).
- Dos modos de política: `TenantOnly` (tablas de negocio) y `TenantOrPlatform` (registro de tenants, outbox). El ámbito `Platform` es explícito, lleva un motivo y no está disponible para tablas de negocio.
- Rol de aplicación `securefact_app`: sin `BYPASSRLS`, sin ser dueño de tablas ni superusuario; las migraciones corren con otro rol. Los `GRANT` los emite cada migración vía `RlsSql.GrantToAppRole`.
- Prueba estructural: toda tabla de cualquier esquema debe tener RLS forzado y al menos una política (excepto `__ef_migrations_history`); un módulo nuevo que lo olvide rompe CI.
- Pruebas (`tests/Security`, PostgreSQL real vía Testcontainers): aislamiento con y sin filtro EF, SQL crudo, ámbito anónimo, inserción/actualización/borrado forzados, fuga por conexión del pool, ámbito plataforma, privilegios del rol. Se verificó por mutación que anular el interceptor hace fallar las pruebas.
- Limitación conocida: `set_config` añade un viaje de ida y vuelta por apertura de conexión; se optimizará fijando la vida de la conexión por request si el perfil lo exige.
- Pendiente: resolución del tenant desde credenciales (depende de Identity) y concesiones explícitas de reseller (`reseller_grant`).
