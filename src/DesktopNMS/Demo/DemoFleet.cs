using System.Globalization;
using System.Text.Json;

namespace DesktopNMS.Demo;

/// <summary>One answer from <see cref="DemoFleet.Handle"/>.</summary>
internal sealed record DemoResponse(string Body, string ContentType = "application/json", int Status = 200, bool Unhandled = false);

/// <summary>
/// The example network demo mode shows, and the screenshots are taken of: three sites,
/// two dozen devices, their ports, sensors, neighbours, alerts and rules.
/// Example names and documentation addresses only (example.net, 192.0.2.0/24,
/// 198.51.100.0/24, 203.0.113.0/24) - never anything from a real network.
/// Answers just enough of LibreNMS's API, in its own shapes, for each screen
/// to fill; anything else gets an empty, successful envelope.
/// </summary>
internal sealed class DemoFleet
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly DateTime _now = DateTime.UtcNow;
    private readonly List<ExampleDevice> _devices = new();
    private readonly List<ExamplePort> _ports = new();
    private readonly List<object> _links = new();
    private readonly List<object> _sensors = new();
    private readonly List<object> _alerts = new();
    private readonly List<object> _rules = new();
    private readonly List<object> _events = new();
    private readonly Dictionary<string, (int Id, double Lat, double Lng)> _locations = new();

    public DemoFleet()
    {
        AddLocations();
        AddDevices();
        AddLinks();
        AddSensors();
        AddRules();
        AddAlerts();
        AddEvents();
    }

    /// <summary>The device the screenshots open Device Details on.</summary>
    public int FeaturedDeviceId => Device("core-sw-01").Id;

    public IReadOnlyList<(int Id, string Name)> PinnedDevices =>
        new[] { "core-sw-01", "core-sw-02", "edge-fw-01", "dist-sw-01", "dc-ups-1" }.Select(n => (Device(n).Id, n)).ToList();

    public IReadOnlyList<(int Id, string Name)> RecentlyViewed =>
        new[] { "core-sw-01", "ap-lobby-3", "dc-ups-1", "edge-fw-01", "esx-02" }.Select(n => (Device(n).Id, n)).ToList();

    /// <summary>Pinned sensors for the dashboard: (sensor id, device id, class, device, description).</summary>
    public List<(int SensorId, int DeviceId, string Class, string Device, string Description)> PinnedSensors { get; } = new();

    // ------------------------------------------------------------ routing

    public DemoResponse Handle(string method, string target, string body)
    {
        var (path, query) = Split(target);
        if (!path.StartsWith("/api/v0/", StringComparison.Ordinal))
        {
            return Fail(404, "Not found");
        }

        var parts = path["/api/v0/".Length..].Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString).ToArray();

        // Writes: accepted and forgotten - nothing is changed by a demo.
        if (method != "GET")
        {
            return Ok(new Dictionary<string, object?> { ["message"] = "ok" });
        }

        if (parts.Length == 0)
        {
            return Ok(new());
        }

        switch (parts[0])
        {
            case "system":
                return Collection("system", new[]
                {
                    new
                    {
                        local_ver = "26.9.1",
                        local_sha = "4f2c9a1b7e",
                        local_date = "1759320000",
                        local_branch = "master",
                        db_schema = "2026_09_12_101500",
                        php_ver = "8.3.12",
                        python_ver = "3.12.6",
                        database_ver = "MariaDB 11.4.3",
                        rrdtool_ver = "1.8.0",
                        netsnmp_ver = "NET-SNMP 5.9.4",
                    },
                });

            case "devices" when parts.Length == 1:
                return Collection("devices", _devices.Select(DeviceJson));

            case "devices" when parts.Length >= 2:
                return DeviceRoute(parts, query);

            case "ports":
                return Collection("ports", _ports.Select(PortJson));

            case "alerts" when parts.Length == 1:
                var states = query.TryGetValue("state", out var s)
                    ? s.Split(',').Select(v => int.Parse(v, CultureInfo.InvariantCulture)).ToHashSet()
                    : new HashSet<int> { 1, 2 };
                return Collection("alerts", _alerts.Where(a => states.Contains(StateOf(a)) || states.Contains(-1)));

            case "alerts":
                return Collection("alerts", _alerts.Where(a => IdOf(a).ToString(CultureInfo.InvariantCulture) == parts[1]));

            case "rules" when parts.Length == 1:
                return Collection("rules", _rules);

            case "rules":
                return Collection("rules", _rules.Where(r => IdOf(r).ToString(CultureInfo.InvariantCulture) == parts[1]));

            case "alert_templates":
                return Collection("alert_templates", new[]
                {
                    new { id = 1, name = "Default Alert Template", template = "{{ $alert->title }}\nSeverity: {{ $alert->severity }}", title = (string?)null, title_rec = (string?)null, alert_rules = Array.Empty<int>() },
                    new { id = 2, name = "Concise for chat", template = "{{ $alert->hostname }}: {{ $alert->name }}", title = (string?)"{{ $alert->hostname }} - {{ $alert->name }}", title_rec = (string?)null, alert_rules = new[] { 1 } },
                });

            case "devicegroups" when parts.Length == 1:
                return Collection("groups", new[]
                {
                    new { id = 1, name = "Core network", desc = "Core and distribution switches", type = "static", rules = (string?)null },
                    new { id = 2, name = "DC1 power", desc = "UPS and PDUs in DC1", type = "static", rules = (string?)null },
                    new { id = 3, name = "Wireless", desc = "Controllers and access points", type = "dynamic", rules = (string?)"{\"condition\":\"AND\",\"rules\":[{\"id\":\"devices.type\",\"field\":\"devices.type\",\"type\":\"string\",\"operator\":\"equal\",\"value\":\"wireless\"}],\"valid\":true}" },
                });

            case "devicegroups":
                var members = parts[1] switch
                {
                    "1" => new[] { "core-sw-01", "core-sw-02", "dist-sw-01", "dist-sw-02", "dc2-core-01", "dc2-core-02" },
                    "2" => new[] { "dc-ups-1", "dc-pdu-2" },
                    _ => new[] { "wlc-01", "ap-lobby-3", "ap-floor2-1" },
                };
                return Collection("devices", members.Select(n => new { device_id = Device(n).Id }));

            case "resources" when parts.Length >= 2:
                return parts[1] switch
                {
                    "sensors" => Collection("sensors", _sensors),
                    "links" => Collection("links", _links),
                    "locations" => Collection("locations", _locations.Select(l => new { id = l.Value.Id, location = l.Key, lat = l.Value.Lat, lng = l.Value.Lng, fixed_coordinates = true })),
                    _ => Ok(new()),
                };

            case "logs" when parts.Length >= 2 && parts[1] == "eventlog":
                var forDevice = parts.Length >= 3 ? parts[2] : null;
                return Collection("logs", _events.Where(e => forDevice is null || HostOf(e) == forDevice || DeviceIdOf(e).ToString(CultureInfo.InvariantCulture) == forDevice));

            default:
                return Ok(new(), unhandled: true);
        }
    }

    private DemoResponse DeviceRoute(string[] parts, IReadOnlyDictionary<string, string> query)
    {
        var device = _devices.FirstOrDefault(d => d.Id.ToString(CultureInfo.InvariantCulture) == parts[1] || d.Name == parts[1]);
        if (device is null)
        {
            return Fail(404, "Device does not exist");
        }

        if (parts.Length == 2)
        {
            return Collection("devices", new[] { DeviceJson(device) });
        }

        var width = query.TryGetValue("width", out var w) ? int.Parse(w, CultureInfo.InvariantCulture) : 600;
        var height = query.TryGetValue("height", out var h) ? int.Parse(h, CultureInfo.InvariantCulture) : 200;

        switch (parts[2])
        {
            case "ports" when parts.Length >= 5:
                return Svg(DemoGraphs.Traffic(width, height, Seed(device.Name + parts[3]), PortOf(device, parts[3])?.Load ?? 0.3));

            case "ports":
                return Collection("ports", _ports.Where(p => p.DeviceId == device.Id).Select(PortJson));

            case "links":
                return Collection("links", _links.Where(l => LocalDeviceOf(l) == device.Id));

            case "graphs":
                return Collection("graphs", new[]
                {
                    new { name = "device_bits", desc = "Traffic" },
                    new { name = "device_processor", desc = "Processors" },
                    new { name = "device_mempool", desc = "Memory pools" },
                    new { name = "device_icmp_perf", desc = "Ping response" },
                    new { name = "device_poller_perf", desc = "Poller time" },
                    new { name = "device_uptime", desc = "Uptime" },
                });

            case "health" when parts.Length == 3:
                return Collection("graphs", new[]
                {
                    new { name = "device_processor", desc = "Processors" },
                    new { name = "device_mempool", desc = "Memory pools" },
                    new { name = "device_storage", desc = "Storage" },
                    new { name = "device_temperature", desc = "Temperature" },
                    new { name = "device_fanspeed", desc = "Fan speed" },
                });

            case "health" when parts.Length == 4:
                return Collection("graphs", parts[3] switch
                {
                    "processor" => new object[] { new { sensor_id = device.Id * 10 + 1 } },
                    "mempool" => new object[] { new { sensor_id = device.Id * 10 + 2 } },
                    "storage" => new object[] { new { sensor_id = device.Id * 10 + 3 }, new { sensor_id = device.Id * 10 + 4 } },
                    _ => Array.Empty<object>(),
                });

            case "health" when parts.Length == 5:
                return Collection("graphs", HealthDetail(device, parts[3], parts[4]));

            case "availability":
                return Collection("availability", new[]
                {
                    new { duration = 86400, availability_perc = device.Up ? 100.0 : 97.9 },
                    new { duration = 604800, availability_perc = device.Up ? 99.998 : 99.61 },
                    new { duration = 2592000, availability_perc = device.Up ? 99.991 : 99.82 },
                    new { duration = 31536000, availability_perc = device.Up ? 99.97 : 99.88 },
                });

            case "groups":
                return Collection("groups", Array.Empty<object>());

            case "maintenance":
                return Ok(new Dictionary<string, object?> { ["is_under_maintenance"] = false });

            case var other when !query.ContainsKey("from"):
                // A list this demo has nothing for: wireless, ARP, VLANs, ...
                return Ok(new(), unhandled: !other.Equals("wireless-sensors", StringComparison.Ordinal));

            default:
                // Anything else under a device, asked for a time range, is a graph, by name.
                return Svg(parts[2] switch
                {
                    "device_bits" => DemoGraphs.Traffic(width, height, Seed(device.Name), device.Load),
                    "device_processor" => DemoGraphs.Percent(width, height, Seed(device.Name + "cpu"), device.Cpu),
                    "device_mempool" => DemoGraphs.Percent(width, height, Seed(device.Name + "mem"), device.Memory),
                    "device_icmp_perf" => DemoGraphs.Latency(width, height, Seed(device.Name + "ping")),
                    _ => DemoGraphs.Percent(width, height, Seed(device.Name + parts[2]), 0.4),
                }, legend: !(query.TryGetValue("legend", out var noLegend) && noLegend == "no"));
        }
    }

    private object[] HealthDetail(ExampleDevice device, string type, string id) => type switch
    {
        "processor" => new object[] { new { processor_id = int.Parse(id, CultureInfo.InvariantCulture), device_id = device.Id, processor_descr = "CPU", processor_usage = Math.Round(device.Cpu * 100), processor_perc_warn = 75 } },
        "mempool" => new object[] { new { mempool_id = int.Parse(id, CultureInfo.InvariantCulture), device_id = device.Id, mempool_descr = "System memory", mempool_perc = Math.Round(device.Memory * 100), mempool_perc_warn = 90, mempool_used = (long)(device.Memory * 17_179_869_184), mempool_free = (long)((1 - device.Memory) * 17_179_869_184), mempool_total = 17_179_869_184L } },
        "storage" => id.EndsWith('3')
            ? new object[] { new { storage_id = int.Parse(id, CultureInfo.InvariantCulture), device_id = device.Id, storage_descr = "bootflash:", storage_perc = 38, storage_perc_warn = 80, storage_used = 4_080_218_931L, storage_free = 6_657_199_309L, storage_size = 10_737_418_240L } }
            : new object[] { new { storage_id = int.Parse(id, CultureInfo.InvariantCulture), device_id = device.Id, storage_descr = "flash:", storage_perc = 61, storage_perc_warn = 80, storage_used = 1_310_720_000L, storage_free = 837_763_072L, storage_size = 2_148_483_072L } },
        _ => Array.Empty<object>(),
    };

    // ------------------------------------------------------------ the fleet

    private void AddLocations()
    {
        _locations["DC1, London"] = (1, 51.5079, -0.0877);
        _locations["DC2, Manchester"] = (2, 53.4794, -2.2453);
        _locations["Office, Leeds"] = (3, 53.7997, -1.5492);
        _locations["Branch, Bristol"] = (4, 51.4545, -2.5879);
    }

    private void AddDevices()
    {
        void Add(string name, string ip, string os, string hardware, string version, string type, string location, double load, double cpu, double memory, bool up = true, bool disabled = false, string? serial = null)
            => _devices.Add(new ExampleDevice(_devices.Count + 1, name, ip, os, hardware, version, type, location, load, cpu, memory, up, disabled, serial ?? "FOC" + (2600 + _devices.Count * 37).ToString(CultureInfo.InvariantCulture) + "X1AB"));

        Add("core-sw-01", "192.0.2.10", "iosxe", "C9500-24Y4C", "17.12.3", "network", "DC1, London", 0.62, 0.18, 0.41);
        Add("core-sw-02", "192.0.2.11", "iosxe", "C9500-24Y4C", "17.12.3", "network", "DC1, London", 0.55, 0.22, 0.43);
        Add("dist-sw-01", "192.0.2.20", "iosxe", "C9300-48P", "17.9.5", "network", "DC1, London", 0.34, 0.12, 0.36);
        Add("dist-sw-02", "192.0.2.21", "iosxe", "C9300-48P", "17.9.5", "network", "DC1, London", 0.81, 0.14, 0.38);
        Add("edge-fw-01", "192.0.2.1", "pfsense", "Netgate 8200", "24.03", "firewall", "DC1, London", 0.47, 0.31, 0.52);
        Add("edge-rtr-01", "192.0.2.2", "junos", "MX204", "23.4R2", "network", "DC1, London", 0.58, 0.09, 0.47);
        Add("dc2-core-01", "198.51.100.10", "arista_eos", "DCS-7050SX3-48YC8", "4.32.2F", "network", "DC2, Manchester", 0.41, 0.15, 0.33);
        Add("dc2-core-02", "198.51.100.11", "arista_eos", "DCS-7050SX3-48YC8", "4.32.2F", "network", "DC2, Manchester", 0.39, 0.13, 0.34);
        Add("dc2-fw-01", "198.51.100.1", "fortigate", "FortiGate 200F", "7.4.4", "firewall", "DC2, Manchester", 0.28, 0.24, 0.49);
        Add("wlc-01", "192.0.2.30", "arubaos", "Aruba 7210", "8.11.2.1", "wireless", "DC1, London", 0.22, 0.19, 0.44);
        Add("ap-lobby-3", "203.0.113.131", "arubaos", "AP-515", "8.11.2.1", "wireless", "Office, Leeds", 0.18, 0.11, 0.29);
        Add("ap-floor2-1", "203.0.113.132", "arubaos", "AP-515", "8.11.2.1", "wireless", "Office, Leeds", 0.26, 0.09, 0.27);
        Add("leeds-sw-01", "203.0.113.10", "arubaos-cx", "6300M 48G", "10.13.1000", "network", "Office, Leeds", 0.31, 0.08, 0.31);
        Add("bristol-rtr-01", "203.0.113.65", "routeros", "CCR2004-1G-12S+2XS", "7.15.3", "network", "Branch, Bristol", 0, 0, 0, up: false);
        Add("bristol-sw-01", "203.0.113.66", "routeros", "CRS326-24G-2S+", "7.15.3", "network", "Branch, Bristol", 0, 0, 0, up: false);
        Add("dc-ups-1", "192.0.2.40", "apc", "Smart-UPS SRT 6000", "UPS 16.3", "power", "DC1, London", 0.1, 0, 0);
        Add("dc-pdu-2", "192.0.2.41", "apc", "AP8853 Rack PDU", "6.9.6", "power", "DC1, London", 0.1, 0, 0);
        Add("esx-01", "192.0.2.50", "vmware", "PowerEdge R760", "ESXi 8.0.3", "server", "DC1, London", 0.44, 0.38, 0.71);
        Add("esx-02", "192.0.2.51", "vmware", "PowerEdge R760", "ESXi 8.0.3", "server", "DC1, London", 0.49, 0.42, 0.76);
        Add("nas-01", "192.0.2.60", "dsm", "RS3621xs+", "DSM 7.2.2", "storage", "DC1, London", 0.36, 0.17, 0.33);
        Add("mon-01", "192.0.2.70", "linux", "Ubuntu 24.04 LTS", "6.8.0-45", "server", "DC1, London", 0.12, 0.21, 0.58);
        Add("printer-2f", "203.0.113.20", "hpprinter", "LaserJet M610", "2510", "printer", "Office, Leeds", 0, 0, 0, disabled: true);
        Add("cam-gate-1", "203.0.113.30", "axiscam", "AXIS P3268-LVE", "11.11.73", "appliance", "Office, Leeds", 0, 0, 0, up: false);
        Add("dc2-ups-1", "198.51.100.40", "eaton-ups", "9PX 6000i", "3.1.8", "power", "DC2, Manchester", 0.1, 0, 0);

        foreach (var device in _devices)
        {
            AddPortsFor(device);
        }
    }

    private void AddPortsFor(ExampleDevice d)
    {
        var names = d.Os switch
        {
            "iosxe" when d.Hardware.StartsWith("C9500", StringComparison.Ordinal) => new[] { "Te1/0/1", "Te1/0/2", "Te1/0/3", "Te1/0/4", "Te1/1/1", "Te1/1/2", "Gi0/0" },
            "iosxe" => new[] { "Gi1/0/1", "Gi1/0/2", "Gi1/0/3", "Gi1/0/24", "Gi1/0/40", "Gi1/0/41", "Gi1/0/48", "Te1/1/1", "Te1/1/2" },
            "junos" => new[] { "xe-0/0/0", "xe-0/0/1", "xe-0/0/2", "xe-0/0/3", "et-0/0/0", "fxp0" },
            "pfsense" => new[] { "ix0", "ix1", "ix2", "igc0" },
            "arista_eos" => new[] { "Ethernet1", "Ethernet2", "Ethernet10", "Ethernet49", "Ethernet50", "Management1" },
            "fortigate" => new[] { "port1", "port2", "wan1", "mgmt" },
            "arubaos" when d.Type == "wireless" && d.Name.StartsWith("ap", StringComparison.Ordinal) => new[] { "bond0", "radio0", "radio1" },
            "arubaos" => new[] { "GE0/0/0", "GE0/0/1" },
            "arubaos-cx" => new[] { "1/1/1", "1/1/2", "1/1/3", "1/1/4", "1/1/49", "1/1/50" },
            "routeros" => new[] { "sfp-sfpplus1", "sfp-sfpplus2", "ether1", "ether2" },
            "vmware" => new[] { "vmnic0", "vmnic1", "vmk0" },
            "dsm" or "linux" => new[] { "eth0", "eth1" },
            _ => new[] { "eth0" },
        };

        var random = new Random(Seed(d.Name));
        var index = 0;
        foreach (var name in names)
        {
            index++;
            var ten = name.StartsWith("Te", StringComparison.Ordinal) || name.StartsWith("xe", StringComparison.Ordinal) || name.StartsWith("ix", StringComparison.Ordinal)
                      || name.StartsWith("sfp", StringComparison.Ordinal) || name.StartsWith("Ethernet", StringComparison.Ordinal) || name.StartsWith("1/1/49", StringComparison.Ordinal);
            var speed = name.StartsWith("et-", StringComparison.Ordinal) ? 100_000_000_000L : ten ? 10_000_000_000L : 1_000_000_000L;
            var up = d.Up && !d.Disabled && !name.StartsWith("radio1", StringComparison.Ordinal) && !(name == "Gi1/0/48");
            var load = up ? Math.Clamp(d.Load * (0.4 + random.NextDouble() * 0.9), 0.01, 0.92) : 0;
            _ports.Add(new ExamplePort(_ports.Count + 1, d.Id, index, name, Alias(d, name), speed, up, load, random.Next(0, 4) == 0 && up && d.Name == "edge-fw-01" ? 37 : 0));
        }
    }

    private static string Alias(ExampleDevice d, string port) => (d.Name, port) switch
    {
        ("core-sw-01", "Te1/1/1") or ("core-sw-02", "Te1/1/1") => "edge-fw-01 uplink",
        ("core-sw-01", "Te1/1/2") or ("core-sw-02", "Te1/1/2") => "core interconnect",
        (_, "Te1/1/1") or (_, "Te1/1/2") => "core uplink",
        ("edge-rtr-01", "xe-0/0/0") => "Transit: upstream-pe.example.org",
        ("edge-rtr-01", "xe-0/0/1") => "WAN: DC2, Manchester",
        ("edge-rtr-01", "xe-0/0/2") => "WAN: Office, Leeds",
        ("edge-rtr-01", "xe-0/0/3") => "WAN: Branch, Bristol",
        ("edge-fw-01", "ix0") => "outside",
        ("edge-fw-01", _) => "inside",
        (_, "Gi1/0/24") => "wlc-01",
        (_, "Gi1/0/40") or (_, "Gi1/0/41") => "power management",
        _ => string.Empty,
    };

    private void AddLinks()
    {
        void Link(string a, string aPort, string b, string bPort, string protocol = "lldp")
        {
            Side(a, aPort, b, bPort, protocol);
            Side(b, bPort, a, aPort, protocol);
        }

        void Side(string local, string localPort, string remote, string remotePort, string protocol)
        {
            var l = Device(local);
            var r = Device(remote);
            var lp = PortOf(l, localPort);
            var rp = PortOf(r, remotePort);
            if (lp is null || rp is null || !l.Up)
            {
                return;
            }

            _links.Add(new
            {
                id = _links.Count + 1,
                local_device_id = l.Id,
                local_port_id = lp.Id,
                remote_port_id = rp.Id,
                remote_port = remotePort,
                remote_hostname = r.Name + ".example.net",
                remote_device_id = r.Id,
                remote_platform = r.Hardware,
                protocol,
                remote_version = $"{r.Hardware}, {r.Version}",
                active = true,
            });
        }

        Link("edge-rtr-01", "xe-0/0/1", "dc2-core-01", "Ethernet49");
        Link("edge-rtr-01", "xe-0/0/2", "leeds-sw-01", "1/1/49");
        Link("edge-rtr-01", "xe-0/0/3", "bristol-rtr-01", "sfp-sfpplus1");
        Link("edge-rtr-01", "et-0/0/0", "edge-fw-01", "ix0");
        Link("edge-fw-01", "ix1", "core-sw-01", "Te1/1/1");
        Link("edge-fw-01", "ix2", "core-sw-02", "Te1/1/1");
        Link("core-sw-01", "Te1/1/2", "core-sw-02", "Te1/1/2");
        Link("core-sw-01", "Te1/0/1", "dist-sw-01", "Te1/1/1", "cdp");
        Link("core-sw-02", "Te1/0/1", "dist-sw-01", "Te1/1/2", "cdp");
        Link("core-sw-01", "Te1/0/2", "dist-sw-02", "Te1/1/1", "cdp");
        Link("core-sw-02", "Te1/0/2", "dist-sw-02", "Te1/1/2", "cdp");
        Link("dist-sw-01", "Gi1/0/1", "esx-01", "vmnic0");
        Link("dist-sw-01", "Gi1/0/2", "esx-02", "vmnic0");
        Link("dist-sw-01", "Gi1/0/3", "mon-01", "eth0");
        Link("dist-sw-01", "Gi1/0/40", "dc-ups-1", "eth0");
        Link("dist-sw-02", "Gi1/0/1", "nas-01", "eth0");
        Link("dist-sw-02", "Gi1/0/2", "esx-01", "vmnic1");
        Link("dist-sw-02", "Gi1/0/3", "esx-02", "vmnic1");
        Link("dist-sw-02", "Gi1/0/24", "wlc-01", "GE0/0/0");
        Link("dist-sw-02", "Gi1/0/41", "dc-pdu-2", "eth0");
        Link("dc2-core-01", "Ethernet50", "dc2-core-02", "Ethernet50");
        Link("dc2-core-01", "Ethernet1", "dc2-fw-01", "port1");
        Link("dc2-core-02", "Ethernet1", "dc2-fw-01", "port2");
        Link("dc2-core-02", "Ethernet10", "dc2-ups-1", "eth0");
        Link("leeds-sw-01", "1/1/1", "ap-lobby-3", "bond0");
        Link("leeds-sw-01", "1/1/2", "ap-floor2-1", "bond0");
        Link("leeds-sw-01", "1/1/3", "printer-2f", "eth0");
        Link("leeds-sw-01", "1/1/4", "cam-gate-1", "eth0");

        // A neighbour LibreNMS doesn't monitor: the transit provider's router.
        var edge = Device("edge-rtr-01");
        _links.Add(new
        {
            id = _links.Count + 1,
            local_device_id = edge.Id,
            local_port_id = PortOf(edge, "xe-0/0/0")!.Id,
            remote_port_id = (int?)null,
            remote_port = "xe-4/1/7",
            remote_hostname = "upstream-pe.example.org",
            remote_device_id = 0,
            remote_platform = "Juniper MX960",
            protocol = "lldp",
            remote_version = "22.4R3",
            active = true,
        });
    }

    private void AddSensors()
    {
        void Sensor(string device, string cls, string descr, double current, double? high, double? highWarn, double? low = null, double? lowWarn = null, bool pin = false)
        {
            var d = Device(device);
            var id = 1000 + _sensors.Count;
            _sensors.Add(new
            {
                sensor_id = id,
                device_id = d.Id,
                sensor_class = cls,
                sensor_descr = descr,
                sensor_index = (_sensors.Count + 1).ToString(CultureInfo.InvariantCulture),
                sensor_current = current,
                sensor_limit = high,
                sensor_limit_warn = highWarn,
                sensor_limit_low = low,
                sensor_limit_low_warn = lowWarn,
                lastupdate = Stamp(_now.AddMinutes(-1)),
            });

            if (pin)
            {
                PinnedSensors.Add((id, d.Id, cls, device, descr));
            }
        }

        foreach (var d in _devices.Where(d => d.Type is "network" or "firewall" && d.Up))
        {
            Sensor(d.Name, "temperature", "Inlet", d.Name == "core-sw-02" ? 71 : 31 + d.Id % 9, 70, 60, 5, 10, pin: d.Name == "core-sw-01");
            Sensor(d.Name, "temperature", "Outlet", d.Name == "core-sw-02" ? 78 : 42 + d.Id % 7, 85, 75);
            Sensor(d.Name, "fanspeed", "Fan 1", 3200 + d.Id * 40, 9000, 8000, 1000, 1500, pin: d.Name == "edge-fw-01");
            Sensor(d.Name, "fanspeed", "Fan 2", 3150 + d.Id * 35, 9000, 8000, 1000, 1500);
            Sensor(d.Name, "power", "PSU 1 output", 182 + d.Id * 3, 650, 600);
            Sensor(d.Name, "voltage", "PSU 1 input", 231.4, 264, 254, 196, 207);
        }

        foreach (var name in new[] { "core-sw-01", "core-sw-02", "edge-rtr-01" })
        {
            Sensor(name, "dbm", "Te1/1/1 Rx power", -3.1 - Device(name).Id * 0.2, 1, 0, -14, -12.5);
            Sensor(name, "dbm", "Te1/1/1 Tx power", -2.4, 1, 0, -10, -8);
        }

        Sensor("ap-lobby-3", "signal", "Client signal (average)", -79, null, null, -85, -75, pin: true);
        Sensor("ap-floor2-1", "signal", "Client signal (average)", -61, null, null, -85, -75);
        Sensor("dc-ups-1", "load", "Output load", 61, 95, 85, pin: true);
        Sensor("dc-ups-1", "runtime", "Battery runtime", 34, null, null, 5, 10);
        Sensor("dc-ups-1", "charge", "Battery charge", 100, null, null, 20, 40);
        Sensor("dc-ups-1", "temperature", "Battery", 24, 40, 35);
        Sensor("dc-pdu-2", "current", "Bank 1", 13.4, 16, 12.8);
        Sensor("dc-pdu-2", "current", "Bank 2", 9.1, 16, 12.8);
        Sensor("dc2-ups-1", "load", "Output load", 44, 95, 85);
        Sensor("esx-01", "temperature", "System board inlet", 23, 47, 42);
        Sensor("esx-02", "temperature", "System board inlet", 24, 47, 42);
        Sensor("nas-01", "temperature", "System", 38, 70, 60);
        Sensor("mon-01", "humidity", "Rack 3 humidity", 41, 80, 70, 15, 20);
    }

    private void AddRules()
    {
        void Rule(string name, string severity, string field, string op, string value, bool disabled = false)
        {
            var builder = JsonSerializer.Serialize(new
            {
                condition = "AND",
                rules = new[] { new { id = field, field, type = "string", input = "text", @operator = op, value } },
                valid = true,
            });

            _rules.Add(new
            {
                id = _rules.Count + 1,
                name,
                severity,
                rule = string.Empty,
                builder,
                query = string.Empty,
                extra = new { mute = false, count = -1, delay = 300, invert = false, interval = 300, recovery = true, acknowledgement = true },
                notes = string.Empty,
                proc = (string?)null,
                disabled,
                invert_map = false,
                devices = Array.Empty<int>(),
                groups = Array.Empty<int>(),
                locations = Array.Empty<int>(),
            });
        }

        Rule("Device Down! Due to no ICMP response.", "critical", "macros.device_down", "equal", "1");
        Rule("Sensor over limit", "critical", "macros.sensor_over_limit", "equal", "1");
        Rule("Port errors rising", "warning", "ports.ifInErrors_rate", "greater", "10");
        Rule("Port utilisation over 80%", "warning", "macros.port_usage_perc", "greater_or_equal", "80");
        Rule("Wireless signal below threshold", "warning", "wireless_sensors.sensor_current", "less", "-75");
        Rule("PDU bank load over 80%", "warning", "sensors.sensor_current", "greater", "12.8");
        Rule("Storage over 85%", "warning", "storage.storage_perc", "greater", "85");
        Rule("Device rebooted", "warning", "devices.uptime", "less", "300");
        Rule("BGP session down", "critical", "bgpPeers.bgpPeerState", "not_equal", "established");
        Rule("UPS on battery", "critical", "sensors.sensor_current", "equal", "2", disabled: true);
    }

    private void AddAlerts()
    {
        void Alert(string device, int ruleId, int state, int minutesAgo, string? note = null)
        {
            var d = Device(device);
            var rule = _rules[ruleId - 1];
            _alerts.Add(new
            {
                id = 500 + _alerts.Count,
                device_id = d.Id,
                rule_id = ruleId,
                state,
                alerted = 1,
                open = 1,
                note = note ?? string.Empty,
                timestamp = Stamp(_now.AddMinutes(-minutesAgo)),
                info = string.Empty,
                hostname = d.Name,
                severity = (string)rule.GetType().GetProperty("severity")!.GetValue(rule)!,
                name = (string)rule.GetType().GetProperty("name")!.GetValue(rule)!,
                proc = (string?)null,
                notes = string.Empty,
            });
        }

        Alert("core-sw-02", 2, 1, 2);
        Alert("bristol-rtr-01", 1, 1, 18);
        Alert("bristol-sw-01", 1, 1, 18);
        Alert("cam-gate-1", 1, 2, 74, "Contractor has the gate camera on the bench until Tuesday");
        Alert("edge-fw-01", 3, 1, 6);
        Alert("ap-lobby-3", 5, 1, 14);
        Alert("dc-pdu-2", 6, 1, 32);
        Alert("dist-sw-02", 4, 1, 47);
        Alert("esx-02", 7, 2, 128, "Old snapshots being cleared tonight");
        Alert("dc2-core-02", 8, 1, 9);
    }

    private void AddEvents()
    {
        void Event(string device, int minutesAgo, string type, string message, int severity)
        {
            var d = Device(device);
            _events.Add(new
            {
                event_id = 9000 + _events.Count,
                device_id = d.Id,
                datetime = Stamp(_now.AddMinutes(-minutesAgo)),
                message,
                type,
                username = (string?)null,
                severity,
                hostname = d.Name,
                sysName = d.Name,
            });
        }

        Event("core-sw-02", 2, "sensor", "Inlet temperature over limit: 71 °C (limit 70 °C)", 5);
        Event("edge-fw-01", 6, "interface", "ix1: input errors rising (37/s)", 4);
        Event("dc2-core-02", 9, "reboot", "Device rebooted after 214 days up", 4);
        Event("ap-lobby-3", 14, "wireless", "Average client signal -79 dBm", 4);
        Event("bristol-rtr-01", 18, "system", "Device status changed to Down from icmp check.", 5);
        Event("bristol-sw-01", 18, "system", "Device status changed to Down from icmp check.", 5);
        Event("core-sw-01", 23, "interface", "Te1/0/3: ifOperStatus changed to up", 1);
        Event("dc-pdu-2", 32, "sensor", "Bank 1 current 13.4 A (warning 12.8 A)", 4);
        Event("dist-sw-02", 47, "interface", "Gi1/0/24: utilisation over 80% inbound", 4);
        Event("edge-rtr-01", 63, "bgp", "BGP session with 203.0.113.254 established", 1);
        Event("cam-gate-1", 74, "system", "Device status changed to Down from icmp check.", 5);
        Event("esx-02", 128, "storage", "datastore1 at 87% used", 4);
        Event("leeds-sw-01", 190, "discovery", "Discovered 2 new neighbours over LLDP", 2);
        Event("nas-01", 260, "system", "Firmware updated to DSM 7.2.2", 2);
        Event("core-sw-01", 410, "config", "Configuration saved by netops", 2);
    }

    // ------------------------------------------------------------ JSON shapes

    private object DeviceJson(ExampleDevice d)
    {
        var location = _locations[d.Location];
        return new
        {
            device_id = d.Id,
            hostname = d.Name + ".example.net",
            sysName = d.Name,
            display = d.Name,
            ip = d.Ip,
            os = d.Os,
            hardware = d.Hardware,
            version = d.Version,
            location = d.Location,
            location_id = location.Id,
            lat = location.Lat,
            lng = location.Lng,
            type = d.Type,
            purpose = string.Empty,
            notes = string.Empty,
            sysDescr = $"{d.Hardware}, {d.Version}",
            sysContact = "netops@example.net",
            sysObjectID = ".1.3.6.1.4.1.9.1.2494",
            serial = d.Serial,
            inserted = Stamp(_now.AddDays(-400 + d.Id)),
            last_discovered = Stamp(_now.AddHours(-3).AddMinutes(-d.Id)),
            last_polled = Stamp(_now.AddMinutes(-1)),
            last_polled_timetaken = 4.2 + d.Id % 5,
            status = d.Up ? 1 : 0,
            disabled = d.Disabled ? 1 : 0,
            ignore = 0,
            uptime = d.Up ? 18_489_600L + d.Id * 86_400L : 0L,
            poller_group = 0,
        };
    }

    private static object PortJson(ExamplePort p) => new
    {
        port_id = p.Id,
        device_id = p.DeviceId,
        ifIndex = p.Index,
        ifName = p.Name,
        ifDescr = p.Name,
        ifAlias = p.Alias,
        ifType = "ethernetCsmacd",
        ifSpeed = p.Speed,
        ifDuplex = "fullDuplex",
        ifMtu = 9216,
        ifPhysAddress = "02:00:5e:" + (p.Id / 256 % 256).ToString("x2", CultureInfo.InvariantCulture) + ":" + (p.Id % 256).ToString("x2", CultureInfo.InvariantCulture) + ":01",
        ifOperStatus = p.Up ? "up" : "down",
        ifAdminStatus = "up",
        ifInOctets_rate = p.Speed / 8.0 * p.Load,
        ifOutOctets_rate = p.Speed / 8.0 * p.Load * 0.42,
        ifInErrors_rate = (double)p.Errors,
        ifOutErrors_rate = 0.0,
        ifInErrors_delta = p.Errors * 300L,
        ifOutErrors_delta = 0L,
        ifInUcastPkts_rate = p.Speed / 8.0 * p.Load / 900,
        ifOutUcastPkts_rate = p.Speed / 8.0 * p.Load / 1400,
        ignore = 0,
        disabled = 0,
        deleted = 0,
    };

    // ------------------------------------------------------------ helpers

    private ExampleDevice Device(string name) => _devices.First(d => d.Name == name);

    private ExamplePort? PortOf(ExampleDevice device, string name) => _ports.FirstOrDefault(p => p.DeviceId == device.Id && p.Name == name);

    private static int Prop(object o, string name) => Convert.ToInt32(o.GetType().GetProperty(name)!.GetValue(o), CultureInfo.InvariantCulture);

    private static int IdOf(object o) => Prop(o, "id");

    private static int StateOf(object o) => Prop(o, "state");

    private static int DeviceIdOf(object o) => Prop(o, "device_id");

    private static int LocalDeviceOf(object o) => Prop(o, "local_device_id");

    private static string? HostOf(object o) => o.GetType().GetProperty("hostname")!.GetValue(o) as string;

    private static string Stamp(DateTime utc) => utc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static int Seed(string text)
    {
        var hash = 17;
        foreach (var c in text)
        {
            hash = unchecked(hash * 31 + c);
        }

        return hash;
    }

    private static (string Path, IReadOnlyDictionary<string, string> Query) Split(string target)
    {
        var q = target.IndexOf('?', StringComparison.Ordinal);
        var path = q < 0 ? target : target[..q];
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (q >= 0)
        {
            foreach (var pair in target[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Split('=', 2);
                query[Uri.UnescapeDataString(kv[0])] = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : string.Empty;
            }
        }

        return (path, query);
    }

    private static DemoResponse Collection(string property, IEnumerable<object> items)
        => Ok(new Dictionary<string, object?> { [property] = items.ToList(), ["count"] = items.Count() });

    private static DemoResponse Ok(Dictionary<string, object?> fields, bool unhandled = false)
    {
        var envelope = new Dictionary<string, object?> { ["status"] = "ok" };
        foreach (var (key, value) in fields)
        {
            envelope[key] = value;
        }

        return new DemoResponse(JsonSerializer.Serialize(envelope, Json), Unhandled: unhandled);
    }

    private static DemoResponse Fail(int status, string message)
        => new(JsonSerializer.Serialize(new { status = "error", message }, Json), Status: status);

    private static DemoResponse Svg(string svg, bool legend = true) => new(legend ? svg : DemoGraphs.WithoutLegend(svg), "image/svg+xml");

    private sealed record ExampleDevice(int Id, string Name, string Ip, string Os, string Hardware, string Version, string Type, string Location, double Load, double Cpu, double Memory, bool Up, bool Disabled, string Serial);

    private sealed record ExamplePort(int Id, int DeviceId, int Index, string Name, string Alias, long Speed, bool Up, double Load, int Errors);
}
