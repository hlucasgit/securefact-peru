# ADR-033: Exportación de servicios de hospedaje (0202) y paquete turístico (0205)

- Estado: Aceptada · Fecha: 2026-10-05

## Fuentes
Hoja `Factura2_0` de las reglas de validación del 26.08.2026 (S16): secciones «Información adicional - exportación de servicios de hospedaje» y «beneficio de hospedaje - paquete turístico» (reglas 3136–3145, 3065, 4235, 4280–4282, 4313, 4251–4253), 2642, 3107 y 2800; catálogos 51 (0202 y 0205: solo facturas), 55 (códigos 4000–4009), 4 y 6. Prueba contra el beta del 2026-10-05.

## Decisión
- **Solo facturas**: el catálogo 51 no admite 0202 ni 0205 en boletas; Billing y el generador los rechazan en boletas. Son exportaciones como las de ADR-031: líneas con afectación 40, tributo 9995, sin IGV, sin detracción ni retención.
- **El huésped va en cada línea** (`guest` en cada línea, `GuestDetail`): las reglas piden las propiedades del ítem en cada línea del documento. El UBL las emite como `cac:AdditionalItemProperty` del ítem con `NameCode` del catálogo 55 y el nombre del concepto del catálogo.
  - **Siempre (0202 y 0205)**: nombre (código 4007, 3 a 200 caracteres), tipo de documento (4008, catálogo 06), número de documento (4009, 3 a 20 caracteres) y país de emisión del pasaporte (4000, ISO 3166-1).
  - **Solo en 0202**: país de residencia (4001), fechas de ingreso al país (4002), de ingreso al establecimiento (4003), de salida (4004) y de consumo (4006), con `UsabilityPeriod/StartDate`, y días de permanencia (4005, hasta 4 dígitos, en `UsabilityPeriod/DurationMeasure` con `unitCode` DAY). La salida no puede ser anterior al ingreso (observación 4282, que la plataforma convierte en rechazo).
  - **0205**: no lleva los datos de la estadía; se rechazan si vienen.
- **Solo donde corresponde**: `guest` solo en 0202 y 0205; en cualquier otro tipo, en boletas o en notas se rechaza (`SF-BIL-006`, sin consumir número).
- **Adquirente**: la hoja no prohíbe el RUC en 0202 ni en 0205 (la regla 2800 de exclusión lista 0200, 0201 y 0204); se acepta un adquirente con RUC o del exterior, como en 0203, 0206, 0207 y 0208.
- **Persistencia y PDF**: como el resto de datos de línea, viven en la solicitud original (`StoredLine`) y vuelven en `DocumentLineDto.Guest`; el PDF imprime una línea de información adicional por ítem con el huésped y, en un hospedaje, su estadía (decisión de producto).

## Verificado en el beta (2026-10-05)
Una factura de cada tipo, en dólares y con el adquirente tipo 0: 0202 con las diez propiedades (4000–4009, fechas y días) y 0205 con las cuatro del huésped. Las dos aceptadas, código 0, sin observaciones.

## Límites (P)
- **Tipo de documento del huésped**: se acepta uno de los tipos del catálogo 06 que la plataforma maneja (0, 1, 4, 6, 7, A); la hoja pide el catálogo completo (observación 4280).
- **Código de país**: solo el formato (dos letras mayúsculas); el catálogo 4 sembrado no trae la lista ISO 3166-1. Es válido para el pasaporte y la residencia cualquier código, `PE` incluido: la hoja no lo prohíbe.
- Coherencia de los días de permanencia con las fechas, y de las fechas con la fecha de emisión: la hoja no las valida y la plataforma tampoco.
- Que el servicio califique como exportación de hospedaje o de paquete turístico lo declara el emisor (como en ADR-031); las fuentes del proyecto no traen esos requisitos.
