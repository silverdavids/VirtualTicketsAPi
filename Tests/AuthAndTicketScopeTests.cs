using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using VirtualTickets.Api.Data;
using VirtualTickets.Api.Middleware;
using VirtualTickets.Api.Services;
using Xunit;

namespace VirtualTickets.Api.Tests;

[Collection("Environment variables")]
public sealed class AuthAndTicketScopeTests
{
    [Fact]
    public void Branch_2_terminal_cannot_request_branch_7_scope()
    {
        var scope = VirtualTicketAccessScopePolicy.Apply(
            new TerminalVirtualTicketScope(2, "branch-2-ticket-user"),
            "branch-7-user",
            7);

        Assert.Equal(2, scope.BranchId);
        Assert.Equal("branch-2-ticket-user", scope.UserId);
    }

    [Fact]
    public void Shared_key_scope_preserves_explicit_administrative_filters()
    {
        var scope = VirtualTicketAccessScopePolicy.Apply(null, "requested-user", 7);
        Assert.Equal(7, scope.BranchId);
        Assert.Equal("requested-user", scope.UserId);
    }

    [Fact]
    public async Task Missing_credentials_never_return_service_unavailable()
    {
        var original = Environment.GetEnvironmentVariable("VIRTUAL_TICKETS_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("VIRTUAL_TICKETS_API_KEY", null);
            var context = ProtectedRequest();
            var middleware = Middleware(new ProductionEnvironment());

            await middleware.InvokeAsync(context);

            Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
            Assert.NotEqual(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("VIRTUAL_TICKETS_API_KEY", original);
        }
    }

    [Fact]
    public async Task Invalid_shared_key_returns_forbidden_not_service_unavailable()
    {
        var original = Environment.GetEnvironmentVariable("VIRTUAL_TICKETS_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("VIRTUAL_TICKETS_API_KEY", "configured-key");
            var context = ProtectedRequest();
            context.Request.Headers["X-Virtual-Tickets-Key"] = "wrong-key";

            await Middleware(new ProductionEnvironment()).InvokeAsync(context);

            Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("VIRTUAL_TICKETS_API_KEY", original);
        }
    }

    [Fact]
    public async Task Invalid_bearer_token_is_left_to_jwt_authentication_as_unauthorized()
    {
        var context = ProtectedRequest();
        context.Request.Headers.Authorization = "Bearer invalid-token";
        var middleware = new VirtualTicketsApiKeyMiddleware(
            nextContext =>
            {
                nextContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            },
            NullLogger<VirtualTicketsApiKeyMiddleware>.Instance,
            new ProductionEnvironment());

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.NotEqual(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
    }

    [Fact]
    public void Terminal_scope_is_resolved_from_current_database_terminal_and_applied_to_details()
    {
        var controller = File.ReadAllText(Path.Combine(ProjectRoot(), "Controllers", "VirtualTicketsController.cs"));
        var database = File.ReadAllText(Path.Combine(ProjectRoot(), "Data", "VirtualTicketDb.cs"));

        Assert.Contains("ResolveTerminalScopeAsync", controller);
        Assert.Contains("t.IsActive = 1", database);
        Assert.Contains("databaseBranchId.Value != identity.BranchId", database);
        Assert.Contains("b.TicketAccountUserId", database);
        Assert.Contains("@UserId IS NULL OR r.UserId = @UserId", database);
        Assert.Contains("@BranchId IS NULL OR r.BranchId = @BranchId", database);
    }

    private static DefaultHttpContext ProtectedRequest()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/virtual-tickets";
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static VirtualTicketsApiKeyMiddleware Middleware(IHostEnvironment environment) => new(
        _ => Task.CompletedTask,
        NullLogger<VirtualTicketsApiKeyMiddleware>.Instance,
        environment);

    private static string ProjectRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private sealed class ProductionEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "VirtualTickets.Api.Tests";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

[CollectionDefinition("Environment variables", DisableParallelization = true)]
public sealed class EnvironmentVariableCollection;
