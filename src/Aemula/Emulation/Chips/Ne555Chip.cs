namespace Aemula.Emulation.Chips;

/// <summary>
/// A 555-type timer wired as a monostable (one-shot) multivibrator: a
/// high-to-low edge on <see cref="TriggerBar"/> drives <see cref="Out"/>
/// high for a fixed on-time, after which <see cref="Out"/> returns low and
/// stays there until the next trigger. Holding <see cref="ResetBar"/> low
/// forces <see cref="Out"/> low and abandons any timing in progress.
///
/// On real hardware the on-time is <c>t = 1.1 * R * C</c>, sensed as the
/// voltage on an external RC network charging through the THRESHOLD pin.
/// Modelling that analog node is the same kind of non-digital-observable
/// detail this codebase already keeps out of scope for DRAM cells and the
/// AY-5-3600's RC oscillator, so the on-time is supplied directly as
/// <see cref="PulseTicks"/> - the number of <see cref="Tick"/> calls
/// <see cref="Out"/> stays high - and the caller turns R and C into that
/// count.
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
    /// The output (pin 3): high from a trigger until <see cref="PulseTicks"/>
    /// ticks have elapsed.
    /// </summary>
    public bool Out { get; private set; }

    private uint _elapsedTicks;

    /// <summary>
    /// Advances the one-shot by one time step. No effect unless
    /// <see cref="Out"/> is currently high.
    /// </summary>
    public void Tick()
    {
        if (!Out)
        {
            return;
        }

        _elapsedTicks++;

        if (_elapsedTicks >= PulseTicks)
        {
            Out = false;
        }
    }
}
