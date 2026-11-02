using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using Amazon.Runtime;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;

namespace EmployeeApi.Services
{
    public sealed class S3SessionRecordingService
    {
        private readonly IAmazonS3 _s3Client;
        private readonly IConfiguration _configuration;
        private readonly ILogger<S3SessionRecordingService> _logger;

        private readonly string _bucketName;

        public S3SessionRecordingService(
            IAmazonS3 s3Client,
            IConfiguration configuration,
            ILogger<S3SessionRecordingService> logger)
        {
            _s3Client = s3Client;
            _configuration = configuration;
            _logger = logger;

            _bucketName =
                configuration["AWS:BucketName"]
                ?? throw new InvalidOperationException(
                    "AWS BucketName is missing.");
        }

        public async Task SaveChunkAsync(
            Guid sessionId,
            int chunkNumber,
            JsonElement events,
            CancellationToken cancellationToken)
        {
            if (sessionId == Guid.Empty)
                throw new ArgumentException("Invalid session ID.");

            if (chunkNumber < 0)
                throw new ArgumentOutOfRangeException(
                    nameof(chunkNumber));

            if (events.ValueKind != JsonValueKind.Array)
                throw new ArgumentException(
                    "Events must be a JSON array.");

            // S3 folder structure is represented by object prefixes.
            // No physical folder creation is required.

            var s3Key =
                $"sessions/{sessionId:D}/chunks/{chunkNumber:D8}.json";

            var payload = JsonSerializer.Serialize(new
            {
                sessionId,
                chunkNumber,
                uploadedUtc = DateTimeOffset.UtcNow,
                events
            });

            var bytes = Encoding.UTF8.GetBytes(payload);

            var hash = Convert.ToHexString(
                SHA256.HashData(bytes));

            await using var stream = new MemoryStream(bytes);

            var request = new PutObjectRequest
            {
                BucketName = _bucketName,
                Key = s3Key,
                InputStream = stream,
                ContentType = "application/json",

                // Prevent duplicate chunk overwrite
                IfNoneMatch = "*",

                Metadata =
            {
                ["sha256"] = hash
            }
            };

            try
            {
                await _s3Client.PutObjectAsync(
                    request,
                    cancellationToken);

                _logger.LogInformation(
                    "Session chunk uploaded to S3. SessionId: {SessionId}, Chunk: {ChunkNumber}, Key: {S3Key}",
                    sessionId,
                    chunkNumber,
                    s3Key);
            }
            catch (AmazonS3Exception ex)
                when (ex.StatusCode ==
                      System.Net.HttpStatusCode.PreconditionFailed)
            {
                throw new InvalidOperationException(
                    "This session chunk has already been uploaded.",
                    ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to upload session chunk to S3. SessionId: {SessionId}, Chunk: {ChunkNumber}",
                    sessionId,
                    chunkNumber);

                throw;
            }
        }


        public async Task<int> CombineChunksAsync(
            Guid sessionId,
            string outputFile,
            CancellationToken cancellationToken)
        {
            if (sessionId == Guid.Empty)
            {
                throw new ArgumentException(
                    "Invalid session ID.");
            }

            var prefix =
                $"sessions/{sessionId:D}/chunks/";

            _logger.LogInformation(
                "Retrieving session chunks from S3. Session: {SessionId}",
                sessionId);

            // STEP 1: List all objects from S3.
            var s3Objects = new List<S3Object>();

            string? continuationToken = null;

            do
            {
                var request = new ListObjectsV2Request
                {
                    BucketName = _bucketName,
                    Prefix = prefix,
                    ContinuationToken = continuationToken
                };

                var response = await _s3Client.ListObjectsV2Async(
                    request,
                    cancellationToken);

                s3Objects.AddRange(
                    response.S3Objects.Where(
                        item => item.Key.EndsWith(
                            ".json",
                            StringComparison.OrdinalIgnoreCase)));

                continuationToken = response.IsTruncated == true
                                    ? response.NextContinuationToken
                                    : null;

            } while (continuationToken != null);

            // STEP 2: Extract and sort chunk numbers.
            var chunks = s3Objects
                .Select(item => new
                {
                    item.Key,
                    Number = ParseChunkNumber(item.Key)
                })
                .OrderBy(item => item.Number)
                .ToList();

            if (chunks.Count == 0)
            {
                throw new InvalidOperationException(
                    $"No JSON chunks found for session {sessionId}.");
            }

            // STEP 3: Check for missing or duplicate chunks.
            for (int i = 0; i < chunks.Count; i++)
            {
                if (chunks[i].Number != i)
                {
                    throw new InvalidOperationException(
                        $"Missing or invalid chunk sequence. " +
                        $"Expected chunk {i}, found {chunks[i].Number}.");
                }
            }

            // STEP 4: Create the output directory.
            var directory = Path.GetDirectoryName(outputFile);

            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new InvalidOperationException(
                    "Invalid output file path.");
            }

            Directory.CreateDirectory(directory);

            var totalEvents = 0;

            // STEP 5: Combine events into one JSON array.
            await using var outputStream = new FileStream(
                outputFile,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                useAsync: true);

            using var writer = new Utf8JsonWriter(outputStream);

            writer.WriteStartArray();

            foreach (var chunk in chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();

                _logger.LogInformation(
                    "Downloading S3 chunk {ChunkNumber}: {Key}",
                    chunk.Number,
                    chunk.Key);

                using var s3Response = await _s3Client.GetObjectAsync(
                    _bucketName,
                    chunk.Key,
                    cancellationToken);

                using var document = await JsonDocument.ParseAsync(
                    s3Response.ResponseStream,
                    cancellationToken: cancellationToken);

                var root = document.RootElement;

                // Verify session ID.
                if (!root.TryGetProperty(
                        "sessionId", out var storedSessionId) ||
                    !Guid.TryParse(
                        storedSessionId.GetString(), out var parsedSessionId) ||
                    parsedSessionId != sessionId)
                {
                    throw new InvalidOperationException(
                        $"Session ID mismatch in chunk {chunk.Key}.");
                }

                // Verify chunk number.
                if (!root.TryGetProperty(
                        "chunkNumber", out var storedChunkNumber) ||
                    !storedChunkNumber.TryGetInt32(out var actualNumber) ||
                    actualNumber != chunk.Number)
                {
                    throw new InvalidOperationException(
                        $"Chunk number mismatch in {chunk.Key}.");
                }

                // Extract events.
                if (!root.TryGetProperty(
                        "events", out var events) ||
                    events.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidOperationException(
                        $"Events array missing in {chunk.Key}.");
                }

                foreach (var item in events.EnumerateArray())
                {
                    item.WriteTo(writer);
                    totalEvents++;
                }

                _logger.LogInformation(
                    "Chunk {ChunkNumber} processed. Event count: {Count}",
                    chunk.Number,
                    events.GetArrayLength());
            }

            writer.WriteEndArray();

            await writer.FlushAsync(cancellationToken);

            _logger.LogInformation(
                "Session chunks combined. Session: {SessionId}, " +
                "Chunks: {ChunkCount}, Events: {EventCount}",
                sessionId,
                chunks.Count,
                totalEvents);

            return totalEvents;
        }



        public async Task<int> CombineChunksAsync(
                        Guid sessionId,
                        string outputFile,
                        int expectedLastChunkNumber,
                        CancellationToken cancellationToken)
        {
            // STEP 1: Validate input parameters.

            if (sessionId == Guid.Empty)
            {
                throw new ArgumentException(
                    "Invalid session ID.",
                    nameof(sessionId));
            }

            if (string.IsNullOrWhiteSpace(outputFile))
            {
                throw new ArgumentException(
                    "Output file path is required.",
                    nameof(outputFile));
            }

            if (expectedLastChunkNumber < 0 ||
                expectedLastChunkNumber == int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(expectedLastChunkNumber),
                    "Invalid last chunk number.");
            }

            var prefix = $"sessions/{sessionId:D}/chunks/";

            _logger.LogInformation(
                "Retrieving session chunks from S3. Session: {SessionId}, Expected last chunk: {LastChunk}",
                sessionId,
                expectedLastChunkNumber);

            // STEP 2: List all S3 objects with pagination.

            var s3Objects = new List<S3Object>();

            string? continuationToken = null;

            do
            {
                cancellationToken.ThrowIfCancellationRequested();

                var request = new ListObjectsV2Request
                {
                    BucketName = _bucketName,
                    Prefix = prefix,
                    ContinuationToken = continuationToken
                };

                var response = await _s3Client.ListObjectsV2Async(
                    request,
                    cancellationToken);

                s3Objects.AddRange(
                    response.S3Objects.Where(item =>
                        item.Key.EndsWith(
                            ".json",
                            StringComparison.OrdinalIgnoreCase)));

                continuationToken = response.IsTruncated == true
                    ? response.NextContinuationToken
                    : null;

            } while (continuationToken != null);

            // STEP 3: Extract and sort chunk numbers.

            var chunks = s3Objects
                .Select(item => new
                {
                    item.Key,
                    Number = ParseChunkNumber(item.Key)
                })
                .OrderBy(item => item.Number)
                .ToList();

            if (chunks.Count == 0)
            {
                throw new InvalidOperationException(
                    $"No JSON chunks found for session {sessionId}.");
            }

            // STEP 4: Validate expected chunk count.

            var expectedChunkCount = expectedLastChunkNumber + 1;

            if (chunks.Count != expectedChunkCount)
            {
                throw new InvalidOperationException(
                    $"Incomplete session recording. " +
                    $"Expected {expectedChunkCount} chunks " +
                    $"(0 to {expectedLastChunkNumber}), " +
                    $"but found {chunks.Count} chunks.");
            }

            // STEP 5: Validate continuous chunk numbering.

            for (var i = 0; i < expectedChunkCount; i++)
            {
                if (chunks[i].Number != i)
                {
                    throw new InvalidOperationException(
                        $"Missing or invalid chunk sequence. " +
                        $"Expected chunk {i}, found {chunks[i].Number}.");
                }
            }

            _logger.LogInformation(
                "S3 chunk validation successful. Session: {SessionId}, ChunkCount: {ChunkCount}",
                sessionId,
                chunks.Count);

            // STEP 6: Validate output file path.

            var fullOutputPath = Path.GetFullPath(outputFile);

            var directory = Path.GetDirectoryName(fullOutputPath);

            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new InvalidOperationException(
                    "Invalid output file directory.");
            }

            Directory.CreateDirectory(directory);

            var totalEvents = 0;

            try
            {
                // STEP 7: Combine all events into one JSON array.

                await using var outputStream = new FileStream(
                    fullOutputPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 64 * 1024,
                    useAsync: true);

                using var writer = new Utf8JsonWriter(outputStream);

                writer.WriteStartArray();

                foreach (var chunk in chunks)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    _logger.LogInformation(
                        "Downloading S3 chunk {ChunkNumber}: {Key}",
                        chunk.Number,
                        chunk.Key);

                    using var s3Response = await _s3Client.GetObjectAsync(
                        _bucketName,
                        chunk.Key,
                        cancellationToken);

                    using var document = await JsonDocument.ParseAsync(
                        s3Response.ResponseStream,
                        cancellationToken: cancellationToken);

                    var root = document.RootElement;

                    if (root.ValueKind != JsonValueKind.Object)
                    {
                        throw new InvalidOperationException(
                            $"Invalid JSON chunk structure: {chunk.Key}.");
                    }

                    // STEP 8: Validate stored session ID.

                    if (!root.TryGetProperty(
                            "sessionId",
                            out var storedSessionId) ||
                        storedSessionId.ValueKind != JsonValueKind.String ||
                        !Guid.TryParse(
                            storedSessionId.GetString(),
                            out var parsedSessionId) ||
                        parsedSessionId != sessionId)
                    {
                        throw new InvalidOperationException(
                            $"Session ID mismatch in chunk {chunk.Key}.");
                    }

                    // STEP 9: Validate stored chunk number.

                    if (!root.TryGetProperty(
                            "chunkNumber",
                            out var storedChunkNumber) ||
                        !storedChunkNumber.TryGetInt32(
                            out var actualChunkNumber) ||
                        actualChunkNumber != chunk.Number)
                    {
                        throw new InvalidOperationException(
                            $"Chunk number mismatch in {chunk.Key}.");
                    }

                    // STEP 10: Extract events array.

                    if (!root.TryGetProperty(
                            "events",
                            out var events) ||
                        events.ValueKind != JsonValueKind.Array)
                    {
                        throw new InvalidOperationException(
                            $"Events array missing in {chunk.Key}.");
                    }

                    foreach (var item in events.EnumerateArray())
                    {
                        item.WriteTo(writer);
                        totalEvents++;
                    }

                    _logger.LogInformation(
                        "Chunk processed. Session: {SessionId}, " +
                        "Chunk: {ChunkNumber}, Events: {EventCount}",
                        sessionId,
                        chunk.Number,
                        events.GetArrayLength());
                }

                // STEP 11: Validate that events exist.

                if (totalEvents == 0)
                {
                    throw new InvalidOperationException(
                        "No rrweb events found in the session chunks.");
                }

                writer.WriteEndArray();

                await writer.FlushAsync(cancellationToken);

                _logger.LogInformation(
                    "Session chunks combined successfully. " +
                    "Session: {SessionId}, Chunks: {ChunkCount}, Events: {EventCount}, Output: {OutputFile}",
                    sessionId,
                    chunks.Count,
                    totalEvents,
                    fullOutputPath);

                return totalEvents;
            }
            catch
            {
                // Remove incomplete output if combination fails.
                if (File.Exists(fullOutputPath))
                {
                    try
                    {
                        File.Delete(fullOutputPath);
                    }
                    catch (Exception cleanupException)
                    {
                        _logger.LogWarning(
                            cleanupException,
                            "Unable to remove incomplete events file: {OutputFile}",
                            fullOutputPath);
                    }
                }

                throw;
            }
        }

        private static int ParseChunkNumber(string key)
        {
            var fileName = Path.GetFileNameWithoutExtension(key);

            if (!int.TryParse(
                    fileName,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var chunkNumber))
            {
                throw new InvalidOperationException(
                    $"Invalid chunk filename: {key}");
            }

            if (chunkNumber < 0)
            {
                throw new InvalidOperationException(
                    $"Invalid chunk number: {key}");
            }

            return chunkNumber;
        }



        public async Task<string> UploadMp4Async(
            Guid sessionId,
            string mp4FilePath,
            CancellationToken cancellationToken)
        {
            if (sessionId == Guid.Empty)
                throw new ArgumentException("Invalid session ID.");

            if (string.IsNullOrWhiteSpace(mp4FilePath))
                throw new ArgumentException("MP4 file path is required.");

            if (!File.Exists(mp4FilePath))
                throw new FileNotFoundException(
                    "Generated MP4 file was not found.",
                    mp4FilePath);

            var fileInfo = new FileInfo(mp4FilePath);

            if (fileInfo.Length == 0)
                throw new InvalidOperationException(
                    "Cannot upload an empty MP4 file.");

            if (!string.Equals(
                fileInfo.Extension,
                ".mp4",
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Only MP4 files can be uploaded.");
            }

            // Final S3 object key
            var s3Key = $"sessions/{sessionId:D}/session.mp4";

            try
            {
                await using var fileStream = new FileStream(
                    mp4FilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 1024 * 1024,
                    useAsync: true);

                var request = new PutObjectRequest
                {
                    BucketName = _bucketName,
                    Key = s3Key,
                    InputStream = fileStream,
                    ContentType = "video/mp4",
                    AutoCloseStream = false,
                    AutoResetStreamPosition = false
                };

                request.Metadata["session-id"] = sessionId.ToString("D");
                request.Metadata["file-size"] = fileInfo.Length.ToString();

                await _s3Client.PutObjectAsync(
                    request,
                    cancellationToken);

                _logger.LogInformation(
                    "Session MP4 uploaded successfully. SessionId: {SessionId}, S3Key: {S3Key}, Size: {Size}",
                    sessionId,
                    s3Key,
                    fileInfo.Length);

                return s3Key;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to upload session MP4. SessionId: {SessionId}",
                    sessionId);

                throw;
            }
        }

    }
}
