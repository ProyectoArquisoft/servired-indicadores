namespace Servired.Indicadores.Indicadores;

public static class Geo
{
    private const double RadioTierraMetros = 6_371_000;
    // Mismo radio que el haversine: si no, la caja queda unos metros más pequeña que el círculo.
    private const double MetrosPorGradoLat = RadioTierraMetros * Math.PI / 180;

    public record CajaCoordenadas(double MinLat, double MaxLat, double MinLng, double MaxLng);

    /// <summary>Caja que contiene el círculo de <paramref name="radioMetros"/> (prefiltro barato en SQL).</summary>
    public static CajaCoordenadas Caja(double lat, double lng, double radioMetros)
    {
        var dLat = radioMetros / MetrosPorGradoLat;
        var cosLat = Math.Max(Math.Cos(lat * Math.PI / 180), 1e-6);
        var dLng = radioMetros / (MetrosPorGradoLat * cosLat);
        return new CajaCoordenadas(lat - dLat, lat + dLat, lng - dLng, lng + dLng);
    }

    /// <summary>Distancia por haversine (WGS84 aproximado a esfera).</summary>
    public static double DistanciaMetros(double lat1, double lng1, double lat2, double lng2)
    {
        static double Rad(double g) => g * Math.PI / 180;
        var dLat = Rad(lat2 - lat1);
        var dLng = Rad(lng2 - lng1);
        var a = Math.Pow(Math.Sin(dLat / 2), 2)
            + Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Pow(Math.Sin(dLng / 2), 2);
        return 2 * RadioTierraMetros * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }
}
