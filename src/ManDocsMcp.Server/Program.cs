using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

var builder = Host.CreateApplicationBuilder(args);

// MCP stdio uses stdout for protocol messages. Keep all logs on stderr.
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();

[McpServerToolType]
public static class DiagnosticTools
{
    [McpServerTool, Description("Basic smoke test confirming that the ManDocsMcp server is reachable.")]
    public static string Ping() => "ManDocsMcp stdio server is running.";
}
