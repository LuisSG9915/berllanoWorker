<#
.SYNOPSIS
  Registra berllanoWorker para correr en la SESION del usuario (app de logon) en vez de servicio.

.DESCRIPTION
  Requerido cuando la PC usa el lector de huellas DigitalPersona (U.are.U):
  el driver usbdpfp / DPFP SDK 1.6.1 solo expone el lector a procesos de la
  sesion interactiva; como Servicio de Windows (sesion 0) cualquier cuenta
  recibe OnReaderDisconnect inmediato (503 HardwareDisconnectedException).

  - Crea run-worker-hidden.vbs en InstallDir (lanza el exe sin consola visible)
  - Registra la tarea programada 'berllanoWorker' con trigger AtLogOn del usuario
  - Detiene y deshabilita el servicio si existe (queda como respaldo)
  - El worker escucha en el mismo puerto configurado (default 5000)

  Requiere: binarios ya copiados a InstallDir (ver install-worker.ps1 pasos 1-4).

.PARAMETER InstallDir
  Carpeta donde esta berllanoWorker.exe. Default: C:\berllanoWorker

.PARAMETER TaskUser
  Usuario de Windows cuya sesion correra el worker. Default: usuario actual.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\install-worker-session.ps1
#>
[CmdletBinding()]
param(
    [string]$InstallDir = "C:\berllanoWorker",
    [string]$TaskUser = "$env:USERDOMAIN\$env:USERNAME",
    [string]$ServiceName = "berllanoWorker",
    [string]$TaskName = "berllanoWorker"
)

$ErrorActionPreference = "Stop"

$esAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $esAdmin) {
    Write-Error "Ejecuta este script como Administrador."
    exit 1
}

if (-not (Test-Path "$InstallDir\berllanoWorker.exe")) {
    Write-Error "No se encontro $InstallDir\berllanoWorker.exe"
    exit 1
}

# --- 1. Launcher oculto ---
$vbsPath = Join-Path $InstallDir "run-worker-hidden.vbs"
$vbs = @'
' Lanza berllanoWorker.exe sin ventana de consola (Kestrel).
Dim fso, shell, exe, workDir
Set fso = CreateObject("Scripting.FileSystemObject")
Set shell = CreateObject("Wscript.Shell")
workDir = fso.GetParentFolderName(WScript.ScriptFullName)
exe = fso.BuildPath(workDir, "berllanoWorker.exe")
shell.CurrentDirectory = workDir
shell.Run """" & exe & """", 0, False
'@
Set-Content -Path $vbsPath -Value $vbs -Encoding ASCII
Write-Host "[1/4] Launcher oculto: $vbsPath" -ForegroundColor Green

# --- 2. Tarea programada: arranca al iniciar sesion del usuario de caja ---
$usuarioSolo = $TaskUser.Split('\')[-1]
$action = New-ScheduledTaskAction -Execute "wscript.exe" -Argument "`"$vbsPath`""
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $TaskUser
$principal = New-ScheduledTaskPrincipal -UserId $usuarioSolo -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew `
    -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)

Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger `
    -Principal $principal -Settings $settings -Force `
    -Description "berllanoWorker en sesion interactiva (requerido por lector de huellas DPFP)" | Out-Null
Write-Host "[2/4] Tarea '$TaskName' registrada (AtLogOn de $TaskUser, sesion interactiva)" -ForegroundColor Green

# --- 3. Servicio: respaldo deshabilitado ---
$servicio = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($servicio) {
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    sc.exe config $ServiceName start= disabled | Out-Null
    Write-Host "[3/4] Servicio '$ServiceName' detenido y deshabilitado (respaldo manual)" -ForegroundColor Green
} else {
    Write-Host "[3/4] No hay servicio que deshabilitar" -ForegroundColor Green
}

# --- 4. Arrancar ahora (equivale a un logon: corre en la sesion actual) ---
Start-ScheduledTask -TaskName $TaskName
Start-Sleep -Seconds 5
try {
    $identidad = Invoke-RestMethod -Uri "http://localhost:5000/api/sistema/identidad" -TimeoutSec 5
    Write-Host "[4/4] Worker activo en sesion: $($identidad.hostname) puerto $($identidad.puerto)" -ForegroundColor Green
} catch {
    Write-Warning "La tarea arranco pero no respondio en localhost:5000. Revisa $InstallDir\logs\"
}

Write-Host ""
Write-Host "=== NOTAS ===" -ForegroundColor Cyan
Write-Host "- Todo corre en :5000 (impresion + terminal + biometria) como pide la spec."
Write-Host "- Si no hay sesion iniciada no hay worker (normal en cajas con autologon)."
Write-Host "- Para volver a servicio: sc.exe config $ServiceName start= auto; Start-Service $ServiceName (huella NO funcionara)."
Write-Host "- Para detenerlo: Task Manager -> berllanoWorker.exe (o Stop-Process -Name berllanoWorker)."
