# ADR-072: Frases propias del revendedor en los correos de su marca

- Estado: Aceptada · Fecha: 2026-10-11
- Completa: ADR-044 (marca blanca), ADR-054 y ADR-055 (avisos por correo), ADR-068 (avisos de cobranza).

## Contexto
Los correos de los usuarios de las cuentas de un revendedor ya salen con su nombre de marca, su correo de soporte y su portal (ADR-044, ADR-052, ADR-054). El revendedor quiere además decirle algo propio a sus clientes: un saludo, el horario o el teléfono de atención. Una plantilla libre por tipo de aviso sería peligrosa: cada aviso lleva hechos que el cliente necesita (importes, fechas, qué pasó) y un correo que sale con el dominio y la reputación de la plataforma no debe servir para mandar al cliente a otro sitio.

## Decisión
- **Dos frases, no plantillas.** La marca del revendedor gana un **saludo** (hasta 200 caracteres) y un **cierre** (hasta 400), ambos opcionales y de **una sola línea**. Cada aviso las pone **alrededor** de lo que dice: el saludo abre, el cierre va antes de la línea de ayuda («Si necesita ayuda, escriba a …») y de la firma. Los hechos del aviso (importes, fechas, enlaces) nunca se reemplazan ni se mueven. En HTML y en texto plano dicen lo mismo, y el HTML las escapa como todo el resto.
- **Qué no pueden llevar** (se rechaza con `SF-BRAND-001`, sin cambiar nada): direcciones web (`://`, `www.`, dominios con una terminación común), correos (`@`), marcas (`<`, `>`) ni saltos de línea u otros caracteres de control. El contacto del revendedor es el correo de soporte de su marca, que cada correo ya muestra; un teléfono y un horario sí caben. Es un freno contra el uso del correo de la plataforma para llevar al cliente a otro lugar, no una garantía contra todo mal uso: la responsabilidad de lo que dice su marca sigue siendo del revendedor (ADR-044) y cada cambio queda en la auditoría con el texto.
- **A quién van**: a los usuarios de las **cuentas del revendedor** en todos los avisos de cuenta, estado, negocio, certificados y cobranza (ADR-054, ADR-055, ADR-068). **No van** en los de seguridad (la recuperación de la contraseña y su aviso de cambio no pasan por esta composición), ni en los que reciben los propios usuarios del revendedor (dominio, resumen de cobranza, liquidación, alta de un administrador del revendedor: ADR-070), ni en los de una cuenta directa de la plataforma o de otro revendedor.
- **Siguen a la marca**: sin nombre de marca no hay frases (quitar la marca las quita, como quita el color y el correo); si la plataforma desactiva al revendedor, sus clientes reciben los correos de la plataforma, sin sus frases.
- **Quién las edita**: el revendedor (`reseller.branding.manage`) y la plataforma (superadministrador), en la misma pantalla y el mismo `PUT` que el resto de la marca. Como todo `PUT`, lo que no se envía queda vacío. No salen en el endpoint público de la marca (el ingreso no las necesita); las lee el compositor de correos por `IBranding.EmailTextsAsync`.
- **Interfaz**: en *Marca*, la sección «Correos a sus clientes» con los dos campos y una vista previa que muestra el saludo, un texto de ejemplo, el cierre, la línea de ayuda y la firma.

## Verificación
`EmailTemplatesApiTests` (API real): el correo de alta de un cliente lleva el saludo al comienzo, el hecho, el cierre y la línea de ayuda, en ese orden, y el HTML las escapa; una cuenta directa, los clientes de otro revendedor y el revendedor que no puso frases no las ven; el aviso de dominio al propio revendedor y el correo de recuperación de contraseña no las llevan; se guardan recortadas (una vacía es ninguna), las edita también la plataforma, quitar la marca las quita y el cambio queda en la auditoría; se rechazan direcciones web, correos, marcas y saltos de línea (y lo guardado no cambia), el límite de largo vale 200 y 400 y caben un teléfono y un horario; un usuario de una cuenta o otro revendedor no las edita. Interfaz (Vitest): la vista previa sigue lo que se escribe y la marca se envía con las frases (vacías como nada) y el motivo de un rechazo se muestra.

## Límites (P)
- Solo español, una frase de cada tipo para todos los avisos: no hay una distinta por tipo de aviso ni por idioma.
- No hay vista previa por aviso real: la de la pantalla es una muestra.
- El filtro de direcciones es por patrones: un texto que las escriba de otro modo («ventas punto com») no se detecta.
- No hay aprobación de la plataforma antes de que las frases salgan.
