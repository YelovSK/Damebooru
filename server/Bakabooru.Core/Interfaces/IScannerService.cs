using System.Threading;
using System.Threading.Tasks;

namespace Bakabooru.Core.Interfaces;

public interface IScannerService
{
    Task ScanLibraryAsync(int libraryId, CancellationToken cancellationToken = default);
    Task ScanAllLibrariesAsync(CancellationToken cancellationToken = default);
}
