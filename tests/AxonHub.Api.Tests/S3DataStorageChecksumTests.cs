using System.Net;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using AxonHub.Api.Management;
using AxonHub.Api.Storage;

namespace AxonHub.Api.Tests;

/// <summary>
/// Ports internal/server/biz/objectstore_s3_checksum_test.go (#981 / #2637):
/// the SDK defaults to computing a CRC32 for PutObject and, over HTTPS,
/// wrapping the body in an aws-chunked trailer. Aliyun OSS's S3-compatible API
/// accepts that request with HTTP 200 but stores a zero-byte object, while the
/// client-side byte count still reports success. The backend must therefore
/// send a plain signed payload; checksums stay available when a caller asks
/// for one explicitly.
/// </summary>
public sealed class S3DataStorageChecksumTests
{
    [Fact]
    public async Task PutObjectSendsAPlainSignedPayloadOverHttps()
    {
        var handler = new PutObjectCaptureHandler();
        var settings = new S3StorageSettings
        {
            BucketName = "axonhub-test",
            Endpoint = "https://s3.example.com",
            Region = "us-east-1",
            AccessKey = "test-access-key",
            SecretKey = "test-secret-key",
            PathStyle = true,
        };

        using var client = S3DataStorageBackend.CreateClient(
            settings,
            new RecordingHttpClientFactory(handler));
        using var backend = new S3DataStorageBackend(client, settings.BucketName);

        // The config itself carries the same decision as the reference's
        // WithRequestChecksumCalculation(WhenRequired) /
        // WithResponseChecksumValidation(WhenRequired).
        Assert.Equal(
            RequestChecksumCalculation.WHEN_REQUIRED,
            ((AmazonS3Config)client.Config).RequestChecksumCalculation);
        Assert.Equal(
            ResponseChecksumValidation.WHEN_REQUIRED,
            ((AmazonS3Config)client.Config).ResponseChecksumValidation);

        // PutObject: the byte Save path.
        var payload = Enumerable.Repeat((byte)'a', 32 * 1024).ToArray();
        await backend.WriteAsync(
            "2/requests/5/request_body.json",
            payload,
            CancellationToken.None);

        AssertPlainSignedPayload(Assert.Single(handler.Requests), payload);

        // WriteFromStreamAsync is the backup path that logged a correct
        // client-side byte count while the server stored nothing; it must not
        // use an aws-chunked trailer either.
        var streamPayload = Enumerable.Repeat((byte)'b', 32 * 1024).ToArray();
        var (key, written) = await backend.WriteFromStreamAsync(
            "2/requests/5/stream.bin",
            new NonSeekableReadStream(streamPayload),
            CancellationToken.None);

        Assert.Equal("2/requests/5/stream.bin", key);
        Assert.Equal(streamPayload.Length, written);
        Assert.Equal(2, handler.Requests.Count);
        AssertPlainSignedPayload(handler.Requests[1], streamPayload);
    }

    private static void AssertPlainSignedPayload(
        RecordedPutRequest request,
        byte[] payload)
    {
        Assert.Equal("PUT", request.Method);
        Assert.DoesNotContain(
            "STREAMING-",
            request.Header("x-amz-content-sha256"),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "aws-chunked",
            request.Header("Content-Encoding"),
            StringComparison.Ordinal);
        Assert.Equal(string.Empty, request.Header("x-amz-trailer"));
        Assert.Equal(payload, request.Body);
    }

    private sealed class NonSeekableReadStream : Stream
    {
        private readonly MemoryStream _inner;

        public NonSeekableReadStream(byte[] data) => _inner = new MemoryStream(data, writable: false);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
            => _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class RecordingHttpClientFactory : HttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public RecordingHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

        public override HttpClient CreateHttpClient(IClientConfig clientConfig)
            => new(_handler, disposeHandler: false);

        public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => false;
    }

    private sealed class PutObjectCaptureHandler : HttpMessageHandler
    {
        private readonly List<RecordedPutRequest> _requests = [];

        public IReadOnlyList<RecordedPutRequest> Requests => _requests;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, values) in request.Headers.NonValidated)
            {
                headers[name] = string.Join(", ", values);
            }

            var body = Array.Empty<byte>();
            if (request.Content is not null)
            {
                foreach (var (name, values) in request.Content.Headers.NonValidated)
                {
                    headers[name] = string.Join(", ", values);
                }

                body = await request.Content.ReadAsByteArrayAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            _requests.Add(new RecordedPutRequest(
                request.Method.Method,
                headers,
                body));

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """<?xml version="1.0" encoding="UTF-8"?><PutObjectOutput><ETag>"d41d8cd98f00b204e9800998ecf8427e"</ETag></PutObjectOutput>""",
                    Encoding.UTF8,
                    "application/xml"),
            };
        }
    }

    private sealed record RecordedPutRequest(
        string Method,
        IReadOnlyDictionary<string, string> Headers,
        byte[] Body)
    {
        public string Header(string name)
            => Headers.TryGetValue(name, out var value) ? value : string.Empty;
    }
}
