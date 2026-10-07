namespace WebAPI.Middleware;

public sealed class AuthRedirectMiddleware
{
    private static readonly HashSet<string> ProtectedPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/",
        "/indexapp.html",
        "/config.html",
        "/secrets.html",
    };

    private const string LoginPagePath = "/login.html";
    private readonly RequestDelegate _next;

    public AuthRedirectMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        if (ProtectedPaths.Contains(context.Request.Path.Value ?? string.Empty) &&
            !(context.User.Identity?.IsAuthenticated ?? false))
        {
            context.Response.Redirect(LoginPagePath);
            return;
        }

        await _next(context);
    }
}
