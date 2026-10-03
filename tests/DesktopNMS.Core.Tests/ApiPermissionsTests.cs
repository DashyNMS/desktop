using System.Net;
using System.Net.Sockets;
using System.Text;
using DesktopNMS.Core.Api;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DesktopNMS.Core.Tests;

public class ApiPermissionsTests
{
    private static readonly LibreNmsApiException Forbidden = new("refused", HttpStatusCode.Forbidden, "This action is unauthorized.");

    [Theory]
    [InlineData("PUT", "alerts/12", ApiPermission.AcknowledgeAlerts)]
    [InlineData("PUT", "alerts/unmute/12", ApiPermission.AcknowledgeAlerts)]
    [InlineData("POST", "rules", ApiPermission.CreateRules)]
    [InlineData("PUT", "rules", ApiPermission.EditRules)]
    [InlineData("DELETE", "rules/7", ApiPermission.DeleteRules)]
    [InlineData("POST", "alert_templates", ApiPermission.ChangeTemplates)]
    [InlineData("POST", "devices", ApiPermission.AddDevices)]
    [InlineData("PATCH", "devices/3", ApiPermission.EditDevices)]
    [InlineData("PATCH", "devices/3/rename/core-sw-02", ApiPermission.EditDevices)]
    [InlineData("POST", "devices/3/maintenance", ApiPermission.EditDevices)]
    [InlineData("DELETE", "devices/3", ApiPermission.DeleteDevices)]
    [InlineData("POST", "devicegroups", ApiPermission.CreateGroups)]
    [InlineData("PATCH", "devicegroups/Core%20switches", ApiPermission.EditGroups)]
    [InlineData("POST", "devicegroups/Core/devices", ApiPermission.EditGroups)]
    [InlineData("DELETE", "devicegroups/Core/devices", ApiPermission.EditGroups)]
    [InlineData("POST", "devicegroups/Core/maintenance", ApiPermission.EditGroups)]
    [InlineData("DELETE", "devicegroups/Core", ApiPermission.DeleteGroups)]
    [InlineData("POST", "locations/", ApiPermission.CreateLocations)]
    [InlineData("PATCH", "locations/4", ApiPermission.EditLocations)]
    [InlineData("DELETE", "locations/4", ApiPermission.DeleteLocations)]
    public void Each_write_route_maps_to_its_LibreNMS_permission(string method, string url, ApiPermission expected)
        => Assert.Equal(expected, ApiPermissions.For(new HttpMethod(method), url));

    [Theory]
    [InlineData("GET", "alerts?state=1")]
    [InlineData("GET", "devices/3/discover")]
    [InlineData("GET", "rules")]
    [InlineData("POST", "devices/3/eventlog")]
    public void Reads_and_unlisted_routes_have_no_permission(string method, string url)
        => Assert.Null(ApiPermissions.For(new HttpMethod(method), url));

    [Fact]
    public void A_403_on_a_write_is_remembered_once()
    {
        var permissions = new ApiPermissions();
        var changes = 0;
        permissions.Changed += (_, _) => changes++;

        Assert.True(permissions.Learn(HttpMethod.Delete, "devices/3", Forbidden));
        Assert.False(permissions.Learn(HttpMethod.Delete, "devices/9", Forbidden));

        Assert.True(permissions.IsRefused(ApiPermission.DeleteDevices));
        Assert.False(permissions.IsRefused(ApiPermission.EditDevices));
        Assert.Equal(1, changes);
    }

    [Fact]
    public void Other_failures_and_reads_teach_nothing()
    {
        var permissions = new ApiPermissions();

        Assert.False(permissions.Learn(HttpMethod.Delete, "devices/3", new LibreNmsApiException("gone", HttpStatusCode.NotFound)));
        Assert.False(permissions.Learn(HttpMethod.Delete, "devices/3", new LibreNmsApiException("bad token", HttpStatusCode.Unauthorized)));
        Assert.False(permissions.Learn(HttpMethod.Get, "alerts", Forbidden));

        Assert.False(permissions.IsRefused(ApiPermission.DeleteDevices));
    }

    [Fact]
    public void Forgetting_starts_over()
    {
        var permissions = new ApiPermissions();
        permissions.Learn(HttpMethod.Post, "rules", Forbidden);

        permissions.Forget();

        Assert.False(permissions.IsRefused(ApiPermission.CreateRules));
    }

    [Fact]
    public void A_403_is_not_a_rejected_token()
    {
        Assert.False(Forbidden.IsAuthenticationFailure);
        Assert.True(Forbidden.IsPermissionDenied);
        Assert.True(new LibreNmsApiException("bad token", HttpStatusCode.Unauthorized).IsAuthenticationFailure);
    }

    [Fact]
    public void A_403_explains_itself_instead_of_LibreNMS_wording()
        => Assert.Equal(LibreNmsApiException.PermissionDeniedMessage, Forbidden.ToUserMessage());

    [Fact]
    public async Task The_transport_learns_from_a_refused_write_and_forgets_on_a_new_connection()
    {
        using var server = new RefusingLibreNms();
        using var transport = new LibreNmsTransport(NullLogger<LibreNmsTransport>.Instance) { RetryTransientFailures = false };
        var connection = new LibreNmsConnection(new Uri($"http://127.0.0.1:{server.Port}/"), "token", timeoutSeconds: 5);
        transport.Configure(connection);

        var ex = await Assert.ThrowsAsync<LibreNmsApiException>(() => transport.SendAsync(HttpMethod.Delete, "rules/7"));

        Assert.True(ex.IsPermissionDenied);
        Assert.True(transport.Permissions.IsRefused(ApiPermission.DeleteRules));

        transport.Configure(connection);

        Assert.False(transport.Permissions.IsRefused(ApiPermission.DeleteRules));
    }

    private static readonly LibreNmsApiException CrashedCheck = new("crashed", HttpStatusCode.InternalServerError, "Server Error");

    [Theory]
    [InlineData("PUT", "alerts/12")]
    [InlineData("PUT", "alerts/unmute/12")]
    [InlineData("PATCH", "devicegroups/Core")]
    [InlineData("DELETE", "devicegroups/Core")]
    [InlineData("PATCH", "locations/4")]
    [InlineData("DELETE", "locations/4")]
    public void A_bare_server_error_where_LibreNMS_check_crashes_is_a_refusal(string method, string url)
        => Assert.True(ApiPermissions.IsCrashedPermissionCheck(new HttpMethod(method), url, CrashedCheck));

    [Fact]
    public void Other_server_errors_stay_server_errors()
    {
        // Rules' check works (a real 403), so a 500 there is a real failure.
        Assert.False(ApiPermissions.IsCrashedPermissionCheck(HttpMethod.Delete, "rules/7", CrashedCheck));
        // A 500 with LibreNMS's own message is the write failing, not the check.
        Assert.False(ApiPermissions.IsCrashedPermissionCheck(HttpMethod.Put, "alerts/12", new LibreNmsApiException("x", HttpStatusCode.InternalServerError, "Database error")));
        Assert.False(ApiPermissions.IsCrashedPermissionCheck(HttpMethod.Get, "alerts", CrashedCheck));
    }

    [Fact]
    public async Task The_transport_reports_a_crashed_check_as_refused()
    {
        using var server = new RefusingLibreNms(500, "Internal Server Error", """{"message":"Server Error"}""");
        using var transport = new LibreNmsTransport(NullLogger<LibreNmsTransport>.Instance) { RetryTransientFailures = false };
        transport.Configure(new LibreNmsConnection(new Uri($"http://127.0.0.1:{server.Port}/"), "token", timeoutSeconds: 5));

        var ex = await Assert.ThrowsAsync<LibreNmsApiException>(() => transport.SendAsync(HttpMethod.Put, "alerts/12", new { note = "x" }));

        Assert.True(ex.IsPermissionDenied);
        Assert.Equal(LibreNmsApiException.PermissionDeniedMessage, ex.ToUserMessage());
        Assert.True(transport.Permissions.IsRefused(ApiPermission.AcknowledgeAlerts));
    }

    /// <summary>A loopback LibreNMS that answers every request the same way - Laravel's 403 by default.</summary>
    private sealed class RefusingLibreNms : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly string _statusLine;
        private readonly string _body;

        public RefusingLibreNms(int status = 403, string reason = "Forbidden", string body = """{"message":"This action is unauthorized."}""")
        {
            _statusLine = $"HTTP/1.1 {status} {reason}";
            _body = body;
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(ServeAsync);
        }

        public int Port { get; }

        public void Dispose() => _listener.Stop();

        private async Task ServeAsync()
        {
            try
            {
                while (true)
                {
                    using var client = await _listener.AcceptTcpClientAsync();
                    var stream = client.GetStream();
                    await stream.ReadAsync(new byte[4096]);

                    var response = $"{_statusLine}\r\nContent-Type: application/json\r\nContent-Length: {_body.Length}\r\nConnection: close\r\n\r\n{_body}";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
                }
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                // Stopped.
            }
        }
    }
}
