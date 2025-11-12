namespace DmsContayPerezIPS.Domain.Entities
{
    public class User
    {
        public long Id { get; set; }

        // 🔹 Datos principales
        public string Username { get; set; } = null!;
        public string PasswordHash { get; set; } = null!;

        // 🔹 Relación con Role
        public long RoleId { get; set; }
        public Role? Role { get; set; }

        // 🔹 Estado y auditoría
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; }   // ⚡ sin valor dinámico
        public DateTime? UpdatedAt { get; set; }

        // =========================
        // 🔹 Relaciones inversas
        // =========================
        public ICollection<AuditLog>? AuditLogs { get; set; }
        public ICollection<Document>? Documents { get; set; }
        public ICollection<Folder>? Folders { get; set; }
        public ICollection<DocumentVersion>? DocumentVersions { get; set; }
    }
}
