using System.Text.RegularExpressions;

namespace Tapedeck.Services.Api;

/// <summary>
/// Shape checks for values that come from web content and end up in request headers or URLs.
/// Anything that doesn't match is dropped (null) rather than sanitised.
/// </summary>
public static partial class Validate
{
    [GeneratedRegex(@"^[A-Za-z0-9_-]{11}$")]
    private static partial Regex VideoIdRegex();

    [GeneratedRegex(@"^\d+\.\d{8}\.\d{2}\.\d{2}$")]
    private static partial Regex ClientVersionRegex();

    [GeneratedRegex(@"^[A-Za-z0-9%_=-]{8,1024}$")]
    private static partial Regex VisitorDataRegex();

    [GeneratedRegex(@"^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})?$")]
    private static partial Regex LocaleRegex();

    public static string? VideoId(string? v) => v is not null && VideoIdRegex().IsMatch(v) ? v : null;

    public static string? ClientVersion(string? v) => v is not null && ClientVersionRegex().IsMatch(v) ? v : null;

    public static string? VisitorData(string? v) => v is not null && VisitorDataRegex().IsMatch(v) ? v : null;

    public static string? Locale(string? v) => v is not null && LocaleRegex().IsMatch(v) ? v : null;

    /// <summary>True when a value is safe to place in an HTTP header (no control characters).</summary>
    public static bool IsHeaderSafe(string v)
    {
        foreach (var c in v)
            if (c < 0x20 || c == 0x7F) return false;
        return true;
    }
}
