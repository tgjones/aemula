using System.Collections.Generic;
using Aemula.Emulation.Systems;

namespace Aemula.Emulation.Systems.SpaceInvaders;

// Space Invaders ran a black-and-white tube mounted a quarter-turn
// anticlockwise (the score ends up along one long edge, the cannon along the
// other), and got its colour from transparent strips glued to the glass: a
// red band over the row the flying saucer crosses, a green band over the
// shields, cannon and ground line. Both are pure presentation - nothing in
// the emulated hardware knows about them - so they live here as the generic
// ScreenRotation / ScreenOverlays the UI already knows how to apply, rather
// than as anything EmulationWindow special-cases.
public sealed partial class SpaceInvadersSystem
{
    public override ScreenRotation ScreenRotation => ScreenRotation.Clockwise270;

    // Regions are in the player-facing, already-rotated picture: X/Y from the
    // top-left, both axes 0..1. The row bands follow the usual quantisation
    // of the real overlay against the 256-pixel-tall upright image - white
    // score strip, red saucer band (rows 32-63), white descent area, green
    // base (rows 184-255).
    private static readonly ScreenOverlay[] _screenOverlays =
    [
        new ScreenOverlay(0f, 32f / 256f, 1f, 32f / 256f, new RgbaByte(0xFF, 0x28, 0x28, 0xFF)),
        new ScreenOverlay(0f, 184f / 256f, 1f, 72f / 256f, new RgbaByte(0x28, 0xFF, 0x28, 0xFF)),
    ];

    public override IReadOnlyList<ScreenOverlay> ScreenOverlays => _screenOverlays;
}
