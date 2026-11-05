using System.Globalization;
using System.Text.RegularExpressions;

namespace Tapedeck.Models;

public static partial class Formatting
{
    [GeneratedRegex(@"^\d{1,2}(:\d{2}){1,2}$")]
    public static partial Regex DurationRegex();

    public static double ParseDuration(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !DurationRegex().IsMatch(text.Trim())) return 0;
        double total = 0;
        foreach (var part in text.Trim().Split(':'))
            total = total * 60 + int.Parse(part, CultureInfo.InvariantCulture);
        return total;
    }

    public static string Time(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) seconds = 0;
        var t = TimeSpan.FromSeconds(Math.Floor(seconds));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : t.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Rewrites a googleusercontent / ggpht thumbnail URL to request a specific size.
    /// i.ytimg.com URLs are returned unchanged.
    /// </summary>
    public static string? SizedThumbnail(string? url, int size)
    {
        if (string.IsNullOrEmpty(url)) return url;
        if (url.StartsWith("//", StringComparison.Ordinal)) url = "https:" + url;
        if (url.Contains("googleusercontent.com") || url.Contains("ggpht.com"))
        {
            var m = SizeSuffixRegex().Match(url);
            if (m.Success) return url[..m.Index] + $"=w{size}-h{size}-l90-rj";
        }
        return url;
    }

    [GeneratedRegex(@"=w\d+-h\d+.*$")]
    private static partial Regex SizeSuffixRegex();
}
