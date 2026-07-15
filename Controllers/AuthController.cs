using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using VirtualTickets.Api.Auth;
using VirtualTickets.Api.Contracts;
using VirtualTickets.Api.Data;

namespace VirtualTickets.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly TerminalAuthDb _terminalAuthDb;
    private readonly JwtTokenService _jwtTokenService;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        TerminalAuthDb terminalAuthDb,
        JwtTokenService jwtTokenService,
        IHostEnvironment environment,
        ILogger<AuthController> logger)
    {
        _terminalAuthDb = terminalAuthDb;
        _jwtTokenService = jwtTokenService;
        _environment = environment;
        _logger = logger;
    }

    [HttpPost("display")]
    public async Task<ActionResult<DisplayAuthResponse>> AuthenticateDisplay(
        [FromBody] DisplayAuthRequest request,
        CancellationToken cancellationToken)
    {
        if (!_jwtTokenService.IsConfigured)
        {
            _logger.LogWarning("[terminal-auth] Display authentication rejected because JWT signing key is missing.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                message = "Display terminal authentication is not configured."
            });
        }

        if (string.IsNullOrWhiteSpace(request.TerminalCode) || string.IsNullOrWhiteSpace(request.Secret))
        {
            _logger.LogWarning("[terminal-auth] Display authentication rejected because terminalCode or secret is missing.");
            return BadRequest(new
            {
                message = "Terminal code and secret are required."
            });
        }

        try
        {
            _logger.LogInformation(
                "[terminal-auth] Controller received display auth request for terminalCode '{TerminalCode}'.",
                request.TerminalCode.Trim());

            var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
            var result = await _terminalAuthDb.AuthenticateAsync(
                request.TerminalCode,
                request.Secret,
                request.Version,
                ipAddress,
                cancellationToken);

            if (!result.IsAuthenticated || result.Terminal is null)
            {
                _logger.LogWarning(
                    "[terminal-auth] Display authentication failed for terminalCode '{TerminalCode}'. FailureCode={FailureCode}, FailureMessage={FailureMessage}.",
                    request.TerminalCode.Trim(),
                    result.FailureCode,
                    result.FailureMessage);

                return Unauthorized(CreateAuthenticationFailureResponse(result));
            }

            var issuedToken = _jwtTokenService.Issue(result.Terminal);
            _logger.LogInformation(
                "[terminal-auth] JWT issued for TerminalId {TerminalId}. ExpiresAt={ExpiresAt:O}.",
                result.Terminal.TerminalId,
                issuedToken.ExpiresAt);

            return Ok(new DisplayAuthResponse
            {
                AccessToken = issuedToken.AccessToken,
                ExpiresAt = issuedToken.ExpiresAt,
                Terminal = new AuthenticatedTerminalResponse
                {
                    TerminalId = result.Terminal.TerminalId,
                    TerminalCode = result.Terminal.TerminalCode,
                    TerminalName = result.Terminal.TerminalName,
                    BranchId = result.Terminal.BranchId,
                    TerminalType = result.Terminal.TerminalType
                }
            });
        }
        catch (Exception exception) when (exception is SqlException or InvalidOperationException)
        {
            _logger.LogError(exception, "Display terminal authentication failed.");
            if (_environment.IsDevelopment())
            {
                return StatusCode(StatusCodes.Status500InternalServerError, new
                {
                    message = "Display terminal authentication could not be completed.",
                    detail = exception.Message
                });
            }

            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                message = "Display terminal authentication could not be completed."
            });
        }
    }

    private object CreateAuthenticationFailureResponse(TerminalAuthenticationResult result)
    {
        if (!_environment.IsDevelopment())
        {
            return new
            {
                message = "Terminal credentials are invalid."
            };
        }

        return new
        {
            message = result.FailureMessage ?? "Terminal credentials are invalid.",
            code = result.FailureCode.ToString()
        };
    }
}
