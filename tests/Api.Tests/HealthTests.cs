using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Overens.Api.Tests;

public sealed class HealthTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Theory]
    [InlineData("/")]
    [InlineData("/health")]
    [InlineData("/alive")]
    public async Task The_api_starts_and_answers(string path)
    {
        using var client = factory.WithWebHostBuilder(host => host.UseSetting("environment", "Development"))
            .CreateClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
