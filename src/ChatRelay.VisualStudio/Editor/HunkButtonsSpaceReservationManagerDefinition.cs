using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace ChatRelay.Editor;

/// <summary>Names the SpaceReservationManager that hosts our hunk-button popup agents.</summary>
public sealed class HunkButtonsSpaceReservationManagerDefinition
{
    public const string Name = "ChatRelayHunkButtons";

    // Place after the intellisense slot so completion lists draw on top of
    // our button row when they collide. Same convention QuickInfo uses.
    [Export(typeof(SpaceReservationManagerDefinition))]
    [Name(Name)]
    [Order(After = "intellisense")]
    public SpaceReservationManagerDefinition? Definition { get; set; }
}
