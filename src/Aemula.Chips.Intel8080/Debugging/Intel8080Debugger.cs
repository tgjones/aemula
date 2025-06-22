using Aemula.Core.Debugging;

namespace Aemula.Chips.Intel8080.Debugging;

public sealed class Intel8080Debugger
{
    private readonly Intel8080 _cpu;

    private ushort _startPC;
    private int _startState;

    public Intel8080Debugger(Intel8080 cpu)
    {
        _cpu = cpu;
    }

    public void RegisterStepModes(Debugger debugger)
    {
        debugger.StepModes.Add(
            new DebuggerStepMode(
                "Step Instruction",
                () => _cpu.Pins.Sync && _cpu.Pins.Data == Intel8080.StatusWordFetch && _cpu.Pins.Address != _startPC,
                () => _startPC = _cpu.Pins.Address));

        debugger.StepModes.Add(
            new DebuggerStepMode(
                "Step CPU Cycle",
                () => _cpu.CombinedMachineCycleTypeAndState != _startState,
                () => _startState = _cpu.CombinedMachineCycleTypeAndState));
    }
}
