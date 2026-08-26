using System.Net;

namespace DeepSeekHarnessDesktop;

public sealed record LaunchOptions(Uri? StartUri)
{
    public static LaunchOptions Parse(IReadOnlyList<string> arguments)
    {
        string? requestedUrl = null;

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argument.StartsWith("--url=", StringComparison.OrdinalIgnoreCase))
            {
                requestedUrl = argument["--url=".Length..];
                break;
            }

            if (string.Equals(argument, "--url", StringComparison.OrdinalIgnoreCase)
                && index + 1 < arguments.Count)
            {
                requestedUrl = arguments[index + 1];
                break;
            }
        }

        requestedUrl ??= Environment.GetEnvironmentVariable("DSH_DESKTOP_URL");
        if (string.IsNullOrWhiteSpace(requestedUrl))
        {
            return new LaunchOptions((Uri?)null);
        }

        if (!Uri.TryCreate(requestedUrl, UriKind.Absolute, out var uri)
            || !IsTrustedLoopbackUri(uri))
        {
            throw new ArgumentException(
                "--url 必须是指向本机服务的 http://localhost、http://127.0.0.1 或 http://[::1] 地址。",
                nameof(arguments));
        }

        return new LaunchOptions(uri);
    }

    private static bool IsTrustedLoopbackUri(Uri uri)
    {
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address);
    }
}
