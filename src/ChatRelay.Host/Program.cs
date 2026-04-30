using ChatRelay.Backends;
using ChatRelay.Permissions;
using StreamJsonRpc;

namespace ChatRelay.Host;

static class Program
{
    static async Task<int> Main()
    {
        var registry = new AdapterRegistry();
        registry.Register(new ClaudeCliAdapter());
        registry.Register(new ClaudeApiAdapter());
        registry.Register(new OllamaAdapter());

        using var broker = new PermissionBrokerService();
        broker.Start();

        var service = new HostService(registry, broker);

        using var stdin = Console.OpenStandardInput();
        using var stdout = Console.OpenStandardOutput();
        var formatter = new SystemTextJsonFormatter();
        formatter.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        formatter.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
        var handler = new HeaderDelimitedMessageHandler(stdout, stdin, formatter);
        using var rpc = new JsonRpc(handler, service);
        service.Rpc = rpc;

        rpc.StartListening();
        await rpc.Completion;
        return 0;
    }
}
