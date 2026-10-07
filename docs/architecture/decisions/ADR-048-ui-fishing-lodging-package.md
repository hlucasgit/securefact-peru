# ADR-048: Recursos hidrobiológicos (detracción 004), hospedaje (0202) y paquete turístico (0205) en la interfaz

- Estado: Aceptada · Fecha: 2026-10-06
- Completa: ADR-046 y ADR-047. Las reglas son las de la API (ADR-030 y las filas de `docs/regulatory/matrix.md`); **este ADR no añade ninguna regla tributaria**. Con esto, **toda operación que emite la API se emite también desde el formulario**, salvo las que el producto no soporta.

## Decisión

### Qué pide cada operación
El formulario decide qué datos pide cada ítem según la operación y el código de la detracción (`lineDetailOf`, con pruebas): un solo tipo de dato por comprobante, como en la API.

| Operación | Cada ítem declara | Observaciones |
|---|---|---|
| Detracción **004** (tipo 1002) | **Recursos hidrobiológicos**: matrícula (hasta 15) y nombre (100) de la embarcación, especie (150), lugar (100) y fecha de descarga, cantidad en toneladas (> 0, 2 decimales) | el tipo de operación sigue del código |
| Detracción **027** (tipo 1004) | transporte de carga (ADR-047) | |
| **0202** hospedaje | **huésped no domiciliado** (nombre, documento, país del pasaporte) **y su estadía**: país de residencia, ingreso al país, ingreso y salida del establecimiento (la salida no antes del ingreso), fecha de consumo y días de permanencia (0 a 9999) | solo facturas |
| **0205** paquete turístico | **el huésped** y nada de la estadía | solo facturas |

- 0202 y 0205 son **exportaciones**: afectación 40 fija en todos los ítems, sin detracción, retención ni leyendas (ADR-046). Su adquirente puede tener RUC o estar en el exterior (a diferencia de 0200, 0201 y 0204).
- Son **solo de facturas**: al elegir *Boleta de venta* el formulario deja de ofrecerlos y, si uno estaba elegido, la operación vuelve a *Venta interna*.
- El huésped usa los tipos de documento de identidad del catálogo 06 que sirve la API (por defecto el pasaporte) y los países se escriben con **dos letras** (ISO 3166-1), como en el país del uso de ADR-046.
- Como en el transporte, un botón **«Usar … del ítem 1 en todos»** copia los datos (embarcación y especie, o huésped) a los demás ítems: un viaje de pesca o un huésped suele repetirse.
- El **detalle del comprobante** muestra, bajo cada ítem, la pesca (especie, toneladas, embarcación, descarga) o el huésped (y su permanencia).

### Vista previa
Sin cambios: el documento previsualizado conserva su detracción y su tipo de operación (ADR-047), de modo que el código 004 y los tipos 0202 y 0205 ya fijan en la vista previa los datos que cada ítem debe traer.

## Verificación
- Lo que cubre la API no cambia: las pruebas de pesca, hospedaje y paquete de ADR-030 y de `ExportApiTests` siguen siendo las que prueban las reglas.
- 5 pruebas unitarias nuevas (`operations.test.ts`, `LinesEditor.test.ts`): qué pide cada operación y cada código, 0202 y 0205 solo para facturas, la pesca que viaja solo con 004 y con la cantidad numérica, y el huésped del paquete sin la estadía y el del hospedaje con ella.
- 3 recorridos de extremo a extremo contra el simulador (`line-details.spec.ts`): una venta de pesca de dos ítems con los datos copiados del primero, el monto de detracción que da el servidor, emitida, mostrada y aceptada; un hospedaje con su estadía, que no se ofrece en boletas, emitido y aceptado; y un paquete turístico con el huésped solo. Accesibilidad (axe) de los formularios.

## Límites (P)
- La entrega inicial y la nota de débito con detracción se agregaron en el ADR-049: con él, todo lo que emite la API se emite desde la interfaz.
- Las operaciones del catálogo 51 que la API no soporta (por ejemplo 0301, 0302, 0401, 2001, 2002, 2100 a 2106) no están en el formulario porque no están en el producto; no es un límite de la interfaz.
- La interfaz no comprueba el contenido de la matrícula, de la especie ni de las fechas más allá de lo que el campo permite (largo, formato, que la salida no sea anterior al ingreso); no hay una lista pública de embarcaciones ni de especies que la plataforma pueda consultar.
