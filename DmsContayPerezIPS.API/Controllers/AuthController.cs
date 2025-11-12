using BCrypt.Net;
using DmsContayPerezIPS.Domain.Entities;
using DmsContayPerezIPS.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.ComponentModel.DataAnnotations;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

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

        // =======================
        //         DTOs
        // =======================
        public class RegisterRequest
        {
            [Required, MinLength(3)]
            public string Username { get; set; } = null!;

            [Required, MinLength(4)]
            public string Password { get; set; } = null!;

            // Debe existir en la tabla Roles (p.ej. Admin, GestClinica, GestiAdmin, GestFinYCon, GestJurid, GestCalidad, SGSST, AdminEquBiomed)
            [Required]
            public string RoleName { get; set; } = null!;
        }

        public class LoginRequest
        {
            [Required]
            public string Username { get; set; } = null!;

            [Required]
            public string Password { get; set; } = null!;
        }

        public record AssignRoleRequest([Required] string Username, [Required] string RoleName);

        // =======================
        //       REGISTER
        // =======================
        [HttpPost("register")]
        [AllowAnonymous]
        public async Task<IActionResult> Register([FromBody] RegisterRequest req, CancellationToken ct)
        {
            if (!ModelState.IsValid) return ValidationProblem(ModelState);

            var username = req.Username.Trim();

            // Usuario ya existe
            var exists = await _db.Users.AnyAsync(u => u.Username == username, ct);
            if (exists) return BadRequest("❌ El usuario ya existe.");

            // Rol debe existir
            var role = await _db.Roles.FirstOrDefaultAsync(r => r.Name == req.RoleName, ct);
            if (role is null) return BadRequest("❌ El rol especificado no existe.");

            var user = new User
            {
                Username = username,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.Password),
                RoleId = role.Id,
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };

            _db.Users.Add(user);
            await _db.SaveChangesAsync(ct);

            return Ok(new
            {
                message = "✅ Usuario registrado con éxito",
                user = new { user.Id, user.Username, role = role.Name }
            });
        }

        // =======================
        //         LOGIN
        // =======================
        [HttpPost("login")]
        [AllowAnonymous]
        public async Task<IActionResult> Login([FromBody] LoginRequest req, CancellationToken ct)
        {
            if (!ModelState.IsValid) return ValidationProblem(ModelState);

            var user = await _db.Users
                .Include(u => u.Role)
                .FirstOrDefaultAsync(u => u.Username == req.Username, ct);

            if (user is null || !BCrypt.Net.BCrypt.Verify(req.Password, user.PasswordHash))
                return Unauthorized("❌ Credenciales inválidas");

            if (!user.IsActive)
                return Unauthorized("❌ Usuario inactivo");

            // ==== Claims ====
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),     // Id usuario
                new Claim(ClaimTypes.Name, user.Username),                    // Username
                new Claim(ClaimTypes.Role, user.Role?.Name ?? "Admin")        // Rol (tu esquema usa 1 rol por usuario)
            };

            var token = BuildJwt(claims);

            return Ok(new
            {
                token = new JwtSecurityTokenHandler().WriteToken(token),
                expiresAtUtc = token.ValidTo,
                user = new
                {
                    user.Id,
                    user.Username,
                    role = user.Role?.Name
                }
            });
        }

        // ======================================
        //    ASIGNAR ROL (solo Admin) OPCIONAL
        // ======================================
        [HttpPost("assign-role")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> AssignRole([FromBody] AssignRoleRequest req, CancellationToken ct)
        {
            var user = await _db.Users.Include(u => u.Role)
                                      .FirstOrDefaultAsync(u => u.Username == req.Username, ct);
            if (user is null) return NotFound("Usuario no existe.");

            var role = await _db.Roles.FirstOrDefaultAsync(r => r.Name == req.RoleName, ct);
            if (role is null) return BadRequest("Rol no existe.");

            user.RoleId = role.Id;
            await _db.SaveChangesAsync(ct);

            return Ok(new
            {
                message = "Rol asignado",
                user = new { user.Id, user.Username, role = role.Name }
            });
        }

        // =======================
        //    JWT builder
        // =======================
        private JwtSecurityToken BuildJwt(IEnumerable<Claim> claims)
        {
            var key = _config["JWT:Key"] ?? "EstaEsUnaClaveJWTDeAlMenos32CaracteresSuperSegura!!123";
            var creds = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256);

            // Opcionales: Issuer/Audience desde configuración
            var issuer = _config["JWT:Issuer"];
            var audience = _config["JWT:Audience"];

            // Expiración (2h) – ajusta si quieres sacarlo de config
            var expires = DateTime.UtcNow.AddHours(2);

            return new JwtSecurityToken(
                issuer: issuer,
                audience: audience,
                claims: claims,
                notBefore: DateTime.UtcNow,
                expires: expires,
                signingCredentials: creds
            );
        }
    }
}
