namespace Blitztext.Core.Text;

/// <summary>Website addresses for "Modus je App": what people type, what browsers report.</summary>
public static class WebsiteHost
{
    /// <summary>
    /// "https://www.Mail.google.com/mail/u/0" or "mail.google.com" -> "mail.google.com".
    /// Null for anything that is not a host name.
    /// </summary>
    public static string? Normalized(string input)
    {
        var trimmed = input.Trim().ToLowerInvariant();
        if (trimmed.Length == 0 || trimmed.Contains(' ')) return null;
        var withScheme = trimmed.Contains("://", StringComparison.Ordinal) ? trimmed : "https://" + trimmed;
        if (!Uri.TryCreate(withScheme, UriKind.Absolute, out var uri)) return null;
        var host = uri.Host;
        if (!host.Contains('.') || host.StartsWith('.') || host.EndsWith('.')) return null;
        if (host.StartsWith("www.", StringComparison.Ordinal))
        {
            host = host[4..];
        }
        return host;
    }

    /// <summary>A rule for "google.com" also covers "mail.google.com", not "notgoogle.com".</summary>
    public static bool Matches(string host, string rule)
    {
        host = host.ToLowerInvariant();
        return host == rule || host.EndsWith("." + rule, StringComparison.Ordinal);
    }

    /// <summary>The host of an address as a browser shows it ("mail.google.com/mail/u/0", "https://…").</summary>
    public static string? HostOfAddress(string address)
    {
        var trimmed = address.Trim();
        if (trimmed.Length == 0 || trimmed.Contains(' ')) return null;
        var withScheme = trimmed.Contains("://", StringComparison.Ordinal) ? trimmed : "https://" + trimmed;
        if (!Uri.TryCreate(withScheme, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https")) return null;
        var host = uri.Host.ToLowerInvariant();
        return host.Contains('.') ? host : null;
    }
}
