# berllanoWorker

Micro-API local de hardware por caja de sucursal (.NET 8, `net8.0-windows`, **x86**).
Expone HTTP en `http://<caja>:5000` para:

- **Impresión** — tickets ESC/POS (`/api/impresion/*`) y PDF vía SumatraPDF.
- **Terminal Banorte/Payworks** — pinpad Ingenico por red (`/api/terminal/*`).
- **Sistema** — identidad/health (`/api/sistema/*`).

Toda ruta `/api/*` (excepto `/api/sistema`) exige header `X-Worker-Key`
(valor de `Worker:WorkerKey` del `appsettings.json` instalado). Sin él → `401`.

---

## 1. Instalación del worker (PC de sucursal)

```powershell
# 1. Publicar (x86 self-contained; no requiere .NET instalado en la caja)
cd C:\desarrollo\cb\berllano\berllanoWorker
dotnet publish -c Release -r win-x86 --self-contained -o publish

# 2. Instalar como servicio de Windows (ejecutar como Administrador)
.\deploy\install-worker.ps1 -WorkerKey "<API_KEY_DE_LA_SUCURSAL>" [-NewComputerName "SUC1-CAJA1"]
```

El instalador: copia binarios a `C:\berllanoWorker`, copia `tools\SumatraPDF.exe`,
crea `C:\asesor\BanortePOS\Logs`, escribe `Worker.Puerto`/`Worker.WorkerKey`,
pone la red en perfil Privada, crea regla de firewall (TCP 5000 solo LocalSubnet)
y registra el servicio `berllanoWorker` (auto-start).

**Nota:** el instalador copia binarios ANTES de detener el servicio; si el worker
ya está corriendo, detenerlo primero: `Stop-Service berllanoWorker -Force`.

Verificar:

```powershell
curl http://localhost:5000/api/sistema/identidad
Get-Service berllanoWorker
```

## 2. Configuración — Impresora

### 2.1 `appsettings.json` → `Impresion` y `CajaLocal`

```json
"Impresion": {
  "CodePage": 858,                  // página de códigos del miniprinter (850/858)
  "Columnas": 40,                   // ancho de ticket (40/42)
  "SumatraPdfPath": "tools\\SumatraPDF.exe",
  "TimeoutDescargaSegundos": 15,
  "TamMaximoDocumentoMB": 50
},
"CajaLocal": {
  "IdEmpresaDefault": 1,            // = cia en t_banorte_usuarios / IdEmpresa
  "SucursalDefault": 1,             // (suc,caja) cuando el request no las trae
  "CajaDefault": 1,
  "CacheMinutos": 5,
  "RutaCacheJson": "C:\\berllanoWorker\\data\\config_cajas_cache.json"
}
```

### 2.2 `cberp.dbo.cat_RecursosWorker` (impresoras/recursos por caja)

Índice único `(IdEmpresa, IdSucursal, Caja, Recurso)`. Columnas clave:

| Columna | Uso |
|---|---|
| `Recurso` | alias lógico: `Tickets`, `Documentos`, `TerminalBanorte`, custom |
| `NombreDispositivoLocal` | para impresoras: nombre EXACTO en Windows; para `TerminalBanorte`: IP del pinpad |
| `Activo` | solo se consultan filas activas |

### 2.3 Resolución del campo `recurso` (reglas)

1. **`recurso` = nombre físico instalado** (ej. `"Microsoft Print to PDF"`) →
   impresión directa, sin consultar la DB (funciona aunque la caja no exista).
2. **`recurso` vacío o alias** (`"Tickets"`, `"Documentos"`, `"Default"`) →
   lookup en `cat_RecursosWorker` por `(IdEmpresa, sucursal, caja, Recurso)` y
   se usa su `NombreDispositivoLocal`. Ticket vacío → `"Tickets"`,
   documento vacío → `"Documentos"`.
3. **`recurso` = alias custom** (ej. `"Facturas"`) → mismo lookup por nombre literal.
4. Sin fila activa → **422** `"La Caja X de la Sucursal Y no tiene una impresora
   asignada para el recurso 'Z' en cat_RecursosWorker."`

El nombre resuelto debe existir en Windows (Panel de control → Impresoras) en
LA PC donde corre el worker. Cada impresora física tiene su propio semáforo:
dos impresoras distintas imprimen en paralelo; la misma → `423 Locked`.

```powershell
# Por caja (resuelve via DB):
curl -X POST http://localhost:5000/api/impresion/ticket -H "X-Worker-Key: <KEY>" -H "Content-Type: application/json" -d "{\"sucursal\":1,\"caja\":1,\"ticket_dataset\":[{\"consecutivo\":1,\"modo\":\"TEXTO_PLANO\",\"data\":\"PRUEBA\"}]}"
# Por nombre físico directo:
curl -X POST http://localhost:5000/api/impresion/ticket -H "X-Worker-Key: <KEY>" -H "Content-Type: application/json" -d "{\"recurso\":\"Miniprinter_Caja1\",\"ticket_dataset\":[{\"consecutivo\":1,\"modo\":\"TEXTO_PLANO\",\"data\":\"PRUEBA\"}]}"
```

## 3. Configuración — Terminal Banorte

### 3.1 Archivos locales requeridos

```
C:\asesor\BanortePOS\
    BanorteWorker.exe            <- CLI VB.NET (op,ip,puerto,usr,pwd,monto,numCtrl,modo,uuid,efectivo)
    Pinpad.tpv / Pinpad.dll      <- libreria nativa de la terminal (P/Invoke)
    BanortePayworksWrapper.dll   <- SOLO si se usara DEVOLUCION (reversa financiera)
    Logs\                        <- trazas del exe (se crea sola / el instalador)
```

### 3.2 `appsettings.json` → `TerminalBanorte`

```json
"TerminalBanorte": {
  "RutaWorkerExe": "C:\\asesor\\BanortePOS\\BanorteWorker.exe",
  "RutaWrapperDll": "C:\\asesor\\BanortePOS\\BanortePayworksWrapper.dll",
  "IpTerminal": "192.168.100.81",      // IP de la pinpad FISICA de esta caja (LAN)
  "PuertoDefault": "1818",             // puerto Payworks: siempre 1818 en la flota
  "ModoDefault": "AUT",                // AUT=prueba (sin cargo) / PRD=real
  "Usuario": "a8626674",               // FALLBACK: creds de ESTA caja si su
  "Password": "...",                   // (suc,caja) no tiene fila en la tabla
  "Afiliacion": "8626674",             // (p.ej. terminal de pruebas fuera del ERP)
  "TimeoutSegundos": 150,
  "ValidarPingTcpPrevio": true         // chequeo TCP 3s antes de hablar al pinpad
}
```

> Los defaults `(sucursal, caja)` ahora viven en `CajaLocal` (arriba, §2.1).
> Por compatibilidad también se leen `TerminalBanorte:SucursalDefault/CajaDefault/Cia`
> si `CajaLocal` no existe. La IP del pinpad puede venir además de una fila
> `Recurso='TerminalBanorte'` en `cat_RecursosWorker` (su `NombreDispositivoLocal`).

### 3.3 `cberp.dbo.t_banorte_usuarios` (fuente de verdad por caja)

PK: `(cia, sucursal, caja)`. Columnas usadas:

| Columna | Uso |
|---|---|
| `usr_banorte` / `pass_banorte` | credenciales Payworks de la terminal |
| `id_terminal` | **T-ID Payworks** (NO es la IP — la IP del pinpad es local por PC) |
| `id_afiliacion` | afiliación del comercio |
| `puerto` | default `1818` |
| `modo` | `AUT`/`PRD` |

La conexión va en `ConnectionStrings:cberp`. Resolución por campo:
**override del request > fila DB > defaults locales**. `ConfiguracionCajaService`
(singleton) centraliza esta lógica y la comparten impresión y terminal: caché
en memoria `CacheMinutos` + archivo `config_cajas_cache.json` para sobrevivir
caídas de WAN (`forzar_recarga:true` en el body fuerza re-consulta). Si
`(suc,caja)` no tiene fila y no es la caja default del equipo → **422**
`"La Caja X de la Sucursal Y no tiene terminal configurada en t_banorte_usuarios."`

Selección de caja por request: body `{"sucursal":1,"caja":1}` o headers
`X-Sucursal`/`X-Caja` (body gana). Aplica igual en `/api/impresion/*`.

## 4. Endpoints `/api/terminal` — curls

```powershell
$K = "b740ecedf34c4cd7885826521ce8ff27"   # ejemplo: key de esta instalación
$B = "http://localhost:5000/api/terminal"
```

### Cajas configuradas (diagnóstico, sin password)

```powershell
curl "$B/cajas" -H "X-Worker-Key: $K"                 # todas (t_banorte_usuarios)
curl "$B/cajas?sucursal=1" -H "X-Worker-Key: $K"      # por sucursal

# Consolidado por caja: impresoras (cat_RecursosWorker, con flag
# instaladaLocal en ESTA pc) + terminal + ipPinpad. Sin password.
curl "http://localhost:5000/api/sistema/cajas?sucursal=1" -H "X-Worker-Key: $K"
```

### Cargar llaves / vinculación (silenciosa en el pinpad)

```powershell
curl -X POST "$B/llaves" -H "X-Worker-Key: $K" -H "Content-Type: application/json" -d '{}'
# respuesta aprobada: "mensajeRespuesta":"Llave obtenida" (200)
```

### Cobro (la pinpad pedirá tarjeta ~30s)

```powershell
curl -X POST "$B/cobro" -H "X-Worker-Key: $K" -H "Content-Type: application/json" `
  -d '{"monto":1.00,"referencia":"VTA-001","sucursal":1,"caja":1}'
# aprobada -> 200: referenciaPayworks, codigoAutorizacion, voucher, pan, emvAid...
# la respuesta eco: sucursal, caja, ipTerminal, idTerminal, afiliacion,
# numControl (generado ddHHmmss+B), referenciaCliente (la enviada)
```

### Retiro con cashback (efectivo)

```powershell
curl -X POST "$B/retiro" -H "X-Worker-Key: $K" -H "Content-Type: application/json" `
  -d '{"monto":50.00,"monto_venta":10.00}'
# monto = efectivo a entregar; monto_venta = cargo a la venta (0 => = monto)
# numControl generado con sufijo "C"
```

### Cancelación

```powershell
# Reset de sesión EMV (aborta lo que el pinpad esté esperando):
curl -X POST "$B/cancelacion" -H "X-Worker-Key: $K" -H "Content-Type: application/json" `
  -d '{"transaccion_id":"RESET"}'

# Devolución FINANCIERA real (reversa de venta):
#   transaccion_id = Referencia Payworks de 12 dígitos + monto a reversar.
#   Requiere BanortePayworksWrapper.dll junto al exe.
curl -X POST "$B/cancelacion" -H "X-Worker-Key: $K" -H "Content-Type: application/json" `
  -d '{"transaccion_id":"821773509772","monto":100.50}'
```

### Verificar transacción (consulta Payworks por numControl)

```powershell
curl -X POST "$B/verificar" -H "X-Worker-Key: $K" -H "Content-Type: application/json" `
  -d '{"numero_control":"10143022B"}'
# estado "VTA": estatus interno A->200 aplicada; D/R/C/otro->400 con detalle
```

## 5. Errores y timeouts

| HTTP | Causa |
|---|---|
| `200` | Operación aprobada (`aprobada:true`) |
| `400` | Declinada/cancelada/rechazada de negocio (`aprobada:false`, ver `mensajeRespuesta`) |
| `401` | `X-Worker-Key` ausente o inválida |
| `408` | Timeout capturando tarjeta (`-2021`) o del wrapper |
| `422` | Datos inválidos: monto fuera de `$0.00–$999,999.99`, campos vacíos, caja sin fila en `t_banorte_usuarios` o sin recurso en `cat_RecursosWorker` |
| `423` | El pinpad de ESA IP o la impresora física de ese recurso está ocupada (semáforo por dispositivo) |
| `503` | Pinpad apagada/IP incorrecta (chequeo TCP), `BanorteWorker.exe` ausente, falla del wrapper (`EX_WORKER`/`-1001`), DB inalcanzable en `/cajas` |

Detalles de implementación que conviene saber:

- **Montos se envían al exe con la cultura local de Windows** (esta flota usa
  coma decimal); NO usar punto fijo — el exe hace `Conversions.ToSingle` con la
  cultura ambiental y `"1.00"` se leía como `100`.
- `numControl` lo genera el worker (`ddHHmmss` + `B`/`C`/`D`/`KEY`): una venta
  puede reintentarse con varias tarjetas y Payworks rechaza duplicados/guiones.
- `VERIFICACION` llega como sobre estándar con payload `VTA%7C...` URL-encodeado
  en el campo mensaje; el parser lo decodifica (estatus interno es el que manda).
- `CANCELAR` NO consulta transacciones: es solo `PPSendTxn(3)` de reset de sesión.
- Locks internos del exe: 30s venta/cashback/llave, 15s verificación, 3s cancelar.

## 6. Pruebas de humo en sucursal

```powershell
# Como Admin, en la caja con pinpad real:
.\deploy\test-terminal.ps1 -WorkerKey "<KEY>"                                    # solo validaciones (no toca pinpad)
.\deploy\test-terminal.ps1 -WorkerKey "<KEY>" -IncluirLlaves                     # + carga de llaves real
.\deploy\test-terminal.ps1 -WorkerKey "<KEY>" -IncluirLlaves -Sucursal 1 -Caja 1 # resolviendo desde t_banorte_usuarios
.\deploy\test-terminal.ps1 -WorkerKey "<KEY>" -IncluirCobro -MontoPrueba 1.00    # cobro real (AUT)
.\deploy\test-terminal.ps1 -WorkerKey "<KEY>" -TransaccionCancelar "RESET"       # reset de sesión
```

Exit code = número de fallas.
