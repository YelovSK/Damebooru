using System.Threading;
using System.Threading.Tasks;

namespace Bakabooru.Core.Interfaces;

public interface ISimilarityService
{
    Task<ulong> ComputeHashAsync(string filePath, CancellationToken cancellationToken = default);
}
