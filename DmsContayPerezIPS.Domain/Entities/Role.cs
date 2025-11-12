namespace DmsContayPerezIPS.Domain.Entities
{
    public class Role
    {
        public long Id { get; set; }

        // 🔹 Nombre único del rol (Admin, User, etc.)
        public string Name { get; set; } = null!;

        // 🔹 Relación inversa
        public ICollection<User>? Users { get; set; }
    }
}
