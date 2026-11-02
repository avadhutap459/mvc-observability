using Amazon.S3;
using EmployeeApi.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace EmployeeApi.Controllers
{
    [ApiController]
    [Route("api/session-video")]
    public class SessionVideoController : ControllerBase
    {
        private readonly FfmpegVideoConverter _converter;
        private readonly IConfiguration _configuration;
        private readonly ILogger<SessionVideoController> _logger;
        private readonly S3SessionRecordingService _recordingService;
        private readonly SessionReplayOrchestrator _orchestrator;

        public SessionVideoController(
            FfmpegVideoConverter converter,
            S3SessionRecordingService recordingService,
            SessionReplayOrchestrator orchestrator,
            IConfiguration configuration,
            ILogger<SessionVideoController> logger)
        {
            _converter = converter;
            _recordingService = recordingService;
            _orchestrator = orchestrator;
            _configuration = configuration;
            _logger = logger;
        }

        [HttpPost("{sessionId:guid}/convert")]
        public async Task<IActionResult> Convert(
            Guid sessionId,
            CancellationToken cancellationToken)
        {
            if (sessionId == Guid.Empty)
                return BadRequest("Invalid session ID.");

            var workingDirectory =
                _configuration["SessionReplay:WorkingDirectory"];

            if (string.IsNullOrWhiteSpace(workingDirectory))
                return StatusCode(500,
                    "SessionReplay:WorkingDirectory is not configured.");

            var sessionDirectory = Path.Combine(
                workingDirectory,
                sessionId.ToString("D"));

            var inputWebm = Path.Combine(
                sessionDirectory,
                "session.webm");

            var outputMp4 = Path.Combine(
                sessionDirectory,
                "session.mp4");

            try
            {
                var generatedFile =
                    await _converter.ConvertWebmToMp4Async(
                        inputWebm,
                        outputMp4,
                        cancellationToken);

                var fileInfo = new FileInfo(generatedFile);

                return Ok(new
                {
                    sessionId,
                    conversionCompleted = true,
                    inputFile = inputWebm,
                    outputFile = outputMp4,
                    outputSizeBytes = fileInfo.Length
                });
            }
            catch (FileNotFoundException ex)
            {
                return NotFound(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Session video conversion failed for {SessionId}",
                    sessionId);

                return StatusCode(500, new
                {
                    sessionId,
                    conversionCompleted = false,
                    error = ex.Message
                });
            }
        }


        [HttpPost("{sessionId:guid}/upload")]
        public async Task<IActionResult> UploadMp4(
            Guid sessionId,
            CancellationToken cancellationToken)
        {
            if (sessionId == Guid.Empty)
                return BadRequest("Invalid session ID.");

            var workingDirectory =
                _configuration["SessionReplay:WorkingDirectory"];

            if (string.IsNullOrWhiteSpace(workingDirectory))
            {
                return StatusCode(500,
                    "SessionReplay:WorkingDirectory is not configured.");
            }

            var sessionDirectory = Path.Combine(
                workingDirectory,
                sessionId.ToString("D"));

            var mp4FilePath = Path.Combine(
                sessionDirectory,
                "session.mp4");

            try
            {
                if (!System.IO.File.Exists(mp4FilePath))
                {
                    return NotFound(new
                    {
                        sessionId,
                        error = "MP4 recording not found. Complete Step 6 first."
                    });
                }

                var s3Key = await _recordingService.UploadMp4Async(
                    sessionId,
                    mp4FilePath,
                    cancellationToken);

                var fileInfo = new FileInfo(mp4FilePath);

                return Ok(new
                {
                    sessionId,
                    uploaded = true,
                    bucket = _configuration["AWS:BucketName"],
                    s3Key,
                    contentType = "video/mp4",
                    fileSizeBytes = fileInfo.Length
                });
            }
            catch (AmazonS3Exception ex)
            {
                _logger.LogError(
                    ex,
                    "AWS S3 upload failed for session {SessionId}",
                    sessionId);

                return StatusCode(502, new
                {
                    sessionId,
                    uploaded = false,
                    error = ex.Message,
                    awsErrorCode = ex.ErrorCode
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "MP4 upload failed for session {SessionId}",
                    sessionId);

                return StatusCode(500, new
                {
                    sessionId,
                    uploaded = false,
                    error = ex.Message
                });
            }
        }



        public sealed record CompleteSessionRequest(
            int LastChunkNumber);

        [HttpPost("{sessionId:guid}/complete")]
        public async Task<IActionResult> CompleteSession(
            Guid sessionId,
            [FromBody] CompleteSessionRequest request,
            CancellationToken cancellationToken)
        {
            if (sessionId == Guid.Empty)
                return BadRequest("Invalid session ID.");

            if (request.LastChunkNumber < 0)
                return BadRequest(
                    "LastChunkNumber must be zero or greater.");

            try
            {
                var result = await _orchestrator.CompleteSessionAsync(
                    sessionId,
                    request.LastChunkNumber,
                    cancellationToken);

                return Ok(result);
            }
            catch (InvalidOperationException ex)
            {
                return Conflict(new
                {
                    sessionId,
                    status = "Failed",
                    error = ex.Message
                });
            }
            catch (FileNotFoundException ex)
            {
                _logger.LogError(ex,
                    "Session replay dependency or file missing.");

                return StatusCode(500, new
                {
                    sessionId,
                    status = "Failed",
                    error = ex.Message
                });
            }
            catch (OperationCanceledException)
            {
                return StatusCode(408, new
                {
                    sessionId,
                    status = "Cancelled"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Session completion failed. SessionId: {SessionId}",
                    sessionId);

                return StatusCode(500, new
                {
                    sessionId,
                    status = "Failed",
                    error = "Session processing failed. Check API logs."
                });
            }
        }


    }
}
