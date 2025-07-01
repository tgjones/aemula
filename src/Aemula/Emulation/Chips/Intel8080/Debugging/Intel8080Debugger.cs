using Aemula.Debugging;

namespace Aemula.Emulation.Chips.Intel8080.Debugging;

public sealed class Intel8080Debugger
{
    private readonly Intel8080Chip _cpu;

    private ushort _startPC;
    private int _startState;

    public Intel8080Debugger(Intel8080Chip cpu)
    {
        _cpu = cpu;
    }

    public void RegisterStepModes(Debugger debugger)
    {
        debugger.StepModes.Add(
            new DebuggerStepMode(
                "Step Instruction",
                () => _cpu.Pins.Sync && _cpu.Pins.Data == Intel8080Chip.StatusWordFetch && _cpu.Pins.Address != _startPC,
                () => _startPC = _cpu.Pins.Address));

        debugger.StepModes.Add(
            new DebuggerStepMode(
                "Step CPU Cycle",
                () => _cpu.CombinedMachineCycleTypeAndState != _startState,
                () => _startState = _cpu.CombinedMachineCycleTypeAndState));
    }
}
