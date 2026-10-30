using System.Text.Json;
using System.Text.Json.Serialization;

namespace Servired.Indicadores.Eventos;

// DTOs de servired-infra/contracts/events (v1). Si el contrato cambia, se actualizan aquí y en
// tests/Servired.Indicadores.Tests/Contratos/eventos.

/// <summary>Sobre común (<c>envelope.schema.json</c>).</summary>
public record SobreEvento(
    Guid EventId,
    string Type,
    int Version,
    DateTimeOffset OccurredAt,
    string Source,
    Guid CorrelationId,
    JsonElement Data);

public record ReporteCreado(
    long IdReporte,
    string CodigoTrazabilidad,
    long IdServicio,
    string TipoServicio,
    string? Severidad,
    string? Departamento,
    string? Municipio,
    double Lat,
    double Lng,
    DateTimeOffset FechaCreacion);

public record ReporteRestablecido(
    long IdReporte,
    long IdServicio,
    string? Departamento,
    string? Municipio,
    DateTimeOffset FechaCreacion,
    DateTimeOffset FechaResolucion,
    double DuracionInterrupcionHoras);

public record ValidacionRegistrada(long IdReporte, int TotalValidaciones);

// Anonimato verificable: un evento de fraude con campos extra (p. ej. idUsuario) se rechaza y va a la DLQ.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public record FraudeValidado(long IdFraude, long IdServicio, double Lat, double Lng, DateTimeOffset Fecha);

/// <summary>
/// El contrato v1 no trae ubicación. <c>Lat</c>/<c>Lng</c> se leen si algún día llegan (propuesta para v0.2),
/// porque sin ellas el rechazo no se puede contar en el patrón por radio.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public record FraudeRechazado(long IdFraude, long IdServicio, DateTimeOffset Fecha, double? Lat = null, double? Lng = null);

public static class TiposEvento
{
    public const string ReporteCreado = "reporte.creado";
    public const string ReporteRestablecido = "reporte.restablecido";
    public const string ValidacionRegistrada = "validacion.registrada";
    public const string FraudeValidado = "fraude.validado";
    public const string FraudeRechazado = "fraude.rechazado";
}

/// <summary>El mensaje no cumple el contrato: no tiene sentido reintentarlo, va a la cola de errores.</summary>
public class EventoInvalidoException(string mensaje, Exception? causa = null) : Exception(mensaje, causa);
