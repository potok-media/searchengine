using Microsoft.Extensions.Options;
using Potok.SearchEngine.Core.Models.Options;

namespace Potok.SearchEngine.Middlewares;

public class ModHeaders
{
    private readonly RequestDelegate _next;

    public ModHeaders(RequestDelegate next)
    {
        _next = next;
    }

    public Task Invoke(HttpContext httpContext, IOptionsSnapshot<Config> configOptions)
    {
        var config = configOptions.Value;

        httpContext.Response.Headers.AccessControlAllowCredentials = "true";
        httpContext.Response.Headers["Access-Control-Allow-Private-Network"] = "true";

        // Allow everything: echo whatever the preflight asks for (a bare "*" is invalid
        // alongside Allow-Credentials), fall back to "*" when there is no preflight.
        httpContext.Response.Headers.AccessControlAllowHeaders =
            httpContext.Request.Headers.TryGetValue("Access-Control-Request-Headers", out var requestHeaders)
                ? requestHeaders.ToString()
                : "*";
        httpContext.Response.Headers.AccessControlAllowMethods =
            httpContext.Request.Headers.TryGetValue("Access-Control-Request-Method", out var requestMethod)
                ? requestMethod.ToString()
                : "*";

        if (httpContext.Request.Headers.TryGetValue("origin", out var origin))
            httpContext.Response.Headers.AccessControlAllowOrigin = origin.ToString();
        else if (httpContext.Request.Headers.TryGetValue("referer", out var referer))
            httpContext.Response.Headers.AccessControlAllowOrigin = referer.ToString();
        else
            httpContext.Response.Headers.AccessControlAllowOrigin = "*";

        if (HttpMethods.IsOptions(httpContext.Request.Method))
        {
            httpContext.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        }

        return _next(httpContext);
    }
}

public static class ModHeadersExtensions
{
    public static IApplicationBuilder UseModHeaders(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<ModHeaders>();
    }
}
