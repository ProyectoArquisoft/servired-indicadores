using Microsoft.EntityFrameworkCore;
using Servired.Indicadores.Datos;

namespace Servired.Indicadores.Indicadores;

/// <summary>Fila de <c>indicador_agregado</c> que un evento puede afectar.</summary>
public record ClaveIndicador(string TipoNovedad, long IdServicio, string Departamento, string Municipio, string Periodo)
{
    public static ClaveIndicador De(Novedad n) => new(n.TipoNovedad, n.IdServicio, n.Departamento, n.Municipio, n.Periodo);
}

/// <summary>
/// Recalcula una fila de <c>indicador_agregado</c> desde <c>novedad</c> en vez de sumar incrementalmente:
/// así el resultado no depende del orden en que lleguen los eventos.
/// </summary>
public class RecalculadorIndicadores(IndicadoresDbContext db)
{
    public async Task RecalcularAsync(ClaveIndicador clave, CancellationToken ct = default)
    {
        var novedades = db.Novedades.Where(n =>
            n.TipoNovedad == clave.TipoNovedad
            && n.IdServicio == clave.IdServicio
            && n.Departamento == clave.Departamento
            && n.Municipio == clave.Municipio
            && n.Periodo == clave.Periodo);

        var resumen = await novedades
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total = g.Count(),
                HorasProm = g.Average(n => n.DuracionInterrupcionHoras),
                TipoServicio = g.Max(n => n.TipoServicio),
            })
            .FirstOrDefaultAsync(ct);

        // reporte.restablecido no trae tipoServicio: se toma de cualquier otro reporte del mismo servicio.
        var tipoServicio = resumen?.TipoServicio
            ?? await db.Novedades
                .Where(n => n.IdServicio == clave.IdServicio && n.TipoServicio != null)
                .Select(n => n.TipoServicio)
                .FirstOrDefaultAsync(ct);

        var fila = await db.Indicadores.FirstOrDefaultAsync(i =>
            i.TipoNovedad == clave.TipoNovedad
            && i.IdServicio == clave.IdServicio
            && i.Departamento == clave.Departamento
            && i.Municipio == clave.Municipio
            && i.Periodo == clave.Periodo, ct);

        // Sin novedades, o sin tipoServicio todavía (llegará con reporte.creado): no se publica la fila.
        if (resumen is null || resumen.Total == 0 || tipoServicio is null)
        {
            if (fila is not null) db.Indicadores.Remove(fila);
            await db.SaveChangesAsync(ct);
            return;
        }

        if (fila is null)
        {
            fila = new IndicadorAgregado
            {
                TipoNovedad = clave.TipoNovedad,
                IdServicio = clave.IdServicio,
                Departamento = clave.Departamento,
                Municipio = clave.Municipio,
                Periodo = clave.Periodo,
            };
            db.Indicadores.Add(fila);
        }

        fila.TipoServicio = tipoServicio;
        fila.FrecuenciaCortes = resumen.Total;
        fila.HorasInterrupcionProm = resumen.HorasProm is { } h ? Math.Round(h, 2) : null;
        await db.SaveChangesAsync(ct);
    }
}
