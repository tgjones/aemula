namespace Aemula.Core.Debugging;

public readonly record struct BreakpointTypeDefinition(
    string Label,
    bool ShowAddress,
    bool ShowComparison,
    bool ShowValueByte,
    bool ShowValueWord,
    EvaluateBreakpointDelegate Evaluate,
    GetDefaultValueDelegate GetDefaultValue);

public delegate bool EvaluateBreakpointDelegate(in Breakpoint breakpoint, ushort pc);

public delegate int GetDefaultValueDelegate(ushort address);
