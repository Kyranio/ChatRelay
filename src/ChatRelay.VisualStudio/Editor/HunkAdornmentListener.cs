using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace ChatRelay.Editor;

/// <summary>
/// MEF entry point for the editor-side hunk renderer. VS calls
/// <see cref="TextViewCreated"/> for every code-editor view that opens —
/// we pair each view with its own <see cref="HunkAdornmentManager"/> so
/// the hunk-update plumbing per file lives where the view does.
///
/// <para>
/// <see cref="ContentTypeAttribute"/> is "text" for breadth: the user
/// might ask Claude to edit Markdown, JSON, plain-text, etc., not just
/// source code. <see cref="TextViewRoleAttribute"/> uses
/// <see cref="PredefinedTextViewRoles.Document"/> to scope to actual
/// document editors and skip Find-result panes, peek windows, and
/// embedded interactive views.
/// </para>
/// </summary>
[Export(typeof(IWpfTextViewCreationListener))]
[ContentType("text")]
[TextViewRole(PredefinedTextViewRoles.Document)]
public sealed class HunkAdornmentListener : IWpfTextViewCreationListener
{
    public void TextViewCreated(IWpfTextView textView)
    {
        // Per-view manager hooks itself to the view's lifetime + the
        // EditorChangesService. Keeping no static reference here means
        // GC follows view lifetime: when VS releases the view, the
        // manager's subscriptions clean up and the manager is collected.
        _ = new HunkAdornmentManager(textView);
    }
}
