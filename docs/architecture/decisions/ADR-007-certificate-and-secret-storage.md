# ADR-007: Almacenamiento de certificados y secretos

- Estado: Aceptada · Fecha: 2026-09-30

## Contexto
Hay secretos de alto valor: PFX/claves privadas, contraseñas de PFX, Clave SOL (usada en WS-Security) y `client_secret` GRE, tokens de PSE/OSE, secretos HMAC de webhooks. Un compromiso permite emitir documentos a nombre de los contribuyentes.

## Decisión
- **Cifrado de envoltura**: cada secreto se cifra con una DEK AES-256-GCM única; la DEK se cifra (wrap) con una KEK gestionada por `IKeyEncryptionProvider`. El registro guarda `ciphertext`, `wrapped_dek`, `kek_id`, `version`.
- Implementaciones de `IKeyEncryptionProvider`: `LocalDevKeyProvider` (clave desde configuración, **solo** Local/Test, rechazada en Production), y adaptadores posteriores para AWS KMS, Azure Key Vault, GCP KMS y HashiCorp Vault. Rotación = re-wrap de DEKs con la KEK nueva.
- Interfaces de dominio: `ICertificateStore` (alta, metadatos, expiración, cadena, rotación) e `IDigitalSignatureProvider` (firma sin exponer la clave: el proveedor firma el digest o ejecuta XMLDSig dentro del límite del proveedor; permite HSM futuro).
- El PFX **nunca** sale por la API; las contraseñas nunca se registran; no se versionan certificados en el repositorio (`.gitignore` bloquea `*.pfx`, `*.p12`, `*.key`, `.env`).
- Alerta de vencimiento de certificados (30/15/7 días) y bloqueo de emisión con certificado vencido o no vigente.
- Los secretos de aplicación (cadena de conexión, claves) vienen de variables de entorno / gestor de secretos, no de archivos versionados.

## Consecuencias
+ Un volcado de BD no revela secretos; rotación sin downtime.
− Complejidad inicial; justificada por el riesgo. Escaneo de secretos en CI (gitleaks).
