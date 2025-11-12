using BCrypt.Net;
using DmsContayPerezIPS.Domain.Entities;
using DmsContayPerezIPS.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
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

        // ============================================
        // GET /api/Auth/roles  -> lista roles (para dropdown)
        // ============================================
        [HttpGet("roles")]
        public async Task<IActionResult> GetRoles()
        {
            var roles = await _db.Roles
                .AsNoTracking()
                .OrderBy(r => r.Name)
                .Select(r => new { r.Id, r.Name })
                .ToListAsync();

            return Ok(roles);
        }

        // ============================================
        // POST /api/Auth/register
        // Registro: elegir rol por roleName o roleId (uno de los dos)
        // ============================================
        [HttpPost("register")]
        public async Task<IActionResult> Register(
            [FromQuery] string username,
            [FromQuery] string password,
            [FromQuery] string? roleName = null,
            [FromQuery] long? roleId = null)
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                return BadRequest("❌ Debes enviar username y password");

            if (await _db.Users.AnyAsync(u => u.Username == username))
                return BadRequest("❌ El usuario ya existe");

            if (string.IsNullOrWhiteSpace(roleName) && !roleId.HasValue)
                return BadRequest("❌ Debes indicar roleName o roleId");

            Role? role = null;

            if (!string.IsNullOrWhiteSpace(roleName))
                role = await _db.Roles.FirstOrDefaultAsync(r => r.Name == roleName);
            else if (roleId.HasValue)
                role = await _db.Roles.FirstOrDefaultAsync(r => r.Id == roleId.Value);

            if (role is null)
                return BadRequest("❌ El rol especificado no existe");

            var user = new User
            {
                Username = username,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
                RoleId = role.Id
            };

            _db.Users.Add(user);
            await _db.SaveChangesAsync();

            return Ok("✅ Usuario registrado con éxito");
        }

        // ============================================
        // POST /api/Auth/login
        // ============================================
        [HttpPost("login")]
        public async Task<IActionResult> Login(string username, string password)
        {
            var user = await _db.Users
                .Include(u => u.Role)
                .FirstOrDefaultAsync(u => u.Username == username);

            if (user == null || !BCrypt.Net.BCrypt.Verify(password, user.PasswordHash))
                return Unauthorized("❌ Credenciales inválidas");

            var claims = new[]
            {
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
                expires: DateTime.Now.AddHours(2),
                signingCredentials: creds
            );

            return Ok(new
            {
                token = new JwtSecurityTokenHandler().WriteToken(token),
                user = user.Username,
                role = user.Role?.Name
            });
        }

        // ============================================
        // POST /api/Auth/Assign-role   (solo Admin)
        // Cambia el rol de un usuario existente.
        // Acepta (userId o username) + (roleId o roleName).
        // Opcional: ?issueNewToken=true para devolver nuevo JWT del usuario reasignado.
        // ============================================
        public class AssignRoleRequest
        {
            public long? UserId { get; set; }
            public string? Username { get; set; }
            public long? RoleId { get; set; }
            public string? RoleName { get; set; }
        }

        [HttpPost("Assign-role")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> AssignRole(
            [FromBody] AssignRoleRequest req,
            [FromQuery] bool issueNewToken = false,
            CancellationToken ct = default)
        {
            if ((req.UserId is null && string.IsNullOrWhiteSpace(req.Username)) ||
                (req.RoleId is null && string.IsNullOrWhiteSpace(req.RoleName)))
                return BadRequest("Debes indicar (UserId o Username) y (RoleId o RoleName).");

            // Usuario
            var user = req.UserId.HasValue
                ? await _db.Users.Include(u => u.Role).FirstOrDefaultAsync(u => u.Id == req.UserId.Value, ct)
                : await _db.Users.Include(u => u.Role).FirstOrDefaultAsync(u => u.Username == req.Username!, ct);

            if (user is null) return NotFound("Usuario no encontrado.");

            // Rol destino
            var role = req.RoleId.HasValue
                ? await _db.Roles.FirstOrDefaultAsync(r => r.Id == req.RoleId.Value, ct)
                : await _db.Roles.FirstOrDefaultAsync(r => r.Name == req.RoleName!, ct);

            if (role is null) return NotFound("Rol no encontrado.");

            if (user.RoleId == role.Id)
            {
                // Idempotencia
                if (!issueNewToken) return Ok(new { message = "El usuario ya tiene ese rol", user = user.Username, role = role.Name });

                // Emitir token igualmente si lo solicitaron
                var tokenSame = IssueToken(user.Username, role.Name);
                return Ok(new { message = "El usuario ya tenía ese rol", user = user.Username, role = role.Name, token = tokenSame });
            }

            user.RoleId = role.Id;
            await _db.SaveChangesAsync(ct);

            if (!issueNewToken)
                return Ok(new { message = "Rol asignado", user = user.Username, role = role.Name });

            // Emitir nuevo token para ese usuario con el rol actualizado
            var token = IssueToken(user.Username, role.Name);
            return Ok(new { message = "Rol asignado", user = user.Username, role = role.Name, token });
        }

        // Helper para emitir JWT con rol
        private string IssueToken(string username, string roleName)
        {
            var claims = new[]
            {
                new Claim(ClaimTypes.Name, username),
                new Claim(ClaimTypes.Role, roleName)
            };

            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(
                _config["JWT:Key"] ?? "EstaEsUnaClaveJWTDeAlMenos32CaracteresSuperSegura!!123"));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var token = new JwtSecurityToken(
                issuer: _config["JWT:Issuer"],
                audience: _config["JWT:Audience"],
                claims: claims,
                expires: DateTime.Now.AddHours(2),
                signingCredentials: creds
            );

            return new JwtSecurityTokenHandler().WriteToken(token);
        }
    }
}
