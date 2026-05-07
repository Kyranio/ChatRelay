using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace ChatRelay.Editor;

/// <summary>
/// Names the two adornment layers used by the hunk renderer.
/// <list type="bullet">
///   <item><c>ChatRelayHunks</c> — turquoise highlight, drawn between
///   Selection and Text so source code stays the topmost visible
///   content and selection sweeps still look natural.</item>
///   <item><c>ChatRelayHunksOverlay</c> — accept/reject buttons, drawn
///   above Text and Caret so they're hit-testable and don't lose focus
///   when the user clicks into the editor (which is the failure mode
///   of the popup-agent / SpaceReservationManager approach).</item>
/// </list>
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
