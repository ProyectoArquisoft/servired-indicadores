using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Servired.Indicadores.Api;
using Servired.Indicadores.Configuracion;
using Servired.Indicadores.Datos;
using Servired.Indicadores.Eventos;
using Servired.Indicadores.Indicadores;
using Servired.Indicadores.Seguridad;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

builder.Services.AddDbContext<IndicadoresDbContext>(o => o.UseNpgsql(
    ConexionBaseDatos.Construir(config),
    npgsql => npgsql.MigrationsHistoryTable("__ef_migraciones", IndicadoresDbContext.Esquema)));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<RecalculadorIndicadores>();
builder.Services.AddScoped<ProcesadorEventos>();
builder.Services.AddScoped<ServicioPatrones>();
builder.Services.Configure<OpcionesPatrones>(config.GetSection("Patrones"));

builder.Services.Configure<OpcionesRabbitMq>(config.GetSection("RabbitMq"));
builder.Services.PostConfigure<OpcionesRabbitMq>(o => o.Url ??= config["RABBITMQ_URL"]);
builder.Services.AddSingleton<EstadoConsumidor>();
builder.Services.AddHostedService<ConsumidorEventos>();

builder.Services.AgregarAutenticacionSupabase(config);
builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = Problemas.Configurar);

builder.Services.AddHealthChecks()
    .AddDbContextCheck<IndicadoresDbContext>("postgres")
    .AddCheck<EstadoConsumidor>("rabbitmq");

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

if (config.GetValue("Database:MigrarAlIniciar", true))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<IndicadoresDbContext>().Database.MigrateAsync();
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment() || config.GetValue("Swagger:Habilitado", false))
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResponseWriter = (ctx, reporte) => ctx.Response.WriteAsJsonAsync(new
    {
        estado = reporte.Status.ToString(),
        componentes = reporte.Entries.ToDictionary(e => e.Key, e => new { estado = e.Value.Status.ToString(), e.Value.Description }),
    }),
});
app.MapearIndicadores();

app.Run();

public partial class Program;
