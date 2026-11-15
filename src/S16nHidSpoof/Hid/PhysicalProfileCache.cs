using System.Text.Json;
using System.Text.Json.Serialization;
using HIDMaestro;
using S16nHidSpoof.Config;
using S16nHidSpoof.Ui;

namespace S16nHidSpoof.Hid;

/// <summary>
/// Persist the physical device's extracted HIDMaestro profile (full descriptor + metadata)
/// so virtual devices match the real controller when it is unplugged.
/// </summary>
internal static class PhysicalProfileCache
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string CachePath { get; } = Path.Combine(AppPaths.DataDirectory, "physical-profile.json");
    public static string HmProfilePath { get; } = Path.Combine(AppPaths.DataDirectory, "physical-profile.hm.json");

    public static bool ExistsFor(PhysicalDeviceOptions physical)
    {
        if (!File.Exists(CachePath) || !File.Exists(HmProfilePath))
            return false;
        try
        {
            var dto = JsonSerializer.Deserialize<CachedMeta>(File.ReadAllText(CachePath), JsonOptions);
            return dto is not null
                   && IdsMatch(dto.Vid, physical.Vid)
                   && IdsMatch(dto.Pid, physical.Pid);
        }
        catch
        {
            return false;
        }
    }

    public static void Save(PhysicalDeviceOptions physical, HMProfile profile, HMHidDeviceInfo? sourceInfo = null)
    {
        var hex = profile.DescriptorHex;
        if (string.IsNullOrWhiteSpace(hex))
        {
            Console.Error.WriteLine("[spoof] refuse to cache profile with empty descriptor");
            return;
        }

        Directory.CreateDirectory(AppPaths.DataDirectory);

        // Full HIDMaestro JSON — round-trips through LoadProfilesFromDirectory for fidelity.
        var hmJson = HMDeviceExtractor.ToJson(profile);
        File.WriteAllText(HmProfilePath, hmJson);

        var inputLen = profile.InputReportSize;
        if (inputLen <= 0 && sourceInfo is not null)
            inputLen = (int)sourceInfo.InputReportByteLength;

        var dto = new CachedMeta
        {
            Vid = physical.Vid,
            Pid = physical.Pid,
            UsagePage = sourceInfo?.TopLevelUsagePage ?? physical.UsagePage,
            Usage = sourceInfo?.TopLevelUsage ?? physical.Usage,
            InputReportByteLength = inputLen,
            DescriptorBytes = hex.Length / 2,
            ProductString = profile.ProductString,
            Type = string.IsNullOrWhiteSpace(profile.Type) ? null : profile.Type,
            SavedUtc = DateTime.UtcNow
        };
        File.WriteAllText(CachePath, JsonSerializer.Serialize(dto, JsonOptions));
        Console.WriteLine(
            $"[spoof] cached physical descriptor ({dto.DescriptorBytes} bytes, inputReport={dto.InputReportByteLength}) → {HmProfilePath}");
    }

    public static bool TryLoad(PhysicalDeviceOptions physical, out HMProfile profile)
    {
        profile = null!;
        if (!File.Exists(HmProfilePath) || !File.Exists(CachePath))
            return false;

        try
        {
            var dto = JsonSerializer.Deserialize<CachedMeta>(File.ReadAllText(CachePath), JsonOptions);
            if (dto is null)
                return false;

            if (!IdsMatch(dto.Vid, physical.Vid) || !IdsMatch(dto.Pid, physical.Pid))
            {
                Console.WriteLine(
                    $"[spoof] ignoring cached profile {dto.Vid}:{dto.Pid} (config is {physical.Vid}:{physical.Pid})");
                return false;
            }

            // Load via HIDMaestro's own profile JSON so descriptor/layout match Extract().
            var tempDir = Path.Combine(Path.GetTempPath(), "s16n-hid-spoof-profile-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var tempFile = Path.Combine(tempDir, "physical.hm.json");
                File.Copy(HmProfilePath, tempFile, overwrite: true);

                using var ctx = new HMContext();
                var loaded = ctx.LoadProfilesFromDirectory(tempDir);
                if (loaded <= 0 || ctx.AllProfiles.Count == 0)
                {
                    Console.Error.WriteLine("[spoof] HIDMaestro failed to load cached profile JSON");
                    return FallbackRebuildFromMeta(dto, physical, out profile);
                }

                profile = ctx.AllProfiles[0];
                if (dto.InputReportByteLength > 0 && profile.InputReportSize <= 0)
                {
                    profile = new HMProfileBuilder()
                        .FromProfile(profile)
                        .InputReportSize(dto.InputReportByteLength)
                        .Build();
                }

                profile = HMDeviceExtractor.ResolveDeviceModel(profile);
                Console.WriteLine(
                    $"[spoof] loaded cached physical profile descriptor={(profile.DescriptorHex ?? "").Length / 2} bytes " +
                    $"inputReportSize={profile.InputReportSize} buttons={profile.ButtonCount} axes={profile.AxisCount}");
                return true;
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); }
                catch { /* ignore */ }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[spoof] failed to load profile cache: {ex.Message}");
            return false;
        }
    }

    private static bool FallbackRebuildFromMeta(CachedMeta dto, PhysicalDeviceOptions physical, out HMProfile profile)
    {
        profile = null!;
        if (string.IsNullOrWhiteSpace(File.ReadAllText(HmProfilePath)))
            return false;

        // Last resort: pull descriptor field from the HM JSON manually.
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(HmProfilePath));
            if (!doc.RootElement.TryGetProperty("descriptor", out var descEl))
                return false;
            var hex = descEl.GetString();
            if (string.IsNullOrWhiteSpace(hex))
                return false;

            var builder = new HMProfileBuilder()
                .Id("s16n-physical-cache")
                .DescriptorHex(hex)
                .Vid(physical.VendorId)
                .Pid(physical.ProductId);

            if (dto.InputReportByteLength > 0)
                builder.InputReportSize(dto.InputReportByteLength);
            if (!string.IsNullOrWhiteSpace(dto.ProductString))
                builder.ProductString(dto.ProductString!);
            if (!string.IsNullOrWhiteSpace(dto.Type))
                builder.Type(dto.Type!);

            profile = HMDeviceExtractor.ResolveDeviceModel(builder.Build());
            Console.WriteLine(
                $"[spoof] rebuilt cached profile from descriptor field ({hex.Length / 2} bytes)");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IdsMatch(string? a, string b)
    {
        try
        {
            return PhysicalDeviceOptions.ParseId(a ?? "", "id") == PhysicalDeviceOptions.ParseId(b, "id");
        }
        catch
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class CachedMeta
    {
        public string Vid { get; set; } = "";
        public string Pid { get; set; } = "";
        public ushort? UsagePage { get; set; }
        public ushort? Usage { get; set; }
        public int InputReportByteLength { get; set; }
        public int DescriptorBytes { get; set; }
        public string? ProductString { get; set; }
        public string? Type { get; set; }
        public DateTime? SavedUtc { get; set; }
    }
}
