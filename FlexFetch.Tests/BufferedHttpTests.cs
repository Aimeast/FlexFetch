using System.Net.Http;
using System.Text;
using FlexFetch.Services;

namespace FlexFetch.Tests;

/// <summary>
/// The buffered GET must leave a response that is still readable after the
/// owning client/handler are disposed; the previous code returned a live
/// response tied to the disposed connection pool.
/// </summary>
[TestClass]
public sealed class BufferedHttpTests
{
    [TestMethod]
    public async Task GetAsync_BuffersBodyAndHeadersBeforeClientDisposal()
    {
        using var server = new TestHttpServer(_ => new TestHttpServer.HttpResponse(
            200,
            Encoding.UTF8.GetBytes("payload"),
            new Dictionary<string, string> { ["Set-Cookie"] = "a=b" }));

        HttpResponseMessage buffered;
        using (var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }))
        {
            buffered = await BufferedHttp.GetAsync(client, new Uri(server.BaseUrl), CancellationToken.None);
        }

        using (buffered)
        {
            Assert.AreEqual("payload", await buffered.Content.ReadAsStringAsync());
            Assert.IsTrue(buffered.Headers.TryGetValues("Set-Cookie", out var cookies));
            Assert.AreEqual("a=b", cookies.Single());
        }
    }
}
