using System.Diagnostics;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Servired.Indicadores.Datos;
using Testcontainers.PostgreSql;

namespace Servired.Indicadores.Tests.Integracion;

/// <summary>Prueba que necesita Docker (Postgres en Testcontainers). Sin Docker se salta.</summary>
public sealed class DockerFactAttribute : FactAttribute
{
    private static readonly Lazy<bool> HayDocker = new(() =>
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("docker", "info")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            })!;
            return p.WaitForExit(15_000) && p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    });

    public DockerFactAttribute()
    {
        if (!HayDocker.Value) Skip = "Docker no está disponible.";
    }
}

/// <summary>Reloj fijo: la ventana de 90 días de /patrones no depende del día en que corran las pruebas.</summary>
public sealed class RelojFijo(DateTimeOffset ahora) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => ahora;
}

/// <summary>Un Postgres y una instancia del servicio compartidos por todas las pruebas de integración.</summary>
public sealed class Entorno : IAsyncLifetime
{
    public const string SecretoJwt = "secreto-de-pruebas-de-al-menos-32-caracteres!!";
    public const string SupabaseUrl = "https://pruebas.supabase.co";
    public static readonly DateTimeOffset Ahora = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private PostgreSqlContainer? postgres;
    public WebApplicationFactory<Program> App { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        // Sin Docker las pruebas se saltan (DockerFact), pero xUnit igual crea la fixture.
        if (new DockerFactAttribute().Skip is not null) return;

        postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await postgres.StartAsync();

        App = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.UseSetting("DB_URL", postgres.GetConnectionString());
            b.UseSetting("RABBITMQ_URL", "");
            b.UseSetting("SUPABASE_URL", SupabaseUrl);
            b.UseSetting("SUPABASE_JWT_SECRET", SecretoJwt);
            b.ConfigureServices(s => s.Replace(ServiceDescriptor.Singleton<TimeProvider>(new RelojFijo(Ahora))));
        });
        _ = App.Server; // arranca y aplica migraciones
    }

    public async Task DisposeAsync()
    {
        if (App is not null) await App.DisposeAsync();
        if (postgres is not null) await postgres.DisposeAsync();
    }

    public async Task LimpiarAsync()
    {
        await using var scope = App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IndicadoresDbContext>();
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE indicadores.novedad, indicadores.indicador_agregado, indicadores.evento_consumido RESTART IDENTITY");
    }

    public static string Token(string? rol)
    {
        var appMetadata = new Dictionary<string, object> { ["provider"] = "email" };
        if (rol is not null) appMetadata["rol"] = rol;

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = $"{SupabaseUrl}/auth/v1",
            Audience = "authenticated",
            Subject = new ClaimsIdentity([new Claim("sub", Guid.NewGuid().ToString())]),
            Claims = new Dictionary<string, object> { ["app_metadata"] = appMetadata },
            Expires = DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SecretoJwt)), SecurityAlgorithms.HmacSha256),
        });
    }
}

[CollectionDefinition(Nombre)]
public class ColeccionIntegracion : ICollectionFixture<Entorno>
{
    public const string Nombre = "integracion";
}

public static class Eventos
{
    private static readonly string Carpeta = Path.Combine(AppContext.BaseDirectory, "Contratos", "eventos");

    /// <summary>Ejemplo de <c>servired-infra/contracts/events/examples</c>.</summary>
    public static string Ejemplo(string nombre) => File.ReadAllText(Path.Combine(Carpeta, nombre));

    public static byte[] Bytes(string json) => Encoding.UTF8.GetBytes(json);

    /// <summary>Copia del ejemplo con otro <c>eventId</c> (para simular eventos distintos).</summary>
    public static string ConNuevoId(string json) =>
        System.Text.RegularExpressions.Regex.Replace(json, "\"eventId\":\\s*\"[^\"]+\"", $"\"eventId\": \"{Guid.NewGuid()}\"");
}
