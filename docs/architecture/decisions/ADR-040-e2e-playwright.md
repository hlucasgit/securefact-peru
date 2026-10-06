# ADR-040: Pruebas de extremo a extremo de la interfaz con Playwright

- Estado: Aceptada · Fecha: 2026-10-06
- Completa: ADR-038 (interfaz web), ADR-039 (simulador de SUNAT)

## Contexto
Las 16 pruebas unitarias de la interfaz no prueban lo que más importa: que la pantalla, la API, la base de datos, los workers y el canal con SUNAT trabajen juntos. Hasta ahora eso se revisaba a mano.

## Decisión
- **Playwright** (`@playwright/test`, Chromium) en `web/e2e/`, contra la **API real** con PostgreSQL y el almacén S3, los **workers** y el **simulador de SUNAT** (ADR-039). La interfaz se sirve **compilada** (`vite preview`): el mismo paquete que se despliega.
- **Cada archivo trabaja en su propia cuenta**: el administrador de plataforma (`SF_E2E_ADMIN_EMAIL`, `SF_E2E_ADMIN_PASSWORD`) crea por la API una cuenta nueva con su propietario para cada prueba, y una empresa lista (certificado, SOL, series) cuando hace falta. Las pruebas preparan su mundo por la API y **ejercitan la interfaz para lo que verifican**; así corren en paralelo (4 trabajadores) sin tocarse, y dos corridas seguidas no chocan.
- **Qué cubren (40 recorridos)**:
  - *Autenticación*: ruta protegida con regreso, clave equivocada, cerrar sesión, recargar, segundo factor completo (con TOTP calculado en la prueba; el código del paso de la inscripción no se acepta dos veces, y la prueba usa el siguiente).
  - *Emisión*: factura con ISC e ICBPER de punta a punta (totales, XML firmado con 2000 y 7152, CDR, PDF), archivo conservado con su hash, boleta con DNI, crédito con cuotas que no suman, empresa sin series.
  - *Seguimiento*: nota de crédito que parte del documento aceptado con su ISC y sus bolsas, nota de débito, observación, falla y rechazo del simulador, baja hasta «Anulado» (1 minuto: SUNAT contesta con un ticket que los workers consultan cada minuto) y resumen diario.
  - *Puesta en marcha*: empresa, series, certificado y credenciales SOL desde la interfaz (la clave SOL no reaparece ni en el HTML), certificado de otro contribuyente o con clave equivocada refusados, clientes y productos.
  - *Roles y aislamiento*: un usuario de solo lectura creado desde la interfaz no ve las acciones y la API refusa lo que intenta por la dirección directa; otra cuenta no abre documentos ni empresas ajenos por su dirección.
  - *Plataforma* (ADR-041): crear inquilino con su propietario, suspender y reactivar con la sesión del propietario en otra ventana, cierre, búsqueda, usuarios, soporte de solo lectura, auditoría y su integridad.
  - *Planes* (ADR-042): crear un plan, asignarlo, agotar un tope de empresas y ampliarlo con la vista del propietario y la auditoría; accesibilidad del catálogo y acceso negado al propietario.
  - *Accesibilidad* (axe-core, WCAG 2 A y AA: falla con violaciones graves o críticas) en el ingreso, la emisión, el detalle y las cinco pestañas de la empresa; y *teléfono* (Pixel 7): menú, navegación, sin desborde horizontal y emisión.
- **CI**: el trabajo `e2e` publica la API y los workers, levanta PostgreSQL y S3 con Docker (`.github/scripts/e2e-stack.sh`: migraciones, administrador de plataforma con clave generada y enmascarada), instala Chromium y corre la suite; sube el informe, las capturas, las trazas y los registros como artefacto.

## Qué encontraron (y se corrigió)
1. **Inicios de sesión simultáneos de una misma cuenta daban 500** (`DbUpdateConcurrencyException` en la fila del usuario): de dos solicitudes a la vez, una perdía. Ahora se repite desde cero con una pausa breve (hasta 24 rondas) y una renovación del mismo *refresh token* a la vez contesta «sesión inválida» en lugar de 500; dos pruebas de seguridad lo cubren.
2. **La etiqueta de cada campo envolvía al control**: el nombre accesible de un `select` incluía el texto de todas sus opciones. Ahora la etiqueta nombra solo al campo.
3. **El PDF se abría con `window.open` tras esperar el archivo**: los bloqueadores de ventanas emergentes lo tratan como una ventana sin gesto del usuario. Ahora la pestaña se abre en el clic y se le indica el destino al llegar el archivo.
4. **La baja no tenía rastro en la pantalla**: tras pedirla no había aviso ni se actualizaba. Ahora muestra «Baja solicitada» y la página se actualiza sola hasta «Anulado»; y, mientras un documento va en camino (listo, enviando o esperando respuesta), se actualiza cada 5 segundos.
5. La navegación lateral no era un elemento `nav` (ahora lo es, con su nombre accesible).

## Cómo correrlas
En `web/README.md`: API con `Sunat__Environment=Sandbox`, los workers a su lado, y `SF_E2E_ADMIN_*`; luego `npm run e2e`. Falla al comenzar y con la causa si la API no responde o no hay administrador.

## Límites (P)
- Solo Chromium (sin Firefox ni WebKit) y una configuración regional (`es-PE`, Lima).
- El **PDF** se comprueba por el archivo descargado (el Chromium sin pantalla no tiene visor), no por su aspecto; tampoco se compara el aspecto de ninguna pantalla (sin pruebas visuales).
- Las pruebas de carga y de recuperación ante caídas de los workers o de la base no son de esta suite.
- La baja tarda un minuto por el intervalo de consulta del ticket (1 minuto, fijo en el código): es la prueba más lenta (≈ 70 s).
