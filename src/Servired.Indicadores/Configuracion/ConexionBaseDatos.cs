using Npgsql;

namespace Servired.Indicadores.Configuracion;

/// <summary>
/// Arma la cadena de conexión de Npgsql a partir de las variables comunes del compose
/// (<c>DB_URL</c>, <c>DB_USER</c>, <c>DB_PASSWORD</c>, <c>DB_SCHEMA</c>), que son las mismas que usan los
/// servicios Spring Boot. <c>DB_URL</c> puede ser JDBC (<c>jdbc:postgresql://host:5432/db?sslmode=require</c>),
/// URI (<c>postgresql://host/db</c>) o una cadena de conexión de Npgsql.
/// </summary>
public static class ConexionBaseDatos
{
    public static string Construir(IConfiguration config)
    {
        var url = config["DB_URL"] ?? config.GetConnectionString("Indicadores")
            ?? throw new InvalidOperationException("Falta la variable DB_URL (o ConnectionStrings:Indicadores).");

        return Construir(url, config["DB_USER"], config["DB_PASSWORD"], config["DB_SCHEMA"]);
    }

    public static string Construir(string url, string? usuario, string? clave, string? esquema)
    {
        var builder = EsUri(url) ? DesdeUri(url) : new NpgsqlConnectionStringBuilder(url);

        if (!string.IsNullOrEmpty(usuario)) builder.Username = usuario;
        if (!string.IsNullOrEmpty(clave)) builder.Password = clave;
        builder.SearchPath = string.IsNullOrEmpty(esquema) ? Datos.IndicadoresDbContext.Esquema : esquema;
        builder.ApplicationName ??= "servired-indicadores";

        return builder.ConnectionString;
    }

    private static bool EsUri(string url) =>
        url.StartsWith("jdbc:", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase);

    private static NpgsqlConnectionStringBuilder DesdeUri(string url)
    {
        if (url.StartsWith("jdbc:", StringComparison.OrdinalIgnoreCase)) url = url[5..];
        var uri = new Uri(url);

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort || uri.Port <= 0 ? 5432 : uri.Port,
            Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')) is { Length: > 0 } db ? db : "postgres",
        };

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var partes = uri.UserInfo.Split(':', 2);
            builder.Username = Uri.UnescapeDataString(partes[0]);
            if (partes.Length == 2) builder.Password = Uri.UnescapeDataString(partes[1]);
        }

        foreach (var (clave, valor) in Parametros(uri.Query))
        {
            switch (clave.ToLowerInvariant())
            {
                case "sslmode":
                    builder.SslMode = Enum.Parse<SslMode>(valor.Replace("-", ""), ignoreCase: true);
                    break;
                case "user":
                    builder.Username = valor;
                    break;
                case "password":
                    builder.Password = valor;
                    break;
                // Parámetros de pgjdbc (p. ej. prepareThreshold) no aplican a Npgsql: se ignoran.
            }
        }

        return builder;
    }

    private static IEnumerable<(string, string)> Parametros(string query) =>
        query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Select(p => (Uri.UnescapeDataString(p[0]), p.Length == 2 ? Uri.UnescapeDataString(p[1]) : ""));
}
