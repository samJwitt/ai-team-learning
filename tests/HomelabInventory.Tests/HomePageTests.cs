using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace HomelabInventory.Tests;

public class HomePageTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task HomePage_ReturnsSuccess_AndContainsAppName()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Homelab Inventory", html);
    }
}
