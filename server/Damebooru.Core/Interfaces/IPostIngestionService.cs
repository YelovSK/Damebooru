using Damebooru.Core.Entities;

namespace Damebooru.Core.Interfaces;

public interface IPostIngestionService
{
    Task EnqueuePostAsync(Post post, CancellationToken cancellationToken);
    Task FlushAsync(CancellationToken cancellationToken);
}
