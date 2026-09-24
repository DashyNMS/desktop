using System.Net;
using System.Text.Json;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Json;
using DesktopNMS.Core.Models;
using Xunit;

namespace DesktopNMS.Core.Tests;

public class RoutingTests
{
    [Fact]
    public async Task No_VRFs_is_an_empty_list_not_an_error()
    {
        var api = new RoutingApi(new ThrowingTransport(HttpStatusCode.NotFound, "VRFs do not exist"));

        Assert.Empty(await api.ListVrfsAsync(3));
    }

    [Fact]
    public async Task No_OSPFv3_neighbours_is_an_empty_list_not_an_error()
    {
        var api = new RoutingApi(new ThrowingTransport(HttpStatusCode.InternalServerError, "Error retrieving ospfv3_nbrs"));

        Assert.Empty(await api.ListOspfv3NeighboursAsync(3));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "Something else broke")]
    [InlineData(HttpStatusCode.Forbidden, "VRFs do not exist")]
    public async Task A_real_failure_still_throws(HttpStatusCode status, string message)
    {
        var api = new RoutingApi(new ThrowingTransport(status, message));

        await Assert.ThrowsAsync<LibreNmsApiException>(() => api.ListVrfsAsync(3));
        await Assert.ThrowsAsync<LibreNmsApiException>(() => api.ListOspfv3NeighboursAsync(3));
    }

    [Fact]
    public async Task Requests_filter_by_device_id()
    {
        var transport = new ThrowingTransport(null, null);
        var api = new RoutingApi(transport);

        await api.ListBgpSessionsAsync(42);
        Assert.Equal("bgp?hostname=42", transport.LastUrl);
        await api.ListOspfNeighboursAsync(42);
        Assert.Equal("ospf?hostname=42", transport.LastUrl);
        await api.ListVrfsAsync(42);
        Assert.Equal("routing/vrf?hostname=42", transport.LastUrl);
    }

    [Fact]
    public void A_BGP_session_parses_from_LibreNMS_columns()
    {
        const string json = """
            {"bgpPeer_id":7,"device_id":12,"vrf_id":null,"astext":"CLOUDFLARENET","bgpPeerIdentifier":"2001:0db8:0000:0000:0000:0000:0000:0001",
             "bgpPeerRemoteAs":"4200000001","bgpPeerState":"established","bgpPeerAdminStatus":"start","bgpPeerLastErrorCode":null,
             "bgpPeerLastErrorText":null,"bgpLocalAddr":"2001:db8::2","bgpPeerRemoteAddr":"2001:db8::1","bgpPeerDescr":"Transit A",
             "bgpPeerInUpdates":10,"bgpPeerOutUpdates":"20","bgpPeerFsmEstablishedTime":"86400","bgpPeerInUpdateElapsedTime":0}
            """;

        var session = JsonSerializer.Deserialize<BgpSession>(json, LibreNmsJson.Options)!;

        Assert.Equal(4200000001, session.RemoteAs);
        Assert.True(session.IsEstablished);
        Assert.False(session.IsAdminDown);
        Assert.Equal("2001:db8::1", session.PeerAddressText);
        Assert.Equal(86400, session.EstablishedSeconds);
        Assert.Equal("Transit A", session.Description);
    }

    [Theory]
    [InlineData("stop", true)]
    [InlineData("halted", true)]
    [InlineData("start", false)]
    [InlineData("running", false)]
    public void Admin_down_covers_both_spellings(string admin, bool down)
    {
        Assert.Equal(down, new BgpSession { AdminStatus = admin }.IsAdminDown);
    }

    [Fact]
    public void VRFs_and_OSPF_neighbours_parse()
    {
        var vrf = JsonSerializer.Deserialize<Vrf>(
            """{"vrf_id":1,"vrf_oid":"1","vrf_name":"Mgmt-vrf","bgpLocalAs":null,"mplsVpnVrfRouteDistinguisher":null,"mplsVpnVrfDescription":"","device_id":102}""",
            LibreNmsJson.Options)!;
        Assert.Equal("Mgmt-vrf", vrf.Name);
        Assert.Null(vrf.LocalAs);

        var ospf = JsonSerializer.Deserialize<OspfNeighbour>(
            """{"id":1,"device_id":5,"port_id":"99","ospfNbrIpAddr":"10.0.0.2","ospfNbrRtrId":"2.2.2.2","ospfNbrPriority":1,"ospfNbrState":"full","ospfNbrEvents":6}""",
            LibreNmsJson.Options)!;
        Assert.Equal(99, ospf.PortId);
        Assert.Equal("full", ospf.State);
    }

    [Theory]
    [InlineData("2001:0db8:0000:0000:0000:0000:0000:0001", "2001:db8::1")]
    [InlineData("10.0.0.1", "10.0.0.1")]
    [InlineData("not-an-ip", "not-an-ip")]
    [InlineData(null, "")]
    public void Addresses_are_shown_compressed(string? stored, string expected)
    {
        Assert.Equal(expected, RoutingText.Address(stored));
    }

    /// <summary>Throws the given LibreNMS error for every collection request (or returns nothing when status is null), remembering the last URL.</summary>
    private sealed class ThrowingTransport : ILibreNmsTransport
    {
        private readonly HttpStatusCode? _status;
        private readonly string? _message;

        public ThrowingTransport(HttpStatusCode? status, string? message)
        {
            _status = status;
            _message = message;
        }

        public string? LastUrl { get; private set; }

        public LibreNmsConnection? Connection => null;

        public Task<IReadOnlyList<T>> GetCollectionAsync<T>(string relativeUrl, string collectionProperty, CancellationToken cancellationToken = default)
        {
            LastUrl = relativeUrl;
            return _status is { } status
                ? Task.FromException<IReadOnlyList<T>>(new LibreNmsApiException("failed", status, _message))
                : Task.FromResult<IReadOnlyList<T>>(Array.Empty<T>());
        }

        public Task<JsonDocument> SendAsync(HttpMethod method, string relativeUrl, object? body = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<string> SendRawAsync(string relativeUrl, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
