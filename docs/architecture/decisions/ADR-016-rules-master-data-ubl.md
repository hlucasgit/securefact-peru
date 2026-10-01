# ADR-016: Reglas servidas, datos maestros y generador UBL

- Estado: Aceptada · Fecha: 2026-10-01

## Reglas con vigencia servidas por `IRuleProvider` (módulo `Rules`)
- Esquema `rules.rule_version` (código, versión, vigencia, `configuration` JSON, fuente, **verificación `Verified`/`Pending`**). Una y solo una versión está vigente en una fecha; si hay dos o ninguna, error explícito (nunca se adivina). Las versiones publicadas son **inmutables**: la semilla se rechaza si cambia el contenido de una (código, versión) ya cargada; un cambio normativo es una versión nueva con su fecha, y la anterior se cierra el día previo.
- Reglas iniciales: `tax.igv.rate` 18 % y `tax.igv.reduced_rate` 10,5 % (**Verified**, S16), `tax.ivap.rate` 4 % y `tax.icbper.unit_amount` S/ 0,50 (**Pending**: la hoja oficial define la fórmula, no el valor), `billing.issue_date_max_age_days` 3 días (**Pending** para boletas). `GET /api/v1/rules` expone qué valores aplica la plataforma y cuán verificados están.
- **Billing ya no acepta tasas del cliente**: IGV, IVAP e ICBPER se resuelven por la fecha de emisión; una prueba envía tasas falsas y comprueba que se ignoran. La tasa reducida (10,5 %) requiere el padrón del emisor y todavía no se ofrece.

## Datos maestros
- `Customers` y `Products`: por tenant (RLS), identidad inmutable (documento de identidad / código interno), validación contra catálogos oficiales (06 y 07), nunca se borran (sin privilegio `DELETE`), búsqueda con comodines escapados (`ILIKE` con carácter de escape explícito: el escape por defecto de EF no funcionaba).
- `IdentityDocuments` (SharedKernel) concentra las reglas estructurales de los tipos de identidad del catálogo 06; Billing y Customers las comparten.
- Un documento puede referenciar `customerId` o llevar el `buyer` en línea (nunca ambos); el adquirente se copia al documento como **instantánea**, de modo que editar el cliente no altera lo emitido.

## Generador UBL (CpeEngine)
- `IUblDocumentGenerator` produce el XML **sin firmar** de factura (01) y boleta (03) a partir de un modelo canónico y del resultado del TaxEngine (los importes del XML son exactamente los calculados). Alcance: líneas gravadas, exoneradas, inafectas y gratuitas sin descuentos/cargos de línea ni globales; IVAP, ISC, ICBPER, exportación, descuentos, cargos y redondeo devuelven `SF-CPE-002` en vez de emitir XML engañoso.
- Estructura tomada de las etiquetas obligatorias de las hojas `Factura2_0`/`Boleta2_0` del libro del 26.08.2026 (S16) y de la guía XML (S18): categoría de impuesto S/E/O/Z (UN/ECE 5305), esquema 1000/9997/9998/9996 con nombre y código internacional del catálogo 05, tipo de operación (catálogo 51) en `InvoiceTypeCode@listID`, domicilio/establecimiento en `PartyLegalEntity/RegistrationAddress`.
- **Pruebas**: el XML valida contra el XSD oficial UBL 2.1 `Invoice` (los de `docs/regulatory/assets`, idénticos a los de SUNAT); **cada etiqueta obligatoria de ambas hojas del libro está presente** (la prueba lee el xlsx versionado, de modo que un cambio de reglas la rompe); nombres y códigos de tributo coinciden con el catálogo 05. La aceptación real por SUNAT solo se puede demostrar contra su servicio beta.
- Pendiente: firma XMLDSig (hoja `Firma`, 594 reglas; el ejemplo de la guía usa RSA-SHA1/SHA-1 de 2017, **algoritmos vigentes por confirmar**), ZIP y envío, parser de CDR, leyenda 1000 (monto en letras, opcional), categoría de exportación (letra no confirmada en la guía), descuentos/cargos con `AllowanceCharge`, codificación (la guía de ejemplo usa ISO-8859-1; se emite UTF-8, por confirmar).
