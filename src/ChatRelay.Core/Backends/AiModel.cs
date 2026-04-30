namespace ChatRelay.Backends
{
    /// <summary>
    /// One model entry in the model dropdown. <see cref="AdapterId"/> is the
    /// grouping key, <see cref="Id"/> is what we hand back to the adapter on
    /// send, and <see cref="DisplayName"/> / <see cref="Version"/> drive the
    /// bold-name + normal-text rendering in the dropdown template.
    /// </summary>
    public class AiModel
    {
        /// <summary>Which adapter this model belongs to (e.g. "claude-cli").</summary>
        public string AdapterId { get; set; } = string.Empty;

        /// <summary>The adapter-level display name shown as the group header (e.g. "Claude CLI").</summary>
        public string AdapterDisplayName { get; set; } = string.Empty;

        /// <summary>Adapter-specific model id (e.g. "opus" for the CLI, "claude-opus-4-5-20250929" for the API, "llama3.2:3b" for Ollama).</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>Short, bolded in the UI (e.g. "Opus", "Llama 3.2").</summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>Version/variant suffix rendered in normal weight (e.g. "4.5", "3b", empty).</summary>
        public string Version { get; set; } = string.Empty;
    }
}
