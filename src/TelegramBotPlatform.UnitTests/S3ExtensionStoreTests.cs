using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using TelegramBotPlatform.Infrastructure;
using TelegramBotPlatform.Infrastructure.Plugins;
using TelegramBotPlatform.Public.Behaviors;

namespace TelegramBotPlatform.UnitTests;

/// <summary>
/// Covers the listing path of <see cref="S3ExtensionStore"/> against the response shapes the SDK really
/// produces. The rest of the store is exercised through <c>InMemoryExtensionStore</c>; what cannot be
/// faked at that seam is how the AWS client answers, which is exactly where the empty-store bug lived.
/// </summary>
public sealed class S3ExtensionStoreTests
{
    [Fact]
    public async Task List_ReportsAnEmptyStore_WhenTheListingMatchedNothing()
    {
        // Regression: the v4 SDK leaves collection properties null rather than empty, so an empty bucket
        // came back with S3Objects == null. Enumerating that threw, List reported StoreUnavailableError,
        // and startup treats an unreachable store as fatal — but an empty bucket is where every fresh
        // deployment starts.
        using var s3 = new QueuedS3Client(new ListObjectsV2Response { S3Objects = null! });
        var store = CreateStore(s3);

        var result = await store.List(TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value);
    }

    [Fact]
    public async Task List_StripsThePrefix_AndKeepsOnlyPackages()
    {
        using var s3 = new QueuedS3Client(new ListObjectsV2Response
        {
            S3Objects =
            [
                new S3Object { Key = "behaviors/" }, // the prefix's own placeholder object
                new S3Object { Key = "behaviors/Reverse.dll" },
                new S3Object { Key = "behaviors/notes.txt" },
            ],
        });
        var store = CreateStore(s3);

        var result = await store.List(TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(["Reverse.dll"], result.Value);
    }

    [Fact]
    public async Task List_FollowsContinuationTokens_AndAlwaysScopesToThePrefix()
    {
        // A truncated listing whose next page matches nothing is the same null hazard, one page in.
        using var s3 = new QueuedS3Client(
            new ListObjectsV2Response
            {
                S3Objects = [new S3Object { Key = "behaviors/Reverse.dll" }],
                IsTruncated = true,
                NextContinuationToken = "next-page",
            },
            new ListObjectsV2Response { S3Objects = null! });
        var store = CreateStore(s3);

        var result = await store.List(TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(["Reverse.dll"], result.Value);
        Assert.Equal(2, s3.Requests.Count);
        Assert.Null(s3.Requests[0].ContinuationToken);
        Assert.Equal("next-page", s3.Requests[1].ContinuationToken);
        // The task role's s3:ListBucket grant carries an s3:prefix condition, so a page that forgot the
        // prefix would be denied outright in the deployed configuration.
        Assert.All(s3.Requests, request => Assert.Equal("behaviors/", request.Prefix));
    }

    [Fact]
    public async Task List_ReportsAnUnreachableStore_WhenTheClientThrows()
    {
        using var s3 = new ThrowingS3Client(new AmazonS3Exception("network is down"));
        var store = CreateStore(s3);

        var result = await store.List(TestContext.Current.CancellationToken);

        Assert.True(result.IsFailed);
        Assert.IsType<StoreUnavailableError>(result.Errors.First());
    }

    private static S3ExtensionStore CreateStore(IAmazonS3 s3) =>
        new(s3, Options.Create(new PlatformOptions
        {
            AdminApiKey = "test-admin-key",
            PluginsBucket = "test-bucket",
            PluginsPrefix = "behaviors/",
        }));

    /// <summary>
    /// Subclasses the real client and overrides only the call the store makes, so the response objects
    /// under test are the SDK's own types with the SDK's own defaults. The credentials are dummies and
    /// nothing reaches the network — every overridden call is answered from the queue.
    /// </summary>
    private sealed class QueuedS3Client(params ListObjectsV2Response[] responses)
        : AmazonS3Client(new BasicAWSCredentials("test-access-key", "test-secret"), RegionEndpoint.EUNorth1)
    {
        private readonly Queue<ListObjectsV2Response> _responses = new(responses);

        public List<ListObjectsV2Request> Requests { get; } = [];

        public override Task<ListObjectsV2Response> ListObjectsV2Async(
            ListObjectsV2Request request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);

            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed class ThrowingS3Client(Exception exception)
        : AmazonS3Client(new BasicAWSCredentials("test-access-key", "test-secret"), RegionEndpoint.EUNorth1)
    {
        public override Task<ListObjectsV2Response> ListObjectsV2Async(
            ListObjectsV2Request request,
            CancellationToken cancellationToken = default) => Task.FromException<ListObjectsV2Response>(exception);
    }
}