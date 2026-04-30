using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;
using System.Runtime.InteropServices;

namespace ChatRelay.Chat.Views
{
    /// <summary>Dockable pane hosting the chat UI.</summary>
    [Guid("a1b2c3d4-5678-9abc-def0-123456789abc")]
    public class ChatWindow : ToolWindowPane
    {
        /// <summary>Most-recently-constructed control; used by commands to push data into the open chat.</summary>
        public static ChatControl? Control { get; private set; }

        public ChatWindow() : base(null)
        {
            Caption = "ChatRelay";
            BitmapImageMoniker = KnownMonikers.ToolWindow;

            var control = new ChatControl();
            Content = control;
            Control = control;
        }

        public static void AppendReferenceToWindow(
            string displayPath, string absolutePath,
            int startLine, int endLine, string content)
        {
            Control?.AppendReference(displayPath, absolutePath, startLine, endLine, content);
        }
    }
}
