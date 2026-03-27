using Cashlane.Api.Infrastructure.Services;

namespace Cashlane.Api.Infrastructure.Middleware;

public sealed class AccountAccessMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IAccountAccessService accountAccessService)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            await accountAccessService.InitializeAsync(context.RequestAborted);
        }

        await next(context);
    }
}
