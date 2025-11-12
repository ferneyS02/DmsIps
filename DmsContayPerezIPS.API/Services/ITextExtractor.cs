using Microsoft.AspNetCore.Http;
using System.Threading;
using System.Threading.Tasks;

namespace DmsContayPerezIPS.API.Services
{
    public interface ITextExtractor
    {
        Task<string> ExtractAsync(IFormFile file, CancellationToken ct = default);
    }
}
