using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using CircleAI.Core;
using CircleAI.Inference;

namespace Concierge.Tests;

/// <summary>Verifies the patched package's resumable bundle path consumed by Concierge.</summary>
public sealed class CircleAiBundleDownloadRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"concierge-circleai-resume-{Guid.NewGuid():N}");

    [Fact]
    public async Task Cancelled_bundle_download_resumes_saved_bytes_after_service_recreation()
    {
        Directory.CreateDirectory(_root);
        var bytes = Enumerable.Range(0, 3 * 1024 * 1024)
            .Select(index => (byte)(index * 31 % 251))
            .ToArray();
        using var stop = new CancellationTokenSource();
        var handler = new ResumableHandler(bytes, bytes.Length / 3, stop.Cancel);
        using var http = new HttpClient(handler);
        var checksum = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var files = new[] { new BundleFileSpec("weights.bin", checksum, bytes.Length) };
        const string modelId = "concierge-bundle-resume-test";

        using (var first = new ModelDownloadService(_root, http))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.EnsureBundleAsync(
                modelId, "any/repo", ModelSource.HuggingFace, files,
                (IProgress<DownloadProgress>?)null, stop.Token));
        }

        var partialPath = Path.Combine(_root, modelId, "weights.bin.tmp");
        Assert.True(handler.Interrupted);
        Assert.True(File.Exists(partialPath),
            "Cancellation discarded the resumable file. Remaining files: "
            + string.Join(", ", Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            + "; requested ranges: " + string.Join(", ", handler.RequestedRanges.Select(offset => offset?.ToString() ?? "full")));
        Assert.Equal(bytes.Length / 3, new FileInfo(partialPath).Length);

        string modelDirectory;
        using (var retry = new ModelDownloadService(_root, http))
        {
            modelDirectory = await retry.EnsureBundleAsync(
                modelId, "any/repo", ModelSource.HuggingFace, files,
                (IProgress<DownloadProgress>?)null, CancellationToken.None);
        }

        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(modelDirectory, "weights.bin")));
        Assert.Contains(handler.RequestedRanges, range => range is { } offset && offset >= bytes.Length / 3);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class ResumableHandler(byte[] bytes, int stopAfter, Action cancel) : HttpMessageHandler
    {
        private int _started;
        private int _interrupted;
        public bool Interrupted => Volatile.Read(ref _interrupted) != 0;
        public List<long?> RequestedRanges { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var range = request.Headers.Range?.Ranges.FirstOrDefault();
            var offset = range?.From ?? 0;
            lock (RequestedRanges)
                RequestedRanges.Add(range?.From);

            if (range is not null)
            {
                var end = range.To ?? bytes.Length - 1;
                var count = checked((int)(end - offset + 1));
                var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(bytes, checked((int)offset), count),
                };
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, end, bytes.Length);
                return Task.FromResult(response);
            }

            var interrupt = Interlocked.CompareExchange(ref _started, 1, 0) == 0;
            HttpContent content = interrupt
                ? new InterruptingContent(bytes, stopAfter, () =>
                {
                    Interlocked.Exchange(ref _interrupted, 1);
                    cancel();
                })
                : new ByteArrayContent(bytes);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class InterruptingContent(byte[] bytes, int stopAfter, Action cancel) : HttpContent
    {
        protected override Task<Stream> CreateContentReadStreamAsync()
            => Task.FromResult<Stream>(new InterruptingStream(bytes, stopAfter, cancel));
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => throw new NotSupportedException();
        protected override bool TryComputeLength(out long length) { length = bytes.Length; return true; }
    }

    private sealed class InterruptingStream(byte[] bytes, int stopAfter, Action cancel) : Stream
    {
        private int _position;
        private bool _interrupted;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (!_interrupted && _position >= stopAfter)
            {
                _interrupted = true;
                cancel();
                throw new OperationCanceledException("Simulated cancellation during bundle download.");
            }

            var available = _interrupted ? bytes.Length - _position : stopAfter - _position;
            if (available <= 0) return 0;
            var copied = Math.Min(count, available);
            Array.Copy(bytes, _position, buffer, offset, copied);
            _position += copied;
            return copied;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scratch = new byte[buffer.Length];
            var read = Read(scratch, 0, scratch.Length);
            scratch.AsMemory(0, read).CopyTo(buffer);
            return ValueTask.FromResult(read);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Read(buffer, offset, count));
        }
    }
}
