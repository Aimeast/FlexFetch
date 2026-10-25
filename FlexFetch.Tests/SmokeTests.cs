using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FlexFetch.Tests;

[TestClass]
public sealed class SmokeTests
{
    [TestMethod]
    public async Task Root_ServesWebUiPage()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.AreEqual("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        var body = await response.Content.ReadAsStringAsync();
        StringAssert.Contains(body, "<!DOCTYPE html>");
        StringAssert.Contains(body, "<title>FlexFetch</title>");
    }
}
