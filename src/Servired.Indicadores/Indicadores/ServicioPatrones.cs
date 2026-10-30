using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Servired.Indicadores.Datos;

namespace Servired.Indicadores.Indicadores;

public class OpcionesPatrones
{
    public int RadioMetros { get; set; } = 1000;
    public int VentanaDias { get; set; } = 90;
}

/// <summary>Respuesta de <c>GET /internal/indicadores/patrones</c> (esquema <c>PatronZona</c>).</summary>
public record PatronZona(
    long IdServicio, int RadioMetros, int VentanaDias, int FraudesValidados, int FraudesRechazados, int CortesReportados);

/// <summary>
/// Patrón histórico alrededor de un punto, para que Moderación decida sobre un reporte de fraude.
/// Sin PostGIS (solo lo tiene Reportes): filtra por caja en SQL y luego por distancia real.
/// </summary>
public class ServicioPatrones(IndicadoresDbContext db, IOptions<OpcionesPatrones> opciones, TimeProvider reloj)
{
    public async Task<PatronZona> ConsultarAsync(long idServicio, double lat, double lng, CancellationToken ct = default)
    {
        var o = opciones.Value;
        var desde = reloj.GetUtcNow().AddDays(-o.VentanaDias);
        var caja = Geo.Caja(lat, lng, o.RadioMetros);

        var candidatas = await db.Novedades
            .AsNoTracking()
            .Where(n => n.IdServicio == idServicio
                && n.Fecha >= desde
                && n.Lat >= caja.MinLat && n.Lat <= caja.MaxLat
                && n.Lng >= caja.MinLng && n.Lng <= caja.MaxLng)
            .Select(n => new { n.TipoNovedad, n.ResultadoFraude, n.Lat, n.Lng })
            .ToListAsync(ct);

        var cercanas = candidatas.Where(n => Geo.DistanciaMetros(lat, lng, n.Lat!.Value, n.Lng!.Value) <= o.RadioMetros).ToList();

        return new PatronZona(
            idServicio,
            o.RadioMetros,
            o.VentanaDias,
            FraudesValidados: cercanas.Count(n => n.TipoNovedad == TiposNovedad.Fraude && n.ResultadoFraude == ResultadosFraude.Validado),
            // ⚠ fraude.rechazado v1 no trae lat/lng: hoy no se puede ubicar y este conteo queda en 0.
            FraudesRechazados: cercanas.Count(n => n.TipoNovedad == TiposNovedad.Fraude && n.ResultadoFraude == ResultadosFraude.Rechazado),
            CortesReportados: cercanas.Count(n => n.TipoNovedad == TiposNovedad.Corte));
    }
}
