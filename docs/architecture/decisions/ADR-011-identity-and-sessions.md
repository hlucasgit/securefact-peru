# ADR-011: Identidad, sesiones y RBAC

- Estado: Aceptada · Fecha: 2026-10-01

## Decisión
- **Contraseñas**: PBKDF2-HMAC-SHA512 (210 000 iteraciones por defecto, sal por contraseña, formato versionado `pbkdf2-sha512$iter$sal$hash`). Elegido por estar en la BCL (sin dependencias); se reevaluará Argon2id si se acepta una dependencia mantenida. Política por longitud (12–128) con lista de denegados, sin reglas de composición. Se consume tiempo equivalente para cuentas inexistentes (anti-enumeración por tiempo).
- **Access token**: JWT firmado HS256 de 10 min con `sub`, `sid`, `tid` y `role`. Anillo de claves con `kid` (actual + anterior) para rotación. Pendiente: clave asimétrica/OIDC para Enterprise.
- **Refresh token**: opaco de 32 bytes, guardado solo como SHA-256, rotado en cada uso; la reutilización de un token ya rotado revoca toda la **familia** (detección de robo). Vigencia inactiva 14 días y absoluta 30.
- **Revocación inmediata**: cada request autenticado comprueba que la sesión (`sid`) siga activa en BD (lectura por clave primaria). Pendiente: caché corta en Redis si el perfil lo exige.
- **Bloqueo**: 5 intentos fallidos → 15 min; respuesta idéntica para cuenta inexistente, contraseña errónea o cuenta bloqueada. Límite por IP en `/auth/*`.
- **MFA TOTP** (RFC 6238, verificado con sus vectores): semilla cifrada con `ISecretProtector` (ADR-007), códigos de un solo uso por ventana.
- **Recuperación**: token de un solo uso (hash), 30 min, revoca todas las sesiones; el token solo viaja por `IPasswordResetNotifier` y nunca por logs ni respuestas.
- **RBAC**: permisos explícitos (`Permissions`) agrupados en roles definidos en código (`RoleCatalog`, versionados con el despliegue). Un rol no implica permisos. Regla anti-escalada: solo se puede otorgar un rol cuyos permisos ya se poseen; roles de plataforma solo los otorga plataforma; no se mezclan roles de plataforma y de tenant; el último `TenantOwner` no se puede quitar. Autenticación obligatoria por defecto (fallback policy).
- **Ámbito de datos**: derivado exclusivamente del token firmado (tenant → `UseTenant`; personal de plataforma → `UsePlatform`). El login, el refresh y la recuperación usan `Elevate("identity:…")`, un ámbito de plataforma acotado y restaurado al terminar.
- El personal de plataforma **no** lee datos de negocio de los tenants (RLS `TenantOnly`); el soporte entra solo con la delegación explícita, temporal y auditada de la cuenta (ADR-069).

## Consecuencias
+ Revocación inmediata y detección de robo de refresh tokens (el navegador lo guarda en una cookie `HttpOnly`: ADR-050). − Una lectura por request para validar la sesión. − Roles personalizados por tenant no existen todavía (se añadirán con tablas cuando haya demanda).
