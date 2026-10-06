# ADR-038: Interfaz web (React) del MVP

- Estado: Aceptada · Fecha: 2026-10-06
- Inicia: Fase 5 del ROADMAP (MVP comercial)

## Contexto
Hasta ahora la plataforma era solo API. El MVP necesita que un contribuyente configure su empresa, emita comprobantes y siga su estado sin llamar a la API a mano.

## Decisión

### Pila
- **`web/`**: React 19 + TypeScript + Vite, `react-router-dom` y `@tanstack/react-query`. Sin librería de componentes ni de formularios: CSS propio con *tokens* (modo claro y oscuro por `prefers-color-scheme`) y componentes pequeños. Pocas dependencias de ejecución (cuatro), `npm audit` sin hallazgos y vigiladas por Dependabot.
- **Un solo origen**: en desarrollo, el *proxy* de Vite reenvía `/api` a la API; en el contenedor, nginx hace lo mismo (`web/nginx.conf`). La API no necesita CORS. Cabeceras de seguridad en nginx: CSP (`default-src 'self'`, sin scripts ni estilos de otros orígenes; solo `style-src-attr 'unsafe-inline'` para los atributos `style`), `X-Frame-Options: DENY`, `nosniff`, `no-referrer`, la página sin caché y los *assets* con *hash* inmutables.
- **La API es la fuente**: la interfaz **no calcula impuestos ni totales**. Envía las cifras tal como se escriben (el ISC al valor como fracción, una bolsa por unidad) y muestra lo que la API calculó. Los códigos (afectación al IGV, tipos de documento de identidad, motivos de las notas) salen de los catálogos de la API, no de listas en el código.
- **Los textos de error** son los de la API (Problem Details, código estable `SF-…`); la interfaz solo los muestra.

### Sesión
- El *access token* vive **solo en memoria**; el *refresh token* en `sessionStorage` (sobrevive a recargar, no a cerrar la pestaña). Un solo `refresh` a la vez (el token rota). Ante un 401 se renueva una vez y se repite la petición; si falla, se cierra la sesión.
- El segundo factor se pide cuando la API contesta `SF-AUTH-005`. Los roles del token se usan **solo para mostrar u ocultar menús**: la autorización es de la API.
- **Límite (P)**: un *refresh token* en `sessionStorage` es legible por un script de la página (XSS). La CSP lo mitiga; moverlo a una cookie `HttpOnly` pide un cambio de la API (cookie, `SameSite`, protección CSRF) que no se hizo.

### Pantallas del MVP
Administración de plataforma: ADR-041. Ingreso (con segundo factor) · Panel (empresas, certificados por vencer, últimos documentos) · Empresas (datos, establecimientos, series, certificado digital, credenciales SOL) · Clientes y productos · **Emitir** factura o boleta (ítems con afectación, descuento, ISC y bolsas de plástico; venta al crédito) · Documentos (lista paginada, detalle, generar y firmar, enviar, consultar, reintentar, XML, PDF, CDR, archivo conservado con su hash, baja) · **Notas** de crédito y débito desde el documento · Resumen diario · Usuarios · Seguridad (activar el segundo factor) · Reglas (valor, fuente y verificación).

### Empaquetado y CI
- `web/Dockerfile` (compilación con Node 24 y nginx sin privilegios) y el servicio `web` de `docker-compose.yml` (`http://localhost:5173`).
- Trabajo `web` del CI: `npm ci`, lint (oxlint), tipos, pruebas (Vitest) y compilación, y `npm audit` de las dependencias de ejecución; la imagen se construye y se escanea con Trivy como las demás.

## Verificación
- 16 pruebas de Vitest: cliente HTTP (token, llave de idempotencia, renovación única ante 401, errores tipados), sesión y el ingreso con segundo factor, armado de las líneas (ISC, bolsas, gratuitas) y formatos (la fecha civil de Lima).
- Revisión manual contra la API real (PostgreSQL, S3, RabbitMQ en Docker): ingreso, panel, emisión de una factura con ISC, de una boleta y de una con bolsas, detalle con ISC e ICBPER, generación del XML firmado, y la pantalla de reglas con el calendario del ICBPER; en modo claro, oscuro y en ancho de móvil. La imagen de nginx se probó con el reenvío de rutas del SPA y las cabeceras.
- **No se probó** el envío a SUNAT desde la interfaz (la API local no tiene canal configurado: `SF-CPE-005`, que la interfaz muestra tal cual); sigue probado por API contra el beta.

## Límites (P)
- Sin pruebas de extremo a extremo con navegador automatizado (solo las unitarias de arriba y la revisión manual).
- La lista de documentos pide el estado de cada fila por separado (la API no lo trae en la lista): 25 llamadas pequeñas por página; un campo de estado en la lista las evitaría.
- Detracción, retención y exportación se agregaron después (ADR-046). Las leyendas de venta exonerada y el transporte de carga se agregaron después (ADR-047). Fuera de esta primera entrega: importación masiva, planes y métricas de consumo, *webhooks*, recuperación de contraseña, auditoría.
- Solo español.
