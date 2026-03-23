namespace Damebooru.Processing.Infrastructure.External.Shared;

internal sealed class PreparedUploadStream(Stream stream, string fileName, string contentType, bool ownsStream) : IAsyncDisposable
{
    private readonly bool _ownsStream = ownsStream;

    public Stream Stream { get; } = stream;
    public string FileName { get; } = fileName;
    public string ContentType { get; } = contentType;

    public ValueTask DisposeAsync() => _ownsStream
        ? Stream.DisposeAsync()
        : ValueTask.CompletedTask;
}
