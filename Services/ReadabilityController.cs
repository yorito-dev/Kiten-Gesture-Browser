using System.Globalization;
using System.Text;
using System.Text.Json;
using TabBrowser.Models;

namespace TabBrowser.Services;

public sealed class ReadabilityController
{

    public bool IsChatGptUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !IsHttpOrHttps(uri))
        {
            return false;
        }

        return uri.Host.Equals("chatgpt.com", StringComparison.OrdinalIgnoreCase);

    }

    private static bool IsHttpOrHttps(Uri uri)
    {
        return uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    }

}
