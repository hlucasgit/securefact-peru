# ADR-015: Catálogos oficiales versionados y contenido del QR

- Estado: Aceptada · Fecha: 2026-10-01

## Catálogos
- **Fuente única**: la hoja `Catálogos` (Anexo N.°8) del libro oficial de reglas de validación, guardado byte a byte en `docs/regulatory/assets/` con su SHA-256 (S16).
- **Importador reproducible**: `tools/SecureFact.CatalogImporter` convierte el libro en `Seeds/sunat-catalogs-2026-08-26.json` (42 catálogos, 794 códigos). Una prueba exige que el JSON versionado sea **idéntico** a lo que produce el importador con el libro almacenado, y que el libro tenga el hash registrado: la fuente y la semilla no pueden divergir en silencio.
- **Esquema `catalog`** (datos de referencia globales, no por tenant): `catalog_edition` (una por catálogo y fuente, con `source_sha256`) y `catalog_entry` (`code`, `description`, `version`, `effective_from`, `effective_to`, `source`, `active`, `metadata`). RLS `EnableGlobalReference`: lectura para todos; escritura solo con ámbito de plataforma y el rol de aplicación **no** tiene privilegios de escritura (cargar es operación del dueño del esquema, `migrate`).
- **Versionado** (ADR-008): cargar el mismo archivo es un no-op (hash); una fuente nueva crea la versión N+1, vigente desde la fecha del libro, y cierra la anterior el día previo. Consultas "a una fecha" devuelven el código vigente ese día, de modo que documentos históricos se evalúan con el catálogo de su época. La primera edición se declara vigente desde `1900-01-01` porque la fuente no publica la fecha de inicio de cada código.
- **API**: `GET /api/v1/catalogs` y `GET /api/v1/catalogs/{número}?asOf=` (autenticado, cualquier rol).
- **Anti-deriva**: pruebas comprueban que las afectaciones del IGV del TaxEngine son **exactamente** el catálogo 07, que los códigos de tributo existen en el 05, los tipos de identidad del comprador en el 06 y los tipos de documento y leyendas usados en los 01 y 52.
- Algunos catálogos (moneda, unidad de medida, país, ubigeo, producto SUNAT) remiten a listas externas (ISO 4217, UN/ECE, ISO 3166, INEI, UNSPSC) y en la hoja solo traen una nota: quedan registrados pero sin lista completa; se cargarán de su fuente cuando se necesiten (R-023, ubigeo).

## QR
`IQrPayloadGenerator` (módulo `CpeEngine`) produce el texto del QR según el Anexo N.° 6 §6.4.3 (S19): `RUC|TIPO|SERIE|NÚMERO|IGV|TOTAL|FECHA|TIPO DOC ADQ|NÚM DOC ADQ|VALOR RESUMEN`, importes con 2 decimales y punto, fecha `YYYY-MM-DD`, valor resumen en Base64 (`ds:DigestValue`). Rechaza `|` y caracteres de control para impedir inyección de campos. El renderizado (QR Code 2005, nivel Q, UTF-8, ≤ 6 × 6 cm, zona de silencio 1 mm) se hará con el PDF en la Fase 3. Suposición abierta (R-018): IGV ausente = `0.00` y campos de adquirente vacíos cuando no existen.
