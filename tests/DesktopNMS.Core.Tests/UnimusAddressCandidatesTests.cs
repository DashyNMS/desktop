using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Models;
using Xunit;

namespace DesktopNMS.Core.Tests;

public class UnimusAddressCandidatesTests
{
    [Fact]
    public void Order_matches_librenms_own_unimus_client()
    {
        var device = new Device { Hostname = "switch1.corp.example.com", Ip = "10.0.0.5" };

        var candidates = UnimusAddressCandidates.ForDevice(device, myDomain: "internal.example.com");

        // LibreNMS's PHP appends mydomain to the raw hostname, not the
        // already-stripped one ($device->hostname . '.' . $domain,
        // verbatim) - so a hostname that already has a domain produces this
        // slightly odd double-domain candidate too. Faithfulness to the
        // reference implementation wins over what looks tidier.
        Assert.Equal(
            new[] { "switch1.corp.example.com", "switch1", "switch1.corp.example.com.internal.example.com", "10.0.0.5" },
            candidates);
    }

    [Fact]
    public void MyDomain_on_a_bare_short_hostname_produces_the_intended_shape()
    {
        // The common real-world case: a short hostname discovered without a
        // domain, with mydomain configured to supply one.
        var device = new Device { Hostname = "switch1", Ip = "10.0.0.5" };

        var candidates = UnimusAddressCandidates.ForDevice(device, myDomain: "internal.example.com");

        Assert.Equal(new[] { "switch1", "switch1.internal.example.com", "10.0.0.5" }, candidates);
    }

    [Fact]
    public void A_hostname_with_no_domain_does_not_produce_a_duplicate_stripped_candidate()
    {
        var device = new Device { Hostname = "switch1", Ip = "10.0.0.5" };

        var candidates = UnimusAddressCandidates.ForDevice(device, myDomain: null);

        Assert.Equal(new[] { "switch1", "10.0.0.5" }, candidates);
    }

    [Fact]
    public void MyDomain_candidate_is_omitted_when_not_configured()
    {
        var device = new Device { Hostname = "switch1.corp.example.com", Ip = "10.0.0.5" };

        var candidates = UnimusAddressCandidates.ForDevice(device, myDomain: null);

        Assert.DoesNotContain(candidates, c => c.Contains("internal", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(new[] { "switch1.corp.example.com", "switch1", "10.0.0.5" }, candidates);
    }

    [Fact]
    public void Blank_myDomain_is_treated_the_same_as_unset()
    {
        var device = new Device { Hostname = "switch1.corp.example.com", Ip = "10.0.0.5" };

        var candidates = UnimusAddressCandidates.ForDevice(device, myDomain: "   ");

        Assert.Equal(new[] { "switch1.corp.example.com", "switch1", "10.0.0.5" }, candidates);
    }

    [Fact]
    public void Missing_hostname_still_yields_the_ip_candidate()
    {
        var device = new Device { Hostname = null, Ip = "10.0.0.5" };

        var candidates = UnimusAddressCandidates.ForDevice(device, myDomain: "internal.example.com");

        Assert.Equal(new[] { "10.0.0.5" }, candidates);
    }

    [Fact]
    public void Missing_ip_still_yields_hostname_candidates()
    {
        var device = new Device { Hostname = "switch1.corp.example.com", Ip = null };

        var candidates = UnimusAddressCandidates.ForDevice(device, myDomain: null);

        Assert.Equal(new[] { "switch1.corp.example.com", "switch1" }, candidates);
    }

    [Fact]
    public void Nothing_at_all_yields_an_empty_list_rather_than_throwing()
    {
        var device = new Device { Hostname = null, Ip = null };

        var candidates = UnimusAddressCandidates.ForDevice(device, myDomain: null);

        Assert.Empty(candidates);
    }

    [Fact]
    public void Duplicate_candidates_collapse_to_one_case_insensitively()
    {
        // A short hostname with no domain and myDomain unset would otherwise
        // list the same bare hostname twice (once verbatim, once "stripped").
        var device = new Device { Hostname = "SWITCH1", Ip = "switch1" };

        var candidates = UnimusAddressCandidates.ForDevice(device, myDomain: null);

        Assert.Single(candidates);
    }
}
