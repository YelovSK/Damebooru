using Bakabooru.Core.Results;
using Microsoft.AspNetCore.Mvc;

namespace Bakabooru.Server.Extensions;

public static class ControllerResultExtensions
{
    public static IActionResult ToHttpResult(this Result result, Func<IActionResult>? onSuccess = null)
    {
        if (result.IsSuccess)
        {
            return onSuccess?.Invoke() ?? new NoContentResult();
        }

        return ToErrorResult(result.Error, result.Message);
    }

    public static IActionResult ToHttpResult<T>(this Result<T> result, Func<T?, IActionResult>? onSuccess = null)
    {
        if (result.IsSuccess)
        {
            if (onSuccess != null)
            {
                return onSuccess(result.Value);
            }

            return result.Value is null ? new NoContentResult() : new OkObjectResult(result.Value);
        }

        return ToErrorResult(result.Error, result.Message);
    }

    public static async Task<IActionResult> ToHttpResult(this Task<Result> resultTask, Func<IActionResult>? onSuccess = null)
    {
        var result = await resultTask;
        return result.ToHttpResult(onSuccess);
    }

    public static async Task<IActionResult> ToHttpResult<T>(this Task<Result<T>> resultTask, Func<T?, IActionResult>? onSuccess = null)
    {
        var result = await resultTask;
        return result.ToHttpResult(onSuccess);
    }

    private static IActionResult ToErrorResult(OperationError? error, string? message)
    {
        var description = string.IsNullOrWhiteSpace(message) ? "Request failed." : message;
        return error switch
        {
            OperationError.NotFound => new NotFoundObjectResult(description),
            OperationError.InvalidInput => new BadRequestObjectResult(description),
            OperationError.Conflict => new ConflictObjectResult(description),
            _ => new ObjectResult(description)
            {
                StatusCode = StatusCodes.Status500InternalServerError
            }
        };
    }
}
