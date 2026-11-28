using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace jellyfin_anidoki_unit_tests.HelperTests;

/// <summary>Response content that records whether it was disposed.</summary>
internal sealed class TrackedContent(string body) : StringContent(body) {
    public bool Disposed { get; private set; }

    protected override void Dispose(bool disposing) {
        Disposed = true;
        base.Dispose(disposing);
    }
}

/// <summary>Answers every request with the one response it was given.</summary>
internal sealed class OneResponse(HttpResponseMessage response) : HttpMessageHandler {
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response);
}
