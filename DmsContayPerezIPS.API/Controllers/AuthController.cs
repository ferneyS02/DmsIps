using System.ComponentModel.DataAnnotations;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Serialization;
using BCrypt.Net;
using DmsContayPerezIPS.Domain.Entities;
using DmsContayPerezIPS.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace DmsContayPerezIPS.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AuthController : ControllerBase
    {
        private readonly AppDbContext _db;
        private readonly IConfiguration _config;

        public AuthController(AppDbContext db, IConfiguration config)
        {
            _db = db;
            _config = config;
        }

        // ===== DTOs =====
        public sealed class RegisterRequest
        {
            // 👉 ORDEN SOLICITADO para Swagger y JSON
            [JsonPropertyOrder(1)]
            [Required] public string Nombre { get; set; } = default!;

            [JsonPropertyOrder(2)]
            [Required] public string NumeroDocumento { get; set; } = default!;

            [JsonPropertyOrder(3)]
            [Required] public string Cargo { get; set; } = default!;

            [JsonPropertyOrder(4)]
            [Required] public string Username { get; set; } = default!;

            [JsonPropertyOrder(5)]
            [Required, MinLength(6)] public string Password { get; set; } = default!;

            [JsonPropertyOrder(6)]
            [Required] public long RoleId { get; set; }
        }

        // Para tipar /me y controlar orden en Swagger
        public sealed class MeResponse
        {
            [JsonPropertyOrder(1)]
            public string Nombre { get; set; } = default!;
            [JsonPropertyOrder(2)]
            public string NumeroDocumento { get; set; } = default!;
            [JsonPropertyOrder(3)]
            public string Cargo { get; set; } = default!;
            [JsonPropertyOrder(4)]
            public string Username { get; set; } = default!;
            [JsonPropertyOrder(5)]
            public long RoleId { get; set; }
            [JsonPropertyOrder(6)]
            public string? RoleName { get; set; }
            [JsonPropertyOrder(7)]
            public long Id { get; set; }
        }

        public sealed class LoginRequest
        {
            [Required] public string Username { get; set; } = default!;
            [Required] public string Password { get; set; } = default!;
        }

        public sealed class AssignRoleRequest
        {
            public long? UserId { get; set; }
            public string? Username { get; set; }
            public long? RoleId { get; set; }
            public string? RoleName { get; set; }
        }

        public sealed class ChangePasswordRequest
        {
            [Required] public string CurrentPassword { get; set; } = default!;
            [Required] public string NewPassword { get; set; } = default!;
        }

        public sealed class AdminChangePasswordRequest
        {
            public long? UserId { get; set; }
            public string? Username { get; set; }
            [Required] public string NewPassword { get; set; } = default!;
        }

        // ========= 0) QUIÉN SOY =========
        [Authorize]
        [HttpGet("me")]
        [ProducesResponseType(typeof(MeResponse), 200)]
        public async Task<IActionResult> Me(CancellationToken ct)
        {
            var myId = GetCurrentUserId(User);
            if (myId is null) return Unauthorized();

            var user = await _db.Users
                .Include(u => u.Role)
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == myId.Value, ct);

            if (user is null) return Unauthorized();

            // 👉 Orden controlado por el DTO MeResponse
            var dto = new MeResponse
            {
                Nombre = user.Nombre,
                NumeroDocumento = user.NumeroDocumento,
                Cargo = user.Cargo,
                Username = user.Username,
                RoleId = user.RoleId,
                RoleName = user.Role?.Name,
                Id = user.Id
            };

            return Ok(dto);
        }

        // ========= 1) Lista de roles =========
        [HttpGet("roles")]
        [AllowAnonymous]
        public async Task<IActionResult> GetRoles()
        {
            var roles = await _db.Roles
                .AsNoTracking()
                .OrderBy(r => r.Name)
                .Select(r => new { r.Id, r.Name })
                .ToListAsync();

            return Ok(roles);
        }

        // ========= 2) Registro (orden de campos en DTO) =========
        [HttpPost("register")]
        [AllowAnonymous]
        public async Task<IActionResult> Register([FromBody] RegisterRequest dto)
        {
            if (!ModelState.IsValid) return ValidationProblem(ModelState);

            if (await _db.Users.AnyAsync(u => u.Username == dto.Username))
                return BadRequest("❌ El usuario ya existe");

            var role = await _db.Roles.FirstOrDefaultAsync(r => r.Id == dto.RoleId);
            if (role is null)
                return BadRequest("❌ El rol especificado no existe");

            if (await _db.Users.AnyAsync(u => u.NumeroDocumento == dto.NumeroDocumento))
                return BadRequest("❌ Ya existe un usuario con ese número de documento");

            var (ok, err) = ValidatePassword(dto.Password);
            if (!ok) return BadRequest(err);

            var user = new User
            {
                Username = dto.Username,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(dto.Password),
                RoleId = dto.RoleId,

                NumeroDocumento = dto.NumeroDocumento.Trim(),
                Nombre = dto.Nombre.Trim(),
                Cargo = dto.Cargo.Trim(),

                MustChangePassword = true,
                PasswordChangedAt = null,
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };

            _db.Users.Add(user);
            await _db.SaveChangesAsync();

            // 👉 También devuelvo en el orden solicitado (después de message)
            return Ok(new
            {
                message = "✅ Usuario registrado con éxito",
                nombre = user.Nombre,
                numeroDocumento = user.NumeroDocumento,
                cargo = user.Cargo,
                username = user.Username,
                roleId = user.RoleId,
                roleName = role.Name
            });
        }

        // ========= 3) Login =========
        [HttpPost("login")]
        [AllowAnonymous]
        public async Task<IActionResult> Login([FromBody] LoginRequest dto)
        {
            if (!ModelState.IsValid) return ValidationProblem(ModelState);

            var user = await _db.Users
                .Include(u => u.Role)
                .FirstOrDefaultAsync(u => u.Username == dto.Username);

            if (user == null || !BCrypt.Net.BCrypt.Verify(dto.Password, user.PasswordHash))
                return Unauthorized("❌ Credenciales inválidas");

            if (!user.IsActive)
                return Unauthorized("❌ Usuario inactivo");

            var mustChange = user.MustChangePassword;

            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Name, user.Username),
                new Claim(ClaimTypes.Role, user.Role?.Name ?? "User"),
                new Claim("pwd_fresh", mustChange ? "false" : "true")
            };

            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(
                _config["JWT:Key"] ?? "EstaEsUnaClaveJWTDeAlMenos32CaracteresSuperSegura!!123"));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var token = new JwtSecurityToken(
                issuer: _config["JWT:Issuer"],
                audience: _config["JWT:Audience"],
                claims: claims,
                expires: DateTime.UtcNow.AddHours(2),
                signingCredentials: creds
            );

            return Ok(new
            {
                token = new JwtSecurityTokenHandler().WriteToken(token),
                user = user.Username,
                role = user.Role?.Name,
                mustChangePassword = mustChange
            });
        }

        // ========= 4) Admin: asignar/cambiar rol =========
        [Authorize(Roles = "Admin")]
        [HttpPost("assign-role")]
        public async Task<IActionResult> AssignRole([FromBody] AssignRoleRequest req, CancellationToken ct)
        {
            if ((req.UserId is null && string.IsNullOrWhiteSpace(req.Username)) ||
                (req.RoleId is null && string.IsNullOrWhiteSpace(req.RoleName)))
                return BadRequest("Debes indicar (UserId o Username) y (RoleId o RoleName).");

            var user = req.UserId.HasValue
                ? await _db.Users.FirstOrDefaultAsync(u => u.Id == req.UserId.Value, ct)
                : await _db.Users.FirstOrDefaultAsync(u => u.Username == req.Username!, ct);

            if (user is null) return NotFound("Usuario no encontrado.");

            Role? role = null;
            if (req.RoleId.HasValue)
                role = await _db.Roles.FirstOrDefaultAsync(r => r.Id == req.RoleId.Value, ct);
            else if (!string.IsNullOrWhiteSpace(req.RoleName))
                role = await _db.Roles.FirstOrDefaultAsync(r => r.Name == req.RoleName.Trim(), ct);

            if (role is null) return NotFound("Rol no encontrado.");

            var oldRoleId = user.RoleId;
            if (oldRoleId == role.Id)
                return Ok(new { message = "El usuario ya tenía ese rol", user = user.Username, role = role.Name });

            user.RoleId = role.Id;
            _db.Users.Update(user);
            await _db.SaveChangesAsync(ct);

            var oldRoleName = await _db.Roles.Where(r => r.Id == oldRoleId).Select(r => r.Name).FirstOrDefaultAsync(ct);

            try
            {
                _db.AuditLogs.Add(new AuditLog
                {
                    UserId = GetCurrentUserId(User),
                    Action = "AssignRole",
                    Entity = "User",
                    EntityId = user.Id,
                    Ts = DateTime.UtcNow,
                    Detail = $"oldRole={oldRoleName}, newRole={role.Name}"
                });
                await _db.SaveChangesAsync(ct);
            }
            catch { }

            return Ok(new
            {
                message = "Rol asignado",
                userId = user.Id,
                user = user.Username,
                oldRole = oldRoleName,
                newRole = role.Name
            });
        }

        // ========= 5) Usuario: cambiar SU contraseña =========
        [Authorize]
        [HttpPost("change-password")]
        public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest dto, CancellationToken ct)
        {
            if (!ModelState.IsValid) return ValidationProblem(ModelState);

            var myId = GetCurrentUserId(User);
            if (myId is null) return Unauthorized();

            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == myId.Value, ct);
            if (user is null) return Unauthorized();

            if (!BCrypt.Net.BCrypt.Verify(dto.CurrentPassword, user.PasswordHash))
                return BadRequest("❌ La contraseña actual no es correcta.");

            if (BCrypt.Net.BCrypt.Verify(dto.NewPassword, user.PasswordHash))
                return BadRequest("❌ La nueva contraseña no puede ser igual a la anterior.");

            var (ok, err) = ValidatePassword(dto.NewPassword);
            if (!ok) return BadRequest(err);

            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(dto.NewPassword);
            user.MustChangePassword = false;
            user.PasswordChangedAt = DateTime.UtcNow;
            _db.Users.Update(user);
            await _db.SaveChangesAsync(ct);

            try
            {
                _db.AuditLogs.Add(new AuditLog
                {
                    UserId = myId,
                    Action = "ChangePassword",
                    Entity = "User",
                    EntityId = user.Id,
                    Ts = DateTime.UtcNow,
                    Detail = "Self-service"
                });
                await _db.SaveChangesAsync(ct);
            }
            catch { }

            return Ok(new { message = "✅ Contraseña actualizada. Vuelve a iniciar sesión para refrescar el token." });
        }

        // ========= 6) Admin: cambiar contraseña de CUALQUIER usuario =========
        [Authorize(Roles = "Admin")]
        [HttpPost("admin-change-password")]
        public async Task<IActionResult> AdminChangePassword([FromBody] AdminChangePasswordRequest dto, CancellationToken ct)
        {
            if (!ModelState.IsValid) return ValidationProblem(ModelState);

            if (dto.UserId is null && string.IsNullOrWhiteSpace(dto.Username))
                return BadRequest("Debes indicar UserId o Username.");

            var user = dto.UserId.HasValue
                ? await _db.Users.FirstOrDefaultAsync(u => u.Id == dto.UserId.Value, ct)
                : await _db.Users.FirstOrDefaultAsync(u => u.Username == dto.Username!, ct);

            if (user is null) return NotFound("Usuario no encontrado.");

            if (BCrypt.Net.BCrypt.Verify(dto.NewPassword, user.PasswordHash))
                return BadRequest("❌ La nueva contraseña no puede ser igual a la anterior.");

            var (ok, err) = ValidatePassword(dto.NewPassword);
            if (!ok) return BadRequest(err);

            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(dto.NewPassword);
            user.MustChangePassword = true;
            user.PasswordChangedAt = null;
            _db.Users.Update(user);
            await _db.SaveChangesAsync(ct);

            try
            {
                _db.AuditLogs.Add(new AuditLog
                {
                    UserId = GetCurrentUserId(User),
                    Action = "AdminChangePassword",
                    Entity = "User",
                    EntityId = user.Id,
                    Ts = DateTime.UtcNow,
                    Detail = $"ByAdmin; Target={user.Username}"
                });
                await _db.SaveChangesAsync(ct);
            }
            catch { }

            return Ok(new { message = "✅ Contraseña actualizada por admin.", user = user.Username });
        }

        // ===== Helpers =====
        private static long? GetCurrentUserId(ClaimsPrincipal user)
        {
            var id = user.FindFirstValue(ClaimTypes.NameIdentifier);
            return long.TryParse(id, out var parsed) ? parsed : null;
        }

        private static (bool ok, string? error) ValidatePassword(string pwd)
        {
            if (string.IsNullOrWhiteSpace(pwd)) return (false, "❌ Contraseña vacía.");
            if (pwd.Length < 8) return (false, "❌ Debe tener al menos 8 caracteres.");
            if (!pwd.Any(char.IsUpper)) return (false, "❌ Debe incluir al menos una mayúscula.");
            if (!pwd.Any(char.IsLower)) return (false, "❌ Debe incluir al menos una minúscula.");
            if (!pwd.Any(char.IsDigit)) return (false, "❌ Debe incluir al menos un número.");
            return (true, null);
        }
    }
}
