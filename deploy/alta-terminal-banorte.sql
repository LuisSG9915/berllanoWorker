-- ============================================================
-- Alta/actualización COMPLETA de la caja de pruebas en cberp:
--   1) dbo.t_banorte_usuarios  -> credenciales Payworks de la pinpad
--   2) dbo.cat_RecursosWorker  -> impresora 'Tickets' + IP del pinpad
--      ('TerminalBanorte') de esta PC
--
-- Pinpad de esta PC: Afiliacion 8626674, Usuario a8626674, IP 192.168.100.81
--
-- Uso:  sqlcmd -S 217.216.95.62,1475 -d cberp -U <usr> -P <pwd> -i alta-terminal-banorte.sql
--       o pégalo en SSMS y ejecútalo. Idempotente: re-ejecutar actualiza.
--
-- AJUSTA ESTAS VARIABLES antes de ejecutar:
--   @sucursal   : sucursal lógica de esta caja. DEBE existir en
--                 Cat_sucursales (FK). Para pruebas existe: 97 'PRUEBAS 2',
--                 98 'PRUEBAS 1' (99 'PRUEBAS CB' ya tiene terminal A08797252).
--                 999 NO existe en el catálogo -> viola el FK.
--   @caja       : caja lógica (todas las filas actuales usan 1).
--   @idTerminal : T-ID Payworks de la pinpad (reportes/portal Payworks). NO es
--                 la IP ni el folio. 'PENDIENTE' si aún no lo tienes — el
--                 worker solo lo devuelve informativo, no se envía al pinpad.
--   @impresora  : nombre EXACTO de la impresora en Windows de esta PC.
--   @ipPinpad   : IP LAN de la pinpad física de esta caja.
--   @ipWorker   : IP LAN de ESTA PC (la que corre el worker).
--   @equipo     : nombre lógico del equipo (etiqueta en la tabla).
-- ============================================================

DECLARE @cia        INT           = 1;
DECLARE @sucursal   INT           = 97;                  -- <- 'PRUEBAS 2' en Cat_sucursales
DECLARE @caja       INT           = 1;                   -- <- AJUSTAR
DECLARE @usr        NVARCHAR(100) = N'a8626674';
DECLARE @pass       NVARCHAR(100) = N'!3g8Ft=Q';
DECLARE @idTerminal NVARCHAR(40)  = N'PENDIENTE';        -- <- T-ID Payworks real cuando lo tengas
DECLARE @afiliacion NVARCHAR(100) = N'8626674';
DECLARE @puerto     VARCHAR(10)   = '1818';
DECLARE @modo       VARCHAR(10)   = 'AUT';               -- AUT=pruebas sin cargo / PRD=produccion

DECLARE @equipo     NVARCHAR(60)  = N'PC-PRUEBAS';       -- <- etiqueta de este equipo
DECLARE @ipWorker   NVARCHAR(50)  = N'192.168.100.54';   -- <- IP de esta PC
DECLARE @hostWorker NVARCHAR(120) = N'win-81avr1hmbne.local';
DECLARE @puertoWkr  INT           = 5000;
DECLARE @impresora  NVARCHAR(150) = N'prueba';           -- <- impresora 'Tickets' en Windows
DECLARE @impresoraDoc NVARCHAR(150) = N'Microsoft Print to PDF'; -- <- impresora 'Documentos'
DECLARE @ipPinpad   NVARCHAR(150) = N'192.168.100.81';   -- <- IP de la pinpad (recurso TerminalBanorte)

-- ------------------------------------------------------------
-- 1) t_banorte_usuarios (PK: cia, sucursal, caja)
-- ------------------------------------------------------------
IF EXISTS (SELECT 1 FROM dbo.t_banorte_usuarios
           WHERE cia = @cia AND sucursal = @sucursal AND caja = @caja)
BEGIN
    UPDATE dbo.t_banorte_usuarios
    SET usr_banorte   = @usr,
        pass_banorte  = @pass,
        id_terminal   = @idTerminal,
        id_afiliacion = @afiliacion,
        puerto        = @puerto,
        modo          = @modo
    WHERE cia = @cia AND sucursal = @sucursal AND caja = @caja;
    PRINT 't_banorte_usuarios: actualizada (' + CAST(@sucursal AS varchar) + ', ' + CAST(@caja AS varchar) + ').';
END
ELSE
BEGIN
    INSERT INTO dbo.t_banorte_usuarios
        (cia, sucursal, caja, usr_banorte, pass_banorte, id_terminal, id_afiliacion, puerto, modo)
    VALUES
        (@cia, @sucursal, @caja, @usr, @pass, @idTerminal, @afiliacion, @puerto, @modo);
    PRINT 't_banorte_usuarios: insertada (' + CAST(@sucursal AS varchar) + ', ' + CAST(@caja AS varchar) + ').';
END

-- ------------------------------------------------------------
-- 2) cat_RecursosWorker (único: IdEmpresa, IdSucursal, Caja, Recurso)
--    Upsert por recurso para no chocar con el índice único.
-- ------------------------------------------------------------
DECLARE @recursos TABLE (Recurso NVARCHAR(40), NombreDispositivoLocal NVARCHAR(150));
INSERT INTO @recursos VALUES
    (N'Tickets',          @impresora),     -- impresora de tickets en esta PC
    (N'Documentos',       @impresoraDoc),  -- impresora de documentos/PDF
    (N'TerminalBanorte',  @ipPinpad);      -- IP del pinpad (la tabla no la tiene)

DECLARE @rec NVARCHAR(40), @dev NVARCHAR(150);
DECLARE cur CURSOR LOCAL FAST_FORWARD FOR SELECT Recurso, NombreDispositivoLocal FROM @recursos;
OPEN cur;
FETCH NEXT FROM cur INTO @rec, @dev;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF EXISTS (SELECT 1 FROM dbo.cat_RecursosWorker
               WHERE IdEmpresa = @cia AND IdSucursal = @sucursal
                 AND Caja = @caja AND Recurso = @rec)
        UPDATE dbo.cat_RecursosWorker
        SET Equipo = @equipo, IpWorker = @ipWorker, HostWorker = @hostWorker,
            Puerto = @puertoWkr, NombreDispositivoLocal = @dev, Activo = 1
        WHERE IdEmpresa = @cia AND IdSucursal = @sucursal
          AND Caja = @caja AND Recurso = @rec;
    ELSE
        INSERT INTO dbo.cat_RecursosWorker
            (IdEmpresa, IdSucursal, Caja, Equipo, Recurso, IpWorker, HostWorker,
             Puerto, NombreDispositivoLocal, Activo)
        VALUES
            (@cia, @sucursal, @caja, @equipo, @rec, @ipWorker, @hostWorker,
             @puertoWkr, @dev, 1);
    FETCH NEXT FROM cur INTO @rec, @dev;
END
CLOSE cur; DEALLOCATE cur;
PRINT 'cat_RecursosWorker: recursos Tickets/Documentos/TerminalBanorte dados de alta.';

-- ------------------------------------------------------------
-- Verificación (sin mostrar el password):
-- ------------------------------------------------------------
SELECT cia, sucursal, caja, usr_banorte, id_terminal, id_afiliacion, puerto, modo
FROM dbo.t_banorte_usuarios
WHERE cia = @cia AND sucursal = @sucursal AND caja = @caja;

SELECT IdRecurso, IdEmpresa, IdSucursal, Caja, Equipo, Recurso,
       IpWorker, HostWorker, Puerto, NombreDispositivoLocal, Activo
FROM dbo.cat_RecursosWorker
WHERE IdEmpresa = @cia AND IdSucursal = @sucursal AND Caja = @caja;
GO
