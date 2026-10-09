# ADR-066: API pública estable y llaves de API

- Estado: Aceptada · Fecha: 2026-10-09
- Completa: ADR-038 (interfaz web, que usaba la misma API con el token de una persona) y ADR-003 (multitenancy).
- Relacionada: ADR-067 (webhooks).

## Contexto
La API ya servía a la interfaz web, pero un programa de un cliente (un ERP, una tienda en línea) no puede ingresar como una persona: no tiene segundo factor, ni se espera que renueve una sesión cada pocos minutos. Tampoco había un contrato que un integrador pudiera tomar por estable, ni una descripción pública de la API fuera del desarrollo, ni un límite de uso para quien la llama.

## Decisión

### Llaves de API
- Una **llave** pertenece a una cuenta y actúa con **un rol** de esa cuenta: `BillingAdmin`, `Sales`, `Accountant`, `Auditor` o `ReadOnly`. No se pueden dar los roles que administran la cuenta (`TenantOwner`, `TenantAdmin`) ni los de plataforma o revendedor, y quien la crea solo puede dar un rol cuyos permisos ya tiene (`RoleCatalog.CanAssign`, el mismo freno contra la escalada de privilegios que los usuarios). Es la consecuencia buscada: **ninguna llave puede crear llaves, usuarios ni webhooks**, de modo que una llave filtrada no se perpetúa ni redirige los eventos.
- Formato `sfk_<id en 32 hexadecimales>_<secreto de 256 bits en base64url>`. El prefijo distingue una llave de un token de acceso en el mismo encabezado. De la llave se guarda **solo el SHA-256 del secreto** (con 256 bits de entropía no hay nada que adivinar: no hace falta una función lenta). Se muestra completa **una vez**, al crearla.
- La busca por su id y compara el hash en tiempo constante, **también cuando el id no existe**, y una llave inexistente, mal escrita, vencida o revocada recibe la misma respuesta (`401`), de modo que no se puede sondear qué ids existen.
- Vence a lo sumo en dos años o no vence; se revoca al instante; hasta 20 activas por cuenta; `last_used_at` se marca como máximo cada 5 minutos para no escribir en cada llamada. Quedan auditadas la creación y la revocación (sin el secreto).
- Tabla `identity.api_key` con `tenant_id` y RLS como todo dato de negocio. Solo se consulta en ámbito de plataforma, de forma acotada (`Elevate`), para autenticar a quien todavía no se sabe quién es.

### Autenticación
Un esquema que elige entre dos: si la solicitud trae `Authorization: Bearer sfk_…` o `X-Api-Key`, la autentica el manejador de llaves; si no, el token de una persona como siempre. El ámbito de datos sale **solo de la llave** (su cuenta), igual que sale solo del token de una persona, y se fija antes de leer el estado de la cuenta (el registro de cuentas solo le muestra a quien llama su propia fila). Una cuenta suspendida o cerrada deja sin efecto sus llaves en cada solicitud. La principal de una llave lleva `sub` = el id de la llave y `amr` = `api_key`; la auditoría registra ese id como autor.

### Límites de uso
Un limitador global (antes que la autenticación) con dos particiones encadenadas: por **credencial** (el hash de lo que se presentó, llave o token; 600 por minuto) y por **dirección** de origen (3 000 por minuto, para que inventar credenciales no lo eluda). `/health` no cuenta. Al pasarse: `429` con `Retry-After` y el código de límite de uso. Ambos son configurables (`RateLimiting:ApiPermitPerMinute`, `RateLimiting:AddressPermitPerMinute`). Los endpoints de credenciales conservan su límite propio, más estricto.

### Contrato estable
- La descripción OpenAPI se publica **en todos los ambientes** y sin autenticación (`GET /openapi/v1.json`), con la forma de autenticarse.
- El archivo [`docs/api/openapi.v1.json`](../../api/openapi.v1.json) es **el contrato de `v1`**: una prueba (`OpenApiContractTests`) compara lo que la API publica con él, de modo que una ruta, un campo o un código que cambien por accidente rompen la compilación; uno deliberado se regenera con `SF_UPDATE_OPENAPI=1` y se revisa en el diff.
- Política de versión: `v1` solo crece (rutas, campos y valores nuevos); lo que rompa va en `v2` junto a `v1`; el retiro de algo se avisa con meses de anticipación. La guía para integradores es [`docs/api/README.md`](../../api/README.md) y una colección Bruno de ejemplo vive en `docs/api/bruno/`.

### Permisos
`apikeys.manage` (propietario y administrador de la cuenta) administra las llaves: `GET|POST /api/v1/api-keys`, `POST /api/v1/api-keys/{id}/revoke`, `GET /api/v1/api-keys/roles`. Códigos `SF-KEY-001` (inválida), `SF-KEY-002` (no existe), `SF-KEY-003` (demasiadas).

## Verificación
`ApiKeysApiTests` (PostgreSQL): la llave se muestra una vez y no vuelve a salir, funciona por los dos encabezados, tiene los permisos de su rol y no los de administración, ve solo su cuenta, llaves inválidas/alteradas/vencidas/revocadas dan `401`, solo quien tiene el permiso las crea y no con roles de administración, el tope de 20, la suspensión de la cuenta, la auditoría sin secreto y el hash guardado; el límite de uso por credencial con `Retry-After`. `OpenApiContractTests`: la descripción es pública y coincide con el contrato.

## Límites (P)
- **Un solo rol por llave**: no hay permisos finos ni restricción por empresa de la cuenta o por dirección IP de origen.
- El límite de uso es **por proceso**: con varias instancias de la API cada una cuenta aparte. Un límite compartido (Redis) queda para cuando haya más de una.
- La auditoría guarda el id de la llave como autor de tipo «usuario»; distinguir en el registro una llave de una persona pide un tipo de autor propio.
- Sin SDK, ni *sandbox* público para terceros, ni portal de desarrollador propio: la guía, la descripción y la colección son lo que hay.
- Las llaves no tienen alcance de solo-prueba: una llave de una cuenta de producción emite de verdad.
