using System.Text.Json;
using System.Text.Json.Serialization;
using HIDMaestro;
using S16nHidSpoof.Config;
using S16nHidSpoof.Ui;

namespace S16nHidSpoof.Hid;

/// <summary>
/// Persist a physical device's extracted HIDMaestro profile (full descriptor + metadata)
/// so virtual devices match the real controller when it is unplugged.
/// One cache pair per device id: physical-profile-{id}.json / .hm.json.
/// </summary>
internal static class PhysicalProfileCache
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string MetaPath(string deviceId) =>
        Path.Combine(AppPaths.DataDirectory, $"physical-profile-{Sanitize(deviceId)}.json");

    public static string HmProfilePath(string deviceId) =>
        Path.Combine(AppPaths.DataDirectory, $"physical-profile-{Sanitize(deviceId)}.hm.json");

    public static bool ExistsFor(string deviceId, PhysicalDeviceOptions physical)
    {
        var metaPath = MetaPath(deviceId);
        var hmPath = HmProfilePath(deviceId);
        if (!File.Exists(metaPath) || !File.Exists(hmPath))
        {
            // Legacy single-device cache (pre Devices[3]).
            if (deviceId is "yoke" or "0"
                && File.Exists(Path.Combine(AppPaths.DataDirectory, "physical-profile.json"))
                && File.Exists(Path.Combine(AppPaths.DataDirectory, "physical-profile.hm.json")))
            {
                TryAdoptLegacy(deviceId, physical);
            }
        }

        metaPath = MetaPath(deviceId);
        hmPath = HmProfilePath(deviceId);
        if (!File.Exists(metaPath) || !File.Exists(hmPath))
            return false;
        try
        {
            var dto = JsonSerializer.Deserialize<CachedMeta>(File.ReadAllText(metaPath), JsonOptions);
            return dto is not null
                   && IdsMatch(dto.Vid, physical.Vid)
                   && IdsMatch(dto.Pid, physical.Pid);
        }
        catch
        {
            return false;
        }
    }

    public static void Save(string deviceId, PhysicalDeviceOptions physical, HMProfile profile, HMHidDeviceInfo? sourceInfo = null)
    {
        var hex = profile.DescriptorHex;
        if (string.IsNullOrWhiteSpace(hex))
        {
            Console.Error.WriteLine($"[spoof:{deviceId}] refuse to cache profile with empty descriptor");
            return;
        }

        Directory.CreateDirectory(AppPaths.DataDirectory);

        var hmPath = HmProfilePath(deviceId);
        var metaPath = MetaPath(deviceId);
        var hmJson = HMDeviceExtractor.ToJson(profile);
        File.WriteAllText(hmPath, hmJson);

        var inputLen = profile.InputReportSize;
        if (inputLen <= 0 && sourceInfo is not null)
            inputLen = (int)sourceInfo.InputReportByteLength;

        var dto = new CachedMeta
        {
            DeviceId = deviceId,
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
        File.WriteAllText(metaPath, JsonSerializer.Serialize(dto, JsonOptions));
        Console.WriteLine(
            $"[spoof:{deviceId}] cached physical descriptor ({dto.DescriptorBytes} bytes, inputReport={dto.InputReportByteLength}) → {hmPath}");
    }

    public static bool TryLoad(string deviceId, PhysicalDeviceOptions physical, out HMProfile profile)
    {
        profile = null!;
        ExistsFor(deviceId, physical); // may adopt legacy

        var hmPath = HmProfilePath(deviceId);
        var metaPath = MetaPath(deviceId);
        if (!File.Exists(hmPath) || !File.Exists(metaPath))
            return false;

        try
        {
            var dto = JsonSerializer.Deserialize<CachedMeta>(File.ReadAllText(metaPath), JsonOptions);
            if (dto is null)
                return false;

            if (!IdsMatch(dto.Vid, physical.Vid) || !IdsMatch(dto.Pid, physical.Pid))
            {
                Console.WriteLine(
                    $"[spoof:{deviceId}] ignoring cached profile {dto.Vid}:{dto.Pid} (config is {physical.Vid}:{physical.Pid})");
                return false;
            }

            var tempDir = Path.Combine(Path.GetTempPath(), "s16n-hid-spoof-profile-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var tempFile = Path.Combine(tempDir, "physical.hm.json");
                File.Copy(hmPath, tempFile, overwrite: true);

                using var ctx = new HMContext();
                var loaded = ctx.LoadProfilesFromDirectory(tempDir);
                if (loaded <= 0 || ctx.AllProfiles.Count == 0)
                {
                    Console.Error.WriteLine($"[spoof:{deviceId}] HIDMaestro failed to load cached profile JSON");
                    return FallbackRebuildFromMeta(deviceId, dto, physical, out profile);
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
                    $"[spoof:{deviceId}] loaded cached physical profile descriptor={(profile.DescriptorHex ?? "").Length / 2} bytes " +
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
            Console.Error.WriteLine($"[spoof:{deviceId}] failed to load profile cache: {ex.Message}");
            return false;
        }
    }

    private static void TryAdoptLegacy(string deviceId, PhysicalDeviceOptions physical)
    {
        try
        {
            var legacyMeta = Path.Combine(AppPaths.DataDirectory, "physical-profile.json");
            var legacyHm = Path.Combine(AppPaths.DataDirectory, "physical-profile.hm.json");
            var dto = JsonSerializer.Deserialize<CachedMeta>(File.ReadAllText(legacyMeta), JsonOptions);
            if (dto is null || !IdsMatch(dto.Vid, physical.Vid) || !IdsMatch(dto.Pid, physical.Pid))
                return;

            File.Copy(legacyMeta, MetaPath(deviceId), overwrite: false);
            File.Copy(legacyHm, HmProfilePath(deviceId), overwrite: false);
            Console.WriteLine($"[spoof:{deviceId}] adopted legacy physical-profile cache");
        }
        catch
        {
            /* best effort */
        }
    }

    private static bool FallbackRebuildFromMeta(
        string deviceId, CachedMeta dto, PhysicalDeviceOptions physical, out HMProfile profile)
    {
        profile = null!;
        var hmPath = HmProfilePath(deviceId);
        if (string.IsNullOrWhiteSpace(File.ReadAllText(hmPath)))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(hmPath));
            if (!doc.RootElement.TryGetProperty("descriptor", out var descEl))
                return false;
            var hex = descEl.GetString();
            if (string.IsNullOrWhiteSpace(hex))
                return false;

            var builder = new HMProfileBuilder()
                .Id($"s16n-physical-cache-{Sanitize(deviceId)}")
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
                $"[spoof:{deviceId}] rebuilt cached profile from descriptor field ({hex.Length / 2} bytes)");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string Sanitize(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return "device";
        var chars = deviceId.Trim().Select(ch =>
            char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_').ToArray();
        return new string(chars);
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
        public string? DeviceId { get; set; }
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
