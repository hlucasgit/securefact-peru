# Correo saliente: operación

Decisión y razones: ADR-052 y ADR-054. La plataforma envía: el enlace para recuperar la contraseña y el aviso de que cambió (en el acto, sin cola), y los avisos de cuenta nueva, de suspensión, reactivación y cierre de una cuenta y de dominio asignado, verificado o perdido (por una **cola con reintentos** que vacía el worker). Sin canal configurado, nadie puede recuperar su contraseña por sí mismo.

## Qué configurar
| Variable (`.env` / compose) | Clave de la API | Qué es |
|---|---|---|
| `SF_EMAIL_PROVIDER` | `Email:Provider` | `None` (no envía; por defecto), `Smtp` o `Sandbox` (archivos `.eml`; solo desarrollo y pruebas, **se rechaza en producción**) |
| `SF_EMAIL_FROM` | `Email:From` | dirección desde la que sale todo (por ejemplo `no-responder@securefact.pe`) |
| `SF_SMTP_HOST`, `SF_SMTP_PORT` | `Email:Smtp:Host`, `Port` | servidor y puerto (587 con `StartTls`, 465 con `Tls`) |
| `SF_SMTP_SECURITY` | `Email:Smtp:Security` | `StartTls`, `Tls` o `None` (**sin cifrar: se rechaza en producción**) |
| `SF_SMTP_USER`, `SF_SMTP_PASSWORD` | `Email:Smtp:User`, `Password` | credenciales; **de entorno o de un gestor de secretos, nunca en el repositorio** |
| `SF_MAIL_DIR` | `Email:Sandbox:Directory` | carpeta de los `.eml` del proveedor `Sandbox` |
| `SF_PUBLIC_URL` | `Web:PublicUrl` | dirección de la interfaz (producción exige `https`); base de los enlaces de la plataforma |

La API **no arranca** si el proveedor es desconocido, si falta el servidor o el remitente del que se eligió, o si producción usa `Sandbox` o un SMTP sin cifrar.

## La cola de avisos (worker de correo)
- Los avisos se guardan en `notifications.email_queue` y los manda `SecureFact.Workers` (`EmailWorker`, cada `Email:WorkerIntervalSeconds`, 5 por defecto). **El worker necesita la misma configuración de correo que la API** (`Email__*`, `Web__PublicUrl`): `docker-compose.yml` ya la pasa a los dos.
- Si el envío falla, reintenta con espera creciente (30 s a 1 h) y a los 10 intentos el correo queda **muerto** (advertencia «was not delivered … dead: True» en el registro del worker, con su id; no hay pantalla para reencolarlo). Lo enviado o muerto se purga a los `Email:RetentionDays` (30).
- Al enviarse se borra el texto del correo de la cola. Sin `Email:Provider` (`None`) los avisos **no se encolan**.
- Para revisar la cola: `SELECT to_address, subject, attempts, sent_at, dead_at, last_error FROM notifications.email_queue ORDER BY created_at DESC;` con la conexión del dueño del esquema (el rol de la aplicación no la ve sin el ámbito de la plataforma).

## Antes de usarlo en producción
1. Use un proveedor de correo transaccional o su propio servidor, con cuenta de envío autenticada.
2. **Autentique el dominio remitente**: SPF, DKIM y DMARC. Sin eso, los correos caen en no deseado o se rechazan, y la recuperación de contraseña «no llega».
3. Envíe un correo de prueba real a un buzón propio: **el SMTP real no se probó en el desarrollo** (ADR-052). Compruebe nombre mostrado, respuesta, enlace y que se abre en la interfaz.
4. Para un revendedor con **dominio verificado** (ADR-051), el enlace apunta a su dominio; el correo sigue saliendo del remitente de la plataforma, con su marca como nombre y su soporte como dirección de respuesta.

## Cuando algo falla
| Síntoma | Causa probable |
|---|---|
| Un aviso no llega (suspensión, bienvenida…) | el worker no corre o no tiene `Email__*`; correo muerto en la cola (`dead_at`); ver «La cola de avisos» más arriba |
| La persona dice que no le llega | proveedor `None` (la API lo advierte en el registro); servidor SMTP caído o credenciales mal (advertencia «was not delivered» con el tipo del error); correo en no deseado (SPF/DKIM) |
| La API no arranca con `Email:…` | combinación no admitida (producción con `Sandbox` o `Security=None`; falta `Host` o `From`) |
| El enlace abre otra dirección | `Web:PublicUrl` mal puesto, o el revendedor tiene un dominio verificado (el enlace va a su dominio) |

Los errores de entrega **nunca** se muestran a quien pidió el enlace (la respuesta es la misma exista o no la cuenta); se ven solo en el registro del servidor, sin el token ni la dirección.

## Seguridad
- El token va en el **fragmento** del enlace (`#token=`): no llega a los registros del *edge* ni a un `Referer`. Vale 30 minutos (`Identity:PasswordResetMinutes`) y se usa una vez.
- Los `.eml` del `Sandbox` contienen enlaces utilizables: es solo para máquinas de desarrollo y del CI; la API los rechaza en producción.
