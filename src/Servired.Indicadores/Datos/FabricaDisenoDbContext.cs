using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Servired.Indicadores.Datos;

/// <summary>Solo para <c>dotnet ef migrations add</c>: no necesita una BD real ni variables de entorno.</summary>
public class FabricaDisenoDbContext : IDesignTimeDbContextFactory<IndicadoresDbContext>
{
    public IndicadoresDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<IndicadoresDbContext>()
            .UseNpgsql("Host=localhost;Database=diseno", o => o.MigrationsHistoryTable("__ef_migraciones", IndicadoresDbContext.Esquema))
            .Options);
}
