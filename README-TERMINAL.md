# berllanoWorker — Terminal Banorte/Payworks

Guía práctica del worker de terminal. Todos los ejemplos son **requests y
respuestas reales** capturados el 12-nov-2026 en la PC de pruebas
`WIN-81AVR1HMBNE` (`192.168.100.54`), contra el pinpad `192.168.100.81:1818`,
sucursal `97` ("PRUEBAS 2"), con la build `f282308` (la última del repo).

## 1. Ambiente de pruebas

| Dato | Valor real |
|---|---|
| PC del worker | `WIN-81AVR1HMBNE` — LAN `192.168.100.54` |
| Puerto HTTP | `5000` (servicio `berllanoWorker`) |
| Pinpad | `192.168.100.81:1818` (ingenico Payworks) |
| Sucursal | `97` = "PRUEBAS 2" |
| Cajas con terminal | `1` y `2` (ambas `AUT` = sin cargo real) |
| Afiliación | `8626674` |
| WorkerKey | la del `appsettings.json` instalado en `C:\berllanoWorker` |

> `AUT` = modo pruebas, **no genera cargo real**. `PRD` = producción.
> La caja 1 de la sucursal 97 está en `AUT`; verificar el modo en
> `GET /api/terminal/terminales` antes de operar.

## 2. Cómo funciona (30 segundos)

```
PWA / Postman / curl
   └─► POST http://<pc-caja>:5000/api/terminal/*     (header X-Worker-Key)
        └─► berllanoWorker  (servicio Windows, C:\berllanoWorker)
             ├─► lee credenciales de cberp:
             │    t_banorte_usuarios  → usuario/pass/afiliación/modo
             │    cat_RecursosWorker  → IP del pinpad ('TerminalBanorte')
             │    appsettings.json    → fallback local de la PC
             └─► C:\asesor\BanortePOS\BanorteWorker.exe → pinpad → Payworks
```

Reglas para saber **a qué pinpad y con qué credenciales** pega una petición:

1. `sucursal`/`caja` vienen del **body** (tienen prioridad) o de los headers
   `X-Sucursal` / `X-Caja`.
2. La fila de `t_banorte_usuarios` de esa `(sucursal, caja)` define
   usuario, password, afiliación y modo. Si no hay fila y la caja es la
   **local de esa PC** (`SucursalDefault`/`CajaDefault` del appsettings),
   usa las credenciales `TerminalBanorte` del `appsettings.json`; si no
   hay fila y no es la caja local → **422**.
3. La **IP del pinpad** NO está en `t_banorte_usuarios` (`id_terminal` es el
   T-ID de Payworks, otra cosa). Sale del recurso `'TerminalBanorte'` en
   `cat_RecursosWorker` (`NombreDispositivoLocal` = IP); si no hay fila,
   cae al `TerminalBanorte:IpTerminal` del appsettings.

## 3. Endpoints — tabla rápida

Todo bajo `http://<pc>:5000`. `/api/sistema/*` no pide key; el resto sí
(`X-Worker-Key`, si falta o es incorrecta → **401**).

| Método | Ruta | Qué hace |
|---|---|---|
| GET | `/api/sistema/identidad` | Health + datos de la PC (hostname, IPs, versión) |
| GET | `/api/sistema/cajas?sucursal=N` | Por caja: impresoras + terminal + IP pinpad |
| GET | `/api/terminal/cajas?sucursal=N` | Filas de `t_banorte_usuarios` (sin password) |
| GET | `/api/terminal/terminales?sucursal=N&verificarPing=true` | Terminales configuradas + `online` real (ping TCP) |
| POST | `/api/terminal/llaves` | Carga de llaves (CARGAR_LLAVE, sin cargo) |
| POST | `/api/terminal/cobro` | Cobro / cashback (`monto_retiro>0`) + post-venta |
| POST | `/api/terminal/retiro` | Retiro de efectivo con cargo a venta |
| POST | `/api/terminal/cancelacion` | Reset de sesión o devolución financiera real |
| POST | `/api/terminal/verificar` | Consulta el estado de un `numero_control` en Payworks |
| POST | `/api/terminal/imprimir-voucher` | Reimprimir voucher (hoy es stub, ver §4.10) |

### Curls reales (sucursal 97, caja 1 — copiar y pegar)

```powershell
$B = "http://localhost:5000"
$K = "b740ecedf34c4cd7885826521ce8ff27"   # WorkerKey real de esta PC de pruebas

# --- Consultas (no tocan el pinpad) ---
curl "http://localhost:5000/api/sistema/identidad"
curl "http://localhost:5000/api/sistema/cajas?sucursal=97" -H "X-Worker-Key: b740ecedf34c4cd7885826521ce8ff27"
curl "http://localhost:5000/api/terminal/cajas?sucursal=97" -H "X-Worker-Key: b740ecedf34c4cd7885826521ce8ff27"
curl "http://localhost:5000/api/terminal/terminales?sucursal=97&verificarPing=true" -H "X-Worker-Key: b740ecedf34c4cd7885826521ce8ff27"

# --- Operaciones (SI tocan el pinpad; modo AUT = sin cargo real) ---
curl -X POST "http://localhost:5000/api/terminal/llaves" -H "X-Worker-Key: b740ecedf34c4cd7885826521ce8ff27" -H "Content-Type: application/json" `
  -d '{"sucursal":97,"caja":1}'

# cobro post-venta (el caso real): cobra, registra en cberp (no_venta)
# e imprime voucher en la impresora 'Tickets' de la caja
curl -X POST "http://localhost:5000/api/terminal/cobro" -H "X-Worker-Key: b740ecedf34c4cd7885826521ce8ff27" -H "Content-Type: application/json" `
  -d '{"monto":1500.50,"referencia":"REF-VTA99231","no_venta":99231,"usuario_pos":"ADMIN","corte":1,"corte_parcial":1,"efectivo":0,"pago_combinado":"","imprimir_voucher":true,"sucursal":97,"caja":1}'

# cobro con retiro de efectivo (cashback): cobra $monto y entrega $monto_retiro
curl -X POST "http://localhost:5000/api/terminal/cobro" -H "X-Worker-Key: b740ecedf34c4cd7885826521ce8ff27" -H "Content-Type: application/json" `
  -d '{"monto":1600.00,"referencia":"REF-CASHBACK","no_venta":99232,"monto_retiro":100.00,"sucursal":97,"caja":1}'

curl -X POST "http://localhost:5000/api/terminal/retiro" -H "X-Worker-Key: b740ecedf34c4cd7885826521ce8ff27" -H "Content-Type: application/json" `
  -d '{"monto":500.00,"monto_venta":0,"sucursal":97,"caja":1}'

# reset de sesion (transaccion_id generico)
curl -X POST "http://localhost:5000/api/terminal/cancelacion" -H "X-Worker-Key: b740ecedf34c4cd7885826521ce8ff27" -H "Content-Type: application/json" `
  -d '{"transaccion_id":"RESET-97","sucursal":97,"caja":1}'

# devolucion financiera real (ref Payworks de 12 digitos + monto + no_venta)
curl -X POST "http://localhost:5000/api/terminal/cancelacion" -H "X-Worker-Key: b740ecedf34c4cd7885826521ce8ff27" -H "Content-Type: application/json" `
  -d '{"transaccion_id":"732308566427","monto":1.00,"no_venta":999999,"sucursal":97,"caja":1}'

curl -X POST "http://localhost:5000/api/terminal/verificar" -H "X-Worker-Key: b740ecedf34c4cd7885826521ce8ff27" -H "Content-Type: application/json" `
  -d '{"numero_control":"99999999Z","sucursal":97,"caja":1}'

curl -X POST "http://localhost:5000/api/terminal/imprimir-voucher" -H "X-Worker-Key: b740ecedf34c4cd7885826521ce8ff27" -H "Content-Type: application/json" `
  -d '{"transaccion_id":"TX-1","no_venta":999999,"sucursal":97,"caja":1}'
```

## 4. Ejemplos reales

Los comandos usan `http://localhost:5000` y `b740ecedf34c4cd7885826521ce8ff27` definidos en §3. Cada ejemplo muestra el
request y la **respuesta real** capturada de esta máquina.

### 4.1 Salud del worker — `GET /api/sistema/identidad` (sin key)

```powershell
curl "http://localhost:5000/api/sistema/identidad"
```

```json
{"hostname":"WIN-81AVR1HMBNE","hostMdns":"win-81avr1hmbne.local",
 "ipsLan":["192.168.100.54"],"puerto":5000,
 "version":"1.0.0+f2823086cb0272965078ca225ae9fc2747ff2829","status":"ONLINE"}
```

### 4.2 Diagnóstico por caja — `GET /api/sistema/cajas?sucursal=97`

```powershell
curl "$B/api/sistema/cajas?sucursal=97" -H "X-Worker-Key: b740ecedf34c4cd7885826521ce8ff27"
```

Respuesta real (un objeto por caja con terminal configurada):

```json
[{"caja":1,
  "impresoras":[
    {"recurso":"Tickets","nombreDispositivoLocal":"prueba","instaladaLocal":true},
    {"recurso":"Documentos","nombreDispositivoLocal":"Microsoft Print to PDF","instaladaLocal":true}],
  "terminal":{"cia":1,"sucursal":97,"caja":1,"idTerminal":"PENDIENTE",
    "puerto":"1818","afiliacion":"8626674","usuario":"a8626674","modo":"AUT","origen":"DB"},
  "ipPinpad":"192.168.100.81"},
 {"caja":2,
  "impresoras":[
    {"recurso":"Tickets","nombreDispositivoLocal":"AJUSTAR_NOMBRE_IMPRESORA_TICKETS","instaladaLocal":false},
    {"recurso":"Documentos","nombreDispositivoLocal":"Microsoft Print to PDF","instaladaLocal":true}],
  "terminal":{"cia":1,"sucursal":97,"caja":2,"idTerminal":"PENDIENTE",
    "puerto":"1818","afiliacion":"8626674","usuario":"a8626674","modo":"AUT","origen":"DB"},
  "ipPinpad":"192.168.3.167"}]
```

- `ipPinpad` = IP que resuelve `cat_RecursosWorker` para esa caja
  (`null` si la caja no tiene recurso `'TerminalBanorte'` — le pasa a la
  sucursal 1).
- `instaladaLocal` = la impresora existe en Windows **de esta PC**.
- `origen` = `"DB"` (las listas solo leen `t_banorte_usuarios`; el flag
  `"+override"` aplica a credenciales resueltas, no a estos listados).

### 4.3 Solo terminales — `GET /api/terminal/cajas?sucursal=97`

```powershell
curl "$B/api/terminal/cajas?sucursal=97" -H "X-Worker-Key: b740ecedf34c4cd7885826521ce8ff27"
```

```json
[{"cia":1,"sucursal":97,"caja":1,"idTerminal":"PENDIENTE","puerto":"1818",
  "afiliacion":"8626674","usuario":"a8626674","modo":"AUT","origen":"DB"},
 {"cia":1,"sucursal":97,"caja":2,"idTerminal":"PENDIENTE","puerto":"1818",
  "afiliacion":"8626674","usuario":"a8626674","modo":"AUT","origen":"DB"}]
```

Es la misma info de `terminal` del endpoint anterior, una fila por caja.
Sin `pass_banorte` (nunca se expone).

### 4.4 Terminales con estado online — `GET /api/terminal/terminales` *(nuevo)*

```powershell
curl "$B/api/terminal/terminales?sucursal=97&verificarPing=true" -H "X-Worker-Key: b740ecedf34c4cd7885826521ce8ff27"
# verificarPing=false -> no hace el ping TCP, responde más rápido
```

Respuesta real:

```json
[{"sucursal":97,"caja":1,"ipTerminal":"192.168.100.81","puerto":"1818",
  "afiliacion":"8626674","usuario":"a8626674","modo":"AUT","online":true},
 {"sucursal":97,"caja":2,"ipTerminal":"192.168.3.167","puerto":"1818",
  "afiliacion":"8626674","usuario":"a8626674","modo":"AUT","online":false}]
```

- `online` = ping TCP real a `ip:puerto` (timeout 800ms, en paralelo).
- Sin `sucursal` lista **todas** las terminales de la empresa (en esta base
  hay 11: sucursales 1-7, 97 x2, 99 y 203).
- `ipTerminal` = recurso `'TerminalBanorte'` de esa caja; si no existe,
  el `IpTerminal` del appsettings local (por eso todas pueden mostrar la
  misma IP de respaldo).

### 4.5 Cargar llaves — `POST /api/terminal/llaves`

```powershell
curl -X POST "$B/api/terminal/llaves" -H "X-Worker-Key: b740ecedf34c4cd7885826521ce8ff27" -H "Content-Type: application/json" -d '{"sucursal":97,"caja":1}'
```

Respuesta real **HTTP 200** (~7s):

```json
{"aprobada":true,"estado":"A","codigoRespuesta":"00",
 "mensajeRespuesta":"Llave obtenida","sucursal":97,"caja":1,
 "ipTerminal":"192.168.100.81","afiliacion":"8626674",
 "numControl":"KEY20261112140534","uuidSesion":"UUID-KEY-20261112140534",
 "monto":0.00,"voucherTexto":"Llave obtenida",
 "rawResponse":"A||Llave obtenida|||...","ultimaTraza":"..."}
```

- Es **silenciosa en el pinpad** (no cambia su pantalla). Si está ocupada →
  `E|-1016` → **503**; reintentar con la pantalla en espera.
- Devuelve el DTO completo de transacción (`referenciaPayworks`, `pan`,
  `voucherTexto`, `rawResponse`, `ultimaTraza`, etc.).

### 4.6 Cobro — `POST /api/terminal/cobro` *(cambió la respuesta)*

El caso real siempre va **con `no_venta`**: una sola llamada cobra en el
pinpad, registra el pago en cberp y manda el voucher a la impresora
(ver §5). Body de ejemplo:

```json
{"monto":1500.50,"referencia":"REF-VTA99231",
 "no_venta":99231,"monto_retiro":0,
 "usuario_pos":"ADMIN","corte":1,"corte_parcial":1,
 "efectivo":0,"pago_combinado":"","imprimir_voucher":true,
 "sucursal":97,"caja":1}
```

Variante **cashback**: `"monto_retiro":100.00` (>0) cambia la operación a
`CASHBACK` — cobra `monto` a la tarjeta y el pinpad entrega
`monto_retiro` en efectivo.

| Campo | Default | Para qué |
|---|---|---|
| `monto` | **obligatorio** | importe a cobrar ($0.01–$999,999.99) |
| `referencia` | `""` | referencia del cliente; vuelve en `referenciaCliente` (el `numControl` real lo genera el worker) |
| `no_venta` | `0` | folio de la venta en el ERP; `>0` activa el registro en cberp y el voucher |
| `monto_retiro` | `0` | `>0` → operación **CASHBACK** (cobra `monto` y entrega `monto_retiro` efectivo) |
| `usuario_pos`,`corte`,`corte_parcial`,`efectivo`,`pago_combinado` | — | contexto que se guarda en `detalle_ventas_pagos` |
| `imprimir_voucher` | `true` | imprimir el voucher por la impresora `Tickets` de la caja |
| `sucursal`,`caja`,`forzar_recarga` | — | selección de caja (body gana a headers) |
| `ipTerminal`,`puerto`,`usuario`,`password`,`modo`,`afiliacion` | — | overrides manuales (ganan sobre la DB; para pruebas) |

Respuesta real **HTTP 200** (cobro de $1.00 AUT aprobado, con `no_venta`
→ post-venta completo):

```json
{"aprobada":true,"estado":"A","codigoRespuesta":"00",
 "mensajeRespuesta":"Aprobada por Payworks modo prueba",
 "ventaInsertada":true,
 "mensajeInsercion":"Pago registrado en cberp (Secuencia: 1)",
 "ticketImpreso":true,
 "mensajeTicket":"Voucher enviado a impresora"}
```

Si el cobro va **sin `no_venta`**, la respuesta es la misma pero con
`ventaInsertada:false` / `"Omitido (no_venta no especificado)"` y
`ticketImpreso:false` / `"Omitido (...)"`.

> ⚠️ **La respuesta del cobro ahora es un RESUMEN de 8 campos.** Ya no
> regresa `referenciaPayworks`, `numControl`, `pan`, `emvAid`, etc. Si
> necesitas la referencia Payworks para una devolución, está en
> `cberp.dbo.detalle_ventas_pagos.ref_financiera` (se guarda cuando mandas
> `no_venta`).

### 4.7 Retiro con cashback — `POST /api/terminal/retiro`

```powershell
curl -X POST "$B/api/terminal/retiro" -H "X-Worker-Key: b740ecedf34c4cd7885826521ce8ff27" -H "Content-Type: application/json" `
  -d '{"monto":500.00,"monto_venta":0,"sucursal":97,"caja":1}'
```

- `monto` = **efectivo a entregar** al cliente; `monto_venta` = cargo a la
  venta (`0`/omitido → el cargo es igual a `monto`). `monto_venta > monto`
  cobra la compra y da vuelto en efectivo en una sola operación.
- Ejecuta `CASHBACK` directo (numControl con sufijo `C`); el pinpad pide
  tarjeta igual que un cobro.
- Devuelve el DTO completo de transacción (como llaves/cancelacion).

### 4.8 Cancelar / devolver — `POST /api/terminal/cancelacion`

**a) Reset de sesión** (cualquier `transaccion_id` no numérico):

```powershell
curl -X POST "$B/api/terminal/cancelacion" -H "X-Worker-Key: b740ecedf34c4cd7885826521ce8ff27" -H "Content-Type: application/json" `
  -d '{"transaccion_id":"RESET-97","sucursal":97,"caja":1}'
```

Respuesta real **HTTP 200**:

```json
{"aprobada":true,"estado":"OK","codigoRespuesta":"0",
 "mensajeRespuesta":"CANCELADO","sucursal":97,"caja":1,
 "ipTerminal":"192.168.100.81","afiliacion":"8626674",
 "numControl":"RESET-97","uuidSesion":"UUID-CAN-20261112140630",
 "rawResponse":"OK|0|CANCELADO|||...|TRACE||Operacion Cancelada",
 "ultimaTraza":"Operacion Cancelada"}
```

**b) Devolución financiera real** — `transaccion_id` = referencia Payworks
de **12 dígitos** + `monto` > 0:

```powershell
curl -X POST "$B/api/terminal/cancelacion" -H "X-Worker-Key: b740ecedf34c4cd7885826521ce8ff27" -H "Content-Type: application/json" `
  -d '{"transaccion_id":"732308566427","monto":1.00,"no_venta":999999,"sucursal":97,"caja":1}'
```

- Requiere `BanortePayworksWrapper.dll` en `C:\asesor\BanortePOS\`.
- Si además mandas `no_venta`, el worker **revierte en cberp**: marca el
  pago como `cancelada=1` en `detalle_ventas_pagos`; con
  `"reversa_venta":true` también revierte la venta completa
  (`detalle_ventas` + `detalle_ventas_medios_pagos`).

### 4.9 Verificar transacción — `POST /api/terminal/verificar` *(no estaba en la guía)*

```powershell
curl -X POST "$B/api/terminal/verificar" -H "X-Worker-Key: $K" -H "Content-Type: application/json" `
  -d '{"numero_control":"99999999Z","sucursal":97,"caja":1}'
```

Respuesta real **HTTP 400** (no existe en Payworks → `aprobada:false` → 400):

```json
{"aprobada":false,"estado":"T","codigoRespuesta":"T",
 "mensajeRespuesta":"Transaccion no existe en Payworks%7C",
 "numControl":"99999999Z","uuidSesion":"UUID-VER-...",
 "voucherTexto":"Transaccion no existe en Payworks%7C", "...":"..."}
```

Consulta el estado de un `numero_control` en Payworks (equivalente a la
validación `numControlOriginal=""` del VBA viejo). El `numero_control` es
el que genera el worker (`ddHHmmss` + `B`/`C`), no la referencia del
cliente.

### 4.10 Imprimir voucher — `POST /api/terminal/imprimir-voucher` *(stub)*

```powershell
curl -X POST "$B/api/terminal/imprimir-voucher" -H "X-Worker-Key: $K" -H "Content-Type: application/json" `
  -d '{"transaccion_id":"TX-1","no_venta":999999,"sucursal":97,"caja":1}'
```

Respuesta real **HTTP 200**:

```json
{"status":200,"mensaje":"Voucher enviado a cola de impresion"}
```

> ⚠️ **Por ahora es un stub**: siempre responde OK sin imprimir nada
> (`TODO` en `TerminalController`). La impresión real del voucher hoy
> sale por el flujo de §5 (`sp_ticket_banorte` → impresora `Tickets`).

## 5. Qué pasa después de un cobro (post-venta, nuevo)

Cuando el pinpad responde (aprobada o declinada), `POST /cobro` hace dos
cosas extra **solo si mandaste `no_venta > 0`**:

```
pinpad responde
   ├─► 1) EXEC sp_registra_pago_banorte2026   → inserta en detalle_ventas_pagos
   │      (tipo_pago=7, tarjeta enmascarada, ref_financiera, monto_retiro, etc.)
   │      y lee el numero_secuencia generado
   └─► 2) si imprimir_voucher=true y la venta se insertó:
          EXEC sp_ticket_banorte → regresa las líneas del voucher
          → se imprimen en la impresora 'Tickets' de la caja
            (cons 99 = corte de papel, cons 100 = apertura de cajón)
```

Fila real que quedó en `detalle_ventas_pagos` tras el cobro de prueba:

```
numero_secuencia=1  no_venta=999999  sucursal=97  caja=1
codigo_transaccion=12140910B   importe=1   ref_financiera=732308566427
aplicada=1  cancelada=0
```

Notas:

- Si el pinpad hace **timeout** (408) o está apagado (503), la excepción
  sale antes de estos bloques → **no se escribe nada en cberp**.
- Si el banco **declina** (`aprobada:false` → HTTP 400), el pago igual se
  registra con `aplicada=0` y el voucher sale como `DECLINADA`.
- Errores de SQL **no tumban el cobro**: se reportan en
  `ventaInsertada:false` / `mensajeInsercion` y `ticketImpreso:false` /
  `mensajeTicket` de la respuesta.

## 6. Errores HTTP (todos reales)

| HTTP | error | Cuándo | Ejemplo real |
|---|---|---|---|
| 200 | — | operación OK (o `aprobada:true`) | cobro aprobado |
| 400 | — | pinpad respondió pero `aprobada:false` (declinada, cancelada, "no existe") | verificar de control inexistente |
| 401 | — | falta o `X-Worker-Key` incorrecta | cualquier `/api/*` sin header |
| 408 | `HardwareTimeoutException` | el cliente no interactuó ~30s en el pinpad | `"La terminal reporto timeout: CANCELADO POR TIMEOUT EN LA PANTALLA DE CAPTURA DE TARJETA"` |
| 422 | `HardwareValidationException` | datos inválidos o caja sin terminal | `"El monto debe ser mayor a $0.00 y no exceder $999,999.99."` |
| 423 | `HardwareBusyException` | la pinpad ya está procesando otra operación | 2 cobros simultáneos a la misma terminal |
| 503 | `HardwareDisconnectedException` | pinpad apagada / IP mala / TCP no responde | `"La terminal 192.168.3.167:1818 no respondio al chequeo TCP (3s). Verifica que este encendida y en la misma red."` |

Otros 422 reales:

- `"La Caja 99 de la Sucursal 97 no tiene terminal configurada en t_banorte_usuarios."`
- `"El campo 'transaccion_id' es obligatorio para cancelar."`
- `"El campo 'numero_control' es obligatorio para verificar."`

## 7. Instalación y configuración

### 7.1 Publicar e instalar (resumen — detalle en `README.md`)

```powershell
cd C:\desarrollo\cb\berllano\berllanoWorker
dotnet publish -c Release -r win-x86 --self-contained -o publish
Stop-Service berllanoWorker -Force          # si ya corre
.\deploy\install-worker.ps1 -PublishDir .\publish -WorkerKey "<KEY_DE_LA_CAJA>"
curl http://localhost:5000/api/sistema/identidad
```

Archivos que deben existir en la caja:

```
C:\asesor\BanortePOS\BanorteWorker.exe          (wrapper -> PINPAD.TPV)
C:\asesor\BanortePOS\Pinpad.tpv / Pinpad.dll    (libreria nativa del pinpad)
C:\asesor\BanortePOS\BanortePayworksWrapper.dll (SOLO para devoluciones)
C:\asesor\BanortePOS\Logs\
```

### 7.2 `C:\berllanoWorker\appsettings.json` (instalado, valores reales de esta PC)

```json
"Worker":  { "Puerto": 5000, "WorkerKey": "<KEY_DE_LA_CAJA>" },
"ConnectionStrings": { "cberp": "Server=217.216.95.62,1475;Database=cberp;..." },
"CajaLocal": {
  "IdEmpresaDefault": 1, "SucursalDefault": 97, "CajaDefault": 1,
  "CacheMinutos": 5, "RutaCacheJson": "C:\\berllanoWorker\\data\\config_cajas_cache.json"
},
"TerminalBanorte": {
  "RutaWorkerExe": "C:\\asesor\\BanortePOS\\BanorteWorker.exe",
  "RutaWrapperDll": "C:\\asesor\\BanortePOS\\BanortePayworksWrapper.dll",
  "IpTerminal": "192.168.100.81",   // fallback si la (suc,caja) no tiene recurso
  "Cia": 1,
  "PuertoDefault": "1818",          // siempre 1818 en toda la flota
  "ModoDefault": "AUT",
  "Usuario": "a8626674",            // respaldo local si no hay fila en la tabla
  "Password": "<pass>",
  "Afiliacion": "8626674",
  "TimeoutSegundos": 150,
  "ValidarPingTcpPrevio": true
}
```

### 7.3 Filas reales en `cberp` (script: `deploy/alta-terminal-banorte.sql`)

```sql
-- t_banorte_usuarios  (FK a Cat_sucursales: la sucursal DEBE existir)
cia=1, sucursal=97, caja=1, usr_banorte='a8626674', id_terminal='PENDIENTE',
id_afiliacion='8626674', puerto='1818', modo='AUT'

-- cat_RecursosWorker  (único por IdEmpresa+IdSucursal+Caja+Recurso)
(1,97,1,'TerminalBanorte')  NombreDispositivoLocal='192.168.100.81'  <- IP pinpad
(1,97,1,'Tickets')          NombreDispositivoLocal='prueba'
(1,97,1,'Documentos')       NombreDispositivoLocal='Microsoft Print to PDF'
```

## 8. Gotchas reales (nos mordieron)

- **Decimal de cultura**: el exe parsea el monto con la cultura de Windows
  (coma decimal en esta flota). El worker ya formatea con cultura local;
  no "arreglen" el monto a mano.
- **`id_terminal` ≠ IP**: es el T-ID Payworks (reportes/portal). La IP va
  en `cat_RecursosWorker` (`'TerminalBanorte'`) o en `IpTerminal` local.
- **La respuesta del cobro es un resumen** (ver §4.6): para obtener la
  referencia Payworks de una venta usa `detalle_ventas_pagos.ref_financiera`
  filtrando por `no_venta`.
- **`numControl` lo genera el worker** (`ddHHmmss` + `B`/`C` en cobro y
  retiro): Payworks rechaza duplicados y guiones; la referencia del
  cliente vuelve en `referenciaCliente`. El `numero_control` de
  `/verificar` es ese valor interno — en cobros ya no viene en la
  respuesta, búscalo en `detalle_ventas_pagos.codigo_transaccion`.
- **Timeout/cancelación no escribe en cberp**: los bloques de post-venta
  solo corren si el pinpad respondió.
- **Caché ~5 min**: cambios en las tablas tardan en verse en la
  resolución de credenciales/IP del pinpad (los GET siempre leen la BD
  fresco, pero las operaciones usan caché — por eso un GET puede mostrar
  la IP nueva y el POST seguir yendo a la vieja → 503). Si pasa:
  `"forzar_recarga":true` en el body o `Restart-Service berllanoWorker`.
  Ojo: si cberp no responde, el worker sirve la entrada cacheada aunque
  esté vieja.
- **La pinpad es un recurso único por caja**: dos operaciones a la vez →
  la segunda da **423**. Espera a que la primera termine.
- **`sucursal` debe existir en `Cat_sucursales`** (FK) — un INSERT con
  sucursal inexistente revienta.
- **Puerto siempre 1818** en toda la flota; lo que cambia por caja es la IP.

## 9. Smoke test

```powershell
.\deploy\test-terminal.ps1 -WorkerKey "$K" -Sucursal 97 -Caja 1 -IncluirLlaves
# Validaciones sin hardware + GET cajas + concurrencia 423.
# -IncluirCobro hace un cobro REAL de $MontoPrueba (revisar modo AUT/PRD).
# -TransaccionCancelar / -NumeroControlVerificar para cancelacion/verificar.
```
