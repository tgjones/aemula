using static Aemula.BitUtility;
using static Aemula.Emulation.Chips.Tia.TiaUtility;

namespace Aemula.Emulation.Chips.Tia;

internal sealed class PlayerAndMissile
{
    // Registers
    public byte Graphics;
    public byte Color;
    public byte Luminance;
    public byte NumberSizePlayer;
    public byte NumberSizeMissile;
    public bool Reflect;
    public byte HorizontalMotionPlayer = 0b1000; // Stored with bit 3 inverted

    // State
    public byte PlayerClockDiv4;
    private PolynomialCounter _counter;
    public bool Reset;
    private bool _draw;
    private byte _graphicsDelay;
    private byte _scanCounter;

    /// <summary>
    /// Whether this player's graphic has a lit pixel at the current colour
    /// clock. Updated by <see cref="DoPlayer"/> each colour clock.
    ///
    /// The player reports this bit rather than writing the video output
    /// itself: TIA's real priority encoder has to decide P0 vs P1 vs
    /// playfield vs background (P0 must win over P1) in a single stage, so a
    /// lone resolver in <see cref="TiaChip"/> reads every object's presence
    /// bit and picks one winner, instead of each object overwriting
    /// <see cref="TiaChip.Lum"/>/<see cref="TiaChip.Col"/> in turn.
    /// </summary>
    public bool PixelOn;

    public void UpdatePlayerDiv4()
    {
        PlayerClockDiv4++;

        if (PlayerClockDiv4 > 3)
        {
            PlayerClockDiv4 = 0;

            _counter.Increment();
            if (_counter.Value == 0b111111 || Reset)
            {
                Reset = false;
                _counter.Reset();
            }

            ExecutePlayerLogic();
        }
    }

    private void ExecutePlayerLogic()
    {
        switch (_counter.Value)
        {
            case 0b111000 when NumberSizePlayer == 0b001 || NumberSizePlayer == 0b011:
            case 0b101111 when NumberSizePlayer == 0b011 || NumberSizePlayer == 0b010 || NumberSizePlayer == 0b110:
            case 0b111001 when NumberSizePlayer == 0b100 || NumberSizePlayer == 0b110:
                _draw = true;
                _scanCounter = 0b111;
                break;

            case 0b101101: // RESET
                Reset = true;
                _draw = true;
                _scanCounter = 0b111;
                break;
        }
    }

    /// <summary>
    /// Advances the player's one-colour-clock graphics pipeline and latches
    /// <see cref="PixelOn"/> for this colour clock. Real TIA clocks the
    /// serial graphics shift one colour clock ahead of the pixel it lights,
    /// so <see cref="_graphicsDelay"/> holds the bit sampled last call and
    /// that bit is what lights (or doesn't light) a pixel now.
    /// </summary>
    public void DoPlayer()
    {
        // The bit sampled on the previous colour clock is the one displayed
        // now - the deliberate one-clock delay (see the method summary).
        PixelOn = _graphicsDelay == 1;

        if (_graphicsDelay == 1)
        {
            _graphicsDelay = 0;
        }

        if (_draw)
        {
            if (_scanCounter == 0b000)
            {
                _draw = false;
            }

            // Handle reflection.
            var graphicsIndex = Reflect
                ? _scanCounter ^ 0b111
                : _scanCounter;

            _graphicsDelay = GetBit(Graphics, graphicsIndex);

            if (_scanCounter == 0b000)
            {
                _scanCounter = 0b111;
            }
            else
            {
                _scanCounter--;
            }
        }
    }
}
