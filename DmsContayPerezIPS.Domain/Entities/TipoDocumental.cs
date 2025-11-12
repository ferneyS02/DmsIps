using DmsContayPerezIPS.Domain.Enums;

namespace DmsContayPerezIPS.Domain.Entities
{
    public class TipoDocumental
    {
        public long Id { get; set; }

        // 🔹 Relación con Subserie
        public long SubserieId { get; set; }
        public SubserieDocumental? Subserie { get; set; }

        // 🔹 Datos principales
        public string Nombre { get; set; } = null!;
        public string? Descripcion { get; set; }   // Opcional: texto explicativo

        // 🔹 Retención en años (puede ser 0 si aplica solo en una etapa)
        public short RetencionGestion { get; set; } = 0;
        public short RetencionCentral { get; set; } = 0;

        // 🔹 Disposición final reglamentaria (CT, E, S, M)
        public DisposicionFinalEnum DisposicionFinal { get; set; } = DisposicionFinalEnum.CT;

        // 🔹 Estado lógico
        public bool IsActive { get; set; } = true;

        // ==========================================================
        // 🔹 Auditoría
        // ==========================================================
        public long? CreatedBy { get; set; }
        public DateTime CreatedAt { get; set; }   // 🚫 sin valor dinámico aquí
        public long? UpdatedBy { get; set; }
        public DateTime? UpdatedAt { get; set; }

        // ==========================================================
        // 🔹 Relaciones inversas
        // ==========================================================
        public ICollection<Document>? Documents { get; set; }
    }
}
