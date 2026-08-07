namespace TelegramBotPlatform.Infrastructure.Plugins;

/// <summary>
/// Stores behavior-extension packages as objects under a prefix in a private S3 bucket, so an uploaded
/// extension survives the compute instance being replaced. Selected when
/// <see cref="PlatformOptions.PluginsBucket"/> is configured.
/// </summary>
public sealed class S3ExtensionStore(IAmazonS3 s3, IOptions<PlatformOptions> platformOptions) : IExtensionStore
{
    private string Bucket => platformOptions.Value.PluginsBucket!;

    private string Prefix => platformOptions.Value.PluginsPrefix;

    public async Task<Result<IReadOnlyList<string>>> List(CancellationToken cancellationToken = default)
    {
        try
        {
            var names = new List<string>();
            string? continuationToken = null;

            do
            {
                // The prefix is REQUIRED, not an optimisation: the task role's s3:ListBucket grant carries an
                // s3:prefix condition, so an unscoped listing is denied outright.
                var response = await s3.ListObjectsV2Async(
                    new ListObjectsV2Request
                    {
                        BucketName = Bucket,
                        Prefix = Prefix,
                        ContinuationToken = continuationToken,
                    },
                    cancellationToken);

                // S3Objects is null, not empty, when the listing matched nothing: the v4 SDK leaves
                // collection properties uninitialised. Enumerating it directly turns an empty store into
                // an ArgumentNullException, which the catch below reports as an unreachable store — and
                // startup treats *that* as fatal. An empty store is not a fault; it is the state every
                // deployment begins in.
                names.AddRange((response.S3Objects ?? [])
                    .Select(s3Object => s3Object.Key[Prefix.Length..])
                    .Where(name => name.Length > 0 && name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)));

                continuationToken = response.IsTruncated is true ? response.NextContinuationToken : null;
            }
            while (continuationToken is not null);

            names.Sort(StringComparer.Ordinal);

            return Result.Ok<IReadOnlyList<string>>(names);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Result.Fail<IReadOnlyList<string>>(Unreachable(exception));
        }
    }

    public async Task<Result<byte[]>> Read(string packageName, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await s3.GetObjectAsync(Bucket, KeyFor(packageName), cancellationToken);

            using var buffer = new MemoryStream();
            await response.ResponseStream.CopyToAsync(buffer, cancellationToken);

            return Result.Ok(buffer.ToArray());
        }
        catch (AmazonS3Exception exception) when (IsMissing(exception))
        {
            return NotFound(packageName);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Result.Fail<byte[]>(Unreachable(exception));
        }
    }

    public async Task<Result> Write(string packageName, Stream content, bool overwrite, CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new PutObjectRequest
            {
                BucketName = Bucket,
                Key = KeyFor(packageName),
                InputStream = content,
                AutoCloseStream = false,
            };

            if (!overwrite)
            {
                // Conditional create. The caller has already checked the name is free; this closes the race
                // between that check and this write, and S3 answers a loser with 412.
                request.IfNoneMatch = "*";
            }

            await s3.PutObjectAsync(request, cancellationToken);

            return Result.Ok();
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            return new ExtensionConflictError($"A behavior extension named \"{packageName}\" already exists.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Result.Fail(Unreachable(exception));
        }
    }

    public async Task<Result> Delete(string packageName, CancellationToken cancellationToken = default)
    {
        try
        {
            // S3 DeleteObject is already idempotent — deleting an absent key succeeds.
            await s3.DeleteObjectAsync(Bucket, KeyFor(packageName), cancellationToken);

            return Result.Ok();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Result.Fail(Unreachable(exception));
        }
    }

    private string KeyFor(string packageName) => $"{Prefix}{packageName}";

    /// <summary>
    /// S3 only answers <c>404</c> for an absent key when the caller holds <c>s3:ListBucket</c> on the
    /// bucket; otherwise it answers <c>403</c> so as not to leak whether the key exists. The task role's
    /// <c>ListBucket</c> grant is conditioned on <c>s3:prefix</c>, and that condition key is absent from a
    /// <c>GetObject</c> authorization context — so in the deployed configuration a missing package usually
    /// surfaces as <c>403</c> and is reported as an unavailable store rather than a not-found. That is the
    /// safer way round (a genuine permission fault is never mistaken for an empty slot), and it costs
    /// nothing in practice because every caller lists before it reads.
    /// </summary>
    private static bool IsMissing(AmazonS3Exception exception) =>
        exception.StatusCode == HttpStatusCode.NotFound
        || string.Equals(exception.ErrorCode, "NoSuchKey", StringComparison.Ordinal);

    private static PackageNotFoundError NotFound(string packageName) =>
        new($"Behavior extension \"{packageName}\" was not found.");

    // Names the bucket and the reason so an operator can tell a misconfiguration (wrong bucket, missing
    // permission) from an outage. Never includes credentials — the SDK does not put them in messages.
    private StoreUnavailableError Unreachable(Exception exception) =>
        new($"The behavior extension store (bucket \"{Bucket}\") could not be reached: {exception.Message}");
}