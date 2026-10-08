# ADR-055: Avisos del negocio por correo y correos fallidos

- Estado: Aceptada · Fecha: 2026-10-08
- Completa: ADR-054 (avisos por correo y cola), que dejó dicho «sin avisos de vencimiento del certificado, límite del plan ni rechazo de un comprobante» y «sin pantalla para los correos muertos».

## Contexto
Las cosas que más cuestan a un contribuyente se enteran tarde: el certificado digital vence y los comprobantes dejan de firmarse, el plan llega a su tope y la siguiente emisión se rechaza, SUNAT rechaza un comprobante que nadie mira. En los tres casos el sistema ya sabe lo que pasa; solo faltaba decírselo al propietario. Y un correo que murió en la cola (ADR-054) solo se veía en el registro del worker.

## Decisión

### Qué se avisa (a los propietarios activos de la cuenta, con su marca)
| Hecho | Cuándo | Una vez por |
|---|---|---|
| **Plan al 80 %** de los comprobantes del mes | al emitir el comprobante que alcanza cuatro quintos del tope | cuenta y mes |
| **Plan al 100 %** | al emitir el que ocupa el último lugar: «no podrá emitir más hasta que cambie el mes» | cuenta y mes |
| **Certificado por vencer** | un paso diario del worker; avisa a los **30, 15 y 7 días** y el día que **vence** | certificado y umbral |
| **Comprobante rechazado por SUNAT**, o que **no pudo enviarse** tras sus reintentos | al quedar en ese estado final; dice la respuesta de SUNAT (código y descripción) cuando es un rechazo | comprobante |

- El aviso del plan se **manda después de confirmar** el comprobante: una emisión que se deshace no avisa nada. Un intento rechazado por el tope tampoco.
- El certificado se mira **cuenta por cuenta, en el ámbito de la cuenta** (`UseTenant`): los certificados son datos del tenant y ni el ámbito de la plataforma los lee (Row Level Security). Si el primer paso ve un certificado ya dentro de un umbral, avisa **solo ese umbral**, no los que se saltó. Un certificado vencido hace más de 30 días se considera olvidado y no se avisa.
- **Cada hecho se dice una vez por dirección**: la cola (`email_queue`) tiene `dedupe_key` (`clave|dirección`, índice único) y la inserción es `ON CONFLICT DO NOTHING`; así un paso diario, un reintento del worker o dos procesos a la vez no repiten un aviso. La clave de un correo enviado se conserva mientras la fila no se purga (30 días).
- Como los demás avisos (ADR-054): mejor esfuerzo, nunca deshacen ni demoran el trabajo que avisan, y nunca llevan secretos.

### Estructura
`IBusinessNotices` (Notifications.Contracts) con implementación vacía por defecto en Billing y CpeEngine, que lo llaman; `BusinessNoticeEmails` (Notifications) la reemplaza. El paso de certificados (`ICertificateExpiryNotices`) lo corre el `EmailWorker` cada 24 horas. `IEmailOutbox.EnqueueAsync` recibe la clave de deduplicación.

### Correos fallidos (plataforma)
`GET /api/v1/platform/emails/dead` (personal de la plataforma) y `POST /api/v1/platform/emails/{id}/requeue` (administración de la plataforma: devuelve un correo muerto a la cola con sus intentos en cero; queda en la auditoría como `notifications.email.requeued`). Un usuario de cuenta o de revendedor recibe 403. Pantalla **Correos fallidos** en el menú de la plataforma: destinatario, asunto, intentos, último error y botón **Reenviar**.

## Verificación
- 4 pruebas de API (`BusinessNoticesApiTests`): el plan avisa al 80 % (4 de 5) y al 100 %, no antes, y ni el intento rechazado ni nada más vuelve a avisar; un certificado a 12 días avisa con el umbral de 15 y tres pasos más no repiten; un certificado lejano no avisa; la misma clave se encola una vez por dirección (sin distinguir mayúsculas), otra dirección u otra clave sí, y sin clave nada se promete.
- 1 prueba del pipeline de SUNAT (`CpePipelineApiTests`): un rechazo avisa con el código y la descripción de SUNAT y una aceptación no avisa.
- 1 prueba de API de los correos muertos (`NoticeEmailsApiTests`): el personal los ve y los reenvía (y sale), un propietario no puede ni listarlos ni reencolarlos, no se reencola lo que no está muerto, y queda en la auditoría.
- 2 pruebas de la pantalla (`DeadEmails.test.tsx`).

## Límites (P)
- **El SMTP real sigue sin probarse** (ADR-052).
- **Solo propietarios**: no se avisa a los demás usuarios ni al revendedor (por ejemplo, cuando el plan de su cliente llega al tope). Sin preferencias de la persona.
- El paso de certificados lee **hasta 200 certificados por cuenta** (el límite de `ListExpiringAsync`) y recorre **todas las cuentas activas** una vez al día: con decenas de miles de cuentas habrá que partirlo.
- El umbral de **plan** es solo el de comprobantes por mes (el de empresas y usuarios se rechaza al intentar crear, pero no avisa antes). Los tramos 80 % y 100 % son fijos.
- El **rechazo** se avisa por comprobante: un lote de rechazos manda un correo por cada uno. No cubre los rechazos de un resumen diario ni de una comunicación de baja (no son comprobantes de la cuenta).
- Una caída entre confirmar el comprobante y encolar el aviso del plan lo pierde (como en ADR-054).
- La deduplicación no sobrevive a la **purga**: un aviso de más de 30 días atrás podría repetirse si el mismo hecho se vuelve a levantar; con los hechos de aquí (mes, umbral, comprobante) no ocurre.
- **Reenviar** un correo muerto es manual; no hay aviso a la plataforma de que murió uno (solo el registro del worker y esta pantalla).
