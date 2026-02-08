using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Bakabooru.Core.Interfaces;

public interface IHasherService
{
    Task<string> ComputeMd5Async(string filePath, CancellationToken cancellationToken = default);
    Task<string> ComputeMd5Async(Stream stream, CancellationToken cancellationToken = default);
}
