using DmsContayPerezIPS.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DmsContayPerezIPS.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class TRDController : ControllerBase
    {
        private readonly AppDbContext _db;

        public TRDController(AppDbContext db)
        {
            _db = db;
        }

        // 🔹 Devuelve todas las series con sus subseries y tipos documentales
        [HttpGet("series")]
        // [Authorize] // 👉 Descomenta si quieres requerir token JWT
        public async Task<IActionResult> GetSeries()
        {
            var series = await _db.Series
                .Include(s => s.Subseries!)
                    .ThenInclude(ss => ss.TiposDocumentales!)
                .Select(s => new
                {
                    s.Id,
                    s.Nombre,
                    Subseries = s.Subseries!.Select(ss => new
                    {
                        ss.Id,
                        ss.Nombre,
                        ss.RetencionGestion,
                        ss.RetencionCentral,
                        ss.DisposicionFinal,
                        Tipos = ss.TiposDocumentales!.Select(t => new
                        {
                            t.Id,
                            t.Nombre,
                            t.DisposicionFinal
                        })
                    })
                })
                .ToListAsync();

            return Ok(series);
        }

        // 🔹 Devuelve una serie específica por Id con sus subseries y tipos
        [HttpGet("series/{id:long}")]
        public async Task<IActionResult> GetSerieById(long id)
        {
            var serie = await _db.Series
                .Include(s => s.Subseries!)
                    .ThenInclude(ss => ss.TiposDocumentales!)
                .Where(s => s.Id == id)
                .Select(s => new
                {
                    s.Id,
                    s.Nombre,
                    Subseries = s.Subseries!.Select(ss => new
                    {
                        ss.Id,
                        ss.Nombre,
                        ss.RetencionGestion,
                        ss.RetencionCentral,
                        ss.DisposicionFinal,
                        Tipos = ss.TiposDocumentales!.Select(t => new
                        {
                            t.Id,
                            t.Nombre,
                            t.DisposicionFinal
                        })
                    })
                })
                .FirstOrDefaultAsync();

            if (serie == null)
                return NotFound(new { message = $"No existe la serie con Id={id}" });

            return Ok(serie);
        }

        // 🔹 Devuelve todas las subseries con sus tipos documentales
        [HttpGet("subseries")]
        public async Task<IActionResult> GetSubseries()
        {
            var subseries = await _db.Subseries
                .Include(ss => ss.TiposDocumentales!)
                .Select(ss => new
                {
                    ss.Id,
                    ss.Nombre,
                    ss.RetencionGestion,
                    ss.RetencionCentral,
                    ss.DisposicionFinal,
                    Tipos = ss.TiposDocumentales!.Select(t => new
                    {
                        t.Id,
                        t.Nombre,
                        t.DisposicionFinal
                    })
                })
                .ToListAsync();

            return Ok(subseries);
        }
    }
}
