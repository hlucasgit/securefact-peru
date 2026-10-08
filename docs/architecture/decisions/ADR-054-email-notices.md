# ADR-054: Avisos por correo y cola de envío

- Estado: Aceptada · Fecha: 2026-10-08
- Completa: ADR-052 (correo saliente y recuperación de la contraseña), que dejó como límite «los demás avisos» y «sin cola de reintento». Usa la marca (ADR-044) y el dominio verificado (ADR-051).

## Contexto
La plataforma suspendía una cuenta, daba de alta a una persona o perdía el dominio de un revendedor, y nadie se enteraba por correo: el dueño de la cuenta se enteraba al no poder ingresar. El correo de recuperación (ADR-052) se manda en el acto y, si el servidor SMTP está caído en ese momento, se pierde; para un aviso eso no basta.

## Decisión

### Qué se avisa y a quién
| Hecho | Quién lo recibe | Qué dice |
|---|---|---|
| Se crea un usuario (`POST /users`, o el propietario de una cuenta de revendedor o de la plataforma) | la persona creada | que existe una cuenta para ella, con enlace a ingresar y a «¿Olvidó su contraseña?». **Nunca la contraseña**: la dio quien creó la cuenta. |
| Una cuenta se **suspende**, se **reactiva** o se **cierra** (la plataforma o su revendedor) | los **propietarios activos** de la cuenta | el estado nuevo y qué significa (suspendida: nadie ingresa ni emite; cerrada: definitivo, los comprobantes y la auditoría se conservan). **No lleva el motivo**: queda en la auditoría, como el revendedor tampoco ve el de la plataforma (ADR-045); el motivo suele ser interno («moroso»). |
| La plataforma **asigna un dominio** al revendedor, el dominio queda **verificado** o **deja de responder** (ADR-051) | los **administradores activos** del revendedor | qué pasó y a qué pantalla ir. El token de la prueba **no** va en el correo: se ve en la pantalla. Quitar el dominio no avisa. |
Todos van firmados con la marca de la cuenta o del revendedor y dicen «Con tecnología SecureFact» (ADR-044); los enlaces apuntan al dominio verificado del revendedor (si lo hay) o a `Web:PublicUrl`.

### La cola
`notifications.email_queue` (módulo Notifications, solo de la plataforma: RLS `platform_only`). Un aviso se **encola** (`IEmailOutbox`), no se manda en la petición; el worker de correo (`EmailWorker`, cada 5 s) lo manda por el mismo canal de ADR-052.
- **Reintentos** con la misma política del *outbox* de los módulos (ADR-022): espera exponencial de 30 s a 1 h, **muerto a los 10 intentos**. At-least-once: se marca enviado después de que el canal lo aceptó, así que una caída entre ambos pasos manda el aviso dos veces.
- Varios workers comparten la cola (`FOR UPDATE SKIP LOCKED`); un arrendamiento de 5 minutos libera lo que retenía un worker muerto.
- **Al enviarse se borran el texto y el HTML**: quedan la dirección, el asunto y las fechas para soporte. Lo enviado o muerto de hace más de `Email:RetentionDays` (30) se purga cada hora.
- **Sin canal configurado (`Email:Provider=None`) no se encola**: un mensaje que no puede salir solo llenaría la cola de fallos. Quien configure el canal después no recibe los avisos viejos.
- **El correo con un token de un solo uso (recuperación de la contraseña, ADR-052) no pasa por la cola**: se manda en el acto y no se guarda; un token en una tabla sería un secreto en reposo.

### Un aviso nunca deshace lo que avisa
Cada aviso es de mejor esfuerzo: si no se puede componer o encolar, el cambio (la suspensión, el alta, el dominio) **se queda** y se registra una advertencia con el **tipo** del error, sin direcciones ni contenido. Una suspensión no puede fallar porque la base del correo esté caída.

### Estructura
- Los módulos de origen no conocen el correo: `ITenantNotices` (Tenancy.Contracts) e `IAccountNotices` (Identity.Contracts) tienen una implementación vacía por defecto, y `AddEmailNotices` (Notifications) las reemplaza. **Notifications** solo referencia los `Contracts` de Identity y Tenancy (`IAccountDirectory` para saber a quién escribir, `IBranding`), como pide `tests/Architecture`.
- Los **workers** también las registran: el `DomainWorker` es quien verifica y pierde los dominios (ADR-051), así que los avisos de dominio nacen allí. Registran solo el directorio de cuentas de Identity (`AddAccountDirectory`), no el módulo completo.
- `NoticeContext` (marca + dirección del portal) lo comparten los avisos y el correo de recuperación; `WebOptions` (`Web:PublicUrl`) pasó a Notifications.
- Migración `notifications.email_queue` (se aplica con `migrate` del API); permisos al rol de la aplicación incluyen `DELETE` para la purga.

## Verificación
- 9 pruebas de API (`NoticeEmailsApiTests`): el alta avisa con la marca del revendedor, sin contraseña, y la de la plataforma con la suya; los dos propietarios de una cuenta reciben la suspensión, la reactivación y el cierre sin el motivo, también cuando suspende la plataforma, y una cuenta sin propietarios no falla; los administradores del revendedor reciben asignación, verificación y pérdida del dominio (sin el token de la prueba) y no la baja; un envío fallido se reintenta con espera y borra el texto al enviarse; un correo que sigue fallando muere a los 10 intentos y nadie lo retoma; la purga quita lo antiguo enviado y nunca lo pendiente; la cola no se ve desde el rol de la aplicación sin el ámbito de la plataforma ni como tenant.
- 2 pruebas unitarias de la plantilla (`NoticeEmail`: texto y HTML iguales, todo escapado, firma y soporte, sin enlace ni soporte cuando no hay).
- 1 recorrido de extremo a extremo (`notices.spec.ts`): con el API y el worker reales, el propietario de una cuenta nueva recibe la bienvenida y, al suspenderla el revendedor, el aviso sin el motivo (como archivos `.eml`).

## Límites (P)
- **El SMTP real sigue sin probarse** (ADR-052).
- Los correos **muertos** se quedan en la tabla (30 días); la plataforma los ve y los reenvía desde la pantalla *Correos fallidos* (ADR-055).
- Una caída del proceso entre el cambio y el encolado **pierde el aviso**: se encola después de guardar el cambio, no en la misma transacción (módulos distintos). Para estos avisos se aceptó; la auditoría del cambio queda siempre.
- **Solo propietarios**: cuando la plataforma suspende la cuenta de un revendedor, el revendedor no recibe copia; tampoco los usuarios que no son propietarios.
- Vencimiento del certificado, plan cerca del tope y rechazo de un comprobante son del ADR-055. Sin avisos de cambio de plan, usuario desactivado ni inicio de sesión desde un lugar nuevo.
- Sin preferencias de la persona (no se puede dejar de recibirlos), ni plantillas editables por el revendedor, ni otro idioma que el español, ni logotipo en el cuerpo.
- La purga y la cola son de la plataforma entera: no hay una cuota de avisos por cuenta; un revendedor que cree miles de usuarios genera miles de correos de bienvenida.
