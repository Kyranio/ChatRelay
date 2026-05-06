using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace ChatRelay.Editor;

/// <summary>
/// Names + orders the two adornment layers the hunk renderer uses.
///
/// <para>
/// Two layers, not one, because the renderer's outputs split into two
/// z-ordering classes:
/// <list type="bullet">
///   <item><c>ChatRelayHunks</c> (background) — turquoise highlight
///   rectangle behind the model's new lines plus the accepted-hunk
///   marker bar. Drawn between Selection and Text so the source code
///   stays the topmost visible content and selection sweeps still
///   look natural.</item>
///   <item><c>ChatRelayHunksOverlay</c> (foreground) — the "removed
///   lines" expander and the accept/reject button row. These are
///   interactive controls anchored in row-space below the highlighted
///   lines; they must sit ABOVE Text or the editor's own text drawn
///   in those rows paints over the buttons and the ghost box body,
///   making the box look transparent and the controls hard to find.
///   Drawn after Caret so the caret stays visible if the user clicks
///   into the read-only ghost TextBox.</item>
/// </list>
/// </para>
/// </summary>
public sealed class HunkAdornmentLayerDefinition
{
    public const string LayerName = "ChatRelayHunks";
    public const string OverlayLayerName = "ChatRelayHunksOverlay";

    [Export(typeof(AdornmentLayerDefinition))]
    [Name(LayerName)]
    [Order(After = PredefinedAdornmentLayers.Selection,
           Before = PredefinedAdornmentLayers.Text)]
    public AdornmentLayerDefinition? BackgroundLayer { get; set; }

    [Export(typeof(AdornmentLayerDefinition))]
    [Name(OverlayLayerName)]
    [Order(After = PredefinedAdornmentLayers.Caret)]
    public AdornmentLayerDefinition? OverlayLayer { get; set; }
}
