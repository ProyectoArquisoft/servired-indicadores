using System.Text;
using Microsoft.EntityFrameworkCore;

namespace Servired.Indicadores.Datos;

public class IndicadoresDbContext(DbContextOptions<IndicadoresDbContext> options) : DbContext(options)
{
    /// <summary>Esquema propio del servicio (base de datos por microservicio, D2).</summary>
    public const string Esquema = "indicadores";

    public DbSet<IndicadorAgregado> Indicadores => Set<IndicadorAgregado>();
    public DbSet<EventoConsumido> EventosConsumidos => Set<EventoConsumido>();
    public DbSet<Novedad> Novedades => Set<Novedad>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Esquema);

        modelBuilder.Entity<IndicadorAgregado>(e =>
        {
            e.HasKey(x => x.IdIndicador);
            e.Property(x => x.TipoServicio).HasMaxLength(20);
            e.Property(x => x.TipoNovedad).HasMaxLength(10);
            e.Property(x => x.Departamento).HasMaxLength(100);
            e.Property(x => x.Municipio).HasMaxLength(100);
            e.Property(x => x.Periodo).HasMaxLength(7).IsFixedLength();
            e.HasIndex(x => new { x.TipoNovedad, x.IdServicio, x.Departamento, x.Municipio, x.Periodo })
                .IsUnique().HasDatabaseName("ux_indicador_agregado_clave");
            // Sin TimescaleDB: índice normal por periodo (brief §7).
            e.HasIndex(x => x.Periodo).HasDatabaseName("ix_indicador_agregado_periodo");
        });

        modelBuilder.Entity<EventoConsumido>(e =>
        {
            e.HasKey(x => x.IdEvento);
            e.Property(x => x.IdEvento).ValueGeneratedNever();
            e.Property(x => x.TipoEvento).HasMaxLength(50);
        });

        modelBuilder.Entity<Novedad>(e =>
        {
            e.HasKey(x => x.IdNovedad);
            e.Property(x => x.TipoNovedad).HasMaxLength(10);
            e.Property(x => x.TipoServicio).HasMaxLength(20);
            e.Property(x => x.Departamento).HasMaxLength(100);
            e.Property(x => x.Municipio).HasMaxLength(100);
            e.Property(x => x.Periodo).HasMaxLength(7).IsFixedLength();
            e.Property(x => x.ResultadoFraude).HasMaxLength(10);
            e.HasIndex(x => new { x.TipoNovedad, x.IdOrigen }).IsUnique().HasDatabaseName("ux_novedad_origen");
            e.HasIndex(x => new { x.IdServicio, x.Fecha }).HasDatabaseName("ix_novedad_servicio_fecha");
        });

        UsarSnakeCase(modelBuilder);
    }

    /// <summary>Tablas y columnas en snake_case, como el modelo de datos del SAD.</summary>
    private static void UsarSnakeCase(ModelBuilder modelBuilder)
    {
        foreach (var entidad in modelBuilder.Model.GetEntityTypes())
        {
            entidad.SetTableName(SnakeCase(entidad.ClrType.Name));
            foreach (var propiedad in entidad.GetProperties())
                propiedad.SetColumnName(SnakeCase(propiedad.Name));
            foreach (var llave in entidad.GetKeys())
                llave.SetName(SnakeCase("Pk" + entidad.ClrType.Name));
        }
    }

    internal static string SnakeCase(string nombre)
    {
        var sb = new StringBuilder(nombre.Length + 8);
        for (var i = 0; i < nombre.Length; i++)
        {
            var c = nombre[i];
            if (char.IsUpper(c) && i > 0) sb.Append('_');
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }
}
