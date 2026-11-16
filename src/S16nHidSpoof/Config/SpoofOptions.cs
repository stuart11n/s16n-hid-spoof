using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace S16nHidSpoof.Config;

public sealed class SpoofOptions
{
    public const string SectionName = "Spoof";
    public const int DeviceCount = 3;

    public HttpOptions Http { get; set; } = new();
    public List<DeviceOptions> Devices { get; set; } = [];

    /// <summary>
    /// If the JSON still uses the legacy single-device shape (top-level Physical /
    /// VirtualDevices), rewrite it as Devices[3] in place.
    /// </summary>
    public static void MigrateLegacyFileIfNeeded(string path)
    {
        var text = File.ReadAllText(path);
        var root = JsonNode.Parse(text) as JsonObject;
        if (root is null)
            return;
        if (root["Devices"] is not null || root["Physical"] is null)
            return;

        var physical = root["Physical"]?.DeepClone();
        var virtuals = root["VirtualDevices"]?.DeepClone();
        var active = root["ActiveSlot"]?.DeepClone() ?? JsonValue.Create(0);
        var forward = root["ForwardOutputToPhysical"]?.DeepClone() ?? JsonValue.Create(true);

        root.Remove("Physical");
        root.Remove("VirtualDevices");
        root.Remove("ActiveSlot");
        root.Remove("ForwardOutputToPhysical");

        root["Devices"] = new JsonArray
        {
            new JsonObject
            {
                ["Id"] = "yoke",
                ["Name"] = "Yoke",
                ["Enabled"] = true,
                ["Physical"] = physical,
                ["VirtualDevices"] = virtuals,
                ["ActiveSlot"] = active,
                ["ForwardOutputToPhysical"] = forward
            },
            PlaceholderDevice("stick", "Stick"),
            PlaceholderDevice("rudder", "Rudder")
        };

        var pretty = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, pretty + Environment.NewLine);
        Console.WriteLine($"[config] migrated legacy single-device settings → Devices[3] in {path}");
    }

    private static JsonObject PlaceholderDevice(string id, string name) => new()
    {
        ["Id"] = id,
        ["Name"] = name,
        ["Enabled"] = false,
        ["Physical"] = new JsonObject
        {
            ["Vid"] = "0x0000",
            ["Pid"] = "0x0000",
            ["UsagePage"] = null,
            ["Usage"] = null,
            ["DevicePathContains"] = null
        },
        ["VirtualDevices"] = new JsonArray
        {
            DisabledSlot($"{id}-0", "0x16D0", "0x0A01", $"{name} slot 0"),
            DisabledSlot($"{id}-1", "0x16D0", "0x0A02", $"{name} slot 1"),
            DisabledSlot($"{id}-2", "0x16D0", "0x0A03", $"{name} slot 2"),
            DisabledSlot($"{id}-3", "0x16D0", "0x0A04", $"{name} slot 3")
        },
        ["ActiveSlot"] = 0,
        ["ForwardOutputToPhysical"] = true
    };

    private static JsonObject DisabledSlot(string id, string vid, string pid, string product) => new()
    {
        ["Id"] = id,
        ["Enabled"] = false,
        ["Vid"] = vid,
        ["Pid"] = pid,
        ["ProductString"] = product
    };
}

public sealed class HttpOptions
{
    public string Url { get; set; } = "http://127.0.0.1:8780";
    /// <summary>When false (default), Http.Url must be a loopback address.</summary>
    public bool AllowNonLocal { get; set; }
}

public sealed class DeviceOptions
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public PhysicalDeviceOptions Physical { get; set; } = new();
    public List<VirtualDeviceOptions> VirtualDevices { get; set; } = [];
    public int ActiveSlot { get; set; }
    public bool ForwardOutputToPhysical { get; set; } = true;

    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Name) ? Name
        : !string.IsNullOrWhiteSpace(Id) ? Id
        : "Device";
}

public sealed class PhysicalDeviceOptions
{
    public string Vid { get; set; } = "0x0000";
    public string Pid { get; set; } = "0x0000";
    public ushort? UsagePage { get; set; }
    public ushort? Usage { get; set; }
    /// <summary>
    /// Optional substring match on the HID device path so multiple units that share
    /// the same VID:PID can be pinned to different device tabs.
    /// </summary>
    public string? DevicePathContains { get; set; }

    public ushort VendorId => ParseId(Vid, nameof(Vid));
    public ushort ProductId => ParseId(Pid, nameof(Pid));

    internal static ushort ParseId(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{name} is required.");

        var text = value.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            text = text[2..];

        if (!ushort.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id))
            throw new InvalidOperationException($"{name} must be a 16-bit hex value (got '{value}').");

        return id;
    }
}

public sealed class VirtualDeviceOptions
{
    public string Id { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string Vid { get; set; } = "0x0000";
    public string Pid { get; set; } = "0x0000";
    public string? ProductString { get; set; }

    public ushort VendorId => PhysicalDeviceOptions.ParseId(Vid, nameof(Vid));
    public ushort ProductId => PhysicalDeviceOptions.ParseId(Pid, nameof(Pid));
}
