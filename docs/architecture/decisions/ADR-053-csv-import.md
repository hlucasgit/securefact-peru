# ADR-053: Importación masiva de clientes y productos desde CSV

- Estado: Aceptada · Fecha: 2026-10-07
- Relacionada con: ADR-003 (multitenancy), ADR-008 (reglas como datos: las afectaciones y tipos de documento salen de los catálogos), ADR-038 (interfaz web).

## Contexto
Un contribuyente que empieza en la plataforma ya tiene su lista de clientes y de productos en una hoja de Excel o en otro programa. Cargarlos de uno en uno (el formulario de «Nuevo cliente») no es viable con cientos de filas. La importación debe ser **segura** (no escribir nada por sorpresa, no pisar datos), **explicable** (decir por qué falla cada fila, con su número de línea) y **respetar las mismas reglas** que el alta manual: ni una validación nueva ni una distinta.

## Decisión

### Qué se importa y qué no
- Solo **crear**. Un cliente cuyo tipo y número de documento ya existen, o un producto cuyo código interno ya existe, se **omite y se informa («Ya existe»)**: no se modifica. Así subir dos veces el mismo archivo no cambia nada (idempotente) y una importación nunca sobrescribe lo que el usuario corrigió a mano. Actualizar en masa es otra función (pendiente).
- La **identidad** es la del alta manual: tipo + número (cliente) y código interno (producto), dentro del tenant. Un cliente de otro tenant no cuenta como existente.
- Cada fila pasa **por el mismo `Validate`** del alta manual (documento estructuralmente válido incluido el dígito verificador del RUC, catálogo 06 y 07 vigentes, longitudes, correo). Cualquier regla futura del alta vale también aquí.

### Dos pasos: revisar y luego importar
`POST /api/v1/customers/import` y `POST /api/v1/products/import` con `{ csv, commit }`.
- **`commit: false` (revisión)**: no escribe nada. Responde cada fila con su línea, un estado (`Ready`, `Existing`, `Invalid`), la clave y el motivo.
- **`commit: true`**: crea las filas válidas **en una sola transacción** (todas o ninguna), con `Created`; las `Existing` e `Invalid` se omiten y se explican. Si otro proceso creó entretanto uno de esos documentos o códigos, nada se guarda y se responde `SF-IMP-002` para repetir.
- Un archivo ilegible se rechaza **entero** con `SF-IMP-001` y el motivo: vacío, más de 2000 filas, más de un millón de caracteres, comilla sin cerrar (con su línea), o faltan columnas obligatorias.
- Permisos: `customers.manage` / `products.manage` (los mismos del alta). El cuerpo está limitado en tamaño.
- Auditoría: **un evento por importación** (`customers.customer.imported`, `products.product.imported`) con cuántas filas se crearon, existían o fallaron y una **huella SHA-256 truncada del archivo**; nunca el contenido (puede tener datos personales).

### Lectura del archivo
Lector propio en `SharedKernel` (`CsvParser`), para lo que produce Excel: RFC 4180 (comillas, comillas dobles, saltos de línea dentro de comillas), **separador detectado** entre `,`, `;` (Excel en español) y tabulación, BOM, líneas y filas de celdas vacías ignoradas.
- Los encabezados se comparan sin tildes, mayúsculas ni signos: «Razón Social», `razon_social` y «RAZON-SOCIAL» son la misma columna; cada columna admite varios nombres (los de la plantilla y algunos más, en el código de cada módulo).
- Una fila con **más celdas que el encabezado** (casi siempre una coma dentro de un texto sin comillas, que corre todas las columnas) se marca con error en vez de guardarla corrida.
- **Clientes**: obligatorias `numero_documento` y `nombre`. `tipo_documento` acepta el código del catálogo 06 o `RUC`, `DNI`, `CE`, `PASAPORTE`. **Solo si el archivo no tiene la columna de tipo**, se deduce: 11 dígitos RUC, 8 dígitos DNI; cualquier otro caso es un error (no se adivina).
- **Productos**: obligatorias `codigo`, `descripcion`, `valor_unitario` (sin IGV) y **`afectacion_igv`** (catálogo 07). **La afectación nunca se supone**: un archivo sin esa columna se rechaza; un código inexistente es error de la fila. `tipo` acepta `bien`/`servicio` (sin la columna, bien); la unidad, si falta, es `NIU` para bienes y `ZZ` para servicios (las mismas que usan los ejemplos del alta manual; es una **convención de la plataforma**, ver límites). El valor lleva punto decimal; con `;` como separador también se acepta una coma decimal.

### Interfaz
«Importar CSV» en *Clientes* y *Productos*: elegir el archivo lo **revisa al instante** (nada se escribe), muestra cuántas filas se crearían, cuántas ya existían y cuántas tienen error, y **lista las filas con problema** (línea, clave, motivo; las primeras 100). El botón «Importar N filas» las crea. Descarga una **plantilla** (con BOM para Excel). La página decodifica el archivo como UTF-8 y, si no es válido, como Windows-1252 (el «CSV» común de Excel en Windows), para no perder tildes ni eñes.

## Verificación
- 22 pruebas unitarias del lector (`CsvParserTests`): delimitadores, comillas, saltos dentro de comillas y su línea, BOM, filas vacías, filas cortas y largas, límites, normalización de encabezados, alias.
- 9 pruebas de API (`ImportApiTests`): la revisión no escribe y la importación crea una vez (repetir el archivo no crea ni cambia nada); cada fila mala se explica con su línea (RUC con dígito malo, DNI corto, documento repetido, nombre vacío, correo malo, tipo desconocido, celda de más) y el resto se importa; el tipo se deduce solo sin columna; un archivo ilegible se rechaza entero (vacío, sin columnas, comilla sin cerrar, demasiado grande, más de 2000 filas); otro tenant no ve ni cuenta los clientes ajenos; quien solo lee no importa y la auditoría no guarda el contenido; productos con la unidad de su tipo, la afectación obligatoria y cada fila mala explicada, coma decimal, celdas con comas y saltos y BOM.
- 4 pruebas de la interfaz (`ImportModal.test.tsx`), 3 de utilidades (`csv.test.ts`) y 2 recorridos de extremo a extremo (`import.spec.ts`): revisar sin escribir, importar, repetir el archivo (nada se crea ni se pisa); un archivo de Excel en español para productos; accesibilidad (axe).

## Límites (P)
- **La unidad por defecto (`NIU`, `ZZ`) no se verificó contra el catálogo de unidades de medida de SUNAT**: es la convención que ya usa el alta manual. El alta solo exige hasta 3 caracteres alfanuméricos; antes de emitir con productos importados sin unidad, confirme que esas unidades sirven en su caso o indique la columna `unidad`.
- **Solo crear**: no actualiza existentes ni desactiva los que faltan en el archivo.
- Hasta **2000 filas** o un millón de caracteres por archivo; hay que dividir uno mayor. La importación es síncrona (una petición): no hay cola ni progreso.
- Sin **Excel (.xlsx)**: solo CSV (el usuario lo guarda así desde Excel). La plantilla es un CSV.
- No consulta a RENIEC ni a SUNAT si el RUC o el DNI existen: solo la forma y el dígito verificador (como el alta manual).
- La deducción del tipo por la longitud es una **comodidad** de la importación; no es una regla de SUNAT.
- Las filas con error no se importan: no hay una opción «todo o nada» sobre el archivo entero (la transacción es de las filas válidas).
- Los productos importados no llevan precios con IGV ni listas de precios; los campos son los del alta manual.
