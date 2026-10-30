using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Servired.Indicadores.Datos;
using Servired.Indicadores.Indicadores;
using Servired.Indicadores.Seguridad;

namespace Servired.Indicadores.Api;

/// <summary>Rutas de <c>servired-infra/contracts/openapi/indicadores.yaml</c> v0.1.0.</summary>
public static partial class IndicadoresEndpoints
{
    public record IndicadorDto(
        long IdIndicador, long IdServicio, string TipoServicio, string TipoNovedad, string Departamento,
        string Municipio, string Periodo, double? HorasInterrupcionProm, int FrecuenciaCortes);

    public record Pagina<T>(IReadOnlyList<T> Items, int Page, int Size, long Total);

    public record FiltroIndicadores(long? IdServicio, string? Departamento, string? Municipio, string? Periodo);

    public static void MapearIndicadores(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/indicadores", ConsultarAsync).WithName("consultarIndicadores").WithTags("Indicadores");
        app.MapGet("/api/indicadores/export", ExportarAsync).WithName("exportarIndicadores").WithTags("Indicadores")
            .RequireAuthorization(Roles.PoliticaExportar);
        app.MapGet("/internal/indicadores/patrones", ConsultarPatronesAsync).WithName("consultarPatronesZona").WithTags("Interno");
    }

    private static async Task<IResult> ConsultarAsync(
        IndicadoresDbContext db, long? idServicio, string? departamento, string? municipio, string? periodo,
        int? page, int? size, CancellationToken ct)
    {
        var p = page ?? 0;
        var s = size ?? 20;
        var errores = new List<Problemas.ErrorCampo>();
        ValidarPeriodo(periodo, errores);
        if (p < 0) errores.Add(new("page", "Debe ser mayor o igual a 0."));
        if (s is < 1 or > 100) errores.Add(new("size", "Debe estar entre 1 y 100."));
        if (errores.Count > 0) return Problemas.SolicitudInvalida(errores);

        var consulta = Filtrar(db, new(idServicio, departamento, municipio, periodo));
        var total = await consulta.LongCountAsync(ct);
        var items = await Ordenar(consulta).Skip(p * s).Take(s).Select(Proyeccion).ToListAsync(ct);

        return Results.Ok(new Pagina<IndicadorDto>(items, p, s, total));
    }

    private static async Task<IResult> ExportarAsync(
        HttpContext http, IndicadoresDbContext db, string? formato, long? idServicio, string? departamento,
        string? municipio, string? periodo, CancellationToken ct)
    {
        var errores = new List<Problemas.ErrorCampo>();
        if (formato is not ("csv" or "json")) errores.Add(new("formato", "Debe ser csv o json."));
        ValidarPeriodo(periodo, errores);
        if (errores.Count > 0) return Problemas.SolicitudInvalida(errores);

        var items = await Ordenar(Filtrar(db, new(idServicio, departamento, municipio, periodo)))
            .Select(Proyeccion).ToListAsync(ct);

        var nombre = $"indicadores-{periodo ?? "todos"}.{formato}";
        http.Response.Headers.ContentDisposition = $"attachment; filename=\"{nombre}\"";

        return formato == "csv"
            ? Results.Text(ExportadorCsv.Generar(items), "text/csv; charset=utf-8")
            : Results.Json(items);
    }

    private static async Task<IResult> ConsultarPatronesAsync(
        ServicioPatrones patrones, long? idServicio, double? lat, double? lng, CancellationToken ct)
    {
        var errores = new List<Problemas.ErrorCampo>();
        if (idServicio is null) errores.Add(new("idServicio", "Es obligatorio."));
        if (lat is null or < -90 or > 90) errores.Add(new("lat", "Es obligatorio y debe estar entre -90 y 90."));
        if (lng is null or < -180 or > 180) errores.Add(new("lng", "Es obligatorio y debe estar entre -180 y 180."));
        if (errores.Count > 0) return Problemas.SolicitudInvalida(errores);

        return Results.Ok(await patrones.ConsultarAsync(idServicio!.Value, lat!.Value, lng!.Value, ct));
    }

    private static IQueryable<IndicadorAgregado> Filtrar(IndicadoresDbContext db, FiltroIndicadores f)
    {
        var q = db.Indicadores.AsNoTracking();
        if (f.IdServicio is { } id) q = q.Where(i => i.IdServicio == id);
        if (!string.IsNullOrWhiteSpace(f.Departamento))
        {
            var dep = f.Departamento.Trim().ToLower();
            q = q.Where(i => i.Departamento.ToLower() == dep);
        }
        if (!string.IsNullOrWhiteSpace(f.Municipio))
        {
            var mun = f.Municipio.Trim().ToLower();
            q = q.Where(i => i.Municipio.ToLower() == mun);
        }
        if (!string.IsNullOrEmpty(f.Periodo)) q = q.Where(i => i.Periodo == f.Periodo);
        return q;
    }

    private static IQueryable<IndicadorAgregado> Ordenar(IQueryable<IndicadorAgregado> q) =>
        q.OrderByDescending(i => i.Periodo)
            .ThenBy(i => i.Departamento)
            .ThenBy(i => i.Municipio)
            .ThenBy(i => i.IdServicio)
            .ThenBy(i => i.TipoNovedad);

    private static readonly System.Linq.Expressions.Expression<Func<IndicadorAgregado, IndicadorDto>> Proyeccion =
        i => new IndicadorDto(i.IdIndicador, i.IdServicio, i.TipoServicio, i.TipoNovedad, i.Departamento,
            i.Municipio, i.Periodo, i.HorasInterrupcionProm, i.FrecuenciaCortes);

    private static void ValidarPeriodo(string? periodo, List<Problemas.ErrorCampo> errores)
    {
        if (periodo is not null && !PatronPeriodo().IsMatch(periodo))
            errores.Add(new("periodo", "Debe tener el formato YYYY-MM."));
    }

    [GeneratedRegex(@"^\d{4}-(0[1-9]|1[0-2])$")]
    private static partial Regex PatronPeriodo();
}
