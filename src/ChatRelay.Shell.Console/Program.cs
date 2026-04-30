using System.Diagnostics;
using StreamJsonRpc;

namespace ChatRelay.Shell.Console;

static class Program
{
    static async Task<int> Main(string[] args)
    {
        var hostExe = args.FirstOrDefault()
            ?? throw new ArgumentException("Pass the ChatRelay.Host.dll path as the first arg.");

        var psi = new ProcessStartInfo("dotnet", $"\"{hostExe}\"")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var host = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start host.");
        host.ErrorDataReceived += (_, e) => { if (e.Data is not null) System.Console.Error.WriteLine($"[host stderr] {e.Data}"); };
        host.BeginErrorReadLine();

        var formatter = new SystemTextJsonFormatter();
        formatter.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        formatter.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
        var handler = new HeaderDelimitedMessageHandler(
            host.StandardInput.BaseStream,
            host.StandardOutput.BaseStream,
            formatter);

        using var rpc = new JsonRpc(handler);
        rpc.StartListening();

        async Task<T?> CallAsync<T>(string method, object? p = null) =>
            p is null ? await rpc.InvokeAsync<T>(method)
                      : await rpc.InvokeWithParameterObjectAsync<T>(method, p);

        void Step(string title, object? result = null)
        {
            System.Console.WriteLine($"→ {title}");
            if (result is not null) System.Console.WriteLine($"  {result}");
        }

        Step("initialize");
        var init = await CallAsync<object>("initialize",
            new { clientName = "ChatRelay.Shell.Console", clientVersion = "0.1.0",
                  protocolVersion = "0", workspacePath = (string?)null });
        System.Console.WriteLine($"  {init}");

        Step("listAdapters");
        System.Console.WriteLine($"  {await CallAsync<object>("listAdapters")}");

        Step("listModels");
        System.Console.WriteLine($"  {await CallAsync<object>("listModels")}");

        Step("listSessions");
        System.Console.WriteLine($"  {await CallAsync<object>("listSessions")}");

        Step("getSettings");
        var settings = await CallAsync<object>("getSettings");
        System.Console.WriteLine($"  (settings loaded: {settings?.ToString()?[..Math.Min(200, settings.ToString()?.Length ?? 0)]}…)");

        Step("listMcpServers");
        System.Console.WriteLine($"  {await CallAsync<object>("listMcpServers")}");

        Step("listMcpFiles");
        System.Console.WriteLine($"  {await CallAsync<object>("listMcpFiles")}");

        Step("shutdown");
        await rpc.InvokeAsync("shutdown");
        rpc.Dispose();
        await host.WaitForExitAsync();
        return host.ExitCode;
    }
}
