using System.Globalization;

namespace S16nHidSpoof.Config;

public sealed class SpoofOptions
{
    public const string SectionName = "Spoof";

    public HttpOptions Http { get; set; } = new();
    public PhysicalDeviceOptions Physical { get; set; } = new();
    public List<VirtualDeviceOptions> VirtualDevices { get; set; } = [];
    public int ActiveSlot { get; set; }
    public bool ForwardOutputToPhysical { get; set; } = true;
}

public sealed class HttpOptions
{
    public string Url { get; set; } = "http://127.0.0.1:8780";
    /// <summary>When false (default), Http.Url must be a loopback address.</summary>
    public bool AllowNonLocal { get; set; }
}

public sealed class PhysicalDeviceOptions
{
    public string Vid { get; set; } = "0x0000";
    public string Pid { get; set; } = "0x0000";
    public ushort? UsagePage { get; set; }
    public ushort? Usage { get; set; }

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
