# ADR-044: Marca blanca de los revendedores

- Estado: Aceptada · Fecha: 2026-10-06
- Completa: ADR-043 (revendedores), ADR-038 (interfaz web)

## Contexto
Un revendedor quiere que sus clientes vean **su** nombre, su color y su logotipo, y que el ingreso en su propio dominio ya los muestre. Este ADR cubre esa presentación en la interfaz web. No cambia lo que la plataforma emite ni quién es responsable de ello.

## Decisión

### Qué es la marca
Cuatro datos del revendedor (`tenancy.reseller`): **nombre de marca** (2 a 60 caracteres, sin caracteres de control), **color** (`#rrggbb`), **correo de soporte** (opcional) y **logotipo** (PNG, JPEG o WebP). Además, el **dominio** (`host`) en que se sirve su portal.
- **Sin nombre de marca no hay marca**: quitar el nombre quita también el color y el correo, y vuelve la apariencia de la plataforma.
- **Solo se muestra la marca de un revendedor activo**. Si la plataforma lo desactiva, sus clientes siguen trabajando (ADR-043) y ven la apariencia por defecto, y su logotipo deja de servirse.

### Reglas que no se dejan al formulario
- **Contraste**: el color lleva texto blanco encima (botones, pestañas activas). El servicio calcula el contraste **WCAG 2.x** contra el blanco y **rechaza** el color por debajo de **4.5:1** (nivel AA para texto normal), con un mensaje que dice el contraste obtenido. Así un revendedor no puede dejar ilegible el botón de ingreso de sus clientes. Se guarda en minúsculas.
- **Logotipo**: hasta **200 KB**, y su tipo se decide **por el contenido** (los primeros bytes), no por lo que diga el cliente. **El SVG no se admite**: puede llevar script y se serviría desde el origen de la aplicación. Se sirve con el tipo detectado, `nosniff` y una versión (huella SHA-256 truncada) en la dirección, de modo que un logotipo cambiado nunca se ve en caché.
- **Dominio**: solo lo asigna **el personal de la plataforma**. Si un revendedor pudiera apuntar un dominio a sí mismo podría hacerse pasar por el portal de otro. Es un nombre de dominio válido (dos o más etiquetas, la última con letras: una dirección IP no vale), sin protocolo ni ruta, **único** (`ux_reseller_host`, 409 `SF-BRAND-003`), y se compara sin distinguir mayúsculas, sin puerto y sin el punto final.
- La marca **no se hereda entre revendedores ni se mezcla**: se resuelve por el dominio, o por el revendedor de la cuenta de quien ingresó.

### API
| Ruta | Quién | Efecto |
|---|---|---|
| `GET /api/v1/branding?host=` | **cualquiera** (limitada por IP, 240/min) | la marca del portal de ese dominio, o 204 |
| `GET /api/v1/branding/logos/{resellerId}?v=` | cualquiera | el logotipo (caché 5 min, `ETag`) |
| `GET /api/v1/branding/current` | quien ingresó | la marca de su revendedor (el suyo, o el de su cuenta), o 204 |
| `GET/PUT /api/v1/reseller/branding`, `PUT/DELETE …/logo` | revendedor (`reseller.branding.manage`) | edita **solo la propia**: el revendedor sale del token |
| `GET/PUT /api/v1/platform/resellers/{id}/branding`, `…/branding/logo` | plataforma (leer: soporte; escribir: superadministrador) | edita la de cualquiera |
| `PUT /api/v1/platform/resellers/{id}/host` | superadministrador | asigna o quita el dominio |

Lo que lee un visitante son **cuatro campos** (nombre, color, correo de soporte, dirección del logotipo): nada que no muestre ya la página de ingreso. Los errores son `SF-BRAND-001` (marca o dominio inválido, o color de poco contraste) y `SF-BRAND-002` (logotipo). Auditoría: `tenancy.reseller.branding_updated` (con los valores anteriores y nuevos) y `tenancy.reseller.logo_changed` (con la versión, nunca los bytes).

### Interfaz
- Al cargar, la página **sin sesión** pregunta por su dominio y aplica la marca; **con sesión**, la de su revendedor (o la apariencia por defecto): quien ingresó ve **su** marca aunque abra otro dominio.
- Se aplica el **color** como propiedades CSS del elemento raíz (permitido por la política CSP, que no admite estilos en línea de hoja), el **nombre** en el menú, en el ingreso y en el título de la pestaña, el **logotipo** en el menú (sobre un fondo claro, por si es oscuro) y en el ingreso, y el correo de soporte en el ingreso. El **modo oscuro conserva los acentos de la plataforma**: el color se eligió para texto blanco sobre fondo claro.
- Siempre se indica **«Con tecnología SecureFact»**: el portal es del revendedor, pero la plataforma no se oculta ni se presenta a nadie como lo que no es (regla 7 de `CLAUDE.md`: no afirmar ser PSE).
- Pantallas: **Marca** del revendedor (nombre, color con vista previa, correo, logotipo) y, para la plataforma, el botón **Marca** de cada revendedor, que además tiene el **dominio**.

## Verificación
- 7 pruebas de API (`BrandingApiTests`): poner y quitar la marca, validación y contraste (amarillo y grises claros rechazados), el logotipo por contenido (texto que dice ser imagen, SVG, vacío, de más de 200 KB y base64 inválido, rechazados; versión que cambia), dominio solo de la plataforma, único y sin importar mayúsculas ni puerto, nadie edita la marca de otro, quién la ve (cliente, revendedor, cuenta directa, plataforma, revendedor desactivado), que lo público son cuatro campos, y la auditoría sin los bytes del logotipo.
- 3 recorridos de extremo a extremo (`branding.spec.ts`), con dominios `*.e2e.test` que el navegador resuelve al servidor local: el ingreso en el dominio del revendedor muestra su nombre, logotipo, color, título y soporte (y otro dominio no hereda nada); el revendedor edita su marca desde la interfaz (color ilegible rechazado, SVG rechazado) y el propietario de su cliente la ve al ingresar; la plataforma asigna el dominio y el repetido se rechaza. Accesibilidad (axe) del ingreso con marca y de la pantalla.

## Límites (P)
- **El PDF, el XML y el CDR no llevan la marca del revendedor.** Son del contribuyente emisor y de SUNAT; su contenido lo fijan las normas, no el revendedor. Un logotipo del propio emisor en la representación impresa es un asunto distinto (pendiente).
- **Correos con marca**: desde el ADR-052 la plataforma envía el correo de recuperación de contraseña con el nombre y el soporte del revendedor; los demás avisos y las plantillas editables por el revendedor siguen pendientes.
- **El dominio** se verifica y obtiene su certificado como dice el ADR-051 (prueba por DNS, un *edge* con TLS bajo demanda); desde él, **solo un dominio verificado muestra la marca**.
- La API de un dominio propio es la misma: el portal de un revendedor no tiene un origen de API distinto, y la política CSP es la misma.
- Un solo color. No hay temas completos, modo oscuro de marca, tipografías, favicon propio ni textos personalizados.
- Un revendedor malintencionado podría poner un nombre que confunda (por ejemplo, el de un banco). La plataforma puede corregir o quitar cualquier marca, y todo cambio queda auditado, pero **no hay aprobación previa**.
- La marca se pide en cada visita (se guarda 5 minutos en el navegador); un cambio puede tardar ese tiempo en verse en otras sesiones.
