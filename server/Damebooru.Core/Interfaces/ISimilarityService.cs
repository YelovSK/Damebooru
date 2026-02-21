using System.Threading;
using System.Threading.Tasks;

namespace Damebooru.Core.Interfaces;

public interface ISimilarityService
{
    Task<SimilarityHashes?> ComputeHashesAsync(string filePath, CancellationToken cancellationToken = default);
}
