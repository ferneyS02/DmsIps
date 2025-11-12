namespace DmsContayPerezIPS.Domain.Entities
{
    public class AuditLog
    {
        public long Id { get; set; }

        // 🔹 Usuario que ejecutó la acción (puede ser null si es anónimo o sistema)
        public long? UserId { get; set; }

        // 🔹 Información de la acción
        public string Action { get; set; } = null!;   // Ej: "CREATE", "UPDATE", "DELETE", "LOGIN"
        public string Entity { get; set; } = null!;   // Nombre de la entidad afectada
        public long? EntityId { get; set; }           // Id de la entidad afectada

        // 🔹 Momento de la acción (lo configuraremos en DbContext con valor por defecto)
        public DateTime Ts { get; set; }

        // 🔹 Detalle opcional (JSON, mensaje, etc.)
        public string? Detail { get; set; }

        // ==========================================================
        // 🔹 Relaciones
        // ==========================================================
        public User? User { get; set; }
    }
}
