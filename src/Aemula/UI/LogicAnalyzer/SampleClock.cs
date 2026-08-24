using System;

namespace Aemula.UI.LogicAnalyzer;

/// <summary>
/// What drives a <see cref="LogicAnalyzerWindow"/>'s <see cref="LogicAnalyzerRecorder.Sample"/>
/// calls, and the real-world rate that implies for its time axis. By default a window
/// samples once per <see cref="Debugging.Debugger.Ticked"/> at the system's own
/// CyclesPerSecond - see <see cref="LogicAnalyzerWindow"/>'s constructor - but a system
/// whose logic-analyzer-worthy signal changes faster than its own Ticked cadence (e.g.
/// Atari2600's composite video, synthesized at 4x TIA's own tick rate) can supply its own
/// finer-grained clock instead, without LogicAnalyzerWindow needing to know anything
/// about that system.
/// </summary>
public readonly struct SampleClock
{
    private readonly Action<Action> _subscribe;
    private readonly Action<Action> _unsubscribe;

    public double Hz { get; }

    public SampleClock(double hz, Action<Action> subscribe, Action<Action> unsubscribe)
    {
        Hz = hz;
        _subscribe = subscribe;
        _unsubscribe = unsubscribe;
    }

    public void Subscribe(Action handler) => _subscribe(handler);

    public void Unsubscribe(Action handler) => _unsubscribe(handler);
}
