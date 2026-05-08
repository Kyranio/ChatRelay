using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace ChatRelay.Editor;

/// <summary>MEF entry: one HunkAdornmentManager per editor view. "text" content type covers source + markdown/json/plain.</summary>
[Export(typeof(IWpfTextViewCreationListener))]
[ContentType("text")]
[TextViewRole(PredefinedTextViewRoles.Document)]
public sealed class HunkAdornmentListener : IWpfTextViewCreationListener
{
    // No static reference: GC follows view lifetime, the manager unsubscribes itself on view-closed.
    public void TextViewCreated(IWpfTextView textView) => _ = new HunkAdornmentManager(textView);
}
