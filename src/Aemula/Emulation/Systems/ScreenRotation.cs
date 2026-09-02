namespace Aemula.Emulation.Systems;

// The clockwise rotation to apply to a system's finished TV picture so it
// faces the way its cabinet's monitor did - Space Invaders and other games
// mounted the tube on its side. Only EmulationWindow honours this; the
// debugger's TelevisionWindow always shows the raw, unrotated raster.
public enum ScreenRotation
{
    None,
    Clockwise90,
    Clockwise180,
    Clockwise270,
}
