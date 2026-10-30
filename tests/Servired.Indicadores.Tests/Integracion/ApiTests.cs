using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Servired.Indicadores.Eventos;

namespace Servired.Indicadores.Tests.Integracion;

[Collection(ColeccionIntegracion.Nombre)]
public class ApiTests(Entorno entorno) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (entorno.App is null) return;
        await entorno.LimpiarAsync();

        await using var scope = entorno.App.Services.CreateAsyncScope();
        var procesador = scope.ServiceProvider.GetRequiredService<ProcesadorEventos>();
        foreach (var ejemplo in new[]
                 {
                     "reporte.creado.example.json", "reporte.restablecido.example.json",
                     "fraude.validado.example.json", "fraude.rechazado.example.json",
                 })
            await procesador.ProcesarAsync(Eventos.Bytes(Eventos.Ejemplo(ejemplo)));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private HttpClient Cliente(string? rol = null, bool conToken = false)
    {
        var cliente = entorno.App.CreateClient();
        if (conToken) cliente.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Entorno.Token(rol));
        return cliente;
    }

    [DockerFact]
    public async Task Consulta_publica_paginada_y_filtrada()
    {
        var r = await Cliente().GetFromJsonAsync<JsonElement>("/api/indicadores?municipio=bogotá d.c.&periodo=2026-10");

        Assert.Equal(1, r.GetProperty("total").GetInt64());
        Assert.Equal(0, r.GetProperty("page").GetInt32());
        Assert.Equal(20, r.GetProperty("size").GetInt32());
        var item = r.GetProperty("items")[0];
        Assert.Equal("ENERGIA", item.GetProperty("tipoServicio").GetString());
        Assert.Equal(5.33, item.GetProperty("horasInterrupcionProm").GetDouble());
        Assert.Equal(1, item.GetProperty("frecuenciaCortes").GetInt32());

        var vacio = await Cliente().GetFromJsonAsync<JsonElement>("/api/indicadores?departamento=Antioquia");
        Assert.Equal(0, vacio.GetProperty("total").GetInt64());
    }

    [DockerFact]
    public async Task Parametros_invalidos_dan_400_problem_json()
    {
        var r = await Cliente().GetAsync("/api/indicadores?periodo=2026-13&size=500");

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("application/problem+json", r.Content.Headers.ContentType?.MediaType);
        var problema = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Solicitud inválida", problema.GetProperty("title").GetString());
        Assert.Equal("about:blank", problema.GetProperty("type").GetString());
        var campos = problema.GetProperty("errores").EnumerateArray().Select(e => e.GetProperty("campo").GetString()).ToList();
        Assert.Equal(["periodo", "size"], campos);
    }

    [DockerFact]
    public async Task Exportar_sin_token_da_401()
    {
        var r = await Cliente().GetAsync("/api/indicadores/export?formato=csv");

        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Equal("application/problem+json", r.Content.Headers.ContentType?.MediaType);
    }

    [DockerFact]
    public async Task Exportar_como_ciudadano_da_403()
    {
        var r = await Cliente("CIUDADANO", conToken: true).GetAsync("/api/indicadores/export?formato=csv");

        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        var problema = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Sin permiso", problema.GetProperty("title").GetString());
    }

    [DockerFact]
    public async Task Operador_exporta_csv()
    {
        var r = await Cliente("OPERADOR", conToken: true).GetAsync("/api/indicadores/export?formato=csv&periodo=2026-10");

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("text/csv", r.Content.Headers.ContentType?.MediaType);
        Assert.Equal("indicadores-2026-10.csv", r.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        var lineas = (await r.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lineas.Length);
        Assert.EndsWith(",ENERGIA,CORTE,Cundinamarca,Bogotá D.C.,2026-10,5.33,1", lineas[1]);
    }

    [DockerFact]
    public async Task Regulador_exporta_json()
    {
        var r = await Cliente("REGULADOR", conToken: true).GetAsync("/api/indicadores/export?formato=json");

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var items = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, items.ValueKind);
        Assert.Equal(1, items.GetArrayLength());
    }

    [DockerFact]
    public async Task Exportar_sin_formato_da_400()
    {
        var r = await Cliente("OPERADOR", conToken: true).GetAsync("/api/indicadores/export");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [DockerFact]
    public async Task Patrones_cuenta_lo_que_esta_dentro_del_radio()
    {
        // El fraude del ejemplo está en (4.5981, -74.0758); el corte en (4.6097, -74.0817), a ~1,4 km.
        var r = await Cliente().GetFromJsonAsync<JsonElement>("/internal/indicadores/patrones?idServicio=1&lat=4.5981&lng=-74.0758");

        Assert.Equal(1, r.GetProperty("idServicio").GetInt64());
        Assert.Equal(1000, r.GetProperty("radioMetros").GetInt32());
        Assert.Equal(90, r.GetProperty("ventanaDias").GetInt32());
        Assert.Equal(1, r.GetProperty("fraudesValidados").GetInt32());
        Assert.Equal(0, r.GetProperty("fraudesRechazados").GetInt32()); // v1 no trae ubicación del rechazo
        Assert.Equal(0, r.GetProperty("cortesReportados").GetInt32());

        var junto = await Cliente().GetFromJsonAsync<JsonElement>("/internal/indicadores/patrones?idServicio=1&lat=4.6097&lng=-74.0817");
        Assert.Equal(1, junto.GetProperty("cortesReportados").GetInt32());

        var otroServicio = await Cliente().GetFromJsonAsync<JsonElement>("/internal/indicadores/patrones?idServicio=3&lat=4.5981&lng=-74.0758");
        Assert.Equal(0, otroServicio.GetProperty("fraudesValidados").GetInt32());
    }

    [DockerFact]
    public async Task Patrones_sin_parametros_da_400()
    {
        var r = await Cliente().GetAsync("/internal/indicadores/patrones?idServicio=1&lat=95");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [DockerFact]
    public async Task Health_responde_con_postgres_sano()
    {
        var r = await Cliente().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var cuerpo = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Healthy", cuerpo.GetProperty("componentes").GetProperty("postgres").GetProperty("estado").GetString());
    }
}
