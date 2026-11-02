using System.Collections.Concurrent;
using System.Diagnostics;

namespace EmployeeApi.Services
{
    public sealed class SessionReplayOrchestrator
    {
        private readonly S3SessionRecordingService _s3Service;
        private readonly FfmpegVideoConverter _ffmpeg;
        private readonly IConfiguration _configuration;
        private readonly ILogger<SessionReplayOrchestrator> _logger;

        // Prevent duplicate processing inside this API process.
        private static readonly ConcurrentDictionary<Guid, SemaphoreSlim>
            SessionLocks = new();

        public SessionReplayOrchestrator(
            S3SessionRecordingService s3Service,
            FfmpegVideoConverter ffmpeg,
            IConfiguration configuration,
            ILogger<SessionReplayOrchestrator> logger)
        {
            _s3Service = s3Service;
            _ffmpeg = ffmpeg;
            _configuration = configuration;
            _logger = logger;
        }

        public async Task<object> CompleteSessionAsync(
            Guid sessionId,
            int lastChunkNumber,
            CancellationToken cancellationToken)
        {
            if (sessionId == Guid.Empty)
                throw new ArgumentException("Invalid session ID.");

            if (lastChunkNumber < 0)
                throw new ArgumentOutOfRangeException(
                    nameof(lastChunkNumber));

            var sessionLock = SessionLocks.GetOrAdd(
                sessionId,
                _ => new SemaphoreSlim(1, 1));

            // Do not start a second conversion for this session.
            if (!await sessionLock.WaitAsync(
                    0, cancellationToken))
            {
                throw new InvalidOperationException(
                    "This session is already being processed.");
            }

            try
            {
                var workingDirectory =
                    _configuration["SessionReplay:WorkingDirectory"];

                var rendererDirectory =
                    _configuration["SessionReplay:RendererDirectory"];

                var nodeExecutable =
                    _configuration["SessionReplay:NodeExecutable"];

                var rendererScript =
                    _configuration["SessionReplay:RendererScript"]
                    ?? "render.mjs";

                if (string.IsNullOrWhiteSpace(workingDirectory) ||
                    string.IsNullOrWhiteSpace(rendererDirectory) ||
                    string.IsNullOrWhiteSpace(nodeExecutable))
                {
                    throw new InvalidOperationException(
                        "SessionReplay configuration is incomplete.");
                }

                if (!File.Exists(nodeExecutable))
                    throw new FileNotFoundException(
                        "Node.js executable not found.",
                        nodeExecutable);

                var scriptPath = Path.Combine(
                    rendererDirectory,
                    rendererScript);

                if (!File.Exists(scriptPath))
                    throw new FileNotFoundException(
                        "Replay renderer script not found.",
                        scriptPath);

                var sessionDirectory = Path.Combine(
                    workingDirectory,
                    sessionId.ToString("D"));

                Directory.CreateDirectory(sessionDirectory);

                var eventsPath = Path.Combine(
                    sessionDirectory,
                    "events.json");

                var webmPath = Path.Combine(
                    sessionDirectory,
                    "session.webm");

                var mp4Path = Path.Combine(
                    sessionDirectory,
                    "session.mp4");

                // STEP 1: Combine and validate S3 chunks.
                _logger.LogInformation(
                    "Combining S3 chunks for session {SessionId}",
                    sessionId);

                var eventCount =
                    await _s3Service.CombineChunksAsync(
                        sessionId,
                        eventsPath,
                        lastChunkNumber,
                        cancellationToken);

                if (eventCount <= 0)
                    throw new InvalidOperationException(
                        "No rrweb events were found.");

                // STEP 2: Generate WebM using Node.js and Playwright.
                _logger.LogInformation(
                    "Starting browser replay for session {SessionId}",
                    sessionId);

                await RunNodeRendererAsync(
                    nodeExecutable,
                    rendererDirectory,
                    scriptPath,
                    eventsPath,
                    webmPath,
                    cancellationToken);

                if (!File.Exists(webmPath) ||
                    new FileInfo(webmPath).Length == 0)
                {
                    throw new InvalidOperationException(
                        "Replay renderer did not generate a valid WebM file.");
                }

                // STEP 3: Convert WebM to MP4.
                _logger.LogInformation(
                    "Converting WebM to MP4 for session {SessionId}",
                    sessionId);

                await _ffmpeg.ConvertWebmToMp4Async(
                    webmPath,
                    mp4Path,
                    cancellationToken);

                // STEP 4: Upload MP4 to S3.
                _logger.LogInformation(
                    "Uploading MP4 to S3 for session {SessionId}",
                    sessionId);

                var s3Key = await _s3Service.UploadMp4Async(
                    sessionId,
                    mp4Path,
                    cancellationToken);

                _logger.LogInformation(
                    "Session replay completed successfully. SessionId: {SessionId}",
                    sessionId);

                return new
                {
                    sessionId,
                    status = "Completed",
                    eventCount,
                    webmFile = webmPath,
                    mp4File = mp4Path,
                    s3Key
                };
            }
            finally
            {
                sessionLock.Release();
            }
        }

        private async Task RunNodeRendererAsync(
            string nodeExecutable,
            string rendererDirectory,
            string scriptPath,
            string eventsPath,
            string webmPath,
            CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = nodeExecutable,
                WorkingDirectory = rendererDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            startInfo.ArgumentList.Add(scriptPath);
            startInfo.ArgumentList.Add(eventsPath);
            startInfo.ArgumentList.Add(webmPath);

            using var process = new Process
            {
                StartInfo = startInfo
            };

            if (!process.Start())
                throw new InvalidOperationException(
                    "Unable to start Node.js renderer.");

            var outputTask =
                process.StandardOutput.ReadToEndAsync(cancellationToken);

            var errorTask =
                process.StandardError.ReadToEndAsync(cancellationToken);

            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);

                throw;
            }

            var output = await outputTask;
            var error = await errorTask;

            if (!string.IsNullOrWhiteSpace(output))
                _logger.LogInformation(
                    "Node renderer output: {Output}", output);

            if (process.ExitCode != 0)
            {
                _logger.LogError(
                    "Node renderer failed. ExitCode: {ExitCode}. Error: {Error}",
                    process.ExitCode,
                    error);

                throw new InvalidOperationException(
                    $"Node.js renderer failed. Exit code: {process.ExitCode}. {error}");
            }
        }
    }
}
