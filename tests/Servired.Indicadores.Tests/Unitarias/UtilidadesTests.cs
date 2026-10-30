using System.Security.Claims;
using Npgsql;
using Servired.Indicadores.Api;
using Servired.Indicadores.Configuracion;
using Servired.Indicadores.Datos;
using Servired.Indicadores.Indicadores;
using Servired.Indicadores.Seguridad;

namespace Servired.Indicadores.Tests.Unitarias;

public class ConexionBaseDatosTests
{
    [Fact]
    public void Acepta_url_jdbc_como_los_servicios_spring()
    {
        var cs = new NpgsqlConnectionStringBuilder(ConexionBaseDatos.Construir(
            "jdbc:postgresql://db.abc.supabase.co:5432/postgres?sslmode=require&prepareThreshold=0",
            "svc_indicadores", "secreta", "indicadores"));

        Assert.Equal("db.abc.supabase.co", cs.Host);
        Assert.Equal(5432, cs.Port);
        Assert.Equal("postgres", cs.Database);
        Assert.Equal("svc_indicadores", cs.Username);
        Assert.Equal("secreta", cs.Password);
        Assert.Equal("indicadores", cs.SearchPath);
        Assert.Equal(SslMode.Require, cs.SslMode);
    }

    [Fact]
    public void Acepta_uri_con_credenciales_y_las_variables_tienen_prioridad()
    {
        var cs = new NpgsqlConnectionStringBuilder(ConexionBaseDatos.Construir(
            "postgresql://u:p%40ss@localhost:6543/servired", null, "otra", null));

        Assert.Equal(6543, cs.Port);
        Assert.Equal("u", cs.Username);
        Assert.Equal("otra", cs.Password);
        Assert.Equal("indicadores", cs.SearchPath);
    }

    [Fact]
    public void Acepta_cadena_de_conexion_npgsql()
    {
        var cs = new NpgsqlConnectionStringBuilder(ConexionBaseDatos.Construir(
            "Host=pg;Database=x;Username=a", null, null, "indicadores"));

        Assert.Equal("pg", cs.Host);
        Assert.Equal("a", cs.Username);
    }
}

public class ZonaTests
{
    [Theory]
    [InlineData("2026-10-26T13:05:00Z", "2026-10")]
    // 1 de noviembre 03:00 UTC = 31 de octubre 22:00 en Colombia.
    [InlineData("2026-11-01T03:00:00Z", "2026-10")]
    [InlineData("2026-11-01T05:00:00Z", "2026-11")]
    public void Periodo_usa_la_hora_de_colombia(string fecha, string esperado) =>
        Assert.Equal(esperado, Zona.Periodo(DateTimeOffset.Parse(fecha)));

    [Theory]
    [InlineData(null, Zona.SinDato)]
    [InlineData("  ", Zona.SinDato)]
    [InlineData(" Antioquia ", "Antioquia")]
    public void Normaliza_zona(string? valor, string esperado) => Assert.Equal(esperado, Zona.Normalizar(valor));
}

public class GeoTests
{
    [Fact]
    public void Distancia_conocida_en_bogota()
    {
        // Plaza de Bolívar → Parque de la 93: ~9,3 km en línea recta.
        var d = Geo.DistanciaMetros(4.5981, -74.0758, 4.6767, -74.0483);
        Assert.InRange(d, 9_000, 9_500);
    }

    [Fact]
    public void La_caja_contiene_el_circulo()
    {
        var caja = Geo.Caja(4.6, -74.08, 1000);
        Assert.True(Geo.DistanciaMetros(4.6, -74.08, caja.MaxLat, -74.08) >= 999);
        Assert.True(Geo.DistanciaMetros(4.6, -74.08, 4.6, caja.MaxLng) >= 999);
    }
}

public class ExportadorCsvTests
{
    [Fact]
    public void Genera_el_formato_del_contrato_y_escapa_comas()
    {
        var csv = ExportadorCsv.Generar([
            new(301, 1, "ENERGIA", "CORTE", "Cundinamarca", "Bogotá D.C.", "2026-10", 3.25, 12),
            new(302, 3, "ACUEDUCTO", "CORTE", "Valle", "Cali, zona \"sur\"", "2026-10", null, 4),
        ]);

        var lineas = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("idIndicador,idServicio,tipoServicio,tipoNovedad,departamento,municipio,periodo,horasInterrupcionProm,frecuenciaCortes", lineas[0]);
        Assert.Equal("301,1,ENERGIA,CORTE,Cundinamarca,Bogotá D.C.,2026-10,3.25,12", lineas[1]);
        Assert.Equal("302,3,ACUEDUCTO,CORTE,Valle,\"Cali, zona \"\"sur\"\"\",2026-10,,4", lineas[2]);
    }
}

public class RolesTests
{
    [Theory]
    [InlineData("{\"rol\":\"OPERADOR\",\"provider\":\"email\"}", "OPERADOR")]
    [InlineData("{\"provider\":\"email\"}", null)]
    [InlineData("no-es-json", null)]
    public void Lee_rol_de_app_metadata(string appMetadata, string? esperado)
    {
        var usuario = new ClaimsPrincipal(new ClaimsIdentity([new Claim("app_metadata", appMetadata, "JSON")], "jwt"));
        Assert.Equal(esperado, Roles.Obtener(usuario));
    }
}
