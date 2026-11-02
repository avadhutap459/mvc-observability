using System.Diagnostics;

namespace EmployeeApi.Services
{
    public class FfmpegVideoConverter
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<FfmpegVideoConverter> _logger;

        public FfmpegVideoConverter(
            IConfiguration configuration,
            ILogger<FfmpegVideoConverter> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        public async Task<string> ConvertWebmToMp4Async(
            string inputWebmPath,
            string outputMp4Path,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(inputWebmPath))
                throw new ArgumentException("Input WebM path is required.");

            if (string.IsNullOrWhiteSpace(outputMp4Path))
                throw new ArgumentException("Output MP4 path is required.");

            if (!File.Exists(inputWebmPath))
            {
                throw new FileNotFoundException(
                    "Input WebM file was not found.",
                    inputWebmPath);
            }

            var ffmpegPath =
                _configuration["SessionReplay:FFmpegExecutable"];

            if (string.IsNullOrWhiteSpace(ffmpegPath))
                throw new InvalidOperationException(
                    "FFmpeg executable path is not configured.");

            if (!File.Exists(ffmpegPath))
                throw new FileNotFoundException(
                    "FFmpeg executable was not found.",
                    ffmpegPath);

            var outputDirectory = Path.GetDirectoryName(outputMp4Path);

            if (string.IsNullOrWhiteSpace(outputDirectory))
                throw new InvalidOperationException(
                    "Invalid output directory.");

            Directory.CreateDirectory(outputDirectory);

            if (File.Exists(outputMp4Path))
                File.Delete(outputMp4Path);

            var timeoutMinutes = _configuration.GetValue<int>(
                "SessionReplay:FFmpegTimeoutMinutes", 60);

            if (timeoutMinutes <= 0)
                timeoutMinutes = 60;

            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            startInfo.ArgumentList.Add("-hide_banner");
            startInfo.ArgumentList.Add("-y");

            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(inputWebmPath);

            startInfo.ArgumentList.Add("-map");
            startInfo.ArgumentList.Add("0:v:0");

            // rrweb browser recording is treated as video-only.
            startInfo.ArgumentList.Add("-an");

            startInfo.ArgumentList.Add("-c:v");
            startInfo.ArgumentList.Add("libx264");

            startInfo.ArgumentList.Add("-preset");
            startInfo.ArgumentList.Add("veryfast");

            startInfo.ArgumentList.Add("-crf");
            startInfo.ArgumentList.Add("23");

            startInfo.ArgumentList.Add("-pix_fmt");
            startInfo.ArgumentList.Add("yuv420p");

            startInfo.ArgumentList.Add("-movflags");
            startInfo.ArgumentList.Add("+faststart");

            startInfo.ArgumentList.Add(outputMp4Path);

            using var process = new Process
            {
                StartInfo = startInfo
            };

            try
            {
                _logger.LogInformation(
                    "Starting FFmpeg conversion. Input: {Input}, Output: {Output}",
                    inputWebmPath,
                    outputMp4Path);

                if (!process.Start())
                    throw new InvalidOperationException(
                        "Unable to start FFmpeg process.");

                // Read both streams concurrently to avoid blocking.
                var standardOutputTask =
                    process.StandardOutput.ReadToEndAsync();

                var standardErrorTask =
                    process.StandardError.ReadToEndAsync();

                using var timeoutCts =
                    new CancellationTokenSource(
                        TimeSpan.FromMinutes(timeoutMinutes));

                using var linkedCts =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken,
                        timeoutCts.Token);

                try
                {
                    await process.WaitForExitAsync(linkedCts.Token);
                }
                catch (OperationCanceledException)
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }

                    if (timeoutCts.IsCancellationRequested &&
                        !cancellationToken.IsCancellationRequested)
                    {
                        throw new TimeoutException(
                            $"FFmpeg conversion exceeded {timeoutMinutes} minutes.");
                    }

                    throw;
                }

                var standardOutput = await standardOutputTask;
                var standardError = await standardErrorTask;

                if (process.ExitCode != 0)
                {
                    _logger.LogError(
                        "FFmpeg failed. Exit code: {ExitCode}. Error: {Error}",
                        process.ExitCode,
                        standardError);

                    throw new InvalidOperationException(
                        $"FFmpeg conversion failed. Exit code: {process.ExitCode}. " +
                        standardError);
                }

                if (!File.Exists(outputMp4Path))
                    throw new InvalidOperationException(
                        "FFmpeg completed but MP4 file was not generated.");

                var outputInfo = new FileInfo(outputMp4Path);

                if (outputInfo.Length == 0)
                    throw new InvalidOperationException(
                        "Generated MP4 file is empty.");

                _logger.LogInformation(
                    "FFmpeg conversion completed. Output size: {Size} bytes",
                    outputInfo.Length);

                return outputMp4Path;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error during WebM to MP4 conversion.");

                throw;
            }
        }
    }
}
