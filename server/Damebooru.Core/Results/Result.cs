namespace Damebooru.Core.Results;

public enum OperationError
{
    NotFound,
    InvalidInput,
    Conflict
}

public readonly record struct Result
{
    public bool IsSuccess { get; init; }
    public OperationError? Error { get; init; }
    public string? Message { get; init; }

    public static Result Success() => new()
    {
        IsSuccess = true
    };

    public static Result Failure(OperationError error, string message) => new()
    {
        IsSuccess = false,
        Error = error,
        Message = message
    };
}

public readonly record struct Result<T>
{
    public bool IsSuccess { get; init; }
    public T? Value { get; init; }
    public OperationError? Error { get; init; }
    public string? Message { get; init; }

    public static Result<T> Success(T value) => new()
    {
        IsSuccess = true,
        Value = value
    };

    public static Result<T> Failure(OperationError error, string message) => new()
    {
        IsSuccess = false,
        Error = error,
        Message = message
    };
}
