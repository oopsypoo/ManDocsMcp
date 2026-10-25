using System.ComponentModel;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;

[McpServerToolType]
public static class UbuntuOnlineManPageTools
{
    private const string SiteRoot = "https://manpages.ubuntu.com";
    private const int MaximumQueryLength = 120;
    private const int MaximumPageBytes = 64 * 1024;
    private const int MaximumSearchResponseBytes = 64 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly Regex ValidRelease = new(@"^[a-z][a-z0-9-]{1,31}$", RegexOptions.CultureInvariant);
    private static readonly Regex ValidSection = new(@"^[1-9][a-zA-Z0-9]{0,7}$", RegexOptions.CultureInvariant);
    private static readonly Regex ValidPageName = new(@"^[a-zA-Z0-9_][a-zA-Z0-9_.:+-]{0,127}$", RegexOptions.CultureInvariant);

    [McpServerTool(Name = "search_ubuntu_manpages", ReadOnly = true, Destructive = false, OpenWorld = true),
     Description("Searches the official Ubuntu Manpage Repository for manual pages in a selected Ubuntu release. Defaults to the local Ubuntu release when detected; results are online and may differ from installed local manual pages. When reporting a version shown in a manual, identify it as the version stated by that manual (often an upstream implementation version), not as the full Ubuntu package version or the locally installed version. Do not infer package versions from this tool.")]
    public static async Task<string> SearchUbuntuManpages(
        [Description("Words or command name to search for, such as 'file permissions' or 'chmod'.")]
        string query,
        [Description("Ubuntu release codename, such as 'resolute', 'noble', or 'jammy'. Omit to use the local Ubuntu release when detected.")]
        string? release = null,
        [Description("Maximum results to return (1-10). Defaults to 5.")]
        int maxResults = 5)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > MaximumQueryLength || query.Any(char.IsControl))
        {
            return $"Invalid query. Provide 1-{MaximumQueryLength} characters without control characters.";
        }

        if (maxResults is < 1 or > 10)
        {
            return "Invalid maxResults. Choose a value from 1 to 10.";
        }

        string? selectedRelease = ResolveRelease(release);
        if (selectedRelease is null)
        {
            return "Could not determine the local Ubuntu release. Supply a release codename explicitly, such as 'resolute', 'noble', or 'jammy'.";
        }

        if (!ValidRelease.IsMatch(selectedRelease))
        {
            return "Invalid Ubuntu release codename. Use letters, numbers, and hyphens, starting with a letter.";
        }

        var searchUri = new Uri($"{SiteRoot}/api/search?q={Uri.EscapeDataString(query)}&release={Uri.EscapeDataString(selectedRelease)}&limit={maxResults}&offset=0");
        try
        {
            using var timeout = new CancellationTokenSource(RequestTimeout);
            using HttpResponseMessage response = await Http.GetAsync(searchUri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return FormatHttpError("Ubuntu manpage search", response.StatusCode, selectedRelease);
            }

            (byte[] body, bool truncated) = await ReadBoundedAsync(await response.Content.ReadAsStreamAsync(timeout.Token), MaximumSearchResponseBytes, timeout.Token);
            if (truncated)
            {
                return "The Ubuntu Manpage Repository search response exceeded the 64 KiB safety limit.";
            }

            string json = Encoding.UTF8.GetString(body);
            string source = $"{SiteRoot}/search?q={Uri.EscapeDataString(query)}&release={Uri.EscapeDataString(selectedRelease)}";
            return $"Official Ubuntu manpage search\nRelease: {selectedRelease}\nSource: {source}\nVersion note: Any version quoted from a manual is the version stated by that manual; it is not necessarily the Ubuntu package version or the version installed locally. This search does not query package metadata.\n\nSearch results (JSON from the Ubuntu Manpage Repository):\n{FormatJson(json)}";
        }
        catch (OperationCanceledException)
        {
            return "The online Ubuntu manpage search timed out after 15 seconds.";
        }
        catch (HttpRequestException ex)
        {
            return $"Could not reach the official Ubuntu Manpage Repository. Details: {ex.Message}";
        }
        catch (JsonException)
        {
            return "The Ubuntu Manpage Repository returned a response that was not valid JSON.";
        }
    }

    [McpServerTool(Name = "get_ubuntu_manpage", ReadOnly = true, Destructive = false, OpenWorld = true),
     Description("Retrieves a plain-text manual page from the official Ubuntu Manpage Repository for a selected Ubuntu release. Defaults to the local Ubuntu release when detected. The response is limited to 64 KiB. Distinguish any version stated in the manual (often an upstream implementation version) from the full Ubuntu package version and from the version installed on this machine. This tool returns manual text only; it does not query package metadata, so do not present a manual's version as an Ubuntu package or installed version.")]
    public static async Task<string> GetUbuntuManpage(
        [Description("Exact manual page name, such as 'ls', 'chmod', or 'systemd.unit'. Do not include a path or section.")]
        string pageName,
        [Description("Manual section, such as '1', '5', or '3pm'.")]
        string section,
        [Description("Ubuntu release codename, such as 'resolute', 'noble', or 'jammy'. Omit to use the local Ubuntu release when detected.")]
        string? release = null)
    {
        if (string.IsNullOrWhiteSpace(pageName) || !ValidPageName.IsMatch(pageName))
        {
            return "Invalid page name. Use 1-128 letters, numbers, underscores, periods, colons, plus signs, or hyphens; do not include a path.";
        }

        if (string.IsNullOrWhiteSpace(section) || !ValidSection.IsMatch(section))
        {
            return "Invalid section. Use a section such as '1', '5', or '3pm'.";
        }

        string? selectedRelease = ResolveRelease(release);
        if (selectedRelease is null)
        {
            return "Could not determine the local Ubuntu release. Supply a release codename explicitly, such as 'resolute', 'noble', or 'jammy'.";
        }

        if (!ValidRelease.IsMatch(selectedRelease))
        {
            return "Invalid Ubuntu release codename. Use letters, numbers, and hyphens, starting with a letter.";
        }

        string sectionDirectory = $"man{section[0]}";
        string fileName = $"{Uri.EscapeDataString(pageName)}.{Uri.EscapeDataString(section)}.txt";
        var pageUri = new Uri($"{SiteRoot}/manpages/{Uri.EscapeDataString(selectedRelease)}/{sectionDirectory}/{fileName}");
        try
        {
            using var timeout = new CancellationTokenSource(RequestTimeout);
            using HttpResponseMessage response = await Http.GetAsync(pageUri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return $"No online Ubuntu manual page was found for {pageName}({section}) in release '{selectedRelease}'. Check the page name, section, and release codename.\nSource checked: {pageUri}";
            }

            if (!response.IsSuccessStatusCode)
            {
                return FormatHttpError("Ubuntu manpage retrieval", response.StatusCode, selectedRelease);
            }

            (byte[] body, bool truncated) = await ReadBoundedAsync(await response.Content.ReadAsStreamAsync(timeout.Token), MaximumPageBytes, timeout.Token);
            string text = Encoding.UTF8.GetString(body).TrimEnd();
            string note = truncated ? "\n\n[Response truncated at 64 KiB.]" : string.Empty;
            return $"Official Ubuntu manual page: {pageName}({section})\nRelease: {selectedRelease}\nSource: {pageUri}\nVersion note: Any version stated in the manual is the version stated by that manual (often an upstream implementation version), not necessarily the full Ubuntu package version or the version installed locally. This tool does not query package metadata.\n\n{text}{note}";
        }
        catch (OperationCanceledException)
        {
            return "Retrieving the online Ubuntu manpage timed out after 15 seconds.";
        }
        catch (HttpRequestException ex)
        {
            return $"Could not reach the official Ubuntu Manpage Repository. Details: {ex.Message}";
        }
    }

    private static string? ResolveRelease(string? requestedRelease)
    {
        if (!string.IsNullOrWhiteSpace(requestedRelease))
        {
            return requestedRelease.Trim().ToLowerInvariant();
        }

        string? codename = ReadOsReleaseValue("UBUNTU_CODENAME") ?? ReadOsReleaseValue("VERSION_CODENAME");
        return string.IsNullOrWhiteSpace(codename) ? null : codename.ToLowerInvariant();
    }

    private static string? ReadOsReleaseValue(string key)
    {
        try
        {
            foreach (string line in File.ReadLines("/etc/os-release"))
            {
                if (line.StartsWith(key + "=", StringComparison.Ordinal))
                {
                    string value = line[(key.Length + 1)..].Trim();
                    if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                    {
                        value = value[1..^1];
                    }
                    else if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
                    {
                        value = value[1..^1];
                    }

                    return value;
                }
            }
        }
        catch (IOException)
        {
            // The caller can provide an explicit release if os-release is unavailable.
        }
        catch (UnauthorizedAccessException)
        {
            // The caller can provide an explicit release if os-release is unavailable.
        }

        return null;
    }

    private static async Task<(byte[] Content, bool Truncated)> ReadBoundedAsync(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using (stream)
        using (var output = new MemoryStream(Math.Min(maximumBytes, 8192)))
        {
            var buffer = new byte[8192];
            while (output.Length <= maximumBytes)
            {
                int remainingPlusOne = maximumBytes + 1 - (int)output.Length;
                int read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remainingPlusOne)), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                output.Write(buffer, 0, read);
            }

            bool truncated = output.Length > maximumBytes;
            byte[] content = output.ToArray();
            if (truncated)
            {
                Array.Resize(ref content, maximumBytes);
            }

            return (content, truncated);
        }
    }

    private static string FormatJson(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = RequestTimeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ManDocsMcp/1.0");
        return client;
    }

    private static string FormatHttpError(string operation, HttpStatusCode statusCode, string release) =>
        $"{operation} failed with HTTP {(int)statusCode} ({statusCode}) for release '{release}'.";
}
