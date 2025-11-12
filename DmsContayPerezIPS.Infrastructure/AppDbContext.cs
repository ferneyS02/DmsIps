using System;
using System.Linq;
using DmsContayPerezIPS.Domain.Entities;
using DmsContayPerezIPS.Domain.Enums;
using Microsoft.EntityFrameworkCore;
// Necesario para HasPostgresExtension / HasGeneratedTsVectorColumn
using Npgsql.EntityFrameworkCore.PostgreSQL;

namespace DmsContayPerezIPS.Infrastructure.Persistence
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        // DbSets
        public DbSet<User> Users { get; set; }
        public DbSet<Role> Roles { get; set; }
        public DbSet<SerieDocumental> Series { get; set; }
        public DbSet<SubserieDocumental> Subseries { get; set; }
        public DbSet<TipoDocumental> TiposDocumentales { get; set; }
        public DbSet<Document> Documents { get; set; }
        public DbSet<DocumentVersion> DocumentVersions { get; set; }
        public DbSet<Folder> Folders { get; set; }
        public DbSet<AuditLog> AuditLogs { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // ==========================================================
            // ✅ Forzar DateTime/DateTime? a "timestamp without time zone"
            // ==========================================================
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                var dateTimeProps = entityType.GetProperties()
                    .Where(p => p.ClrType == typeof(DateTime) || p.ClrType == typeof(DateTime?));

                foreach (var p in dateTimeProps)
                    p.SetColumnType("timestamp without time zone");
            }

            // ==========================================================
            // 🔹 Conversión de enum DisposicionFinal → string
            // ==========================================================
            modelBuilder.Entity<TipoDocumental>()
                .Property(t => t.DisposicionFinal)
                .HasConversion<string>();

            modelBuilder.Entity<SubserieDocumental>()
                .Property(s => s.DisposicionFinal)
                .HasConversion<string>();

            // ==========================================================
            // 🔹 Relaciones (sin Tags)
            // ==========================================================
            modelBuilder.Entity<Document>()
                .HasMany(d => d.Versions)
                .WithOne(v => v.Document)
                .HasForeignKey(v => v.DocumentId);

            modelBuilder.Entity<SubserieDocumental>()
                .HasMany(s => s.TiposDocumentales)
                .WithOne(td => td.Subserie!)
                .HasForeignKey(td => td.SubserieId);

            modelBuilder.Entity<TipoDocumental>()
                .HasMany(td => td.Documents!)
                .WithOne(d => d.TipoDocumental!)
                .HasForeignKey(d => d.TipoDocId);

            // ==========================================================
            // 🔹 Defaults de auditoría
            // ==========================================================
            modelBuilder.Entity<Document>()
                .Property(d => d.CreatedAt)
                .HasDefaultValueSql("NOW() AT TIME ZONE 'UTC'");

            // ===============================================
            // 🔎 PostgreSQL Full-Text Search (spanish + GIN)
            // ===============================================
            modelBuilder.HasPostgresExtension("unaccent");

            modelBuilder.Entity<Document>(b =>
            {
                // Columna tsvector generada a partir de SearchText, usando diccionario 'spanish'
                b.HasGeneratedTsVectorColumn(
                    d => d.SearchVector,
                    "spanish",
                    d => new { d.SearchText }
                );

                // Índice GIN para acelerar matches FTS
                b.HasIndex(d => d.SearchVector)
                    .HasDatabaseName("IX_Documents_SearchVector")
                    .HasMethod("GIN");
            });

            // ==========================================================
            // 🔹 Seed Roles
            // ==========================================================
            modelBuilder.Entity<Role>().HasData(
                new Role { Id = 1, Name = "Admin" },
                new Role { Id = 2, Name = "User" }
            );

            // ==========================================================
            // 🔹 Seed Series (7)
            // ==========================================================
            modelBuilder.Entity<SerieDocumental>().HasData(
                new SerieDocumental { Id = 1, Nombre = "Gestión Clínica" },
                new SerieDocumental { Id = 2, Nombre = "Gestión Administrativa" },
                new SerieDocumental { Id = 3, Nombre = "Gestión Financiera y Contable" },
                new SerieDocumental { Id = 4, Nombre = "Gestión Jurídica" },
                new SerieDocumental { Id = 5, Nombre = "Gestión de Calidad" },
                new SerieDocumental { Id = 6, Nombre = "SG-SST" },
                new SerieDocumental { Id = 7, Nombre = "Administración de equipos biomédicos" }
            );

            // ==========================================================
            // 🔹 Seed Subseries (ordenadas 1..19)
            // ==========================================================
            modelBuilder.Entity<SubserieDocumental>().HasData(
                // Serie 1: Gestión Clínica
                new SubserieDocumental { Id = 1, SerieId = 1, Nombre = "Historias clínicas", RetencionGestion = 15, RetencionCentral = 0, DisposicionFinal = DisposicionFinalEnum.CT },
                new SubserieDocumental { Id = 2, SerieId = 1, Nombre = "Incapacidades médicas", RetencionGestion = 5, RetencionCentral = 0, DisposicionFinal = DisposicionFinalEnum.E },
                new SubserieDocumental { Id = 3, SerieId = 1, Nombre = "Capacitaciones médicas", RetencionGestion = 5, RetencionCentral = 0, DisposicionFinal = DisposicionFinalEnum.E },

                // Serie 2: Gestión Administrativa
                new SubserieDocumental { Id = 4, SerieId = 2, Nombre = "Contratos laborales", RetencionGestion = 10, RetencionCentral = 10, DisposicionFinal = DisposicionFinalEnum.CT },
                new SubserieDocumental { Id = 5, SerieId = 2, Nombre = "Correspondencia", RetencionGestion = 2, RetencionCentral = 0, DisposicionFinal = DisposicionFinalEnum.E },
                new SubserieDocumental { Id = 6, SerieId = 2, Nombre = "Capacitaciones administrativas", RetencionGestion = 5, RetencionCentral = 0, DisposicionFinal = DisposicionFinalEnum.E },

                // Serie 3: Gestión Financiera y Contable
                new SubserieDocumental { Id = 7, SerieId = 3, Nombre = "Estados financieros", RetencionGestion = 20, RetencionCentral = 0, DisposicionFinal = DisposicionFinalEnum.CT },
                new SubserieDocumental { Id = 8, SerieId = 3, Nombre = "Facturas", RetencionGestion = 5, RetencionCentral = 0, DisposicionFinal = DisposicionFinalEnum.E },
                new SubserieDocumental { Id = 9, SerieId = 3, Nombre = "Capacitaciones contables", RetencionGestion = 5, RetencionCentral = 0, DisposicionFinal = DisposicionFinalEnum.E },

                // Serie 4: Gestión Jurídica
                new SubserieDocumental { Id = 10, SerieId = 4, Nombre = "Procesos judiciales", RetencionGestion = 10, RetencionCentral = 20, DisposicionFinal = DisposicionFinalEnum.CT },
                new SubserieDocumental { Id = 11, SerieId = 4, Nombre = "Capacitaciones jurídicas", RetencionGestion = 5, RetencionCentral = 0, DisposicionFinal = DisposicionFinalEnum.E },

                // Serie 5: Gestión de Calidad
                new SubserieDocumental { Id = 12, SerieId = 5, Nombre = "Manuales de procesos", RetencionGestion = 5, RetencionCentral = 0, DisposicionFinal = DisposicionFinalEnum.S },
                new SubserieDocumental { Id = 13, SerieId = 5, Nombre = "Registros de calidad", RetencionGestion = 3, RetencionCentral = 0, DisposicionFinal = DisposicionFinalEnum.E },
                new SubserieDocumental { Id = 14, SerieId = 5, Nombre = "Capacitaciones en calidad", RetencionGestion = 5, RetencionCentral = 0, DisposicionFinal = DisposicionFinalEnum.E },

                // Serie 6: SG-SST
                new SubserieDocumental { Id = 15, SerieId = 6, Nombre = "Accidentes laborales", RetencionGestion = 20, RetencionCentral = 0, DisposicionFinal = DisposicionFinalEnum.CT },
                new SubserieDocumental { Id = 16, SerieId = 6, Nombre = "Capacitaciones SST", RetencionGestion = 5, RetencionCentral = 0, DisposicionFinal = DisposicionFinalEnum.E },

                // Serie 7: Administración de equipos biomédicos
                new SubserieDocumental { Id = 17, SerieId = 7, Nombre = "Hojas de vida de equipos", RetencionGestion = 0, RetencionCentral = 0, DisposicionFinal = DisposicionFinalEnum.M },
                new SubserieDocumental { Id = 18, SerieId = 7, Nombre = "Mantenimientos", RetencionGestion = 5, RetencionCentral = 0, DisposicionFinal = DisposicionFinalEnum.E },
                new SubserieDocumental { Id = 19, SerieId = 7, Nombre = "Capacitaciones en equipos biomédicos", RetencionGestion = 5, RetencionCentral = 0, DisposicionFinal = DisposicionFinalEnum.E }
            );

            // ==========================================================
            // 🔹 Seed Tipos Documentales (IDs 1..42)
            // ==========================================================
            var fixedCreatedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

            modelBuilder.Entity<TipoDocumental>().HasData(
                // Subserie 1
                new TipoDocumental { Id = 1, SubserieId = 1, Nombre = "Historia de ingreso", DisposicionFinal = DisposicionFinalEnum.CT, IsActive = true, CreatedAt = fixedCreatedAt },
                new TipoDocumental { Id = 2, SubserieId = 1, Nombre = "Notas de evolución", DisposicionFinal = DisposicionFinalEnum.CT, IsActive = true, CreatedAt = fixedCreatedAt },
                new TipoDocumental { Id = 3, SubserieId = 1, Nombre = "Resultados de laboratorio", DisposicionFinal = DisposicionFinalEnum.CT, IsActive = true, CreatedAt = fixedCreatedAt },

                // Subserie 2
                new TipoDocumental { Id = 4, SubserieId = 2, Nombre = "Certificado de incapacidad", DisposicionFinal = DisposicionFinalEnum.E, IsActive = true, CreatedAt = fixedCreatedAt },
                new TipoDocumental { Id = 5, SubserieId = 2, Nombre = "Soporte médico", DisposicionFinal = DisposicionFinalEnum.E, IsActive = true, CreatedAt = fixedCreatedAt },

                // Subserie 3
                new TipoDocumental { Id = 6, SubserieId = 3, Nombre = "Lista de asistencia", DisposicionFinal = DisposicionFinalEnum.E, IsActive = true, CreatedAt = fixedCreatedAt },
                new TipoDocumental { Id = 7, SubserieId = 3, Nombre = "Material entregado", DisposicionFinal = DisposicionFinalEnum.E, IsActive = true, CreatedAt = fixedCreatedAt },
                new TipoDocumental { Id = 8, SubserieId = 3, Nombre = "Certificados de participación", DisposicionFinal = DisposicionFinalEnum.E, IsActive = true, CreatedAt = fixedCreatedAt },

                // Subserie 4
                new TipoDocumental { Id = 9, SubserieId = 4, Nombre = "Contrato firmado", DisposicionFinal = DisposicionFinalEnum.CT, IsActive = true, CreatedAt = fixedCreatedAt },
                new TipoDocumental { Id = 10, SubserieId = 4, Nombre = "Acta de terminación", DisposicionFinal = DisposicionFinalEnum.CT, IsActive = true, CreatedAt = fixedCreatedAt },

                // Subserie 5
                new TipoDocumental { Id = 11, SubserieId = 5, Nombre = "Carta enviada", DisposicionFinal = DisposicionFinalEnum.E, IsActive = true, CreatedAt = fixedCreatedAt },
                new TipoDocumental { Id = 12, SubserieId = 5, Nombre = "Carta recibida", DisposicionFinal = DisposicionFinalEnum.E, IsActive = true, CreatedAt = fixedCreatedAt },

                // Subserie 6
                new TipoDocumental { Id = 13, SubserieId = 6, Nombre = "Lista de asistencia", DisposicionFinal = DisposicionFinalEnum.E, IsActive = true, CreatedAt = fixedCreatedAt },
                new TipoDocumental { Id = 14, SubserieId = 6, Nombre = "Material entregado", DisposicionFinal = DisposicionFinalEnum.E, IsActive = true, CreatedAt = fixedCreatedAt },
                new TipoDocumental { Id = 15, SubserieId = 6, Nombre = "Certificados de participación", DisposicionFinal = DisposicionFinalEnum.E, IsActive = true, CreatedAt = fixedCreatedAt },

                // Subserie 7
                new TipoDocumental { Id = 16, SubserieId = 7, Nombre = "Balance general", DisposicionFinal = DisposicionFinalEnum.CT, IsActive = true, CreatedAt = fixedCreatedAt },
                new TipoDocumental { Id = 17, SubserieId = 7, Nombre = "Estado de resultados", DisposicionFinal = DisposicionFinalEnum.CT, IsActive = true, CreatedAt = fixedCreatedAt },

                // Subserie 8
                new TipoDocumental { Id = 18, SubserieId = 8, Nombre = "Factura de proveedor", DisposicionFinal = DisposicionFinalEnum.E, IsActive = true, CreatedAt = fixedCreatedAt },
                new TipoDocumental { Id = 19, SubserieId = 8, Nombre = "Factura de cliente", DisposicionFinal = DisposicionFinalEnum.E, IsActive = true, CreatedAt = fixedCreatedAt },

                // Subserie 9
                new TipoDocumental { Id = 20, SubserieId = 9, Nombre = "Lista de asistencia", DisposicionFinal = DisposicionFinalEnum.E, IsActive = true, CreatedAt = fixedCreatedAt },
                new TipoDocumental { Id = 21, SubserieId = 9, Nombre = "Material entregado", DisposicionFinal = DisposicionFinalEnum.E, IsActive = true, CreatedAt = fixedCreatedAt },
                new TipoDocumental { Id = 22, SubserieId = 9, Nombre = "Certificados de participación", DisposicionFinal = DisposicionFinalEnum.E, IsActive = true, CreatedAt = fixedCreatedAt },

                // Subserie 10
                new TipoDocumental { Id = 23, SubserieId = 10, Nombre = "Demanda", DisposicionFinal = DisposicionFinalEnum.CT, IsActive = true, CreatedAt = fixedCreatedAt },
                new TipoDocumental { Id = 24, SubserieId = 10, Nombre = "Sentencia", DisposicionFinal = DisposicionFinalEnum.CT, IsActive = true, CreatedAt = fixedCreatedAt },

                // Subserie 11
                new TipoDocumental { Id = 25, SubserieId = 11, Nombre = "Lista de asistencia", DisposicionFinal = DisposicionFinalEnum.E, IsActive = true, CreatedAt = fixedCreatedAt },
                new TipoDocumental { Id = 26, SubserieId = 11, Nombre = "Material entregado", DisposicionFinal = DisposicionFinalEnum.E, IsActive = true, CreatedAt = fixedCreatedAt },
                new TipoDocumental { Id = 27, SubserieId = 11, Nombre = "Certificados de participación", DisposicionFinal = DisposicionFinalEnum.E, IsActive = true, CreatedAt = fixedCreatedAt },

                // Subserie 12
                new TipoDocumental { Id = 28, SubserieId = 12, Nombre = "Manual de calidad", DisposicionFinal = DisposicionFinalEnum.S, IsActive = true, CreatedAt = fixedCreatedAt },

                // Subserie 13
                new TipoDocumental { Id = 29, SubserieId = 13, Nombre = "Registro de auditoría interna", DisposicionFinal = DisposicionFinalEnum.E, IsActive = true, CreatedAt = fixedCreatedAt },

                // Subserie 14
                new TipoDocumental { Id = 30, SubserieId = 14, Nombre = "Lista de asistencia", DisposicionFinal = DisposicionFinalEnum.E, IsActive = true, CreatedAt = fixedCreatedAt },
                new TipoDocumental { Id = 31, SubserieId = 14, Nombre = "Material entregado", DisposicionFinal = DisposicionFinalEnum.E, IsActive = true, CreatedAt = fixedCreatedAt },
                new TipoDocumental { Id = 32, SubserieId = 14, Nombre = "Certificados de participación", DisposicionFinal = DisposicionFinalEnum.E, IsActive = true, CreatedAt = fixedCreatedAt },

                // Subserie 15
                new TipoDocumental { Id = 33, SubserieId = 15, Nombre = "Reporte de accidente", DisposicionFinal = DisposicionFinalEnum.CT, IsActive = true, CreatedAt = fixedCreatedAt },

                // Subserie 16
                new TipoDocumental { Id = 34, SubserieId = 16, Nombre = "Lista de asistencia", DisposicionFinal = DisposicionFinalEnum.E, IsActive = true, CreatedAt = fixedCreatedAt },
                new TipoDocumental { Id = 35, SubserieId = 16, Nombre = "Material entregado", DisposicionFinal = DisposicionFinalEnum.E, IsActive = true, CreatedAt = fixedCreatedAt },
                new TipoDocumental { Id = 36, SubserieId = 16, Nombre = "Certificados de participación", DisposicionFinal = DisposicionFinalEnum.E, IsActive = true, CreatedAt = fixedCreatedAt },

                // Subserie 17
                new TipoDocumental { Id = 37, SubserieId = 17, Nombre = "Ficha técnica del equipo", DisposicionFinal = DisposicionFinalEnum.M, IsActive = true, CreatedAt = fixedCreatedAt },

                // Subserie 18
                new TipoDocumental { Id = 38, SubserieId = 18, Nombre = "Reporte de mantenimiento preventivo", DisposicionFinal = DisposicionFinalEnum.E, IsActive = true, CreatedAt = fixedCreatedAt },
                new TipoDocumental { Id = 39, SubserieId = 18, Nombre = "Reporte de mantenimiento correctivo", DisposicionFinal = DisposicionFinalEnum.E, IsActive = true, CreatedAt = fixedCreatedAt },

                // Subserie 19
                new TipoDocumental { Id = 40, SubserieId = 19, Nombre = "Lista de asistencia", DisposicionFinal = DisposicionFinalEnum.E, IsActive = true, CreatedAt = fixedCreatedAt },
                new TipoDocumental { Id = 41, SubserieId = 19, Nombre = "Material entregado", DisposicionFinal = DisposicionFinalEnum.E, IsActive = true, CreatedAt = fixedCreatedAt },
                new TipoDocumental { Id = 42, SubserieId = 19, Nombre = "Certificados de participación", DisposicionFinal = DisposicionFinalEnum.E, IsActive = true, CreatedAt = fixedCreatedAt }
            );
        }
    }
}
