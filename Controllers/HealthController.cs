using Microsoft.AspNetCore.Mvc;
using VirtualTickets.Api.Services;

namespace VirtualTickets.Api.Controllers;

[ApiController]
[Route("api/health")]
public sealed class HealthController : ControllerBase
{
    [HttpGet]
    public IActionResult Get()
    {
        return Ok(new
        {
            status = "healthy",
            service = "VirtualTickets.Api",
            buildSha = BuildInformation.Sha,
            utc = DateTimeOffset.UtcNow
        });
    }
}
