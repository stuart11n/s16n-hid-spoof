using System.Reflection;

namespace S16nHidSpoof.Ui;

/// <summary>Loads branded icon/logo from embedded resources, with loose Assets\ as fallback.</summary>
internal static class BrandAssets
{
    public static Icon? LoadAppIcon()
    {
        try
        {
            using var stream = Open("Assets.app.ico");
            if (stream is not null)
                return new Icon(stream);

            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(path))
                return new Icon(path);
        }
        catch
        {
            /* optional */
        }

        return null;
    }

    public static Image? LoadLogo()
    {
        try
        {
            using var stream = Open("Assets.logo.png");
            if (stream is not null)
            {
                using var temp = Image.FromStream(stream);
                return new Bitmap(temp);
            }

            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "logo.png");
            if (!File.Exists(path))
                return null;
            return Image.FromFile(path);
        }
        catch
        {
            return null;
        }
    }

    private static Stream? Open(string dottedRelative)
    {
        var asm = Assembly.GetExecutingAssembly();
        // SDK names EmbeddedResource as RootNamespace.<folders.with.dots>.<file>
        var preferred = $"S16nHidSpoof.{dottedRelative}";
        var stream = asm.GetManifestResourceStream(preferred);
        if (stream is not null)
            return stream;

        var suffix = "." + dottedRelative;
        var match = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.Equals(preferred, StringComparison.OrdinalIgnoreCase)
                                 || n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        return match is null ? null : asm.GetManifestResourceStream(match);
    }
}
