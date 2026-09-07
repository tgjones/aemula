namespace Aemula.Emulation.Chips;

/// <summary>
/// A 555-type timer. By default it is a monostable (one-shot) multivibrator:
/// a high-to-low edge on <see cref="TriggerBar"/> drives <see cref="Out"/>
/// high for a fixed on-time, after which <see cref="Out"/> returns low and
/// stays there until the next trigger. Setting <see cref="LowTicks"/> to a
/// non-zero value instead makes it free-running (astable): after the on-time
/// <see cref="Out"/> stays low for <see cref="LowTicks"/> ticks and then
/// returns high on its own, with no trigger, repeating for as long as
/// <see cref="ResetBar"/> is released. Holding <see cref="ResetBar"/> low
/// forces <see cref="Out"/> low and abandons any timing in progress (an
/// astable timer resumes oscillating once it is released).
///
/// On real hardware the times are set by an external RC network charging and
/// discharging through the THRESHOLD/TRIGGER pins - <c>t_high = 0.693 *
/// (Ra + Rb) * C</c> and <c>t_low = 0.693 * Rb * C</c> for the classic
/// astable, <c>t = 1.1 * R * C</c> for the monostable. Modelling that analog
/// node is the same kind of non-digital-observable detail this codebase
/// already keeps out of scope for DRAM cells and the AY-5-3600's RC
/// oscillator, so the times are supplied directly as <see cref="PulseTicks"/>
/// / <see cref="LowTicks"/> - counts of <see cref="Tick"/> calls - and the
/// caller turns R and C into those counts.
///
/// The trigger is treated as edge-sensitive and retriggerable - the
/// behaviour of the NE556/NE558 multi-timer packages, and all the Apple II
/// paddle circuit needs: each falling edge restarts the on-time from zero,
/// whether or not <see cref="Out"/> is already high.
/// </summary>
public sealed class Ne555Chip
{
    private bool _triggerBar = true;

    /// <summary>
    /// Trigger, active low. A high-to-low transition starts (or restarts)
    /// the output pulse, unless <see cref="ResetBar"/> is held low.
    /// </summary>
    public bool TriggerBar
    {
        set
        {
            var fallingEdge = _triggerBar && !value;
            _triggerBar = value;

            if (fallingEdge && _resetBar)
            {
                _elapsedTicks = 0;
                Out = true;
            }
        }
    }

    private bool _resetBar = true;

    /// <summary>
    /// Reset, active low. While held low, <see cref="Out"/> is forced low
    /// and no timing runs.
    /// </summary>
    public bool ResetBar
    {
        set
        {
            _resetBar = value;

            if (!value)
            {
                _elapsedTicks = 0;
                Out = false;
            }
        }
    }

    /// <summary>
    /// The output on-time, in <see cref="Tick"/> calls. Normally set once
    /// before triggering; if changed mid-pulse it is compared against the
    /// already-elapsed count on the next <see cref="Tick"/>.
    /// </summary>
    public uint PulseTicks { private get; set; }

    /// <summary>
    /// The output off-time, in <see cref="Tick"/> calls. Zero (the default)
    /// leaves the timer monostable - <see cref="Out"/> stays low after a
    /// pulse until the next trigger. A non-zero value makes it free-running:
    /// <see cref="Out"/> falls low for this many ticks and then returns high
    /// on its own.
    /// </summary>
    public uint LowTicks { private get; set; }

    /// <summary>
    /// The output (pin 3): high from a trigger until <see cref="PulseTicks"/>
    /// ticks have elapsed.
    /// </summary>
    public bool Out { get; private set; }

    private uint _elapsedTicks;

    /// <summary>
    /// Advances the timer by one time step: counts down the current on-time
    /// while <see cref="Out"/> is high and, when free-running
    /// (<see cref="LowTicks"/> non-zero) and not held in reset, the off-time
    /// while it is low.
    /// </summary>
    public void Tick()
    {
        if (Out)
        {
            _elapsedTicks++;

            if (_elapsedTicks >= PulseTicks)
            {
                Out = false;
                _elapsedTicks = 0;
            }
        }
        else if (_resetBar && LowTicks != 0)
        {
            _elapsedTicks++;

            if (_elapsedTicks >= LowTicks)
            {
                Out = true;
                _elapsedTicks = 0;
            }
        }
    }
}
