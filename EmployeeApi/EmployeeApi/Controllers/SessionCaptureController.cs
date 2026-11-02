using EmployeeApi.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace EmployeeApi.Controllers
{
    [Route("api/session-capture")]
    [ApiController]
    public class SessionCaptureController : ControllerBase
    {
        private readonly S3SessionRecordingService _recordingService;
        private readonly IConfiguration _configuration;

        public SessionCaptureController(
            S3SessionRecordingService recordingService,
            IConfiguration configuration)
        {
            _recordingService = recordingService;
            _configuration = configuration;
        }

        public sealed record UploadChunkRequest(
            int ChunkNumber,
            JsonElement Events);

        [HttpPost("{sessionId:guid}/chunks")]
        [RequestSizeLimit(2_000_000)]
        public async Task<IActionResult> UploadChunk(
            Guid sessionId,
            [FromBody] UploadChunkRequest request,
            CancellationToken cancellationToken)
        {
            if (sessionId == Guid.Empty)
                return BadRequest("Invalid session ID.");

            if (request.ChunkNumber < 0)
                return BadRequest("Chunk number must be non-negative.");

            if (request.Events.ValueKind != JsonValueKind.Array)
                return BadRequest("Events must be a JSON array.");

            var maxBytes = _configuration.GetValue<int>(
                "SessionReplay:MaxChunkBytes", 2_000_000);

            if (Request.ContentLength is long length &&
                length > maxBytes)
            {
                return StatusCode(413, "Chunk is too large.");
            }

            try
            {
                await _recordingService.SaveChunkAsync(
                    sessionId,
                    request.ChunkNumber,
                    request.Events,
                    cancellationToken);

                return Ok(new
                {
                    sessionId,
                    request.ChunkNumber,
                    uploaded = true
                });
            }
            catch (InvalidOperationException ex)
            {
                return Conflict(new { error = ex.Message });
            }
        }
    }
}
