<#
.SYNOPSIS
  Pruebas de humo de /api/terminal/* del berllanoWorker en una PC de sucursal.

.DESCRIPTION
  Verifica la cadena completa: worker -> BanorteWorker.exe -> pinpad Payworks.
  - Preflight: existe BanorteWorker.exe y hay TCP a la terminal.
  - Pruebas de validacion (422/401/503/423) NO tocan el pinpad.
  - Las pruebas que interactuan con la terminal son OPT-IN:
      -IncluirLlaves      POST /api/terminal/llaves (CARGAR_LLAVE, sin cargo)
      -IncluirCobro       POST /api/terminal/cobro por -MontoPrueba (CARGO REAL si Modo=PRD)
      -TransaccionCancelar X  POST /api/terminal/cancelacion con transaccion_id=X
                            (id generico -> reset de sesion; ref Payworks de 12 digitos
                            + -MontoDevolucion -> DEVOLUCION financiera REAL)
      -NumeroControlVerificar X POST /api/terminal/verificar con numero_control=X

.PARAMETER WorkerKey
  Valor de Worker:WorkerKey del appsettings.json instalado.

.PARAMETER InstallDir
  Carpeta de instalacion del worker (para leer TerminalBanorte). Default: C:\berllanoWorker

.EXAMPLE
  # Solo validaciones (seguro, no toca el pinpad)
  powershell -ExecutionPolicy Bypass -File .\test-terminal.ps1 -WorkerKey "abc123"

.EXAMPLE
  # Todo menos el cobro real
  powershell -ExecutionPolicy Bypass -File .\test-terminal.ps1 -WorkerKey "abc123" -IncluirLlaves -NumeroControlVerificar "10143022B"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$WorkerKey,
    [string]$BaseUrl = "http://localhost:5000",
    [string]$InstallDir = "C:\berllanoWorker",
    [decimal]$MontoPrueba = 1.00,
    [decimal]$MontoDevolucion = 0,
    [string]$TransaccionCancelar = "",
    [string]$NumeroControlVerificar = "",
    [switch]$IncluirLlaves,
    [switch]$IncluirCobro
)

$headers = @{ "X-Worker-Key" = $WorkerKey; "Content-Type" = "application/json" }
$script:fallas = 0

function Probar([string]$nombre, [string]$endpoint, [string]$body, [int]$esperado) {
    try {
        $resp = Invoke-WebRequest -Method POST -Uri "$BaseUrl/api/terminal/$endpoint" `
            -Headers $headers -Body $body -TimeoutSec 200 -SkipHttpErrorCheck
        $code = [int]$resp.StatusCode
        $detalle = $resp.Content
    } catch {
        $code = 0; $detalle = $_.Exception.Message
    }
    $ok = ($code -eq $esperado)
    if (-not $ok) { $script:fallas++ }
    $color = if ($ok) { "Green" } else { "Red" }
    $marca = if ($ok) { "OK  " } else { "FAIL" }
    Write-Host "[$marca] $nombre -> HTTP $code (esperado $esperado)" -ForegroundColor $color
    if ($detalle.Length -gt 300) { $detalle = $detalle.Substring(0, 300) + "..." }
    Write-Host "       $detalle" -ForegroundColor DarkGray
}

Write-Host "=== PREFLIGHT ===" -ForegroundColor Cyan
$cfg = Get-Content "$InstallDir\appsettings.json" -Raw | ConvertFrom-Json
$exe = $cfg.TerminalBanorte.RutaWorkerExe
$ip = $cfg.TerminalBanorte.IpTerminal
$puerto = [int]$cfg.TerminalBanorte.Puerto

if (Test-Path $exe) {
    Write-Host "[OK  ] BanorteWorker.exe encontrado en $exe" -ForegroundColor Green
} else {
    $script:fallas++
    Write-Host "[FAIL] NO existe $exe (todas las operaciones daran 503)" -ForegroundColor Red
}
$tcp = Test-NetConnection -ComputerName $ip -Port $puerto -WarningAction SilentlyContinue
if ($tcp.TcpTestSucceeded) {
    Write-Host "[OK  ] TCP $ip`:$puerto alcanzable" -ForegroundColor Green
} else {
    $script:fallas++
    Write-Host "[FAIL] Sin TCP a $ip`:$puerto (todas las operaciones daran 503)" -ForegroundColor Red
}
if ([string]::IsNullOrWhiteSpace($cfg.TerminalBanorte.Usuario)) {
    Write-Warning "TerminalBanorte:Usuario vacio en appsettings -> las operaciones daran 422 sin overrides"
}
$wrapperDll = $cfg.TerminalBanorte.RutaWrapperDll
if ($wrapperDll -and -not (Test-Path $wrapperDll)) {
    Write-Warning "No existe $wrapperDll -> las devoluciones financieras (ref Payworks + monto) daran 503"
}

Write-Host "`n=== VALIDACION (sin hardware) ===" -ForegroundColor Cyan
Probar "cobro monto=0 -> 422"        "cobro"       '{"monto":0,"referencia":"T"}' 422
Probar "cobro sobre techo -> 422"    "cobro"       '{"monto":1500000,"referencia":"T"}' 422
Probar "cancelacion id vacio -> 422" "cancelacion" '{"transaccion_id":""}'        422
Probar "verificar ctrl vacio -> 422" "verificar"   '{"numero_control":""}'        422

# IP inalcanzable: el chequeo TCP falla antes de tocar el pinpad -> 503.
# Dos disparadas a la vez: la primera ocupa el semaforo 3s -> la segunda da 423.
$bodyIpMala = '{"monto":10,"referencia":"T","usuario":"x","password":"x","ipTerminal":"192.0.0.1","puerto":"1818"}'
Probar "TCP inalcanzable -> 503"     "cobro"       $bodyIpMala                    503

Write-Host "`n=== CONCURRENCIA (semaforo pinpad) ===" -ForegroundColor Cyan
$j1 = Start-Job -ScriptBlock {
    param($u, $h, $b)
    Invoke-WebRequest -Method POST -Uri "$u/api/terminal/cobro" -Headers $h -Body $b -TimeoutSec 30 -SkipHttpErrorCheck
} -ArgumentList $BaseUrl, $headers, $bodyIpMala
Start-Sleep -Milliseconds 300
Probar "segundo cobro concurrente -> 423" "cobro" $bodyIpMala 423
Receive-Job $j1 -Wait -AutoRemoveJob | Out-Null

Write-Host "`n=== OPERACIONES CON TERMINAL (opt-in) ===" -ForegroundColor Cyan
if ($IncluirLlaves) {
    Probar "llaves CARGAR_LLAVE -> 200" "llaves" '{}' 200
}
if ($IncluirCobro) {
    Write-Warning "Se ejecutara un COBRO de `$$MontoPrueba (revisa TerminalBanorte:Modo=AUT)"
    Probar "cobro `$$MontoPrueba -> 200" "cobro" "{`"monto`":$MontoPrueba,`"referencia`":`"SMOKE-$([DateTime]::Now.ToString('HHmmss'))`"}" 200
}
if ($TransaccionCancelar) {
    if ($MontoDevolucion -gt 0 -and $TransaccionCancelar -match '^\d{12}$') {
        Write-Warning "Se ejecutara una DEVOLUCION REAL de `$$MontoDevolucion sobre ref Payworks $TransaccionCancelar"
        Probar "devolucion $TransaccionCancelar x `$$MontoDevolucion" "cancelacion" "{`"transaccion_id`":`"$TransaccionCancelar`",`"monto`":$MontoDevolucion}" 200
    } else {
        Probar "cancelacion $TransaccionCancelar (reset sesion)" "cancelacion" "{`"transaccion_id`":`"$TransaccionCancelar`"}" 200
    }
}
if ($NumeroControlVerificar) {
    Probar "verificar $NumeroControlVerificar" "verificar" "{`"numero_control`":`"$NumeroControlVerificar`"}" 200
}
if (-not ($IncluirLlaves -or $IncluirCobro -or $TransaccionCancelar -or $NumeroControlVerificar)) {
    Write-Host "      (omitidas: usa -IncluirLlaves/-IncluirCobro/-TransaccionCancelar/-NumeroControlVerificar)"
}

Write-Host "`n=== RESUMEN: $($script:fallas) falla(s) ===" -ForegroundColor $(if ($script:fallas) { "Red" } else { "Green" })
exit $script:fallas
