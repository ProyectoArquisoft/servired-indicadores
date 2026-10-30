using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Servired.Indicadores.Datos;
using Servired.Indicadores.Eventos;

namespace Servired.Indicadores.Tests.Integracion;

[Collection(ColeccionIntegracion.Nombre)]
public class ProcesadorEventosTests(Entorno entorno) : IAsyncLifetime
{
    public Task InitializeAsync() => entorno.App is null ? Task.CompletedTask : entorno.LimpiarAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<ResultadoProcesamiento> ProcesarAsync(string json)
    {
        await using var scope = entorno.App.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ProcesadorEventos>().ProcesarAsync(Eventos.Bytes(json));
    }

    private async Task<List<IndicadorAgregado>> IndicadoresAsync()
    {
        await using var scope = entorno.App.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IndicadoresDbContext>().Indicadores.AsNoTracking().ToListAsync();
    }

    [DockerFact]
    public async Task Reporte_creado_genera_el_indicador_del_mes()
    {
        Assert.Equal(ResultadoProcesamiento.Aplicado, await ProcesarAsync(Eventos.Ejemplo("reporte.creado.example.json")));

        var i = Assert.Single(await IndicadoresAsync());
        Assert.Equal(("ENERGIA", "CORTE", "Cundinamarca", "Bogotá D.C.", "2026-10"),
            (i.TipoServicio, i.TipoNovedad, i.Departamento, i.Municipio, i.Periodo));
        Assert.Equal(1, i.FrecuenciaCortes);
        Assert.Null(i.HorasInterrupcionProm);
    }

    [DockerFact]
    public async Task Restablecimiento_calcula_horas_promedio()
    {
        await ProcesarAsync(Eventos.Ejemplo("reporte.creado.example.json"));
        await ProcesarAsync(Eventos.Ejemplo("reporte.restablecido.example.json"));

        var i = Assert.Single(await IndicadoresAsync());
        Assert.Equal(1, i.FrecuenciaCortes);
        Assert.Equal(5.33, i.HorasInterrupcionProm);
    }

    [DockerFact]
    public async Task Un_evento_repetido_no_se_cuenta_dos_veces()
    {
        var creado = Eventos.Ejemplo("reporte.creado.example.json");
        await ProcesarAsync(creado);

        Assert.Equal(ResultadoProcesamiento.Duplicado, await ProcesarAsync(creado));
        Assert.Equal(1, Assert.Single(await IndicadoresAsync()).FrecuenciaCortes);
    }

    [DockerFact]
    public async Task Eventos_desordenados_dan_el_mismo_resultado()
    {
        await ProcesarAsync(Eventos.Ejemplo("reporte.restablecido.example.json"));
        // Sin reporte.creado todavía no se conoce tipoServicio: no se publica la fila.
        Assert.Empty(await IndicadoresAsync());

        await ProcesarAsync(Eventos.Ejemplo("reporte.creado.example.json"));

        var i = Assert.Single(await IndicadoresAsync());
        Assert.Equal(1, i.FrecuenciaCortes);
        Assert.Equal(5.33, i.HorasInterrupcionProm);
    }

    [DockerFact]
    public async Task Promedia_varios_cortes_de_la_misma_zona()
    {
        await ProcesarAsync(Eventos.Ejemplo("reporte.creado.example.json"));
        await ProcesarAsync(Eventos.Ejemplo("reporte.restablecido.example.json"));

        var otro = Eventos.ConNuevoId(Eventos.Ejemplo("reporte.creado.example.json")).Replace("\"idReporte\": 1024", "\"idReporte\": 1025");
        await ProcesarAsync(otro);
        var otroRestablecido = Eventos.ConNuevoId(Eventos.Ejemplo("reporte.restablecido.example.json"))
            .Replace("\"idReporte\": 1024", "\"idReporte\": 1025")
            .Replace("5.33", "2.67");
        await ProcesarAsync(otroRestablecido);

        var i = Assert.Single(await IndicadoresAsync());
        Assert.Equal(2, i.FrecuenciaCortes);
        Assert.Equal(4.0, i.HorasInterrupcionProm);
    }

    [DockerFact]
    public async Task Validacion_y_fraudes_no_cambian_indicadores()
    {
        Assert.Equal(ResultadoProcesamiento.SinEfecto, await ProcesarAsync(Eventos.Ejemplo("validacion.registrada.example.json")));
        Assert.Equal(ResultadoProcesamiento.SinEfecto, await ProcesarAsync(Eventos.Ejemplo("fraude.validado.example.json")));
        Assert.Equal(ResultadoProcesamiento.SinEfecto, await ProcesarAsync(Eventos.Ejemplo("fraude.rechazado.example.json")));
        Assert.Empty(await IndicadoresAsync());
    }

    [DockerFact]
    public async Task Fraude_con_datos_de_usuario_se_rechaza_sin_guardar_nada()
    {
        await Assert.ThrowsAsync<EventoInvalidoException>(() =>
            ProcesarAsync(Eventos.Ejemplo("invalido.fraude.validado.con-usuario.json")));

        await using var scope = entorno.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IndicadoresDbContext>();
        Assert.False(await db.Novedades.AnyAsync());
        Assert.False(await db.EventosConsumidos.AnyAsync());
    }

    [DockerFact]
    public async Task Evento_sin_campos_obligatorios_es_invalido()
    {
        var sinIdServicio = Eventos.Ejemplo("reporte.creado.example.json").Replace("\"idServicio\": 1,", "");
        await Assert.ThrowsAsync<EventoInvalidoException>(() => ProcesarAsync(sinIdServicio));
        await Assert.ThrowsAsync<EventoInvalidoException>(() => ProcesarAsync("{ no es json"));
    }
}
