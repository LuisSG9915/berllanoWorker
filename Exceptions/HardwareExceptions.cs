namespace berllanoWorker.Exceptions;

public abstract class HardwareException : Exception
{
    public abstract int StatusCode { get; }

    protected HardwareException(string message) : base(message) { }
}

// 503: hardware fisico desconectado del puerto USB/COM o apagado.
public class HardwareDisconnectedException : HardwareException
{
    public override int StatusCode => StatusCodes.Status503ServiceUnavailable;
    public HardwareDisconnectedException(string message) : base(message) { }
}

// 423: recurso ocupado por otro proceso o hilo (impresion en curso, etc).
public class HardwareBusyException : HardwareException
{
    public override int StatusCode => StatusCodes.Status423Locked;
    public HardwareBusyException(string message) : base(message) { }
}

// 408: el hardware respondia, pero el usuario no interactuo a tiempo.
public class HardwareTimeoutException : HardwareException
{
    public override int StatusCode => StatusCodes.Status408RequestTimeout;
    public HardwareTimeoutException(string message) : base(message) { }
}

// 422: el hardware funciona, pero los datos pasados no son validos
// (impresora inexistente, URL rota, modo de linea desconocido).
public class HardwareValidationException : HardwareException
{
    public override int StatusCode => StatusCodes.Status422UnprocessableEntity;
    public HardwareValidationException(string message) : base(message) { }
}
