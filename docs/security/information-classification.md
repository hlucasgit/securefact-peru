# Clasificación de información

| Nivel | Ejemplos | Controles mínimos |
|-------|----------|-------------------|
| Secreto | PFX/claves privadas, contraseñas de PFX, Clave SOL, `client_secret` GRE, KEK | Cifrado de envoltura, acceso mínimo, nunca en logs, rotación, auditoría de acceso |
| Confidencial | CPE y CDR, datos de clientes del emisor, API keys (hash) | RLS por tenant, TLS, cifrado en reposo, retención legal |
| Interno | Métricas, configuración | Acceso autenticado |
| Público | Documentación de API | — |
