using System.ComponentModel.DataAnnotations;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
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
            [Required] public string Username { get; set; } = default!;
            [Required, MinLength(6)] public string Password { get; set; } = default!;
            // Elige UNO: RoleName (recomendado) o RoleId
            public string? RoleName { get; set; }
            public long? RoleId { get; set; }
        }

        public sealed class LoginRequest
        {
            [Required] public string Username { get; set; } = default!;
            [Required] public string Password { get; set; } = default!;
        }

        public sealed class AssignRoleRequest
        {
            // Identifica usuario con uno:
            public long? UserId { get; set; }
            public string? Username { get; set; }
            // Identifica rol con uno:
            public long? RoleId { get; set; }
            public string? RoleName { get; set; }
        }

        // ========= 1) Lista de roles (para el dropdown del registro) =========
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

        // ========= 2) Registro (JSON en body) =========
        [HttpPost("register")]
        [AllowAnonymous]
        public async Task<IActionResult> Register([FromBody] RegisterRequest dto)
        {
            if (!ModelState.IsValid) return ValidationProblem(ModelState);

            if (await _db.Users.AnyAsync(u => u.Username == dto.Username))
                return BadRequest("❌ El usuario ya existe");

            if (string.IsNullOrWhiteSpace(dto.RoleName) && !dto.RoleId.HasValue)
                return BadRequest("❌ Debes indicar RoleName o RoleId");

            Role? role = null;

            if (!string.IsNullOrWhiteSpace(dto.RoleName))
            {
                var rn = dto.RoleName.Trim();
                role = await _db.Roles.FirstOrDefaultAsync(r => r.Name == rn);
            }
            else if (dto.RoleId.HasValue)
            {
                role = await _db.Roles.FirstOrDefaultAsync(r => r.Id == dto.RoleId.Value);
            }

            if (role is null)
                return BadRequest("❌ El rol especificado no existe");

            var user = new User
            {
                Username = dto.Username,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(dto.Password),
                RoleId = role.Id
            };

            _db.Users.Add(user);
            await _db.SaveChangesAsync();

            return Ok(new { message = "✅ Usuario registrado con éxito", user = user.Username, role = role.Name });
        }

        // ========= 3) Login (JSON en body) =========
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

            // 👇 IMPORTANTE: NameIdentifier (Id) + Name + Role
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Name, user.Username),
                new Claim(ClaimTypes.Role, user.Role?.Name ?? "User")
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
                role = user.Role?.Name
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

            // Usuario
            var user = req.UserId.HasValue
                ? await _db.Users.FirstOrDefaultAsync(u => u.Id == req.UserId.Value, ct)
                : await _db.Users.FirstOrDefaultAsync(u => u.Username == req.Username!, ct);

            if (user is null) return NotFound("Usuario no encontrado.");

            // Rol
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

            return Ok(new
            {
                message = "Rol asignado",
                userId = user.Id,
                user = user.Username,
                oldRole = oldRoleName,
                newRole = role.Name
            });
        }
    }
}
