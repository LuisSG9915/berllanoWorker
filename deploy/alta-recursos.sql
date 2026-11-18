
CREATE TABLE [dbo].[cat_RecursosWorker](
	[IdRecurso] [int] IDENTITY(1,1) NOT NULL,
	[IdEmpresa] [int] NOT NULL,
	[IdSucursal] [int] NOT NULL,
	[Equipo] [varchar](60) NOT NULL,
	[Recurso] [varchar](40) NOT NULL,
	[IpWorker] [varchar](50) NOT NULL,
	[HostWorker] [varchar](120) NULL,
	[Puerto] [int] NOT NULL,
	[NombreDispositivoLocal] [varchar](150) NULL,
	[Activo] [bit] NOT NULL,
	[FechaRegistro] [datetime] NOT NULL,
	[Caja] [int] NOT NULL,
PRIMARY KEY CLUSTERED 
(
	[IdRecurso] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
GO

ALTER TABLE [dbo].[cat_RecursosWorker] ADD  DEFAULT ((5000)) FOR [Puerto]
GO

ALTER TABLE [dbo].[cat_RecursosWorker] ADD  DEFAULT ((1)) FOR [Activo]
GO

ALTER TABLE [dbo].[cat_RecursosWorker] ADD  DEFAULT (getdate()) FOR [FechaRegistro]
GO

ALTER TABLE [dbo].[cat_RecursosWorker] ADD  CONSTRAINT [DF_cat_RecursosWorker_Caja]  DEFAULT ((1)) FOR [Caja]
GO




USE [CBERP]
GO

/****** Objeto: StoredProcedure [dbo].[sp_cat_RecursosWorker_Eliminar] Fecha de script: 11/17/2026 2:37:55 PM ******/
SET ANSI_NULLS ON
GO

SET QUOTED_IDENTIFIER OFF
GO

CREATE   PROCEDURE [dbo].[sp_cat_RecursosWorker_Eliminar]
    @IdRecurso INT = NULL,
    @IdEmpresa INT = 1,
    @IdSucursal INT = NULL,
    @Caja INT = NULL,
    @Recurso NVARCHAR(40) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF @IdRecurso IS NOT NULL
        DELETE FROM dbo.cat_RecursosWorker WHERE IdRecurso = @IdRecurso;
    ELSE IF @IdSucursal IS NOT NULL AND @Caja IS NOT NULL AND @Recurso IS NOT NULL
        DELETE FROM dbo.cat_RecursosWorker
        WHERE IdEmpresa=@IdEmpresa AND IdSucursal=@IdSucursal
          AND Caja=@Caja AND Recurso=@Recurso;
    ELSE
        RAISERROR('Se requiere @IdRecurso o la llave completa (@IdSucursal,@Caja,@Recurso).', 16, 1);
END

GO


USE [CBERP]
GO

/****** Objeto: StoredProcedure [dbo].[sp_cat_RecursosWorker_Guardar] Fecha de script: 11/17/2026 2:38:07 PM ******/
SET ANSI_NULLS ON
GO

SET QUOTED_IDENTIFIER OFF
GO

CREATE   PROCEDURE [dbo].[sp_cat_RecursosWorker_Guardar]
    @IdEmpresa INT = 1,
    @IdSucursal INT,
    @Caja INT = 1,
    @Equipo NVARCHAR(60),
    @Recurso NVARCHAR(40),
    @IpWorker NVARCHAR(50),
    @HostWorker NVARCHAR(120) = NULL,
    @Puerto INT = 5000,
    @NombreDispositivoLocal NVARCHAR(150) = NULL,
    @Activo BIT = 1
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM dbo.cat_RecursosWorker
               WHERE IdEmpresa=@IdEmpresa AND IdSucursal=@IdSucursal
                 AND Caja=@Caja AND Recurso=@Recurso)
    BEGIN
        UPDATE dbo.cat_RecursosWorker
        SET Equipo=@Equipo, IpWorker=@IpWorker, HostWorker=@HostWorker,
            Puerto=@Puerto, NombreDispositivoLocal=@NombreDispositivoLocal,
            Activo=@Activo
        WHERE IdEmpresa=@IdEmpresa AND IdSucursal=@IdSucursal
          AND Caja=@Caja AND Recurso=@Recurso;
        SELECT IdRecurso FROM dbo.cat_RecursosWorker
        WHERE IdEmpresa=@IdEmpresa AND IdSucursal=@IdSucursal
          AND Caja=@Caja AND Recurso=@Recurso;
    END
    ELSE
    BEGIN
        INSERT INTO dbo.cat_RecursosWorker
            (IdEmpresa, IdSucursal, Caja, Equipo, Recurso, IpWorker, HostWorker,
             Puerto, NombreDispositivoLocal, Activo)
        VALUES (@IdEmpresa, @IdSucursal, @Caja, @Equipo, @Recurso, @IpWorker,
                @HostWorker, @Puerto, @NombreDispositivoLocal, @Activo);
        SELECT SCOPE_IDENTITY() AS IdRecurso;
    END
END

GO

USE [CBERP]
GO

/****** Objeto: StoredProcedure [dbo].[sp_cat_RecursosWorker_Obtener] Fecha de script: 11/17/2026 2:38:16 PM ******/
SET ANSI_NULLS ON
GO

SET QUOTED_IDENTIFIER OFF
GO


CREATE   PROCEDURE [dbo].[sp_cat_RecursosWorker_Obtener]
    @IdEmpresa INT = 1,
    @IdSucursal INT = NULL,
    @Caja INT = NULL,
    @Recurso NVARCHAR(50) = NULL,
    @SoloActivos BIT = 0
AS
BEGIN
    SET NOCOUNT ON;
    SELECT IdRecurso, IdEmpresa, IdSucursal, Caja, Equipo, Recurso,
           IpWorker, HostWorker, Puerto, NombreDispositivoLocal, Activo, FechaRegistro
    FROM dbo.cat_RecursosWorker WITH (NOLOCK)
    WHERE IdEmpresa = @IdEmpresa
      AND (@IdSucursal IS NULL OR IdSucursal = @IdSucursal)
      AND (@Caja IS NULL OR Caja = @Caja)
      AND (@Recurso IS NULL OR Recurso = @Recurso)
      AND (@SoloActivos = 0 OR Activo = 1)
    ORDER BY IdSucursal, Caja, Recurso;
END;
SELECT 'Obtener OK' AS r;

GO




