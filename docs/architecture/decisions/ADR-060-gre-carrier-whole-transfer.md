# ADR-060: Traslado total de bienes en la guía del transportista

- Estado: Aceptada · Fecha: 2026-10-10
- Desarrolla: R-078 de la matriz (fuente S27, hoja `Guía-Transportista2_0`, reglas 3344, 3435, 3458, 4429, 4430 y 4434).
- Completa: ADR-057, que dejó fuera el indicador de traslado total de bienes y la anotación de la línea «0».

## Contexto
Cuando el transportista lleva **todos** los bienes de un comprobante (una factura, una boleta…) no tiene sentido que los liste otra vez: la hoja de SUNAT prevé el indicador `SUNAT_Envio_IndicadorTrasladoTotal` en el traslado. Lo que cambia es qué se exige según el comprobante, porque SUNAT solo conoce los bienes de las facturas y liquidaciones de compra **electrónicas**:

| Comprobante relacionado | Los bienes | Línea de anotación «0» |
|---|---|---|
| Factura (`01`) o liquidación de compra (`04`) de serie que no empieza con número (F…, L…, E001) | los tiene SUNAT: **no se informan** (4434) | no existe (3458) |
| Boleta (`03`), ticket (`12`), comprobante de la Ley 29972 (`48`), o factura o liquidación de serie numérica | SUNAT no los tiene; se pueden listar | **obligatoria** (4429), de 3 a 500 caracteres (4430) |

La anotación «0» no puede existir en ningún otro caso (3458). Esto obligó a corregir ADR-057: la línea de forma que el esquema pide cuando no hay bienes **no** puede llevar el orden «0», porque con una guía del remitente relacionada la regla 3458 la habría observado.

## Decisión
- `CreateGreCarrierRequest` gana `WholeTransfer` y `WholeTransferNote`.
- El validador (`CarrierGoods`) aplica la tabla: el traslado total necesita un comprobante relacionado de los cinco tipos (regla propia `SF`, derivada de las condiciones de 3435, 4429 y 4434, que son las únicas que lo nombran); sin traslado total sigue valiendo «al menos un bien, o una guía del remitente que los lista». Una guía del remitente `T…` relacionada y el traslado total pueden convivir.
- El XML escribe el indicador entre los demás (`cbc:SpecialInstructions`), los bienes que haya (orden 1, 2…) y, si hace falta, la línea «0» con la anotación como descripción. Si no hay ni bienes ni anotación, una **línea de forma de orden 1** sin cantidad (descrita como «Bienes según …»), que no es un bien con cantidad (4434) ni la anotación (3458). Esa misma línea de forma reemplaza a la «0» de ADR-057 para la guía del remitente relacionada.
- Pantalla: una casilla «Se trasladan todos los bienes del comprobante relacionado» con la anotación; con ella los bienes son opcionales. La hoja impresa dice «Traslado total de los bienes del documento relacionado …» y la anotación.

## Verificación
- Unidad (`GreCarrierTests`): cada fila de la tabla con sus códigos, la anotación de 3 a 500 caracteres, el indicador sin comprobante, el XML de los tres casos contra el XSD oficial y el orden de las líneas.
- API (`GreCarrierApiTests`, PostgreSQL): factura aceptada con su indicador y su hoja, boleta sin anotación devuelta con el 4429 y con anotación emitida, con la anotación en el XML y en la hoja.
- Web: `carrierGuide.test.ts` y un recorrido de extremo a extremo con una boleta.

## Límites (P)
- **Sin SUNAT real** (ADR-056). La línea de forma de orden 1 sin cantidad es nuestra elección para cumplir el esquema sin romper 3458 y 4434; la hoja solo la prescribe, con otro orden, para la orden de entrega `92` del remitente.
- El traslado total de las declaraciones aduaneras (`50` y `52`) no se admite en la guía del transportista (ADR-057).
- Que el comprobante exista y que sea del remitente (3380, 3408, 4382, 3207) lo contesta SUNAT.
- Una factura relacionada de serie numérica se reconoce por que el número empieza con un dígito, que es como la hoja lo dice («serie que empieza con número»).
