using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Servired.Indicadores.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class Inicial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "indicadores");

            migrationBuilder.CreateTable(
                name: "evento_consumido",
                schema: "indicadores",
                columns: table => new
                {
                    id_evento = table.Column<Guid>(type: "uuid", nullable: false),
                    tipo_evento = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    id_origen = table.Column<long>(type: "bigint", nullable: false),
                    fecha_consumo = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_evento_consumido", x => x.id_evento);
                });

            migrationBuilder.CreateTable(
                name: "indicador_agregado",
                schema: "indicadores",
                columns: table => new
                {
                    id_indicador = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    id_servicio = table.Column<long>(type: "bigint", nullable: false),
                    tipo_servicio = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    tipo_novedad = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    departamento = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    municipio = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    periodo = table.Column<string>(type: "character(7)", fixedLength: true, maxLength: 7, nullable: false),
                    horas_interrupcion_prom = table.Column<double>(type: "double precision", nullable: true),
                    frecuencia_cortes = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_indicador_agregado", x => x.id_indicador);
                });

            migrationBuilder.CreateTable(
                name: "novedad",
                schema: "indicadores",
                columns: table => new
                {
                    id_novedad = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    tipo_novedad = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    id_origen = table.Column<long>(type: "bigint", nullable: false),
                    id_servicio = table.Column<long>(type: "bigint", nullable: false),
                    tipo_servicio = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    departamento = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    municipio = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    lat = table.Column<double>(type: "double precision", nullable: true),
                    lng = table.Column<double>(type: "double precision", nullable: true),
                    fecha = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    periodo = table.Column<string>(type: "character(7)", fixedLength: true, maxLength: 7, nullable: false),
                    fecha_resolucion = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    duracion_interrupcion_horas = table.Column<double>(type: "double precision", nullable: true),
                    total_validaciones = table.Column<int>(type: "integer", nullable: false),
                    resultado_fraude = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_novedad", x => x.id_novedad);
                });

            migrationBuilder.CreateIndex(
                name: "ix_indicador_agregado_periodo",
                schema: "indicadores",
                table: "indicador_agregado",
                column: "periodo");

            migrationBuilder.CreateIndex(
                name: "ux_indicador_agregado_clave",
                schema: "indicadores",
                table: "indicador_agregado",
                columns: new[] { "tipo_novedad", "id_servicio", "departamento", "municipio", "periodo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_novedad_servicio_fecha",
                schema: "indicadores",
                table: "novedad",
                columns: new[] { "id_servicio", "fecha" });

            migrationBuilder.CreateIndex(
                name: "ux_novedad_origen",
                schema: "indicadores",
                table: "novedad",
                columns: new[] { "tipo_novedad", "id_origen" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "evento_consumido",
                schema: "indicadores");

            migrationBuilder.DropTable(
                name: "indicador_agregado",
                schema: "indicadores");

            migrationBuilder.DropTable(
                name: "novedad",
                schema: "indicadores");
        }
    }
}
