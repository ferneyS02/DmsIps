using DmsContayPerezIPS.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DmsContayPerezIPS.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class TiposDocumentalesController : ControllerBase
    {
        private readonly AppDbContext _db;

        public TiposDocumentalesController(AppDbContext db)
        {
            _db = db;
        }

        [HttpGet("list")]
        [Authorize]
        public IActionResult List()
        {
            var tipos = _db.TiposDocumentales.Select(t => new
            {
                t.Id,
                t.Nombre,
                Subserie = t.Subserie != null ? t.Subserie.Nombre : null,
                Serie = t.Subserie != null && t.Subserie.Serie != null ? t.Subserie.Serie.Nombre : null
            }).ToList();

            return Ok(tipos);
        }
    }
}
