using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;

[McpServerToolType]
public static class ManPageTools
{
    private const int MaximumQueryLength = 120;
    private const int MaximumQueryTerms = 8;
    private const int MaximumOutputCharacters = 64 * 1024;
    private const int MaximumErrorCharacters = 4 * 1024;
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(10);
    private static readonly Regex ValidSection = new(@"^[0-9][a-zA-Z0-9]{0,7}$", RegexOptions.CultureInvariant);
    private static readonly Regex ValidTerm = new(@"^[a-zA-Z0-9_.:+-]+$", RegexOptions.CultureInvariant);

    [McpServerTool(Name = "search_man", ReadOnly = true, Destructive = false, OpenWorld = false), Description("Searches the local system's manual-page names and short descriptions using apropos. Searches all terms; optionally restricts results to a manual section. Returns at most maxResults entries.")]
    public static async Task<string> SearchMan(
        [Description("Topic or keywords to search for, such as 'file permissions'. Use up to 8 simple words.")] string query,
        [Description("Optional manual section, such as '1', '5', or '3pm'. Omit to search all sections.")] string? section = null,
        [Description("Maximum number of matching entries to return (1-50). Defaults to 20.")] int maxResults = 20)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > MaximumQueryLength || query.Any(char.IsControl))
        {
            return $"Invalid query. Provide 1-{MaximumQueryLength} characters without control characters.";
        }

        string[] terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length is 0 or > MaximumQueryTerms || terms.Any(term => !ValidTerm.IsMatch(term)))
        {
            return "Invalid query. Use 1-8 words containing only letters, numbers, underscores, periods, colons, plus signs, or hyphens.";
        }

        if (section is not null && !ValidSection.IsMatch(section))
        {
            return "Invalid section. Use a section such as '1', '5', or '3pm'.";
        }

        if (maxResults is < 1 or > 50)
        {
            return "Invalid maxResults. Choose a value from 1 to 50.";
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "apropos",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("--and");
        startInfo.ArgumentList.Add("--long");
        if (section is not null)
        {
            startInfo.ArgumentList.Add("--sections");
            startInfo.ArgumentList.Add(section);
        }

        startInfo.ArgumentList.Add("--");
        foreach (string term in terms)
        {
            startInfo.ArgumentList.Add(EscapeExtendedRegex(term));
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return "Could not start apropos.";
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return "Could not find 'apropos'. Install man-db (or the package providing apropos) on the Linux host.";
        }

        Task<(string Text, bool Truncated)> stdoutTask = ReadBoundedAsync(
            process.StandardOutput, MaximumOutputCharacters, () => TryKill(process));
        Task<(string Text, bool Truncated)> stderrTask = ReadBoundedAsync(
            process.StandardError, MaximumErrorCharacters, () => TryKill(process));

        using var timeout = new CancellationTokenSource(SearchTimeout);
        bool timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            TryKill(process);
            await process.WaitForExitAsync();
        }

        (string output, bool outputTruncated) = await stdoutTask;
        (string error, bool errorTruncated) = await stderrTask;

        if (timedOut)
        {
            return "The local apropos search exceeded the 10-second time limit and was stopped.";
        }

        if (errorTruncated)
        {
            return "The local apropos search produced an oversized error response and was stopped.";
        }

        if (process.ExitCode == 16)
        {
            return "No local manual pages matched the query.";
        }

        if (process.ExitCode != 0 && !outputTruncated)
        {
            string details = string.IsNullOrWhiteSpace(error) ? string.Empty : $" Details: {error.Trim()}";
            return $"apropos failed with exit code {process.ExitCode}.{details}";
        }

        string searchableOutput = output;
        if (outputTruncated && !searchableOutput.EndsWith('\n'))
        {
            int lastCompleteLine = searchableOutput.LastIndexOf('\n');
            searchableOutput = lastCompleteLine >= 0 ? searchableOutput[..lastCompleteLine] : string.Empty;
        }

        string[] results = searchableOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (results.Length == 0)
        {
            return "No local manual pages matched the query.";
        }

        bool hasMoreResults = outputTruncated || results.Length > maxResults;
        string resultText = string.Join(Environment.NewLine, results.Take(maxResults));
        string footer = hasMoreResults
            ? $"\nShowing up to the first {maxResults} results. Refine the query or section for more specific matches."
            : string.Empty;

        return $"Local manual-page matches for '{query}'{(section is null ? string.Empty : $" (section {section})")}:\n{resultText}{footer}";
    }

    private static readonly Regex ValidPageName = new(@"^[a-zA-Z0-9_][a-zA-Z0-9_.:+-]{0,127}$", RegexOptions.CultureInvariant);

    [McpServerTool(Name = "get_man_page", ReadOnly = true, Destructive = false, OpenWorld = false), Description("Retrieves one local manual page as plain text. Optionally restricts lookup to a manual section. The response is limited to 64 KiB.")]
    public static async Task<string> GetManPage(
        [Description("Exact manual page name, such as 'ls', 'chmod', or 'systemd.unit'. Do not include a path or section.")] string pageName,
        [Description("Optional manual section, such as '1', '5', or '3pm'. Omit to use the system's normal section preference.")] string? section = null)
    {
        if (string.IsNullOrWhiteSpace(pageName) || !ValidPageName.IsMatch(pageName))
        {
            return "Invalid page name. Use 1-128 letters, numbers, underscores, periods, colons, plus signs, or hyphens; the name must start with a letter, number, or underscore and must not contain a path.";
        }

        if (section is not null && !ValidSection.IsMatch(section))
        {
            return "Invalid section. Use a section such as '1', '5', or '3pm'.";
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "man",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        // Force non-interactive output and avoid inherited man options changing the invocation.
        startInfo.ArgumentList.Add("--pager=cat");
        if (section is not null)
        {
            startInfo.ArgumentList.Add("--sections");
            startInfo.ArgumentList.Add(section);
        }

        startInfo.ArgumentList.Add(pageName);
        startInfo.Environment["MANOPT"] = string.Empty;
        startInfo.Environment["MANPAGER"] = "cat";
        startInfo.Environment["PAGER"] = "cat";
        startInfo.Environment["MANROFFOPT"] = string.Empty;
        startInfo.Environment["MANWIDTH"] = "100";

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return "Could not start man.";
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return "Could not find 'man'. Install man-db (or the package providing man) on the Linux host.";
        }

        Task<(string Text, bool Truncated)> stdoutTask = ReadBoundedAsync(
            process.StandardOutput, MaximumOutputCharacters, () => TryKill(process));
        Task<(string Text, bool Truncated)> stderrTask = ReadBoundedAsync(
            process.StandardError, MaximumErrorCharacters, () => TryKill(process));

        using var timeout = new CancellationTokenSource(SearchTimeout);
        bool timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            TryKill(process);
            await process.WaitForExitAsync();
        }

        (string output, bool outputTruncated) = await stdoutTask;
        (string error, bool errorTruncated) = await stderrTask;

        if (timedOut)
        {
            return "Retrieving the local manual page exceeded the 10-second time limit and was stopped.";
        }

        if (errorTruncated)
        {
            return "The local man command produced an oversized error response and was stopped.";
        }

        if (process.ExitCode == 16 || string.IsNullOrWhiteSpace(output))
        {
            return $"No local manual page was found for '{pageName}'{(section is null ? string.Empty : $" in section {section}")}.";
        }

        if (process.ExitCode != 0 && !outputTruncated)
        {
            string details = string.IsNullOrWhiteSpace(error) ? string.Empty : $" Details: {error.Trim()}";
            return $"man failed with exit code {process.ExitCode}.{details}";
        }

        string heading = $"Local manual page: {pageName}{(section is null ? string.Empty : $" (section {section})")}";
        string truncationNote = outputTruncated ? "\n\n[Output truncated at 64 KiB.]" : string.Empty;
        return $"{heading}\n\n{output.TrimEnd()}{truncationNote}";
    }

    private static string EscapeExtendedRegex(string value)
    {
        var escaped = new StringBuilder(value.Length * 2);
        foreach (char character in value)
        {
            if (character is '\\' or '.' or '^' or '$' or '|' or '(' or ')' or '[' or ']' or '{' or '}' or '*' or '+' or '?')
            {
                escaped.Append('\\');
            }

            escaped.Append(character);
        }

        return escaped.ToString();
    }

    private static async Task<(string Text, bool Truncated)> ReadBoundedAsync(
        StreamReader reader,
        int maximumCharacters,
        Action onLimitReached)
    {
        var output = new StringBuilder(Math.Min(maximumCharacters, 4096));
        var buffer = new char[4096];
        bool truncated = false;

        while (true)
        {
            int read = await reader.ReadAsync(buffer.AsMemory());
            if (read == 0)
            {
                break;
            }

            int remaining = maximumCharacters - output.Length;
            if (remaining > 0)
            {
                output.Append(buffer, 0, Math.Min(remaining, read));
            }

            if (read > remaining && !truncated)
            {
                truncated = true;
                onLimitReached();
            }
        }

        return (output.ToString(), truncated);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between the check and the kill request.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The child is already exiting or cannot be signalled.
        }
    }
}
