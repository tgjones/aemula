namespace Aemula.Debugging;

public struct Breakpoint
{
    public int Type;
    public BreakpointCondition Condition;
    public bool Enabled;
    public ushort Address;
    public int Value;
}
