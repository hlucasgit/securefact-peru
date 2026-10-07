# ADR-030: Tipos de operación de detracción 1002, 1003 y 1004

- Estado: Aceptada · Fecha: 2026-10-02

## Fuentes
Hoja `Factura2_0` de las reglas de validación del 26.08.2026 (S16): reglas 3127–3129 (el tipo de operación 1001–1004 exige los términos de pago «Detraccion» y el código 004, 028 o 027 según el tipo), 3063 y 3130–3135 con 3115, 4280 y 4281 (recursos hidrobiológicos), 3116–3126 con 4200, 4236 y 4270 (transporte de carga), 3208 y 4265; catálogos 51 (1002 «Recursos Hidrobiológicos», 1003 «Servicios de Transporte Pasajeros», 1004 «Servicios de Transporte Carga»), 54 (004, 027, 028) y 55 (3001–3006). Prueba contra el beta del 2026-10-02.

## Decisión
- **El tipo de operación sigue al código de la detracción.** `OperationTypes.ForDetraction`: 004 → **1002**, 028 → **1003**, 027 → **1004**, cualquier otro código del catálogo 54 → 1001. El cliente puede pedir el tipo explícitamente (`operationTypeCode`), pero debe coincidir con el que corresponde al código; pedir un tipo 1001–1004 sin detracción también se rechaza. La hoja solo obliga al sentido «1002 ⇒ 004, 1003 ⇒ 028, 1004 ⇒ 027» (regla 3129); que 004, 027 y 028 no se usen con 1001 es una decisión de la plataforma, para que una venta de pesca o de transporte siempre lleve los datos que SUNAT exige en su tipo. El generador UBL repite la comprobación.
- **1003 (transporte de pasajeros)** no pide más datos: solo el tipo de operación y el código 028.
- **1002 (recursos hidrobiológicos)**: cada línea lleva `fishing` con matrícula de la embarcación (1–15 caracteres, código 3001), nombre (1–100, 3002), tipo de especie (1–150, 3003), lugar de descarga (1–100, 3004), fecha de descarga (3005) y cantidad en toneladas métricas (3006, mayor que cero, hasta 2 decimales, `unitCode` TNE). El UBL los emite como `cac:AdditionalItemProperty` del ítem con `NameCode` del catálogo 55, `Value`, `UsabilityPeriod/StartDate` (fecha) y `ValueQuantity` (cantidad).
- **1004 (transporte de carga)**: cada línea lleva `transport` con origen y destino (ubigeo de 6 dígitos y dirección de 3 a 200 caracteres), detalle del viaje (3 a 500) y los tres valores referenciales en soles, mayores que cero: del servicio (tipo 01), de la carga efectiva (02) y de la carga útil nominal (03). El UBL los emite en `cac:Delivery` de la línea, antes de los cargos y descuentos (orden UBL): `DeliveryLocation` (destino), `Despatch` (`Instructions` y `DespatchAddress` del origen) y tres `DeliveryTerms`.
- **Solo donde corresponde**: `fishing` solo con 1002, `transport` solo con 1004; en cualquier otra operación, en boletas o en notas se rechazan (`SF-BIL-006`). Los textos no admiten saltos de línea ni tabulaciones (reglas 4280, 4236 y 4270).
- **Persistencia**: como el resto de datos de línea, viven en la solicitud original (`StoredLine`) y se leen en `DocumentLineDto.Fishing` y `Transport`; el tipo de operación del documento leído se deduce del código de la detracción guardada.
- **PDF**: una línea de información adicional por ítem con los datos de pesca o de transporte (decisión de producto: el anexo no fija su ubicación).

## Verificado en el beta (2026-10-02)
Una factura de cada tipo, con detracción (cuenta de once ceros): 1002 con código 004 y los seis datos de la línea; 1003 con código 028; 1004 con código 027 y el viaje completo. Las tres aceptadas, código 0, sin observaciones.

## Límites (P)
- **Tramos y vehículos del transporte de carga**: ADR-034.
- La regla 4200 (ubigeo fuera del listado) no se comprueba más allá del formato de 6 dígitos: el catálogo 13 sembrado no trae el listado del INEI.
- Los porcentajes, montos y la cuenta siguen siendo datos del emisor (ADR-029); la plataforma no trae la tabla de porcentajes por bien ni los valores referenciales del anexo del D. S. 010-2006-MTC.
- Sin detracción en boletas ni en notas de crédito; la nota de débito sobre una factura puede llevarla, sin tipo de operación ni datos de línea (ADR-049).
