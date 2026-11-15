using System.Reflection;

namespace S16nHidSpoof;

internal static class AppVersion
{
    public static string Display { get; } = Resolve();

    private static string Resolve()
    {
        var info = typeof(AppVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(info))
        {
            var v = typeof(AppVersion).Assembly.GetName().Version;
            return v is null ? "dev" : $"{v.Major}.{v.Minor}.{v.Build}";
        }

        // Drop CI build metadata noise; keep a short +githash when present.
        var cut = info.IndexOfAny(['+', '@']);
        if (cut < 0)
            return info;

        var core = info[..cut];
        if (info[cut] == '+' && info.Length > cut + 1)
        {
            var hash = info[(cut + 1)..];
            if (hash.Length > 7)
                hash = hash[..7];
            return $"{core}+{hash}";
        }

        return core;
    }
}
