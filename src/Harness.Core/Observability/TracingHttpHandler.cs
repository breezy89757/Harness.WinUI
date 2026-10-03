// Harness.WinUI — Licensed under the MIT License.

using System.Diagnostics;
using System.Text;

namespace Harness.Core.Observability;

/// <summary>
/// Records each HTTP exchange with the model provider as an <c>http</c> span under the current model
/// call: the exact request body that was sent and the response as it arrived (for streaming replies, the
/// raw server-sent events). The response is copied while the caller reads it — nothing is read ahead or
/// delayed — and the span ends when the caller finishes reading. The API key header is never recorded.
/// </summary>
public sealed class TracingHttpHandler() : DelegatingHandler(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
{
    /// <summary>Bodies above this are cut (a request carries the whole conversation; a few MB is plenty).</summary>
    private const int MaxBodyChars = 4 * 1024 * 1024;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var activity = Telemetry.Source.StartActivity(Telemetry.OpHttp, ActivityKind.Client);
        if (activity is null)
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        activity.DisplayName = $"{request.Method} {request.RequestUri?.AbsolutePath}";
        activity.SetTag(Telemetry.TagHttpMethod, request.Method.Method);
        activity.SetTag(Telemetry.TagUrl, request.RequestUri?.GetLeftPart(UriPartial.Path));
        if (Telemetry.CaptureContent && request.Content is not null)
        {
            // Buffers the body once; sending then reuses the buffer.
            var body = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            activity.SetTag(Telemetry.TagHttpRequestBody, Telemetry.Redact(Cut(body)));
        }

        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Fail(activity, ex);
            activity.Stop();
            throw;
        }

        activity.SetTag(Telemetry.TagHttpStatus, (int)response.StatusCode);
        foreach (var header in s_responseHeaders)
        {
            if (response.Headers.TryGetValues(header, out var values))
                activity.SetTag($"http.response.header.{header}", string.Join(", ", values));
        }
        if (!response.IsSuccessStatusCode)
            activity.SetStatus(ActivityStatusCode.Error, $"HTTP {(int)response.StatusCode}");

        var original = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var capture = new CapturingStream(original, Telemetry.CaptureContent, (text, error) =>
        {
            if (text is not null)
                activity.SetTag(Telemetry.TagHttpResponseBody, Telemetry.Redact(Cut(text)));
            if (error is not null)
                Fail(activity, error);
            activity.Stop();
        });
        var replacement = new StreamContent(capture);
        foreach (var header in response.Content.Headers)
            replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
        response.Content = replacement;
        return response;
    }

    // Useful for support and rate-limit questions; none of them carry secrets.
    private static readonly string[] s_responseHeaders =
    [
        "x-request-id", "apim-request-id", "x-ms-region", "openai-processing-ms",
        "x-ratelimit-remaining-requests", "x-ratelimit-remaining-tokens", "retry-after",
    ];

    private static string Cut(string text) =>
        text.Length <= MaxBodyChars ? text : text[..MaxBodyChars] + $"\n…(cut at {MaxBodyChars:N0} characters)";

    private static void Fail(Activity activity, Exception ex)
    {
        var cancelled = ex is OperationCanceledException;
        activity.SetStatus(ActivityStatusCode.Error, cancelled ? Telemetry.StatusCancelled : ex.Message);
        activity.SetTag(Telemetry.TagErrorType, ex.GetType().Name);
    }

    /// <summary>
    /// Passes reads through unchanged while keeping a copy; reports once, at the end of the stream, on
    /// a read error, or when disposed early (e.g. the user stopped the reply).
    /// </summary>
    private sealed class CapturingStream(Stream inner, bool keepCopy, Action<string?, Exception?> completed) : Stream
    {
        private readonly MemoryStream? _copy = keepCopy ? new MemoryStream() : null;
        private int _done;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            try
            {
                var n = inner.Read(buffer);
                Keep(buffer[..n]);
                return n;
            }
            catch (Exception ex)
            {
                Complete(ex);
                throw;
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try
            {
                var n = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                Keep(buffer.Span[..n]);
                return n;
            }
            catch (Exception ex)
            {
                Complete(ex);
                throw;
            }
        }

        private void Keep(ReadOnlySpan<byte> data)
        {
            if (data.IsEmpty)
                Complete(null);
            else if (_copy is { Length: < MaxBodyChars * 2L })
                _copy.Write(data);
        }

        private void Complete(Exception? error)
        {
            if (Interlocked.Exchange(ref _done, 1) != 0)
                return;
            var text = _copy is null ? null : Encoding.UTF8.GetString(_copy.GetBuffer(), 0, (int)_copy.Length);
            completed(text, error);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Disposed before the end of the stream: the caller stopped reading (e.g. the user stopped the reply).
                Complete(null);
                inner.Dispose();
            }
            base.Dispose(disposing);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
