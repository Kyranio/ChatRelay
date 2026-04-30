using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Stdio MCP server spawned by the Claude CLI via --mcp-config. Exposes one
// tool — "approve" in <see cref="ChatRelay.PermissionBroker.PermissionTool"/> —
// that forwards approval requests to the VS extension over a named pipe.
//
// Stdout is reserved for JSON-RPC frames, so every diagnostic goes to stderr.
// We clear the default logging providers that would otherwise dump to stdout
// and corrupt the protocol stream.
var builder = Host.CreateEmptyApplicationBuilder(settings: null);

builder.Logging.AddConsole(o =>
{
    // Log *everything* to stderr — the CLI surfaces it under "Server stderr"
    // when running with --debug, which is the only easy way to see what the
    // broker is doing during troubleshooting.
    o.LogToStandardErrorThreshold = LogLevel.Trace;
});

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<ChatRelay.PermissionBroker.PermissionTool>();

await builder.Build().RunAsync();
