namespace DmsContayPerezIPS.Domain.Entities
{
    public class SerieDocumental
    {
        public long Id { get; set; }

        // 📌 Nombre de la serie (ej: Gestión Clínica, etc.)
        public string Nombre { get; set; } = null!;

        // 👤 Auditoría (sin DateTime.UtcNow)
        public long? CreatedBy { get; set; }
        public DateTime CreatedAt { get; set; }
        public long? UpdatedBy { get; set; }
        public DateTime? UpdatedAt { get; set; }

        // 🔹 Relación con Subseries
        public ICollection<SubserieDocumental>? Subseries { get; set; }
    }
}
