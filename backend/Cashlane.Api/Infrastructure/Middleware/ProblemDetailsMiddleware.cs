using System.Net;
using Microsoft.AspNetCore.Mvc;

namespace Cashlane.Api.Infrastructure.Middleware;

public sealed class ProblemDetailsMiddleware(
    RequestDelegate next,
    ILogger<ProblemDetailsMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (AppException exception)
        {
            await WriteProblemAsync(context, exception.StatusCode, exception.Title, exception.Message);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unhandled application exception.");
            await WriteProblemAsync(context, HttpStatusCode.InternalServerError, "Server error", "An unexpected error occurred.");
        }
    }

    private static async Task WriteProblemAsync(HttpContext context, HttpStatusCode statusCode, string title, string detail)
    {
        context.Response.StatusCode = (int)statusCode;
        context.Response.ContentType = "application/problem+json";

        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = (int)statusCode,
            Title = title,
            Detail = detail
        });
    }
}

public sealed class AppException(HttpStatusCode statusCode, string title, string message) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string Title { get; } = title;
}
