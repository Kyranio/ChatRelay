using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ChatRelay.Settings;

/// <summary>
/// User-editable preferences. Grouped by category so the settings window
/// can dedicate one panel per section. All fields have sensible defaults
/// so an empty / missing settings.json works out of the box.
///
/// This is both the on-disk shape (serialised by <c>SettingsStore</c>,
/// which lives in <c>ChatRelay.Core</c>) and the wire shape
/// (<c>UpdateSettingsParams.Patch</c> in <c>ChatRelay.Host.Protocol</c>).
/// Pure data — no behaviour lives on this type.
/// </summary>
public class ExtensionSettings
{
    public int SchemaVersion { get; set; } = 1;
    public GeneralSettings General { get; set; } = new GeneralSettings();

    /// <summary>Pre-approved / pre-denied tool patterns. Only consumed by the Claude CLI adapter today.</summary>
    public PermissionSettings Permissions { get; set; } = new PermissionSettings();

    /// <summary>
    /// Registered MCP configuration files — global (always merged) and
    /// project-scoped (merged only when the tied solution is open).
    /// Auto-seeded on first load from the two well-known locations
    /// (user <c>%LocalAppData%\ChatRelay\.chatrelay.mcp.json</c> + any
    /// detected project <c>.chatrelay.mcp.json</c>). Users add/remove
    /// entries from the Settings window's MCP tab.
    /// </summary>
    public List<TrackedMcpFile> McpFiles { get; set; } = new List<TrackedMcpFile>();
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum McpFileScope
{
    /// <summary>Merged into every send regardless of which solution is open.</summary>
    Global,

    /// <summary>Only merged when the <see cref="TrackedMcpFile.ScopedSolutionPath"/> matches the currently-open solution.</summary>
    Project
}

/// <summary>
/// One tracked MCP configuration file. Persisted in settings.json so
/// the extension can iterate the full set at send time and the
/// settings UI can render the file-management list.
/// </summary>
public class TrackedMcpFile
{
    /// <summary>Absolute path to the .chatrelay.mcp.json file on disk.</summary>
    public string FilePath { get; set; } = string.Empty;

    public McpFileScope Scope { get; set; }

    /// <summary>
    /// Absolute path of the .sln this entry is tied to — only used
    /// when <see cref="Scope"/> is <see cref="McpFileScope.Project"/>.
    /// Compared against the currently-open solution at send time.
    /// </summary>
    public string? ScopedSolutionPath { get; set; }
}

public class GeneralSettings
{
    /// <summary>
    /// When true (default), the currently-visible editor document is
    /// auto-attached as a whole-file reference on every send. Disable to
    /// require explicit pinning.
    /// </summary>
    public bool AutoAttachActiveFile { get; set; } = true;

    /// <summary>
    /// When true, the "💭 Thinking" expander on assistant bubbles starts
    /// expanded rather than collapsed. Off by default so long reasoning
    /// traces don't flood the chat.
    /// </summary>
    public bool ThinkingExpandedByDefault { get; set; } = false;
}

/// <summary>
/// Pre-approval / pre-denial lists for Claude CLI tool invocations. Both
/// map 1:1 to the CLI's <c>--allowedTools</c> / <c>--disallowedTools</c>
/// flags. Patterns follow the CLI's syntax:
///   <list type="bullet">
///     <item><c>Bash</c> — allow/deny every Bash invocation</item>
///     <item><c>Bash(git:*)</c> — allow/deny just <c>git</c> subcommands</item>
///     <item><c>Read</c>, <c>Edit</c>, <c>WebFetch</c>, …</item>
///     <item><c>mcp__&lt;server&gt;__&lt;tool&gt;</c> — specific MCP tool</item>
///   </list>
/// Deny wins over allow on conflict — CLI enforces this server-side.
/// </summary>
public class PermissionSettings
{
    /// <summary>Tools pre-approved — no approval prompt before they run.</summary>
    public List<string> AllowedTools { get; set; } = new List<string>();

    /// <summary>Tools always refused — no approval prompt, never run.</summary>
    public List<string> DisallowedTools { get; set; } = new List<string>();

    /// <summary>
    /// Extra directories the CLI is allowed to read/write outside the
    /// current solution. Passed as one <c>--add-dir &lt;path&gt;</c>
    /// each. Absolute paths only.
    /// </summary>
    public List<string> AdditionalDirectories { get; set; } = new List<string>();

    /// <summary>
    /// Individual MCP tools the user has toggled off from the
    /// chat window's MCP menu. Each entry is a fully-qualified CLI id
    /// of the form <c>mcp__&lt;server&gt;__&lt;tool&gt;</c>. At send time these
    /// are merged into <see cref="DisallowedTools"/> so the CLI refuses
    /// them even though the underlying MCP server is still reachable.
    /// Disabling a tool never stops the MCP server process itself;
    /// it only removes the tool from the model's allowed surface.
    /// </summary>
    public List<string> DisabledMcpTools { get; set; } = new List<string>();

    /// <summary>
    /// MCP server names the user has toggled off entirely. Every known
    /// tool from a server in this list is added to
    /// <see cref="DisallowedTools"/> at send time. Tools that appear
    /// later (server added a new tool since the last menu refresh) are
    /// not retroactively blocked — the user needs to reopen the menu
    /// to see / gate them.
    /// </summary>
    public List<string> DisabledMcpServers { get; set; } = new List<string>();
}
