# berllanoWorker

Micro-API local (Kestrel self-hosted, Servicio de Windows) que expone el
hardware de cada PC de sucursal a la PWA Berllano. Esta fase: **impresión**
(ESC/POS a miniprinter + PDF carta). Terminal bancaria y biometría quedan
diferidas — la arquitectura ya las contempla.

## Endpoints

| Método | Ruta | Descripción |
|--------|------|-------------|
| GET  | `/api/sistema/identidad` | Health check + auto-descubrimiento (sin auth) |
| GET  | `/api/impresion/impresoras` | Impresoras instaladas en Windows |
| POST | `/api/impresion/ticket` | Imprime ticket ESC/POS (`{recurso, ticket_dataset[]}`) |
| POST | `/api/impresion/documento` | Descarga PDF de URL e imprime en carta (`{url_file, recurso}`) |
| GET  | `/api/impresion/prueba?recurso=X` | Ticket de diagnóstico |

Auth: header `X-Worker-Key` (config `Worker:WorkerKey`) en todo `/api/*`
menos `/api/sistema/*`. Errores de hardware: 503 desconectado / 423 ocupado /
408 timeout / 422 datos inválidos — JSON `{status, error, mensaje}`.

## Desarrollo

```powershell
dotnet build
dotnet run   # levanta en http://0.0.0.0:5000
```

Nota: `PlatformTarget=x86` prepara el proyecto para SDKs de 32 bits de las
siguientes fases. Para correr en dev sin runtime x86, compilar sin
PlatformTarget temporalmente o usar el publish self-contained.

## Instalación en PC de sucursal

```powershell
# En la máquina de desarrollo:
dotnet publish -c Release -r win-x86 --self-contained -o publish

# Copiar la carpeta publish a la PC de caja y ejecutar como Administrador:
.\deploy\install-worker.ps1 -WorkerKey "<llave>"
```

El script: copia a `C:\berllanoWorker`, configura appsettings, pone la red en
Privada, abre el firewall (TCP 5000 solo LocalSubnet) y crea el servicio.

Pasos manuales posteriores:
- Renombrar la PC a `[SUCURSAL]-[EQUIPO]` (ej. `SUCA-CAJA1`) y reiniciar.
- Para `/api/impresion/documento`: copiar `SumatraPDF.exe` a `C:\berllanoWorker\tools\`.
- Dar de alta la relación en `cat_RecursosWorker` (modal de la PWA o SP).
- `VITE_WORKER_KEY` de la PWA debe coincidir con `Worker:WorkerKey`.

## Configuración (`appsettings.json`)

```json
"Worker":     { "Puerto": 5000, "WorkerKey": "..." },
"Impresion":  { "CodePage": 858, "Columnas": 40, "SumatraPdfPath": "tools\\SumatraPDF.exe",
                "TimeoutDescargaSegundos": 30, "TamMaximoDocumentoMB": 50 }
```

- `CodePage`: codepage ESC/POS de la impresora (858 por defecto; cambiar a
  437/850 si los acentos salen mal) — mapa en `EscPosBuilder.CodePageEscT`.
- Logs rotativos diarios en `logs\worker-*.log` (14 días).
