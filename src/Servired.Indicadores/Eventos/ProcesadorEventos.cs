using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Servired.Indicadores.Datos;
using Servired.Indicadores.Indicadores;

namespace Servired.Indicadores.Eventos;

public enum ResultadoProcesamiento
{
    Aplicado,
    /// <summary>El <c>eventId</c> ya se había consumido (reentrega del broker o reintento del publicador).</summary>
    Duplicado,
    /// <summary>Evento válido que no cambia ningún indicador.</summary>
    SinEfecto,
}

/// <summary>
/// Aplica un evento en una sola transacción: registra el <c>eventId</c> en <c>evento_consumido</c>,
/// actualiza la novedad y recalcula los indicadores afectados. Si algo falla no queda nada a medias.
/// </summary>
public class ProcesadorEventos(IndicadoresDbContext db, RecalculadorIndicadores recalculador, TimeProvider reloj)
{
    private static readonly HashSet<string> TiposServicio = ["ENERGIA", "GAS", "ACUEDUCTO", "ALCANTARILLADO", "ASEO"];

    // Campos obligatorios del contrato: si faltan o vienen null el evento es inválido (no un 0 silencioso).
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
    };

    public async Task<ResultadoProcesamiento> ProcesarAsync(ReadOnlyMemory<byte> cuerpo, CancellationToken ct = default)
    {
        var sobre = Deserializar<SobreEvento>(cuerpo.Span, "sobre");
        if (sobre.EventId == Guid.Empty || string.IsNullOrEmpty(sobre.Type))
            throw new EventoInvalidoException("El sobre no tiene eventId o type.");
        if (sobre.Version != 1)
            throw new EventoInvalidoException($"Versión {sobre.Version} de {sobre.Type} no soportada.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        if (await db.EventosConsumidos.AnyAsync(e => e.IdEvento == sobre.EventId, ct))
            return ResultadoProcesamiento.Duplicado;

        var (idOrigen, claves) = sobre.Type switch
        {
            TiposEvento.ReporteCreado => await AplicarAsync(Datos<ReporteCreado>(sobre), ct),
            TiposEvento.ReporteRestablecido => await AplicarAsync(Datos<ReporteRestablecido>(sobre), ct),
            TiposEvento.ValidacionRegistrada => await AplicarAsync(Datos<ValidacionRegistrada>(sobre), ct),
            TiposEvento.FraudeValidado => await AplicarAsync(Datos<FraudeValidado>(sobre), ct),
            TiposEvento.FraudeRechazado => await AplicarAsync(Datos<FraudeRechazado>(sobre), ct),
            _ => throw new EventoInvalidoException($"Tipo de evento desconocido: {sobre.Type}."),
        };

        db.EventosConsumidos.Add(new EventoConsumido
        {
            IdEvento = sobre.EventId,
            TipoEvento = sobre.Type,
            IdOrigen = idOrigen,
            FechaConsumo = reloj.GetUtcNow(),
        });
        await db.SaveChangesAsync(ct);

        foreach (var clave in claves.Distinct())
            await recalculador.RecalcularAsync(clave, ct);

        await tx.CommitAsync(ct);
        return claves.Count > 0 ? ResultadoProcesamiento.Aplicado : ResultadoProcesamiento.SinEfecto;
    }

    private async Task<(long, List<ClaveIndicador>)> AplicarAsync(ReporteCreado e, CancellationToken ct)
    {
        if (!TiposServicio.Contains(e.TipoServicio))
            throw new EventoInvalidoException($"tipoServicio desconocido: {e.TipoServicio}.");

        var corte = await BuscarAsync(TiposNovedad.Corte, e.IdReporte, ct);
        var claveAnterior = corte is null ? null : ClaveIndicador.De(corte);

        if (corte is null)
        {
            corte = new Novedad { TipoNovedad = TiposNovedad.Corte, IdOrigen = e.IdReporte };
            db.Novedades.Add(corte);
        }

        // Si ya llegó el restablecimiento, se conserva la duración y solo se completa lo que faltaba.
        corte.IdServicio = e.IdServicio;
        corte.TipoServicio = e.TipoServicio;
        corte.Departamento = Zona.Normalizar(e.Departamento);
        corte.Municipio = Zona.Normalizar(e.Municipio);
        corte.Lat = e.Lat;
        corte.Lng = e.Lng;
        corte.Fecha = e.FechaCreacion.ToUniversalTime();
        corte.Periodo = Zona.Periodo(e.FechaCreacion);
        await db.SaveChangesAsync(ct);

        return (e.IdReporte, Claves(claveAnterior, ClaveIndicador.De(corte)));
    }

    private async Task<(long, List<ClaveIndicador>)> AplicarAsync(ReporteRestablecido e, CancellationToken ct)
    {
        var corte = await BuscarAsync(TiposNovedad.Corte, e.IdReporte, ct);
        var claveAnterior = corte is null ? null : ClaveIndicador.De(corte);

        if (corte is null)
        {
            // Llegó antes que reporte.creado: se crea con lo que trae; tipoServicio y ubicación llegan después.
            corte = new Novedad
            {
                TipoNovedad = TiposNovedad.Corte,
                IdOrigen = e.IdReporte,
                IdServicio = e.IdServicio,
                Departamento = Zona.Normalizar(e.Departamento),
                Municipio = Zona.Normalizar(e.Municipio),
                Fecha = e.FechaCreacion.ToUniversalTime(),
                Periodo = Zona.Periodo(e.FechaCreacion),
            };
            db.Novedades.Add(corte);
        }

        corte.FechaResolucion = e.FechaResolucion.ToUniversalTime();
        corte.DuracionInterrupcionHoras = e.DuracionInterrupcionHoras;
        await db.SaveChangesAsync(ct);

        return (e.IdReporte, Claves(claveAnterior, ClaveIndicador.De(corte)));
    }

    private async Task<(long, List<ClaveIndicador>)> AplicarAsync(ValidacionRegistrada e, CancellationToken ct)
    {
        // No cambia ningún indicador; se guarda el conteo si el corte ya existe.
        var corte = await BuscarAsync(TiposNovedad.Corte, e.IdReporte, ct);
        if (corte is not null && e.TotalValidaciones > corte.TotalValidaciones)
        {
            corte.TotalValidaciones = e.TotalValidaciones;
            await db.SaveChangesAsync(ct);
        }
        return (e.IdReporte, []);
    }

    private async Task<(long, List<ClaveIndicador>)> AplicarAsync(FraudeValidado e, CancellationToken ct)
    {
        await GuardarFraudeAsync(e.IdFraude, e.IdServicio, e.Fecha, e.Lat, e.Lng, ResultadosFraude.Validado, ct);
        // ⚠ Pendiente §11.4 del brief: sin departamento/municipio no hay fila FRAUDE en indicador_agregado.
        return (e.IdFraude, []);
    }

    private async Task<(long, List<ClaveIndicador>)> AplicarAsync(FraudeRechazado e, CancellationToken ct)
    {
        await GuardarFraudeAsync(e.IdFraude, e.IdServicio, e.Fecha, e.Lat, e.Lng, ResultadosFraude.Rechazado, ct);
        return (e.IdFraude, []);
    }

    private async Task GuardarFraudeAsync(
        long idFraude, long idServicio, DateTimeOffset fecha, double? lat, double? lng, string resultado, CancellationToken ct)
    {
        var fraude = await BuscarAsync(TiposNovedad.Fraude, idFraude, ct);
        if (fraude is null)
        {
            fraude = new Novedad { TipoNovedad = TiposNovedad.Fraude, IdOrigen = idFraude };
            db.Novedades.Add(fraude);
        }

        fraude.IdServicio = idServicio;
        fraude.Fecha = fecha.ToUniversalTime();
        fraude.Periodo = Zona.Periodo(fecha);
        fraude.Lat = lat ?? fraude.Lat;
        fraude.Lng = lng ?? fraude.Lng;
        fraude.ResultadoFraude = resultado;
        await db.SaveChangesAsync(ct);
    }

    private Task<Novedad?> BuscarAsync(string tipo, long idOrigen, CancellationToken ct) =>
        db.Novedades.FirstOrDefaultAsync(n => n.TipoNovedad == tipo && n.IdOrigen == idOrigen, ct);

    private static List<ClaveIndicador> Claves(ClaveIndicador? anterior, ClaveIndicador actual) =>
        anterior is null || anterior == actual ? [actual] : [anterior, actual];

    private static T Datos<T>(SobreEvento sobre)
    {
        if (sobre.Data.ValueKind != JsonValueKind.Object)
            throw new EventoInvalidoException($"{sobre.Type} sin data.");
        return Deserializar<T>(sobre.Data.GetRawText(), sobre.Type);
    }

    private static T Deserializar<T>(ReadOnlySpan<byte> json, string que)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, Json) ?? throw new EventoInvalidoException($"{que} vacío.");
        }
        catch (JsonException ex)
        {
            throw new EventoInvalidoException($"{que} no cumple el contrato: {ex.Message}", ex);
        }
    }

    private static T Deserializar<T>(string json, string que) =>
        Deserializar<T>(System.Text.Encoding.UTF8.GetBytes(json), que);
}
