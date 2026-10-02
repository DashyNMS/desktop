using System.Text.Json;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Json;
using Xunit;

namespace DesktopNMS.Core.Tests;

/// <summary>
/// LibreNMS's edit_location applies every key it's sent, so these check the
/// exact keys in each body - a null "location" key wiped the name (#169).
/// </summary>
public class LocationsApiTests
{
    [Fact]
    public async Task An_update_sends_the_name_and_coordinates_and_nothing_else()
    {
        var transport = new RecordingTransport();
        var api = new LocationsApi(transport);

        await api.UpdateAsync(7, "London", 51.5, -0.12);

        Assert.Equal(HttpMethod.Patch, transport.Method);
        Assert.Equal("locations/7", transport.Url);

        var body = transport.BodyJson!.RootElement;
        Assert.Equal(new[] { "location", "lat", "lng" }, body.EnumerateObject().Select(p => p.Name));
        Assert.Equal("London", body.GetProperty("location").GetString());
        Assert.Equal(51.5, body.GetProperty("lat").GetDouble());
    }

    [Fact]
    public async Task An_update_never_sends_a_blank_name()
    {
        var api = new LocationsApi(new RecordingTransport());

        await Assert.ThrowsAnyAsync<ArgumentException>(() => api.UpdateAsync(7, " ", 51.5, -0.12));
    }

    [Fact]
    public async Task A_create_still_sends_fixed_coordinates()
    {
        var transport = new RecordingTransport();
        var api = new LocationsApi(transport);

        await api.CreateAsync("London", 51.5, -0.12, fixedCoordinates: true);

        var body = transport.BodyJson!.RootElement;
        Assert.Equal(new[] { "location", "lat", "lng", "fixed_coordinates" }, body.EnumerateObject().Select(p => p.Name));
        Assert.Equal(1, body.GetProperty("fixed_coordinates").GetInt32());
    }

    private sealed class RecordingTransport : ILibreNmsTransport
    {
        public HttpMethod? Method { get; private set; }

        public string? Url { get; private set; }

        public JsonDocument? BodyJson { get; private set; }

        public LibreNmsConnection? Connection => null;

        public Task<JsonDocument> SendAsync(HttpMethod method, string relativeUrl, object? body = null, CancellationToken cancellationToken = default)
        {
            Method = method;
            Url = relativeUrl;

            // Serialised exactly as the real transport does.
            BodyJson = body is null ? null : JsonDocument.Parse(JsonSerializer.Serialize(body, LibreNmsJson.Options));
            return Task.FromResult(JsonDocument.Parse("{\"status\":\"ok\"}"));
        }

        public Task<IReadOnlyList<T>> GetCollectionAsync<T>(string relativeUrl, string collectionProperty, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<T>>(Array.Empty<T>());

        public Task<string> SendRawAsync(string relativeUrl, CancellationToken cancellationToken = default)
            => Task.FromResult(string.Empty);
    }
}
