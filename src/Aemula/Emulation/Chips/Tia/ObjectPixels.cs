namespace Aemula.Emulation.Chips.Tia;

/// <summary>
/// The six raw "my pixel is lit here" bits - one per TIA object - for the
/// colour clock <see cref="TiaChip.DoVideo"/> just processed, captured
/// <em>before</em> the priority resolver collapses them to a single winner.
///
/// The resolver only tells you which object was drawn; collision detection
/// needs to know which objects <em>overlapped</em>, which is priority-
/// independent (a hidden pixel still registers a collision). So the raw bits
/// are published here rather than being reconstructed from
/// <see cref="TiaChip.Lum"/>/<see cref="TiaChip.Col"/>.
///
/// A plain value type with no methods: it is overwritten every colour clock
/// on the video hot path, so it must not allocate.
/// </summary>
internal struct ObjectPixels
{
    /// <summary>Player 0 graphic pixel (COLUP0 slot).</summary>
    public bool Player0;

    /// <summary>Missile 0 pixel - shares player 0's colour and priority slot.</summary>
    public bool Missile0;

    /// <summary>Player 1 graphic pixel (COLUP1 slot).</summary>
    public bool Player1;

    /// <summary>Missile 1 pixel - shares player 1's colour and priority slot.</summary>
    public bool Missile1;

    /// <summary>
    /// Playfield pixel - the decoded PF0/PF1/PF2 bit for this horizontal
    /// position (already mirrored for the right half when CTRLPF D0 is set).
    /// </summary>
    public bool Playfield;

    /// <summary>Ball pixel - shares the playfield's priority group, keeps COLUPF.</summary>
    public bool Ball;
}
