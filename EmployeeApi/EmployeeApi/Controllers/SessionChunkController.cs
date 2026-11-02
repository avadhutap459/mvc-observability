using EmployeeApi.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace EmployeeApi.Controllers
{
    [ApiController]
    [Route("api/session-chunks")]
    public class SessionChunkController : ControllerBase
    {
        private readonly S3SessionRecordingService _service;
        private readonly ILogger<SessionChunkController> _logger;

        public SessionChunkController(
            S3SessionRecordingService service,
            ILogger<SessionChunkController> logger)
        {
            _service = service;
            _logger = logger;
        }

        [HttpPost("{sessionId:guid}/combine")]
        public async Task<IActionResult> Combine(
            Guid sessionId,
            CancellationToken cancellationToken)
        {
            if (sessionId == Guid.Empty)
            {
                return BadRequest("Invalid session ID.");
            }

            var outputDirectory = Path.Combine(
                Path.GetTempPath(),
                "SessionReplay",
                sessionId.ToString("D"));

            var outputFile = Path.Combine(
                outputDirectory,
                "events.json");

            try
            {
                var totalEvents =
                    await _service.CombineChunksAsync(
                        sessionId,
                        outputFile,
                        cancellationToken);

                return Ok(new
                {
                    sessionId,
                    totalEvents,
                    fileName = Path.GetFileName(outputFile),
                    outputFile,
                    status = "Combined successfully"
                });
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Session chunks could not be combined.");

                return Conflict(new
                {
                    message = ex.Message
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error combining session chunks.");

                return Problem(
                    title: "Unable to combine session chunks.",
                    detail: ex.Message,
                    statusCode: 500);
            }
        }
    }
}
