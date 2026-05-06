using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace ChatRelay.Editor;

/// <summary>
/// Names + orders the adornment layer the hunk renderer (Phase 4.3) will
/// draw onto. The export is on the field, not the class — this is the
/// stock pattern in the VS editor MEF surface; the type itself just hosts
/// the attributes.
///
/// <para>
/// Layer order: above <see cref="PredefinedAdornmentLayers.Selection"/>
/// (so highlighted selection sweeps look natural over our hunks) and
/// below <see cref="PredefinedAdornmentLayers.Text"/> (so the actual
/// source code stays the topmost visible content). When Phase 4.3
/// renders block adornments anchored to spans, this ordering keeps the
/// "diff strip" visually behind the live code.
/// </para>
/// </summary>
public sealed class HunkAdornmentLayerDefinition
{
    public const string LayerName = "ChatRelayHunks";

    [Export(typeof(AdornmentLayerDefinition))]
    [Name(LayerName)]
    [Order(After = PredefinedAdornmentLayers.Selection,
           Before = PredefinedAdornmentLayers.Text)]
    public AdornmentLayerDefinition? Layer { get; set; }
}
