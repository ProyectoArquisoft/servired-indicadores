using System.Globalization;
using System.Text;

namespace Servired.Indicadores.Api;

/// <summary>CSV de datos abiertos (RF8): columnas y formato del ejemplo del contrato, punto decimal.</summary>
public static class ExportadorCsv
{
    private const string Encabezado =
        "idIndicador,idServicio,tipoServicio,tipoNovedad,departamento,municipio,periodo,horasInterrupcionProm,frecuenciaCortes";

    public static string Generar(IEnumerable<IndicadoresEndpoints.IndicadorDto> items)
    {
        var sb = new StringBuilder(Encabezado).Append('\n');
        foreach (var i in items)
        {
            sb.Append(i.IdIndicador.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(i.IdServicio.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(Campo(i.TipoServicio)).Append(',')
                .Append(Campo(i.TipoNovedad)).Append(',')
                .Append(Campo(i.Departamento)).Append(',')
                .Append(Campo(i.Municipio)).Append(',')
                .Append(Campo(i.Periodo)).Append(',')
                .Append(i.HorasInterrupcionProm?.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(i.FrecuenciaCortes.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>RFC 4180: comillas si el valor tiene coma, comillas o salto de línea.</summary>
    private static string Campo(string valor) =>
        valor.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{valor.Replace("\"", "\"\"")}\"" : valor;
}
