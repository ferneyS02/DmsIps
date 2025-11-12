using DmsContayPerezIPS.Domain.Entities;
using DmsContayPerezIPS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Minio;
using Minio.DataModel.Args;

namespace DmsContayPerezIPS.Infrastructure.Seed
{
    public static class SeederService
    {
        public static async Task SeedAsync(AppDbContext db, IMinioClient minio, string bucket)
        {
            // 1. Asegurar Roles
            if (!await db.Roles.AnyAsync())
            {
                db.Roles.AddRange(
                    new Role { Id = 1, Name = "Admin" },
                    new Role { Id = 2, Name = "User" }
                );
                await db.SaveChangesAsync();
                Console.WriteLine("✅ Roles iniciales creados.");
            }

            // 2. Crear usuario Admin si no existe
            if (!await db.Users.AnyAsync(u => u.Username == "admin"))
            {
                var passwordHash = BCrypt.Net.BCrypt.HashPassword("Admin123*");

                db.Users.Add(new User
                {
                    Username = "admin",
                    PasswordHash = passwordHash,
                    RoleId = 1,
                    // CreatedAt = DateTime.UtcNow // 👉 activa si tu entidad User tiene este campo
                });

                await db.SaveChangesAsync();
                Console.WriteLine("✅ Usuario administrador creado: admin / Admin123*");
            }

            // 3. Crear bucket en MinIO si no existe
            bool bucketExists = await minio.BucketExistsAsync(new BucketExistsArgs().WithBucket(bucket));
            if (!bucketExists)
            {
                await minio.MakeBucketAsync(new MakeBucketArgs().WithBucket(bucket));
                Console.WriteLine($"✅ Bucket '{bucket}' creado en MinIO.");
            }
            else
            {
                Console.WriteLine($"ℹ️ Bucket '{bucket}' ya existe en MinIO.");
            }
        }
    }
}
