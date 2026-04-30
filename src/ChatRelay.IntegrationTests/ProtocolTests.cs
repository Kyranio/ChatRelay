using System.Text.Json;
using StreamJsonRpc;

namespace ChatRelay.IntegrationTests;

// Smoke-tests the protocol surface end-to-end: spawns the host, calls every
// non-mutating RPC method, asserts shape. Catches drift between the host's
// JSON-RPC method names / DTOs and any shell that consumes them.
public sealed class ProtocolTests : IClassFixture<HostFixture>
{
    readonly HostFixture _fx;
    public ProtocolTests(HostFixture fx) => _fx = fx;

    [Fact]
    public async Task ListAdapters_returns_array()
    {
        var raw = await _fx.Rpc.InvokeAsync<JsonElement>("listAdapters");
        Assert.Equal(JsonValueKind.Array, raw.ValueKind);
    }

    [Fact]
    public async Task ListModels_returns_array()
    {
        var raw = await _fx.Rpc.InvokeAsync<JsonElement>("listModels");
        Assert.Equal(JsonValueKind.Array, raw.ValueKind);
    }

    [Fact]
    public async Task ListSessions_returns_array()
    {
        var raw = await _fx.Rpc.InvokeAsync<JsonElement>("listSessions");
        Assert.Equal(JsonValueKind.Array, raw.ValueKind);
    }

    [Fact]
    public async Task GetSettings_returns_blob_with_expected_shape()
    {
        var raw = await _fx.Rpc.InvokeAsync<JsonElement>("getSettings");
        Assert.Equal(JsonValueKind.Object, raw.ValueKind);
        Assert.True(raw.TryGetProperty("general", out _) || raw.TryGetProperty("General", out _));
        Assert.True(raw.TryGetProperty("permissions", out _) || raw.TryGetProperty("Permissions", out _));
    }

    [Fact]
    public async Task ListMcpServers_returns_array()
    {
        var raw = await _fx.Rpc.InvokeAsync<JsonElement>("listMcpServers");
        Assert.Equal(JsonValueKind.Array, raw.ValueKind);
    }

    [Fact]
    public async Task ListMcpFiles_returns_array()
    {
        var raw = await _fx.Rpc.InvokeAsync<JsonElement>("listMcpFiles");
        Assert.Equal(JsonValueKind.Array, raw.ValueKind);
    }

    [Fact]
    public async Task OpenSession_creates_fresh_when_id_unknown()
    {
        var result = await _fx.Rpc.InvokeWithParameterObjectAsync<JsonElement>(
            "openSession", new { sessionId = (string?)null });
        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        Assert.True(result.TryGetProperty("sessionId", out var id));
        var sid = id.GetString();
        Assert.False(string.IsNullOrEmpty(sid));

        // Clean up the empty session we just created so we don't accumulate
        // orphans in the no-solution bucket on the test machine.
        await _fx.Rpc.InvokeWithParameterObjectAsync(
            "deleteSession", new { sessionId = sid });
    }

    [Fact]
    public async Task SetSessionDraft_round_trips()
    {
        var opened = await _fx.Rpc.InvokeWithParameterObjectAsync<JsonElement>(
            "openSession", new { sessionId = (string?)null });
        var sid = opened.GetProperty("sessionId").GetString()!;

        await _fx.Rpc.InvokeWithParameterObjectAsync(
            "setSessionDraft", new { sessionId = sid, text = "hello draft" });

        var reopened = await _fx.Rpc.InvokeWithParameterObjectAsync<JsonElement>(
            "openSession", new { sessionId = sid });
        var draft = reopened.GetProperty("draftText").GetString();
        Assert.Equal("hello draft", draft);

        await _fx.Rpc.InvokeWithParameterObjectAsync(
            "deleteSession", new { sessionId = sid });
    }

    [Fact]
    public async Task SetWorkspace_does_not_throw()
    {
        await _fx.Rpc.InvokeWithParameterObjectAsync(
            "setWorkspace", new { path = (string?)null });
    }

    [Fact]
    public async Task RefreshAdapters_does_not_throw()
    {
        await _fx.Rpc.InvokeAsync("refreshAdapters");
    }
}
