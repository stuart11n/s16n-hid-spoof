using System.Net;

namespace S16nHidSpoof.Ui;

internal static class HttpSecurity
{
    public static void ValidateListenUrl(string url, bool allowNonLocal)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException($"Http.Url is not a valid http(s) URL: '{url}'.");
        }

        if (allowNonLocal)
            return;

        if (IsLoopbackHost(uri.Host))
            return;

        throw new InvalidOperationException(
            $"Http.Url must be loopback (127.0.0.1 / localhost / ::1). Got host '{uri.Host}'. " +
            "Set Http.AllowNonLocal=true to override (exposes the control API on the network).");
    }

    public static bool IsLoopbackConnection(ConnectionInfo connection)
    {
        var addr = connection.RemoteIpAddress;
        if (addr is null)
            return false;
        if (IPAddress.IsLoopback(addr))
            return true;
        // Kestrel sometimes presents IPv4-mapped IPv6.
        if (addr.IsIPv4MappedToIPv6)
            return IPAddress.IsLoopback(addr.MapToIPv4());
        return false;
    }

    private static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
        || host.Equals("::1", StringComparison.OrdinalIgnoreCase)
        || host.Equals("[::1]", StringComparison.OrdinalIgnoreCase);
}
