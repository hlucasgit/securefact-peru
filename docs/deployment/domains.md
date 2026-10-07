# Dominios de los revendedores: operación y alta

Decisión y razones: ADR-051. Este documento es lo que hace quien opera la plataforma y lo que hace un revendedor.

## Cómo funciona
1. La **plataforma asigna** un dominio al revendedor (por ejemplo `portal.ejemplo.pe`) desde *Revendedores → Marca → Dominio del portal*. El dominio nace **pendiente**.
2. La pantalla del revendedor (*Marca → Dominio del portal*) le dice **qué dos registros crear en el DNS de su dominio**:

   | Registro | Nombre | Valor | Para qué |
   |---|---|---|---|
   | `TXT` | `_securefact-challenge.portal.ejemplo.pe` | `securefact-verification=<48 caracteres hexadecimales>` | prueba de que controla el dominio |
   | `CNAME` | `portal.ejemplo.pe` | el valor de `Domains:EdgeHost` (por ejemplo `edge.securefact.pe`) | lleva el dominio al *edge* de la plataforma |

   Si el nombre no puede tener un `CNAME` (la raíz de una zona, `ejemplo.pe`), sirven registros `A` o `AAAA` con las direcciones de `Domains:EdgeAddresses`.
3. Los workers revisan el DNS **cada pocos minutos** (`Domains:PendingRecheckMinutes`), y el revendedor o la plataforma pueden pedirlo ya con **Verificar ahora**. Cuando ambos registros están bien, el dominio pasa a **verificado**.
4. **Verificado** significa dos cosas: el portal **muestra la marca** del revendedor en ese dominio (ADR-044) y el *edge* **puede pedir un certificado** para el nombre. El certificado se emite **solo, la primera vez que alguien entra** al dominio.
5. La plataforma **vuelve a revisar** un dominio verificado cada `Domains:SettledRecheckMinutes` (6 horas). Si falla **tres veces seguidas** (`Domains:UnreachableAfterFailures`) pasa a **sin respuesta**: deja de mostrar la marca y de renovar el certificado, y **se recupera solo** cuando el DNS vuelve a estar bien. Un fallo suelto no baja el portal.

## Configuración del operador (sección `Domains`)
| Clave | Qué es | Por defecto |
|---|---|---|
| `Domains:Dns:Provider` | `System` (consultas reales) o `Sandbox` (todo pasa; solo desarrollo y pruebas, **se rechaza en producción**) | `System` |
| `Domains:Dns:Nameservers` | resolvedores a consultar (direcciones IP); vacío = los de la máquina | vacío |
| `Domains:EdgeHost` | el nombre al que los dominios deben ser alias (CNAME): el *edge* | vacío |
| `Domains:EdgeAddresses` | direcciones del *edge*, para los dominios que no admiten CNAME | vacío |
| `Domains:PlatformHosts` | hosts propios de la plataforma: siempre tienen certificado y ningún revendedor puede tomarlos | vacío |
| `Domains:EdgeSecret` | secreto con el que el *edge* pregunta qué nombres pueden tener certificado (**de entorno, nunca en el repositorio**) | vacío: no se contesta |
| `Domains:PendingRecheckMinutes` / `SettledRecheckMinutes` | cada cuánto revisa el worker un dominio pendiente y uno verificado | 2 / 360 |
| `Domains:UnreachableAfterFailures` | fallos seguidos para que un verificado pase a sin respuesta | 3 |
| `Domains:MinimumCheckSeconds` | tiempo mínimo entre dos revisiones del mismo dominio (`SF-DOM-002`) | 10 |

Sin `EdgeHost` ni `EdgeAddresses`, **ningún dominio puede verificarse** (la pantalla lo dice): hay que decir a dónde deben apuntar.

## El *edge* (certificados por dominio)
`deploy/edge/Caddyfile` y `docker-compose.edge.yml` ponen un **Caddy** delante de la API y de la web, con **TLS bajo demanda**:
- El certificado de un nombre se pide la primera vez que se ve el nombre, **pero antes Caddy pregunta a la API** (`GET /api/v1/edge/tls-allowed?domain=…&secret=…`). La API contesta 200 solo si el nombre es **un host de la plataforma** o el **dominio verificado de un revendedor que está activo**; si no, 404 y no hay certificado. Sin esa pregunta, cualquiera podría apuntar un nombre cualquiera al *edge* y gastar los límites de la autoridad de certificación.
- La pregunta **solo se contesta con el secreto** y **el *edge* no reenvía esa ruta al exterior** (`respond @internal 404`): hacen falta las dos cosas.
- Arranque: `docker compose -f docker-compose.yml -f docker-compose.edge.yml up -d`, con `SF_EDGE_SECRET` y `SF_ACME_EMAIL` en `.env`, los puertos 80 y 443 abiertos y la API con `SF_DOMAINS_DNS_PROVIDER=System`.

> **Estado de esta configuración.** El `Caddyfile` y el *compose* del *edge* **no se ejecutaron**: se escribieron a partir de la documentación de Caddy y no se descargó la imagen para validarlos. **Antes de usarlos**, ejecute `caddy validate --config deploy/edge/Caddyfile` y pruebe con un dominio de prueba contra el entorno de pruebas de la autoridad (Let's Encrypt *staging*, `acme_ca` en el bloque global). Lo que sí está probado es la parte de la API: la verificación, los estados y la respuesta al *edge* (`DomainsApiTests`).

## Qué dice la pantalla cuando algo falla
| Mensaje | Causa |
|---|---|
| «No se encontró el registro TXT `_securefact-challenge.…` con el valor …» | el TXT no existe, tiene otro valor o el DNS aún no lo publicó |
| «… no apunta a la plataforma: debe tener un CNAME hacia … Hoy lleva …» | el nombre apunta a otro lado (o a ningún lado) |
| «No se pudo consultar el DNS en este momento» | el resolvedor no contestó: se reintenta solo |
| «El operador … no configuró a dónde debe apuntar el dominio» | falta `Domains:EdgeHost` o `Domains:EdgeAddresses` |
| `SF-DOM-002` | se revisó hace menos de `MinimumCheckSeconds` |

## Seguridad
- La **prueba por TXT** impide que alguien presente como suyo un dominio que no controla (por ejemplo, un subdominio olvidado que apunta a la plataforma): sin el TXT no hay marca ni certificado.
- Los dominios los **asigna solo el personal de la plataforma**; el revendedor solo los ve y los revisa. Un nombre es de **un solo revendedor**, y los nombres de la plataforma no se pueden asignar.
- Cambiar el dominio **reinicia todo**: otra prueba, otro estado pendiente, y el nombre anterior deja de ser del revendedor (sin marca ni certificado nuevo).
- Un revendedor **desactivado** no recibe certificados nuevos y sus workers no lo revisan; su portal vuelve a la apariencia por defecto (ADR-044).
- Cada cambio de dominio, cada verificación y cada pérdida queda en la auditoría (`tenancy.reseller.domain_changed`, `.domain_verified`, `.domain_unreachable`).

## Límites
- **Sin comodines** (`*.ejemplo.pe`): un dominio es un nombre. Cada subdominio de un revendedor es un dominio aparte.
- **Sin DNSSEC** propio: se consulta lo que el resolvedor da por bueno.
- Los **límites de la autoridad** de certificación (por ejemplo, Let's Encrypt limita los certificados por dominio registrado y por semana) siguen aplicando: muchos dominios nuevos de un mismo revendedor en una semana pueden no obtener certificado hasta que pase el límite.
- La revisión es **de DNS**: no comprueba que el *edge* responda con un certificado válido en ese nombre.
