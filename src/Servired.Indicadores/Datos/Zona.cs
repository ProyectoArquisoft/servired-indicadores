namespace Servired.Indicadores.Datos;

/// <summary>Normalización de departamento / municipio y del periodo mensual.</summary>
public static class Zona
{
    /// <summary>Valor usado cuando el evento no trae la zona (el contrato permite <c>null</c>).</summary>
    public const string SinDato = "Sin información";

    // Colombia no tiene horario de verano: UTC-5 fijo.
    private static readonly TimeSpan HoraColombia = TimeSpan.FromHours(-5);

    public static string Normalizar(string? valor) =>
        string.IsNullOrWhiteSpace(valor) ? SinDato : valor.Trim();

    /// <summary>Periodo <c>YYYY-MM</c> según la hora de Colombia (un corte del 31 a las 22:00 cuenta en ese mes).</summary>
    public static string Periodo(DateTimeOffset fecha) =>
        fecha.ToOffset(HoraColombia).ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
}
