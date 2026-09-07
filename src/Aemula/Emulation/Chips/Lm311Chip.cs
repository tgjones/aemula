namespace Aemula.Emulation.Chips;

/// <summary>
/// A voltage comparator with a little external positive feedback (hysteresis).
/// In the Apple Cassette Interface an LM311 wired this way is the tape-input
/// zero-crossing detector: the AC-coupled, half-supply-biased signal off the
/// recorder's earphone jack is squared up into a clean logic level for the
/// firmware to poll.
/// </summary>
/// <remarks>
/// Only the switching behaviour matters, so the analog input is modelled on the
/// same nominal [-1, 1] scale as the rest of the audio path, with 0 as the
/// (DC-blocked) switching midpoint. The 47 kΩ feedback resistor gives the real
/// circuit a small dead band around that midpoint: the output only flips once
/// the input has moved past ±<see cref="Hysteresis"/>, which keeps noise and
/// slow crossings near 0 from producing output chatter.
/// </remarks>
public sealed class Lm311Chip
{
    /// <summary>
    /// Half-width of the dead band around the switching midpoint, as a fraction
    /// of full-scale input, representative of the ACI's 47 kΩ feedback divider.
    /// </summary>
    public const double DefaultHysteresis = 0.05;

    public Lm311Chip(double hysteresis = DefaultHysteresis)
    {
        Hysteresis = hysteresis;
    }

    /// <summary>The dead-band half-width; see <see cref="DefaultHysteresis"/>.</summary>
    public double Hysteresis { get; }

    private double _input;

    /// <summary>
    /// The analog signal on the comparator input, nominally [-1, 1] with 0 the
    /// switching midpoint. Assigning re-evaluates <see cref="Out"/>.
    /// </summary>
    public double Input
    {
        private get => _input;
        set
        {
            _input = value;

            if (value > Hysteresis)
            {
                Out = true;
            }
            else if (value < -Hysteresis)
            {
                Out = false;
            }
        }
    }

    /// <summary>
    /// The squared-up output: high once the input rises past +<see cref="Hysteresis"/>,
    /// low once it falls past -<see cref="Hysteresis"/>, holding its last state
    /// in the dead band between.
    /// </summary>
    public bool Out { get; private set; }

    /// <summary>Clear to the power-on state (midpoint input, output low).</summary>
    public void Reset()
    {
        _input = 0;
        Out = false;
    }
}
