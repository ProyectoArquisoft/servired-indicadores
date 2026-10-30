namespace Servired.Indicadores.Datos;

/// <summary>Fila de la tabla <c>indicador_agregado</c>: lo que expone la API pública.</summary>
public class IndicadorAgregado
{
    public long IdIndicador { get; set; }
    public long IdServicio { get; set; }
    public string TipoServicio { get; set; } = "";
    public string TipoNovedad { get; set; } = "";
    public string Departamento { get; set; } = "";
    public string Municipio { get; set; } = "";
    /// <summary>Mes <c>YYYY-MM</c> en hora de Colombia.</summary>
    public string Periodo { get; set; } = "";
    public double? HorasInterrupcionProm { get; set; }
    public int FrecuenciaCortes { get; set; }
}

/// <summary>Registro de idempotencia: un evento con el mismo <c>eventId</c> no se aplica dos veces.</summary>
public class EventoConsumido
{
    public Guid IdEvento { get; set; }
    public string TipoEvento { get; set; } = "";
    public long IdOrigen { get; set; }
    public DateTimeOffset FechaConsumo { get; set; }
}

/// <summary>
/// Detalle de cada corte o fraude recibido por eventos (tabla <c>novedad</c>, adicional al modelo del SAD).
/// Permite recalcular <see cref="IndicadorAgregado"/> aunque los eventos lleguen desordenados y responder
/// <c>/internal/indicadores/patrones</c>, que cuenta por radio y no por municipio. No guarda datos de usuario.
/// </summary>
public class Novedad
{
    public long IdNovedad { get; set; }
    public string TipoNovedad { get; set; } = "";
    /// <summary><c>idReporte</c> para cortes, <c>idFraude</c> para fraudes.</summary>
    public long IdOrigen { get; set; }
    public long IdServicio { get; set; }
    public string? TipoServicio { get; set; }
    public string Departamento { get; set; } = Zona.SinDato;
    public string Municipio { get; set; } = Zona.SinDato;
    public double? Lat { get; set; }
    public double? Lng { get; set; }
    /// <summary>Fecha de creación del reporte o fecha del fraude (UTC).</summary>
    public DateTimeOffset Fecha { get; set; }
    public string Periodo { get; set; } = "";
    public DateTimeOffset? FechaResolucion { get; set; }
    public double? DuracionInterrupcionHoras { get; set; }
    public int TotalValidaciones { get; set; }
    /// <summary>Solo fraudes: <c>VALIDADO</c> o <c>RECHAZADO</c>.</summary>
    public string? ResultadoFraude { get; set; }
}

public static class TiposNovedad
{
    public const string Corte = "CORTE";
    public const string Fraude = "FRAUDE";
}

public static class ResultadosFraude
{
    public const string Validado = "VALIDADO";
    public const string Rechazado = "RECHAZADO";
}
