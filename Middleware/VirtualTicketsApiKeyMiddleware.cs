using System.Security.Cryptography;
using System.Security.Claims;
using System.Text;

namespace VirtualTickets.Api.Middleware;

public sealed class VirtualTicketsApiKeyMiddleware
{
    private const string VirtualTicketsPathPrefix = "/api/virtual-tickets";
    private const string TicketValidatePath = "/api/tickets/validate";
    private const string TicketPlacePath = "/api/tickets/place";
    private const string HeaderName = "X-Virtual-Tickets-Key";
    private const string BearerPrefix = "Bearer ";
    private readonly RequestDelegate _next;
    private readonly ILogger<VirtualTicketsApiKeyMiddleware> _logger;
    private readonly IHostEnvironment _environment;
    private bool _warnedMissingDevelopmentKey;

    public VirtualTicketsApiKeyMiddleware(
        RequestDelegate next,
        ILogger<VirtualTicketsApiKeyMiddleware> logger,
        IHostEnvironment environment)
    {
        _next = next;
        _logger = logger;
        _environment = environment;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // TODO: Replace shared API key with Terminal Registration + JWT backed by dbo.Terminals.
        if (!IsProtectedTicketRequest(context.Request))
        {
            await _next(context);
            return;
        }

        if (HasBearerToken(context.Request))
        {
            await _next(context);
            return;
        }

        var configuredKey = Environment.GetEnvironmentVariable("VIRTUAL_TICKETS_API_KEY");
        if (string.IsNullOrWhiteSpace(configuredKey))
        {
            if (_environment.IsDevelopment())
            {
                LogMissingDevelopmentKeyWarningOnce();
                await _next(context);
                return;
            }

            _logger.LogError("[virtual-tickets-api] VIRTUAL_TICKETS_API_KEY is not set; ticket API auth is unavailable.");
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new
            {
                message = "Ticket API authentication is not configured."
            });
            return;
        }

        if (!context.Request.Headers.TryGetValue(HeaderName, out var suppliedKey) ||
            string.IsNullOrWhiteSpace(suppliedKey))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new
            {
                message = "Ticket API authentication key is required."
            });
            return;
        }

        if (!KeysMatch(configuredKey, suppliedKey.ToString()))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new
            {
                message = "Ticket API authentication key is invalid."
            });
            return;
        }

        context.User = CreateApiKeyPrincipal();
        await _next(context);
    }

    private static bool IsProtectedTicketRequest(HttpRequest request)
    {
        return !HttpMethods.IsOptions(request.Method) &&
            (request.Path.StartsWithSegments(VirtualTicketsPathPrefix, StringComparison.OrdinalIgnoreCase) ||
                request.Path.Equals(TicketValidatePath, StringComparison.OrdinalIgnoreCase) ||
                request.Path.Equals(TicketPlacePath, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasBearerToken(HttpRequest request)
    {
        return request.Headers.TryGetValue("Authorization", out var authorization) &&
            authorization.ToString().StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static ClaimsPrincipal CreateApiKeyPrincipal()
    {
        var identity = new ClaimsIdentity(
        [
            new Claim("auth_type", "shared_api_key")
        ], "SharedApiKey");

        return new ClaimsPrincipal(identity);
    }

    private void LogMissingDevelopmentKeyWarningOnce()
    {
        if (_warnedMissingDevelopmentKey)
        {
            return;
        }

        _warnedMissingDevelopmentKey = true;
        _logger.LogWarning("[virtual-tickets-api] VIRTUAL_TICKETS_API_KEY is not set; ticket API is running without auth.");
    }

    private static bool KeysMatch(string configuredKey, string suppliedKey)
    {
        var configuredBytes = Encoding.UTF8.GetBytes(configuredKey);
        var suppliedBytes = Encoding.UTF8.GetBytes(suppliedKey);

        return configuredBytes.Length == suppliedBytes.Length &&
            CryptographicOperations.FixedTimeEquals(configuredBytes, suppliedBytes);
    }
}
