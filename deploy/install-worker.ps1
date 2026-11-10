<#
.SYNOPSIS
  Instala berllanoWorker como Servicio de Windows en una PC de sucursal.

.DESCRIPTION
  - Copia los binarios publicados a C:\berllanoWorker (incluye tools\SumatraPDF.exe si existe)
  - Escribe WorkerKey / puerto en appsettings.json
  - Fuerza el perfil de red activo a Privada (requisito para mDNS/LLMNR)
  - Abre el puerto TCP del worker solo para la subred local
  - Crea e inicia el servicio de Windows
  - Opcionalmente renombra la PC bajo la norma [SUCURSAL]-[EQUIPO]

.PARAMETER PublishDir
  Carpeta con el publish self-contained (dotnet publish -r win-x86). Default: ..\publish

.PARAMETER InstallDir
  Carpeta destino. Default: C:\berllanoWorker

.PARAMETER Puerto
  Puerto Kestrel. Default: 5000

.PARAMETER WorkerKey
  Llave compartida que exige el worker en el header X-Worker-Key.
  Debe coincidir con VITE_WORKER_KEY de la PWA.

.PARAMETER NewComputerName
  Opcional. Nuevo hostname bajo la norma [SUCURSAL]-[EQUIPO] (ej. SUC1-CAJA1).
  Requiere reinicio para aplicar; el mDNS .local derivado sera <nombre>.local.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\install-worker.ps1 -WorkerKey "SUCA-2024-XYZ" -NewComputerName "SUC1-CAJA1"
#>
[CmdletBinding()]
param(
    [string]$PublishDir = "$PSScriptRoot\..\publish",
    [string]$InstallDir = "C:\berllanoWorker",
    [int]$Puerto = 5000,
    [Parameter(Mandatory = $true)][string]$WorkerKey,
    [string]$ServiceName = "berllanoWorker",
    [string]$NewComputerName = ""
)

$ErrorActionPreference = "Stop"

# --- Requiere admin ---
$esAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $esAdmin) {
    Write-Error "Ejecuta este script como Administrador."
    exit 1
}

# --- 1. Copiar binarios ---
if (-not (Test-Path "$PublishDir\berllanoWorker.exe")) {
    Write-Error "No se encontro berllanoWorker.exe en '$PublishDir'. Ejecuta primero:`n  dotnet publish -c Release -r win-x86 --self-contained -o publish"
    exit 1
}

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item "$PublishDir\*" -Destination $InstallDir -Recurse -Force
Write-Host "[1/7] Binarios copiados a $InstallDir" -ForegroundColor Green

# --- 1b. SumatraPDF (impresion de documentos carta) ---
$sumatraOrigen = Join-Path $PublishDir "tools\SumatraPDF.exe"
if (Test-Path $sumatraOrigen) {
    New-Item -ItemType Directory -Force -Path "$InstallDir\tools" | Out-Null
    Copy-Item $sumatraOrigen -Destination "$InstallDir\tools\SumatraPDF.exe" -Force
    Write-Host "      SumatraPDF.exe copiado a $InstallDir\tools\" -ForegroundColor Green
} else {
    Write-Warning "SumatraPDF.exe no esta en $PublishDir\tools\ - /api/impresion/documento no funcionara hasta copiarlo."
}

# --- 2. Configurar appsettings.json ---
$appSettingsPath = Join-Path $InstallDir "appsettings.json"
$json = Get-Content $appSettingsPath -Raw | ConvertFrom-Json
$json.Worker.Puerto = $Puerto
$json.Worker.WorkerKey = $WorkerKey
$json | ConvertTo-Json -Depth 10 | Set-Content $appSettingsPath -Encoding UTF8
Write-Host "[2/7] appsettings.json configurado (puerto=$Puerto)" -ForegroundColor Green

# --- 3. Perfil de red Privada (requisito mDNS/LLMNR) ---
Get-NetConnectionProfile | Where-Object { $_.NetworkCategory -ne 'Private' } | ForEach-Object {
    Set-NetConnectionProfile -InterfaceIndex $_.InterfaceIndex -NetworkCategory Private
    Write-Host "      Red '$($_.Name)' -> Privada"
}
Write-Host "[3/7] Perfil de red en Privada" -ForegroundColor Green

# --- 4. Firewall: puerto solo para la subred local ---
$regla = Get-NetFirewallRule -DisplayName "Worker Kestrel API LAN" -ErrorAction SilentlyContinue
if ($regla) { Remove-NetFirewallRule -DisplayName "Worker Kestrel API LAN" }
New-NetFirewallRule -DisplayName "Worker Kestrel API LAN" `
    -Direction Inbound -Action Allow -Protocol TCP -LocalPort $Puerto `
    -Profile Private,Domain -RemoteAddress LocalSubnet | Out-Null
Write-Host "[4/7] Regla de firewall creada (TCP $Puerto, solo LocalSubnet)" -ForegroundColor Green

# --- 5. Servicio de Windows ---
$servicio = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($servicio) {
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}
$binPath = "`"$InstallDir\berllanoWorker.exe`""
sc.exe create $ServiceName binPath= $binPath start= auto | Out-Null
sc.exe description $ServiceName "berllanoWorker - micro-API local de hardware (impresion/terminal/biometria)" | Out-Null
Write-Host "[5/7] Servicio '$ServiceName' registrado" -ForegroundColor Green

# --- 6. Iniciar y verificar ---
Start-Service -Name $ServiceName
Start-Sleep -Seconds 3
try {
    $identidad = Invoke-RestMethod -Uri "http://localhost:$Puerto/api/sistema/identidad" -TimeoutSec 5
    Write-Host "[6/7] Worker activo: $($identidad.hostname) ($($identidad.ipsLan -join ', ')) puerto $($identidad.puerto)" -ForegroundColor Green
} catch {
    Write-Warning "El servicio inicio pero no respondio en localhost:$Puerto. Revisa logs\$InstallDir\logs\"
}

# --- 7. Hostname bajo la norma [SUCURSAL]-[EQUIPO] ---
if ($NewComputerName) {
    if ($NewComputerName -notmatch '^[A-Za-z0-9-]{1,15}$') {
        Write-Warning "Nombre '$NewComputerName' invalido (max 15 caracteres, solo letras/numeros/guion). No se renombro."
    } elseif ($env:COMPUTERNAME -ne $NewComputerName) {
        Rename-Computer -NewName $NewComputerName -Force
        Write-Host "[7/7] Equipo renombrado a $NewComputerName (pendiente reinicio)" -ForegroundColor Yellow
    } else {
        Write-Host "[7/7] El equipo ya se llama $NewComputerName" -ForegroundColor Green
    }
}

Write-Host ""
Write-Host "=== SIGUIENTES PASOS MANUALES ===" -ForegroundColor Cyan
if (-not $NewComputerName) {
    Write-Host "1. Renombra la PC a [SUCURSAL]-[EQUIPO] (o reusa -NewComputerName) y reinicia."
} else {
    Write-Host "1. Reinicia la PC para aplicar el nombre $NewComputerName."
}
Write-Host "2. Da de alta la fila en cat_RecursosWorker (desde el modal de la PWA o el SP)."
Write-Host "3. VITE_WORKER_KEY de la PWA debe ser: $WorkerKey"
