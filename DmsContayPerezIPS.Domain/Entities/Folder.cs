namespace DmsContayPerezIPS.Domain.Entities
{
    public class Folder
    {
        public long Id { get; set; }

        // 📂 Datos básicos
        public string Name { get; set; } = null!;
        public long? ParentId { get; set; }

        // 👤 Auditoría (sin valores DateTime.UtcNow aquí)
        public long? CreatedBy { get; set; }
        public DateTime CreatedAt { get; set; }
        public long? UpdatedBy { get; set; }
        public DateTime? UpdatedAt { get; set; }

        // 🔹 Relaciones
        public Folder? Parent { get; set; }
        public User? Creator { get; set; }
        public ICollection<Folder>? Subfolders { get; set; }
        public ICollection<Document>? Documents { get; set; }
    }
}
