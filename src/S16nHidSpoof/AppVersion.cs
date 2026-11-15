using System.Reflection;

namespace S16nHidSpoof;

internal static class AppVersion
{
    /// <summary>Build stamp: YYYYMMDD-&lt;git short hash&gt; (from InformationalVersion).</summary>
    public static string Current { get; } = Resolve();

    private static string Resolve()
    {
        var asm = typeof(AppVersion).Assembly;
        string? info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
            return info.Trim();

        return asm.GetName().Version?.ToString() ?? "unknown";
    }
}
