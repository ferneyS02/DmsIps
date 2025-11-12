namespace DmsContayPerezIPS.Domain.Entities
{
    public class DocumentVersion
    {
        public long Id { get; set; }

        // 🔹 Relación con el documento principal
        public long DocumentId { get; set; }
        public Document? Document { get; set; }

        // 🔹 Número de versión
        public int VersionNumber { get; set; }

        // 🔹 Nombre real del archivo en MinIO
        public string ObjectName { get; set; } = null!;

        // 🔹 Auditoría de subida
        public long? UploadedBy { get; set; }
        public User? Uploader { get; set; }

        // ❗ Importante: sin inicializador dinámico
        public DateTime UploadedAt { get; set; }
    }
}
