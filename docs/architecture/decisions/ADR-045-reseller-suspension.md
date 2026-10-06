# ADR-045: Suspensión de cuentas por el revendedor

- Estado: Aceptada · Fecha: 2026-10-06
- Completa: ADR-041 (suspender, reactivar y cerrar) y ADR-043 (revendedores). Cierra un límite que ADR-043 dejó escrito: «un revendedor no suspende cuentas».

## Contexto
Suspender por falta de pago es una necesidad real del revendedor: es quien cobra a su cliente. ADR-043 lo dejó fuera hasta decidir **cómo se audita y cómo se evita el abuso**. Estas son las decisiones.

## Decisión

### Qué puede y qué no
- Un revendedor **suspende** una cuenta suya (activa) y **reactiva** una cuenta **que él mismo suspendió**, con un motivo de 3 a 300 caracteres que queda en la auditoría de la cuenta.
- **Nunca cierra** una cuenta: el cierre es definitivo y es de la plataforma (`SF-TEN-003`).
- **No levanta una suspensión de la plataforma** (`403 SF-TEN-004`). Suspender por deuda es del revendedor; suspender por uso indebido o por una investigación no lo es, y deshacerlo menos.
- El efecto en la cuenta es **el mismo** que el de una suspensión de la plataforma (ADR-041): los usuarios no ingresan, no renuevan y su sesión abierta deja de servir en la siguiente solicitud; los workers dejan de enviar y de resumir en su nombre; el sondeo y el archivo continúan.
- Una cuenta de otro revendedor, sin revendedor o inexistente contesta **404**, como en todo el resto de la vista del revendedor.

### Quién suspendió
`tenancy.tenant.suspended_by` (`Platform` o `Reseller`, nulo si no está suspendida). La migración marca como `Platform` toda suspensión existente, porque hasta ahora solo la plataforma podía hacerla.
- La plataforma **reactiva cualquiera**.
- La plataforma **puede suspender una cuenta que el revendedor ya suspendió** (`Suspended → Suspended`, solo en ese caso): la suspensión pasa a ser suya y el revendedor deja de poder levantarla. Sin esto, la plataforma no tendría cómo asegurar una suspensión sin antes reactivarla. Suspender dos veces seguidas una cuenta que ya es suya sigue siendo inválido.
- El DTO de la cuenta lleva `suspendedBy`, para que cada pantalla diga quién la suspendió y qué botones ofrece.

### Auditoría
Cada cambio es `tenancy.tenant.suspended` o `tenancy.tenant.reactivated` en la cadena de la cuenta, con el estado anterior, el nuevo, el motivo, quién suspendió (`suspendedBy`) y, si lo hizo un revendedor, su id (`byReseller`). Verificable con `POST /api/v1/audit/verify`.

### API
`POST /api/v1/reseller/tenants/{id}/status` `{status: "Suspended"|"Active", reason}`, permiso nuevo `reseller.tenants.suspend` (solo `ResellerAdmin`; lo tiene también el superadministrador por incluir todos). La caché de estado (10 s) se invalida en el proceso que cambia el estado, como en la plataforma.

### Interfaz
- **Revendedor**: en el detalle de la cuenta, **Suspender** (con motivo) o **Reactivar**. Si la suspendió la plataforma, un aviso lo dice, **no se ofrece ningún botón** y se le indica contactar a soporte. No se le muestra el motivo de la plataforma.
- **Plataforma**: el detalle dice cuando la suspensión es **del revendedor**, y ofrece además **Suspender por la plataforma** para tomarla.

## Verificación
- 4 pruebas de API (`ResellerSuspensionApiTests`): suspender y reactivar con el efecto completo sobre el cliente (token, ingreso `SF-TEN-002`); no cierra, motivo y transiciones inválidas; la suspensión de la plataforma no se levanta (`SF-TEN-004`) y la plataforma toma la del revendedor; solo las cuentas propias, solo el rol, y la auditoría con motivo y revendedor. Las pruebas de la plataforma (ADR-041) siguen pasando.
- 2 recorridos de extremo a extremo (`resellers.spec.ts`): el revendedor suspende y reactiva con el cliente expulsado y de vuelta, y no puede reactivar la suspensión de la plataforma; la plataforma ve que la suspensión es del revendedor y la toma.

## Límites (P)
- **Sin aviso al cliente ni al revendedor**: no hay módulo de correos. El cliente se entera al ingresar (`SF-TEN-002`), y la plataforma, en la auditoría. El mensaje de ingreso dice «comuníquese con soporte» aun si quien suspendió fue el revendedor; no distingue a quién escribir.
- **Sin freno propio contra el abuso**: no hay límite de suspensiones ni aprobación previa. Lo que contiene el abuso es que quede auditado con motivo, que la plataforma pueda reactivar o tomar cualquier suspensión, y que el revendedor solo alcance sus cuentas. Un revendedor que suspenda sin razón se detecta por la auditoría, no se impide.
- **Sin suspensión programada ni automática** por mora; el revendedor decide y ejecuta.
- Un revendedor desactivado (ADR-043) no puede actuar; sus suspensiones siguen vigentes hasta que la plataforma las levante.
