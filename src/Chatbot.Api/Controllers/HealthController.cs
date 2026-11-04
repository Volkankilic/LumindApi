using Chatbot.Application.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Chatbot.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/health")]
public class HealthController(ISchemaStatusProvider schema, IWebHostEnvironment environment) : ControllerBase
{
    /// <summary>
    /// "ok" when the database answers and the schema version is what this build expects, otherwise 503 "not-ready".
    /// Only Development adds the schema detail (expected / found version): Production says nothing more.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<HealthResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<HealthResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var status = await schema.GetAsync(ct);
        var detail = environment.IsDevelopment() ? new SchemaDetail(status.Expected, status.Found, status.Message) : null;
        var body = new HealthResponse(status.Ready ? "ok" : "not-ready", detail);
        return status.Ready ? Ok(body) : StatusCode(StatusCodes.Status503ServiceUnavailable, body);
    }
}
