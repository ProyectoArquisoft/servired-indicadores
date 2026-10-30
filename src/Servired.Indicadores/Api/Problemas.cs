using Microsoft.AspNetCore.Http.HttpResults;

namespace Servired.Indicadores.Api;

/// <summary>Errores RFC 7807 con los títulos de <c>servired-public.yaml</c>.</summary>
public static class Problemas
{
    public record ErrorCampo(string Campo, string Mensaje);

    public static ProblemHttpResult SolicitudInvalida(IReadOnlyList<ErrorCampo> errores) =>
        TypedResults.Problem(
            title: "Solicitud inválida",
            statusCode: StatusCodes.Status400BadRequest,
            detail: string.Join(" ", errores.Select(e => $"{e.Campo}: {e.Mensaje}")),
            extensions: new Dictionary<string, object?> { ["errores"] = errores });

    /// <summary>Título por defecto según el código (401, 403, 404, 500... sin cuerpo propio).</summary>
    public static string Titulo(int status) => status switch
    {
        400 => "Solicitud inválida",
        401 => "No autenticado",
        403 => "Sin permiso",
        404 => "No encontrado",
        405 => "Método no permitido",
        _ when status >= 500 => "Error interno",
        _ => "Error",
    };

    public static void Configurar(ProblemDetailsContext ctx)
    {
        var status = ctx.ProblemDetails.Status ?? ctx.HttpContext.Response.StatusCode;
        // Como en los ejemplos del contrato (ASP.NET pondría la URL del RFC 9110).
        ctx.ProblemDetails.Type = "about:blank";
        ctx.ProblemDetails.Title = Titulo(status);
        if (status == 403)
            ctx.ProblemDetails.Detail ??= "Se requiere el rol OPERADOR o REGULADOR.";
    }
}
