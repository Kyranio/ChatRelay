using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace ChatRelay.Editor;

/// <summary>Two adornment layers: highlight below Text (so selection looks natural), buttons above Caret (hit-testable, focus-stable).</summary>
public sealed class HunkAdornmentLayerDefinition
{
    public const string LayerName = "ChatRelayHunks";
    public const string OverlayLayerName = "ChatRelayHunksOverlay";

    [Export(typeof(AdornmentLayerDefinition))]
    [Name(LayerName)]
    [Order(After = PredefinedAdornmentLayers.Selection, Before = PredefinedAdornmentLayers.Text)]
    public AdornmentLayerDefinition? BackgroundLayer { get; set; }

    [Export(typeof(AdornmentLayerDefinition))]
    [Name(OverlayLayerName)]
    [Order(After = PredefinedAdornmentLayers.Caret)]
    public AdornmentLayerDefinition? OverlayLayer { get; set; }
}
