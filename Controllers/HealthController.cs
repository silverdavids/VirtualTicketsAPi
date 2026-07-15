using Microsoft.AspNetCore.Mvc;

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
            status = "ok",
            service = "VirtualTickets.Api",
            utc = DateTimeOffset.UtcNow
        });
    }
}
