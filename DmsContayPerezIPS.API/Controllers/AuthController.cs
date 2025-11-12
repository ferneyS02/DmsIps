using BCrypt.Net;
using DmsContayPerezIPS.Domain.Entities;
using DmsContayPerezIPS.Infrastructure.Persistence;
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

        // ✅ Registro de usuario
        [HttpPost("register")]
        public async Task<IActionResult> Register(string username, string password, int roleId = 2)
        {
            // Si ya existe el usuario
            if (await _db.Users.AnyAsync(u => u.Username == username))
                return BadRequest("❌ El usuario ya existe");

            // Verificar que el RoleId sea válido
            if (!await _db.Roles.AnyAsync(r => r.Id == roleId))
                return BadRequest("❌ El rol especificado no existe");

            var user = new User
            {
                Username = username,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
                RoleId = roleId // por defecto 2 (User)
            };

            _db.Users.Add(user);
            await _db.SaveChangesAsync();

            return Ok("✅ Usuario registrado con éxito");
        }

        // ✅ Login
        [HttpPost("login")]
        public async Task<IActionResult> Login(string username, string password)
        {
            var user = await _db.Users
                .Include(u => u.Role)
                .FirstOrDefaultAsync(u => u.Username == username);

            if (user == null || !BCrypt.Net.BCrypt.Verify(password, user.PasswordHash))
                return Unauthorized("❌ Credenciales inválidas");

            // Claims
            var claims = new[]
            {
                new Claim(ClaimTypes.Name, user.Username),
                new Claim(ClaimTypes.Role, user.Role?.Name ?? "User")
            };

            // 🔑 JWT Config (usar appsettings.json / .env)
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
    }
}
