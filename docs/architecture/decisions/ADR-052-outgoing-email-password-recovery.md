# ADR-052: Correo saliente y recuperación de la contraseña

- Estado: Aceptada · Fecha: 2026-10-07
- Completa: ADR-044 (marca blanca: «la plataforma aún no envía correos»), ADR-051 (el dominio verificado del revendedor). Usa los endpoints de restablecimiento de contraseña de Identity.

## Contexto
Identity tenía desde el inicio `POST /api/v1/auth/password-reset/request` y `/confirm`, pero el envío del enlace lo hacía un marcador (`UnconfiguredPasswordResetNotifier`) que descartaba el token: **sin canal de entrega, nadie podía recuperar su contraseña**, y la interfaz no tenía pantallas para hacerlo. Además, ADR-044 dejó anotado que el correo, cuando existiera, necesitaría la marca del revendedor.

## Decisión

### Un canal de correo con tres proveedores (`Email:Provider`)
Módulo nuevo **Notifications** (`IEmailSender`, `EmailMessage`, `EmailDeliveryException` en su `Contracts`; MailKit/MimeKit para construir y enviar).
| Proveedor | Qué hace | Producción |
|---|---|---|
| `None` (por defecto) | no envía; quien lo pide recibe `EmailDeliveryException` | sí (es el modo seguro: lo que no se configura, no sale) |
| `Smtp` | envía por el servidor del operador (`Email:Smtp:Host/Port/Security/User/Password`, TLS por `StartTls` o `Tls`) | sí, **cifrado obligatorio** (`Security=None` se rechaza) |
| `Sandbox` | escribe cada correo como archivo `.eml` en `Email:Sandbox:Directory` | **se rechaza**: un `.eml` guarda tokens de un solo uso |
- El correo sale **desde la dirección de la plataforma** (`Email:From`): el operador autentica ese dominio (SPF, DKIM). La marca del revendedor es **solo el nombre que se muestra** y la dirección de respuesta (`Reply-To` = su correo de soporte). No se envía desde el dominio del revendedor: no se puede firmar por él y se confundiría con suplantación.
- La contraseña del SMTP viene del entorno (`Email__Smtp__Password`); nunca se registra. El mensaje de una falla de envío lleva solo el tipo de la excepción, no el texto del servidor SMTP (puede repetir el destinatario o las credenciales).

### El enlace de recuperación
`EmailPasswordResetNotifier` (host de la API) compone el correo y reemplaza al marcador:
- **Marca**: la del revendedor del usuario (un usuario de revendedor, o el dueño de una cuenta que abrió ese revendedor) si tiene nombre de marca y está activo; si no, la de la plataforma. Siempre dice «Con tecnología SecureFact» (ADR-044).
- **Dirección del enlace**: `https://<dominio verificado del revendedor>/restablecer#token=…` si el revendedor tiene un dominio verificado (ADR-051); si no, `Web:PublicUrl`. (Producción exige `Web:PublicUrl` absoluto y `https`.)
- **El token va en el fragmento (`#`)**, no en la consulta (`?`): el navegador no envía el fragmento al servidor, así que no queda en los registros del *edge* ni de un proxy ni en una cabecera `Referer`. La interfaz lo lee una vez y lo **borra de la barra de direcciones**.
- Vale `Identity:PasswordResetMinutes` (30) y se usa una vez; pedir otro anula los anteriores (ya era así). Al elegir contraseña nueva se cierran todas las sesiones del usuario (ya era así).
- **Una entrega fallida no se le dice al que pide**: la respuesta es la misma (204) exista o no la cuenta y funcione o no el correo; el servidor lo registra como advertencia **sin el token ni la dirección**. Así el envío no sirve para averiguar qué cuentas existen.
- El HTML escapa el nombre de la marca y el enlace; el texto plano va siempre junto al HTML.
- **Aviso de cambio**: al elegir la contraseña nueva, se manda «Su contraseña de <marca> cambió» con la hora (de Lima), el cierre de sesiones y un enlace a `/recuperar` por si no fue el titular (alguien con acceso a su correo). **No lleva token ni nada que dé acceso.** Si el aviso no sale, el cambio **no se deshace** (ya está hecho y es válido).

### Interfaz
`/recuperar` (pide la dirección; **responde lo mismo** haya o no cuenta) y `/restablecer` (lee el token del fragmento; contraseña nueva y su repetición, mínimo 12 caracteres; muestra el motivo si la contraseña es débil; si el enlace no sirve —no hay token o la API dice `SF-AUTH-009`— ofrece pedir otro). El ingreso ahora tiene «¿Olvidó su contraseña?». Las pantallas llevan la marca como el ingreso.

### Configuración
`docker-compose.yml`/`.env.example`: `SF_EMAIL_PROVIDER`, `SF_EMAIL_FROM`, `SF_SMTP_*`, `SF_MAIL_DIR`, `SF_PUBLIC_URL`. El CI de extremo a extremo usa `Sandbox` en un directorio que las pruebas leen. Runbook: `docs/deployment/email.md`.

## Verificación
- 7 pruebas de API (`PasswordResetEmailApiTests`) con el notificador real y un canal que captura: el usuario de la plataforma recibe el enlace de la plataforma con el token en el fragmento y nunca en la consulta; una dirección sin cuenta no recibe nada; el usuario de un revendedor y el dueño de su cuenta reciben nombre de marca, `Reply-To` de soporte y el enlace a su dominio verificado (y la plataforma no desaparece del texto); con el dominio sin verificar el enlace es el de la plataforma pero la marca firma igual; el nombre con marcado se escapa en el HTML; un canal caído contesta lo mismo y no deja el token en los registros; el aviso de cambio sale sin token y, si el canal cae justo entonces, la contraseña cambia igual. Las pruebas de identidad existentes (restablecer, reutilizar, débil, registros sin secretos) ahora pasan **por el notificador real**, leyendo el token del enlace.
- 12 pruebas unitarias del canal (`EmailChannelTests`): sin canal no sale nada; el Sandbox escribe un `.eml` con el nombre de marca, la dirección de respuesta y el texto y el HTML; una dirección inválida es una falla de entrega, no un error; un servidor SMTP que no contesta no repite el cuerpo en el error; producción rechaza el Sandbox y el SMTP sin cifrar; faltan servidor o remitente; las plantillas escapan y firman.
- 5 pruebas de interfaz (Vitest, `PasswordRecovery.test.tsx`) y 3 recorridos de extremo a extremo (`recovery.spec.ts`): pedir el enlace, leer el correo del directorio, elegir la contraseña (el fragmento desaparece de la dirección), entrar con la nueva y no con la anterior, y que el enlace sirve una vez; una dirección sin cuenta; y el correo de un revendedor con su dominio. Accesibilidad (axe) de las dos pantallas.

## Límites (P)
- **El SMTP real no se probó** (no hay servidor en el entorno de desarrollo): lo probado es la construcción del mensaje (`.eml` abierto con MimeKit), la falla ante un servidor que no contesta y la configuración. Antes del primer uso real, mande un correo de prueba a un buzón propio y revise SPF/DKIM/DMARC del dominio remitente y que no caiga en correo no deseado.
- **El correo de recuperación se manda en el acto y no se reintenta**: si el servidor SMTP está caído en ese momento, el enlace no llega y la persona debe pedirlo de nuevo (la advertencia queda en los registros). Es a propósito: un token de un solo uso no se guarda en una cola (ADR-054, que sí reintenta los demás avisos).
- **Solo estos dos correos** (el enlace y el aviso de cambio); los avisos de suspensión, bienvenida y dominio son del ADR-054. No hay logotipo en el cuerpo del correo (la imagen exigiría incrustarla o servirla sin sesión).
- **Solo en español**, con la marca como texto: sin plantillas editables por el revendedor.
- Un revendedor con dominio verificado pero **sin nombre de marca** manda a sus usuarios al portal de la plataforma (su marca vacía no cuenta como marca).
- Límite de pedidos: el del endpoint de ingreso (por dirección IP). No hay límite por cuenta: alguien puede llenar de correos legítimos el buzón de una persona (hasta ese límite por minuto).
