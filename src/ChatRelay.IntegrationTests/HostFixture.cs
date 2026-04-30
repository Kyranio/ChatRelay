using System.Diagnostics;
using System.Text.Json;
using StreamJsonRpc;

namespace ChatRelay.IntegrationTests;

// xUnit fixture: spawns one host process for the whole class, tears it down.
public sealed class HostFixture : IAsyncLifetime
{
    Process? _proc;
    public JsonRpc Rpc { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var hostDll = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "ChatRelay.Host", "bin", "Debug", "net10.0", "ChatRelay.Host.dll"));
        Assert.True(File.Exists(hostDll), "Host DLL not found: " + hostDll);

        var psi = new ProcessStartInfo("dotnet", $"\"{hostDll}\"")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        _proc = Process.Start(psi)!;

        var formatter = new SystemTextJsonFormatter();
        formatter.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        formatter.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
        var handler = new HeaderDelimitedMessageHandler(
            _proc.StandardInput.BaseStream,
            _proc.StandardOutput.BaseStream,
            formatter);
        Rpc = new JsonRpc(handler);
        Rpc.StartListening();

        await Rpc.InvokeWithParameterObjectAsync<object>("initialize", new
        {
            clientName = "ChatRelay.IntegrationTests",
            clientVersion = "0.1.0",
            protocolVersion = "0",
            workspacePath = (string?)null,
        });
    }

    public async Task DisposeAsync()
    {
        try { await Rpc.InvokeAsync("shutdown"); } catch { }
        try { Rpc.Dispose(); } catch { }
        if (_proc is not null)
        {
            try { if (!_proc.HasExited) _proc.Kill(); } catch { }
            _proc.Dispose();
        }
    }
}
