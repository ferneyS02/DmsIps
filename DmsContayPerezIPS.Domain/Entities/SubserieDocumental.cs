using DmsContayPerezIPS.Domain.Enums;

namespace DmsContayPerezIPS.Domain.Entities
{
    public class SubserieDocumental
    {
        public long Id { get; set; }

        // 🔗 Relación con Serie Documental
        public long SerieId { get; set; }
        public SerieDocumental? Serie { get; set; }

        // 📌 Nombre de la subserie
        public string Nombre { get; set; } = null!;

        // 📌 Relación con Tipos Documentales
        public ICollection<TipoDocumental>? TiposDocumentales { get; set; }

        // =========================
        // 🔹 Campos de TRD
        // =========================

        // Retención en archivo de gestión (en años)
        public short RetencionGestion { get; set; }

        // Retención en archivo central (en años)
        public short RetencionCentral { get; set; }

        // Disposición final (enum: CT, E, S, M)
        public DisposicionFinalEnum DisposicionFinal { get; set; } = DisposicionFinalEnum.E;

        // =========================
        // 🔹 Auditoría
        // =========================
        public long? CreatedBy { get; set; }
        public DateTime CreatedAt { get; set; }   // ⚡ sin valor dinámico
        public long? UpdatedBy { get; set; }
        public DateTime? UpdatedAt { get; set; }
    }
}
