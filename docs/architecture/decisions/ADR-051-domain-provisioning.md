# ADR-051: Aprovisionamiento de dominios de los revendedores

- Estado: Aceptada · Fecha: 2026-10-06
- Completa: ADR-044 (marca blanca), que registraba el dominio de un revendedor pero dejaba «el registro DNS, el certificado TLS y que el balanceador atienda ese nombre» al operador, sin más.

## Contexto
Con ADR-044 la plataforma **anotaba** el dominio de un revendedor y la interfaz mostraba su marca en él. Nada comprobaba que el dominio fuera del revendedor, que apuntara a la plataforma ni que hubiera un certificado: bastaba con que el personal de la plataforma lo escribiera. Un nombre mal asignado (o un subdominio olvidado que apunta a la plataforma) mostraba la marca de un revendedor sin pruebas de que el portal fuera suyo, y poner un certificado a cada dominio era un trabajo manual de cada alta.

## Decisión

### El dominio tiene un ciclo de vida
`Ninguno → Pendiente → Verificado ⇄ Sin respuesta` (`tenancy.reseller.host_status`, con la prueba, la fecha de la última revisión y el último error).
- La **plataforma asigna** el nombre. Es un nombre de dominio válido (dos o más etiquetas, la última con letras: una IP no vale), sin protocolo, puerto ni ruta, **único** y **que no sea de la plataforma** (`Domains:PlatformHosts` y `Domains:EdgeHost` no se pueden asignar a un revendedor). Cambiar o quitar el dominio **reinicia todo**: otra prueba, otro estado pendiente. Asignar el mismo no cambia nada.
- El **revendedor crea dos registros en su DNS**, que la pantalla le dice con sus valores:
  - **`TXT` `_securefact-challenge.<dominio>` = `securefact-verification=<token>`**: la **prueba de que controla el dominio**. El token son 192 bits del generador del sistema, en hexadecimal, nuevo con cada dominio.
  - **`CNAME` `<dominio>` → `Domains:EdgeHost`** (el *edge* que termina TLS), o, si el nombre no admite CNAME, registros `A` o `AAAA` con `Domains:EdgeAddresses`: la **ruta**.
- La plataforma **verifica** consultando el DNS (la biblioteca `DnsClient`, sin caché, con los resolvedores de la máquina o los configurados). Si faltan registros, el mensaje dice **cuál falta y qué se encontró**. Un resolvedor que no contesta cuenta como revisión fallida, no como error. Dos revisiones del mismo dominio no se hacen a menos de `Domains:MinimumCheckSeconds` (`SF-DOM-002`).
- **Verificado** es lo único que hace dos cosas: la interfaz **muestra la marca** en ese dominio (antes, con ADR-044, bastaba con asignarlo), y el *edge* **puede pedir un certificado** para él. Un dominio pendiente, sin respuesta o de un revendedor desactivado no muestra marca ni recibe certificado.
- Un verificado que falla **tres revisiones seguidas** pasa a **sin respuesta** (un fallo suelto del DNS no baja un portal), y **se recupera solo** cuando vuelve a estar bien.
- Un **worker** (`DomainWorker`, cada `Domains:WorkerIntervalSeconds`) revisa los pendientes cada pocos minutos —el revendedor no tiene que pedirlo— y los verificados y sin respuesta cada varias horas; omite a los revendedores desactivados. La plataforma y el revendedor pueden pedir la revisión ya.
- Cada cambio de dominio, cada verificación y cada pérdida queda en la auditoría (`tenancy.reseller.domain_changed`, `.domain_verified`, `.domain_unreachable`).
- La migración **deja verificados los dominios que ya existían** (ADR-044): servían su portal y no se baja a nadie con este cambio; su prueba nueva queda para cuando se revisen.

### Certificados: el *edge* los pide solo, pero solo para lo que la plataforma dice
`deploy/edge/Caddyfile` y `docker-compose.edge.yml` (con `docs/deployment/domains.md`): un **Caddy** delante de la API y de la web con **TLS bajo demanda**. Antes de pedir un certificado, Caddy pregunta a `GET /api/v1/edge/tls-allowed?domain=…&secret=…`, que contesta **200 solo si el nombre es un host de la plataforma o el dominio verificado de un revendedor activo**, y 404 si no. Sin esa pregunta cualquiera podría apuntar un nombre al *edge* y gastar los límites de la autoridad de certificación.
- **Se contesta solo con el secreto** (`Domains:EdgeSecret`, comparación en tiempo constante; sin secreto configurado, a nadie), y **el *edge* no reenvía esa ruta al exterior**. Hacen falta las dos cosas.
- El certificado se emite **la primera vez que alguien entra** al dominio verificado.

### API e interfaz
- `GET /api/v1/reseller/domain` y `POST /api/v1/reseller/domain/verify` (el revendedor, solo el suyo); `GET /api/v1/platform/resellers/{id}/domain`, `PUT /api/v1/platform/resellers/{id}/host` (asignar: solo el superadministrador) y `POST /api/v1/platform/resellers/{id}/domain/verify`; y la ruta del *edge*.
- **Dominio del portal** en la pantalla de la marca (del revendedor y de la plataforma): el estado, **los dos registros con sus valores y un botón para copiarlos**, lo que falta, **Verificar ahora** y, mientras está pendiente o sin respuesta, la página se mira sola cada 20 segundos. Solo la plataforma ve el campo para asignar.
- `Domains:Dns:Provider` = `Sandbox` (todo pasa; **solo desarrollo y pruebas, se rechaza en producción**), como el simulador de SUNAT (ADR-039): lo usan `docker compose`, el CI de extremo a extremo y el desarrollo local.

## Verificación
- 8 pruebas de API (`DomainsApiTests`), con un DNS controlado por la prueba: el dominio nuevo pendiente con sus dos registros; la verificación que dice qué falta (TXT, ruta equivocada) y que solo con ambos muestra la marca y da certificado; la ruta por direcciones; la revisión que no se repite en segundos y el resolvedor que no contesta; el verificado que pasa a sin respuesta tras tres fallos y vuelve, con su auditoría; el cambio y el borrado que reinician, y los nombres de la plataforma que no se asignan; cada revendedor solo ve y revisa el suyo; la pregunta del *edge* (con y sin secreto, los hosts de la plataforma, el revendedor desactivado); y el paso del worker que promueve a los pendientes y omite al desactivado. Las pruebas de marca (ADR-044) ahora verifican el dominio antes de esperar su marca.
- **El adaptador de DNS real** (`SystemDomainNameSystem`) se probó **a mano contra el DNS público** (TXT de un dominio real, la cadena de CNAME de un alias, y nombres inexistentes que devuelven vacío) y así se encontró y corrigió un error de construcción con los resolvedores vacíos. No tiene prueba automática: depende de Internet.
- 1 recorrido de extremo a extremo (`branding.spec.ts`): el revendedor ve los registros, **verifica** el dominio y el ingreso en él pasa de la marca de la plataforma a la suya; más los de ADR-044, que ahora verifican antes. Accesibilidad (axe).

## Límites (P)
- **El *edge* no se ejecutó.** El `Caddyfile` y el *compose* se escribieron a partir de la documentación de Caddy y **no se validaron**: no se descargó la imagen. La parte de la API que contesta al *edge* sí está probada; que Caddy la use como se espera, y la emisión real de certificados, **no**. Está dicho en el propio archivo y en `docs/deployment/domains.md`, con lo que hay que hacer antes de usarlo (`caddy validate`, y la autoridad de pruebas).
- La revisión es de DNS: **no comprueba que el *edge* responda con un certificado válido** en el nombre. Sin comodines (un subdominio es un dominio), sin DNSSEC propio, y los límites de la autoridad de certificación aplican.
- El revendedor **no asigna su dominio**: lo hace la plataforma (la prueba por TXT protege al revendedor de un nombre ajeno, pero no hay autoservicio).
- Con varias instancias de la API, la pregunta del *edge* y la revisión leen la misma base de datos: no hay estado propio de proceso.
