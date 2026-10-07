# ADR-050: El refresh token en una cookie HttpOnly

- Estado: Aceptada · Fecha: 2026-10-06
- Reemplaza el límite (P) de ADR-038 («un *refresh token* en `sessionStorage` es legible por un script de la página»). Completa ADR-011 (identidad y sesiones: rotación y detección de reutilización).

## Contexto
La interfaz guardaba el *refresh token* en `sessionStorage`. Un script inyectado en la página (XSS) podía leerlo y llevárselo: el *refresh token* dura días y renueva sesiones, mientras que el *access token* dura minutos y vive solo en memoria. La CSP estricta (ADR-038) hace difícil inyectar un script, pero no es una razón para dejar la credencial más duradera al alcance de uno.

## Decisión

### API: un modo de cookie que pide el navegador
- Con el encabezado **`X-SecureFact-Session: cookie`** en `POST /api/v1/auth/login` y `POST /api/v1/auth/refresh`, el servidor pone el *refresh token* en la cookie **`sf_rt`** y **no lo devuelve en el cuerpo**: el cuerpo trae solo el *access token* (`refreshToken` vacío).
  - `HttpOnly`: la página no puede leerla.
  - `SameSite=Strict`: el navegador no la envía en una solicitud que nace en otro sitio.
  - `Path=/api/v1/auth`: no se envía a ninguna otra ruta, ni al resto de la API.
  - **De sesión** (sin `Expires` ni `Max-Age`): se va con la sesión del navegador, como se iba con la pestaña el `sessionStorage`; la sesión del servidor tiene además su propio vencimiento.
  - `Secure` siempre, salvo en un servidor de desarrollo sobre HTTP simple (donde el navegador la rechazaría).
- **`/auth/refresh` con el encabezado lee la cookie** y rota la cookie con cada uso (la rotación y la detección de reutilización de ADR-011 no cambian: gastar dos veces el mismo valor cierra toda la sesión). **Sin el encabezado, la cookie no se usa**: el token se lee del cuerpo, como antes. El encabezado no lo puede añadir un formulario ni una solicitud de otro sitio, así que también es la **protección CSRF**: una solicitud que la página no hizo no puede gastar la cookie.
- Una negativa (token ausente, malo, ya gastado, sesión cerrada, contraseña equivocada) **borra la cookie**. `POST /auth/logout` la borra siempre.
- **Los clientes de la API que no mandan el encabezado no cambian**: reciben el *refresh token* en el cuerpo, no reciben cookie y renuevan con el cuerpo. Las integraciones y las pruebas siguen igual.

### Interfaz
- Ya **no guarda ningún token**: el *access token* en memoria y el de renovación en una cookie que no ve. Lo único que queda en `localStorage` es una **pista** (`sf.session`, sin secreto) de que el navegador tuvo una sesión, para intentar restaurarla al cargar y no pedir una renovación que el servidor negaría a un visitante.
- **Pestañas**: la cookie es de todo el navegador, así que una **pestaña nueva entra sin volver a ingresar** (antes cada pestaña tenía su sesión). Como el token rota, dos pestañas que renuevan en el mismo instante gastarían el mismo valor y el servidor lo tomaría por un robo; **el candado del navegador (`navigator.locks`) serializa las renovaciones de todas las pestañas**: la segunda sigue con la cookie que dejó la primera.

## Verificación
- 7 pruebas de API (`RefreshCookieApiTests`): la cookie `HttpOnly`, `SameSite=Strict`, con la ruta de la autenticación, sin vencimiento y sin el token en el cuerpo; la renovación y la rotación (el valor viejo ya gastado cierra la sesión y borra la cookie); la cookie sin el encabezado que no se gasta, y la ausente o mala que se niega y se borra; los clientes de la API que no la piden siguen con el cuerpo y sin cookie; el cierre de sesión que la borra; la contraseña equivocada que no la pone; y la bandera `Secure` sobre HTTPS.
- Interfaz: una prueba unitaria (el ingreso pide el modo de cookie y no deja ningún token en el almacenamiento, solo la pista) y 2 recorridos de extremo a extremo nuevos (la cookie es `HttpOnly` y `Strict` con la ruta de la autenticación, `document.cookie` no la muestra y el almacenamiento no tiene ningún token; una segunda pestaña entra sola y las dos renuevan a la vez sin romper la sesión), y los de ingreso, recarga y cierre de sesión que ya existían.

## Límites (P)
- **Un script inyectado sigue pudiendo pedir un *access token***: la cookie viaja sola en una llamada a `/auth/refresh` hecha desde la página. Lo que ya no puede es **llevarse** la credencial duradera: el *access token* dura minutos y vive en memoria, y cada renovación rota la cookie. Es la ventaja de la cookie `HttpOnly`, no una inmunidad ante XSS.
- Con TLS terminado en un balanceador, `Request.IsHttps` es falso dentro del servicio: la cookie es `Secure` por ser un entorno que no es de desarrollo, no por lo que el servicio ve. En un despliegue de producción **sin HTTPS** los navegadores la rechazarían (y se vería como «la sesión no se restaura»).
- Los dominios de los revendedores (ADR-044) sirven la interfaz y la API del mismo origen: la cookie no cruza dominios y cada dominio tiene la suya.
- La pista en `localStorage` sobrevive al cierre del navegador mientras la cookie de sesión no: el primer intento de restaurar una sesión así se niega, borra la pista y deja la pantalla de ingreso.
