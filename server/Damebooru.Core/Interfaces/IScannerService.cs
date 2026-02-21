using System.Threading;
using System.Threading.Tasks;
using Damebooru.Core.Results;

namespace Damebooru.Core.Interfaces;

public interface IScannerService
{
    Task ScanLibraryAsync(int libraryId, IProgress<float>? progress = null, IProgress<string>? status = null, CancellationToken cancellationToken = default);
    Task<ScanResult> ScanAllLibrariesAsync(IProgress<float>? progress = null, IProgress<string>? status = null, CancellationToken cancellationToken = default);
}
