namespace Aemula.Emulation.Systems;

// A transparent coloured strip taped over the CRT - how Space Invaders and
// its contemporaries got colour out of a black-and-white tube. Region is in
// normalised coordinates of the picture as the player sees it (i.e. after
// ScreenRotation is applied): (0,0) top-left, (1,1) bottom-right. Colour
// multiplies the monochrome pixels underneath, so a lit pixel takes the
// gel's colour and an unlit one stays black, matching a filter over an
// emissive display; Colour.A is the gel strength (255 = full colour, 0 =
// clear).
public readonly record struct ScreenOverlay(
    float X,
    float Y,
    float Width,
    float Height,
    RgbaByte Color);
