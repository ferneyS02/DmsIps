using System;

namespace DmsContayPerezIPS.Domain.Entities
{
    public class User
    {
        public long Id { get; set; }

        // Login
        public string Username { get; set; } = default!;
        public string PasswordHash { get; set; } = default!;

        // Perfil
        public string NumeroDocumento { get; set; } = default!; // 👈 NUEVO
        public string Nombre { get; set; } = default!;          // 👈 NUEVO
        public string Cargo { get; set; } = default!;           // 👈 NUEVO

        // Rol
        public long RoleId { get; set; }
        public Role? Role { get; set; }

        // Seguridad (cambio obligatorio)
        public bool MustChangePassword { get; set; } = true;
        public DateTime? PasswordChangedAt { get; set; }

        // Estado y trazas
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
    }
}
