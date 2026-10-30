using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Tokens;

namespace Servired.Indicadores.Seguridad;

public static class Roles
{
    public const string Ciudadano = "CIUDADANO";
    public const string Operador = "OPERADOR";
    public const string Regulador = "REGULADOR";

    /// <summary>Política de <c>GET /api/indicadores/export</c> (RF8).</summary>
    public const string PoliticaExportar = "ExportarIndicadores";

    /// <summary>Lee <c>app_metadata.rol</c> del JWT de Supabase.</summary>
    public static string? Obtener(ClaimsPrincipal usuario)
    {
        var appMetadata = usuario.FindFirst("app_metadata")?.Value;
        if (string.IsNullOrEmpty(appMetadata)) return null;
        try
        {
            using var doc = JsonDocument.Parse(appMetadata);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("rol", out var rol)
                && rol.ValueKind == JsonValueKind.String
                    ? rol.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Valida el JWT de Supabase Auth (defensa en profundidad: el gateway ya lo validó).
/// Usa <c>SUPABASE_JWKS_URL</c> (claves asimétricas) o <c>SUPABASE_JWT_SECRET</c> (HS256), según el
/// proyecto (pregunta abierta §11.5 del brief). Sin ninguna de las dos, toda ruta protegida responde 401.
/// </summary>
public static class AutenticacionSupabase
{
    public static IServiceCollection AgregarAutenticacionSupabase(this IServiceCollection services, IConfiguration config)
    {
        var supabaseUrl = config["SUPABASE_URL"]?.TrimEnd('/');
        var jwksUrl = config["SUPABASE_JWKS_URL"];
        var secreto = config["SUPABASE_JWT_SECRET"];

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(o =>
            {
                o.MapInboundClaims = false;
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = supabaseUrl is not null,
                    ValidIssuer = supabaseUrl is null ? null : $"{supabaseUrl}/auth/v1",
                    ValidAudience = "authenticated",
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    RoleClaimType = "rol",
                };

                if (!string.IsNullOrEmpty(jwksUrl))
                {
                    var jwks = new ConfigurationManager<JsonWebKeySet>(
                        jwksUrl, new RecuperadorJwks(), new HttpDocumentRetriever { RequireHttps = jwksUrl.StartsWith("https") });
                    o.TokenValidationParameters.IssuerSigningKeyResolver = (_, _, _, _) =>
                        jwks.GetConfigurationAsync(CancellationToken.None).GetAwaiter().GetResult().GetSigningKeys();
                }
                else if (!string.IsNullOrEmpty(secreto))
                {
                    o.TokenValidationParameters.IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secreto));
                    o.TokenValidationParameters.ValidAlgorithms = [SecurityAlgorithms.HmacSha256];
                }
                else
                {
                    // Sin claves configuradas ningún token es válido.
                    o.TokenValidationParameters.SignatureValidator = (_, _) =>
                        throw new SecurityTokenSignatureKeyNotFoundException("No hay SUPABASE_JWKS_URL ni SUPABASE_JWT_SECRET.");
                }
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(Roles.PoliticaExportar, p => p.RequireAuthenticatedUser().RequireAssertion(ctx =>
                Roles.Obtener(ctx.User) is Roles.Operador or Roles.Regulador));

        return services;
    }

    private sealed class RecuperadorJwks : IConfigurationRetriever<JsonWebKeySet>
    {
        public async Task<JsonWebKeySet> GetConfigurationAsync(string address, IDocumentRetriever retriever, CancellationToken cancel) =>
            new(await retriever.GetDocumentAsync(address, cancel));
    }
}
