using System.Text.Json;
using ChatRelay.Logging;

namespace ChatRelay.Changes;

/// <summary>
/// Adapter-emitted record of a tool call observed in the assistant stream.
/// We only act on the file-mutating tools (<c>Edit</c>, <c>MultiEdit</c>,
/// <c>Write</c>, <c>NotebookEdit</c>); everything else is ignored at the
/// tracker level.
///
/// <para>
/// Two phases: <c>Requested</c> fires when the model emits a <c>tool_use</c>
/// content block (pre-write window — read disk now to capture the baseline);
/// <c>Completed</c> fires when the corresponding <c>tool_result</c> arrives,
/// at which point the on-disk content is the new <c>LastApplied</c>.
/// </para>
/// </summary>
public sealed class ToolCallObservation
{
    public required string ToolName { get; init; }
    public required string InputJson { get; init; }
    public required ToolCallPhase Phase { get; init; }
}

public enum ToolCallPhase
{
    /// <summary>Model emitted a tool_use block — file write hasn't happened yet.</summary>
    Requested,

    /// <summary>Tool finished — the post-write state is now on disk.</summary>
    Completed,
}

/// <summary>
/// Recognises the file-mutating tools and pulls the target path out of the
/// tool input. The set is fixed for now — the four tools the Claude CLI
/// (and Anthropic API) use to write to disk. New tools can be added without
/// touching anything else.
/// </summary>
public static class FileMutatingTools
{
    public static readonly HashSet<string> Names = new(StringComparer.Ordinal)
    {
        "Edit",
        "MultiEdit",
        "Write",
        "NotebookEdit",
    };

    public static bool IsKnown(string toolName) => Names.Contains(toolName);

    /// <summary>
    /// Pulls the target file path from a tool_use input JSON for any of the
    /// four mutating tools. Returns null if the JSON is missing the path
    /// field (tool_use shape changed or input was malformed) — the tracker
    /// then skips this observation rather than trying to track a phantom
    /// file.
    /// </summary>
    public static string? TryExtractPath(string toolName, string inputJson)
    {
        if (string.IsNullOrEmpty(inputJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(inputJson);
            var root = doc.RootElement;
            return toolName switch
            {
                "Edit"         => ReadString(root, "file_path"),
                "MultiEdit"    => ReadString(root, "file_path"),
                "Write"        => ReadString(root, "file_path"),
                "NotebookEdit" => ReadString(root, "notebook_path"),
                _              => null,
            };
        }
        catch (JsonException ex)
        {
            ExtensionLogger.Warn("changes", $"Bad tool_use JSON for {toolName}: {ex.Message}");
            return null;
        }
    }

    static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
