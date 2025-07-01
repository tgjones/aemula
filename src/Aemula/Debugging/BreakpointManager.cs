using System;
using System.Collections.Generic;

namespace Aemula.Debugging;

public sealed class BreakpointManager
{
    private const int MaxBreakpoints = 32;

    internal const string ExecutionTypeLabel = "Break at";
    private const string ValueByteLabel = "Byte at";
    private const string ValueWordLabel = "Word at";

    private readonly Breakpoint[] _breakpoints;
    private readonly DebuggerMemoryCallbacks _memoryCallbacks;

    private readonly List<BreakpointTypeDefinition> _breakpointTypeDefinitions;
    private readonly List<string> _breakpointTypeNames;

    internal int NumBreakpoints { get; private set; }

    internal IReadOnlyList<BreakpointTypeDefinition> BreakpointTypeDefinitions => _breakpointTypeDefinitions;

    internal IReadOnlyList<string> BreakpointTypeNames => _breakpointTypeNames;

    internal BreakpointManager(DebuggerMemoryCallbacks memoryCallbacks)
    {
        _breakpointTypeDefinitions = new List<BreakpointTypeDefinition>(16);
        _breakpointTypeNames = new List<string>(16);

        AddBreakpointTypeDefinition(new BreakpointTypeDefinition(
            ExecutionTypeLabel,
            true,
            false,
            false,
            false,
            (in Breakpoint breakpoint, ushort pc) => pc == breakpoint.Address,
            address => 0));

        AddBreakpointTypeDefinition(new BreakpointTypeDefinition(
            ValueByteLabel,
            true,
            true,
            true,
            false,
            (in Breakpoint breakpoint, ushort pc) => EvaluateCondition(breakpoint, _memoryCallbacks.Read(breakpoint.Address)),
            address => _memoryCallbacks.Read(address)));

        AddBreakpointTypeDefinition(new BreakpointTypeDefinition(
            ValueWordLabel,
            true,
            true,
            false,
            true,
            (in Breakpoint breakpoint, ushort pc) => EvaluateCondition(breakpoint, _memoryCallbacks.ReadWord(breakpoint.Address)),
            address => _memoryCallbacks.ReadWord(address)));

        _breakpoints = new Breakpoint[MaxBreakpoints];
        NumBreakpoints = 0;

        _memoryCallbacks = memoryCallbacks;
    }

    public void AddBreakpointTypeDefinition(in BreakpointTypeDefinition breakpointTypeDefinition)
    {
        _breakpointTypeDefinitions.Add(breakpointTypeDefinition);

        _breakpointTypeNames.Add(breakpointTypeDefinition.Label);
    }

    public void AddExecutionBreakpoint(bool enabled, ushort address)
    {
        AddBreakpoint(ExecutionTypeLabel, enabled, address);
    }

    public void ToggleExecutionBreakpoint(ushort address)
    {
        var index = FindIndex(ExecutionTypeLabel, address);
        if (index >= 0)
        {
            RemoveAt(index);
        }
        else
        {
            AddExecutionBreakpoint(true, address);
        }
    }

    public void AddValueByteBreakpoint(bool enabled, ushort address)
    {
        AddBreakpoint(ValueByteLabel, enabled, address);
    }

    public void AddValueWordBreakpoint(bool enabled, ushort address)
    {
        AddBreakpoint(ValueWordLabel, enabled, address);
    }

    private void AddBreakpoint(string label, bool enabled, ushort address)
    {
        if (NumBreakpoints >= MaxBreakpoints)
        {
            return;
        }

        var typeIndex = GetBreakpointTypeIndex(label);

        var type = _breakpointTypeDefinitions[typeIndex];

        var breakpoint = new Breakpoint
        {
            Type = typeIndex,
            Condition = BreakpointCondition.Equal,
            Address = address,
            Value = type.GetDefaultValue(address),
            Enabled = enabled,
        };

        _breakpoints[NumBreakpoints++] = breakpoint;
    }

    public int FindIndex(string label, ushort address)
    {
        var typeIndex = GetBreakpointTypeIndex(label);

        for (var i = 0; i < NumBreakpoints; i++)
        {
            ref readonly var breakpoint = ref _breakpoints[i];
            if (breakpoint.Type == typeIndex && breakpoint.Address == address)
            {
                return i;
            }
        }

        return -1;
    }

    private int GetBreakpointTypeIndex(string label) => _breakpointTypeNames.IndexOf(label);

    internal ref Breakpoint GetBreakpoint(int index) => ref _breakpoints[index];

    public void RemoveAt(int index)
    {
        for (var i = index; i < NumBreakpoints - 1; i++)
        {
            _breakpoints[i] = _breakpoints[i + 1];
        }
        NumBreakpoints--;
    }

    public void DisableAll()
    {
        for (var i = 0; i < NumBreakpoints; i++)
        {
            _breakpoints[i].Enabled = false;
        }
    }

    public void EnableAll()
    {
        for (var i = 0; i < NumBreakpoints; i++)
        {
            _breakpoints[i].Enabled = true;
        }
    }

    public void DeleteAll()
    {
        NumBreakpoints = 0;
    }

    public bool ShouldBreak(ushort pc)
    {
        for (var i = 0; i < NumBreakpoints; i++)
        {
            ref readonly var breakpoint = ref _breakpoints[i];

            if (!breakpoint.Enabled)
            {
                continue;
            }

            var breakpointTypeDefinition = _breakpointTypeDefinitions[breakpoint.Type];

            if (breakpointTypeDefinition.Evaluate(breakpoint, pc))
            {
                return true;
            }
        }

        return false;
    }

    private static bool EvaluateCondition(in Breakpoint breakpoint, ushort value)
    {
        return breakpoint.Condition switch
        {
            BreakpointCondition.Equal => value == breakpoint.Value,
            BreakpointCondition.NotEqual => value != breakpoint.Value,
            BreakpointCondition.GreaterThan => value > breakpoint.Value,
            BreakpointCondition.GreaterThanOrEqual => value >= breakpoint.Value,
            BreakpointCondition.LessThan => value < breakpoint.Value,
            BreakpointCondition.LessThanOrEqual => value <= breakpoint.Value,
            _ => throw new InvalidOperationException(),
        };
    }
}
