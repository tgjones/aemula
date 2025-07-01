using System.Numerics;
using Aemula.Debugging;
using ImGuiNET;

namespace Aemula.UI;

public sealed class BreakpointsWindow : DebuggerWindow
{
    private readonly Debugger _debugger;

    public override string DisplayName => "Breakpoints";

    public BreakpointsWindow(Debugger debugger)
    {
        _debugger = debugger;
    }

    protected override void DrawOverride(EmulatorTime time)
    {
        if (ImGui.Button("Add..."))
        {
            _debugger.Breakpoints.AddExecutionBreakpoint(false, _debugger.LastPC);
        }
        ImGui.SameLine();

        if (ImGui.Button("Disable All"))
        {
            _debugger.Breakpoints.DisableAll();
        }
        ImGui.SameLine();

        if (ImGui.Button("Delete All"))
        {
            _debugger.Breakpoints.DeleteAll();
        }

        ImGui.Separator();

        ImGui.BeginChild("##breakpoint_list", Vector2.Zero, false);

        var deleteIndex = -1;
        for (var i = 0; i < _debugger.Breakpoints.NumBreakpoints; i++)
        {
            ImGui.PushID(i);

            ref var breakpoint = ref _debugger.Breakpoints.GetBreakpoint(i);

            ImGui.Checkbox("##enabled", ref breakpoint.Enabled);
            ImGui.SameLine();

            ImGui.PushItemWidth(100);

            var updateValue = false;
            if (ImGuiComboBreakpointType(ref breakpoint.Type))
            {
                updateValue = true;
            }

            ImGui.PopItemWidth();

            var breakpointType = _debugger.Breakpoints.BreakpointTypeDefinitions[breakpoint.Type];

            if (breakpointType.ShowAddress)
            {
                ImGui.SameLine();

                var oldAddress = breakpoint.Address;

                ImGuiUtility.InputUInt16("##addr", ref breakpoint.Address);

                if (updateValue || oldAddress != breakpoint.Address)
                {
                    breakpoint.Value = breakpointType.GetDefaultValue(breakpoint.Address);
                }
            }

            if (breakpointType.ShowComparison)
            {
                ImGui.SameLine();
                ImGui.PushItemWidth(42);
                var conditionAsInt = (int)breakpoint.Condition;
                if (ImGui.Combo("##condition", ref conditionAsInt, "==\0!=\0>\0>=\0<\0<=\0"))
                {
                    breakpoint.Condition = (BreakpointCondition)conditionAsInt;
                }
                ImGui.PopItemWidth();
            }

            if (breakpointType.ShowValueByte || breakpointType.ShowValueWord)
            {
                ImGui.SameLine();
                if (breakpointType.ShowValueByte)
                {
                    var valueAsByte = (byte)breakpoint.Value;
                    if (ImGuiUtility.InputByte("##valuebyte", ref valueAsByte))
                    {
                        breakpoint.Value = valueAsByte;
                    }
                }
                else
                {
                    var valueAsUInt16 = (ushort)breakpoint.Value;
                    if (ImGuiUtility.InputUInt16("##valueword", ref valueAsUInt16))
                    {
                        breakpoint.Value = valueAsUInt16;
                    }
                }
            }

            ImGui.SameLine();

            if (ImGui.Button("Del"))
            {
                deleteIndex = i;
            }

            ImGui.PopID();
        }

        if (deleteIndex != -1)
        {
            _debugger.Breakpoints.RemoveAt(deleteIndex);
        }

        ImGui.EndChild();
    }

    private bool ImGuiComboBreakpointType(ref int type)
    {
        return ImGuiUtility.Combo("##type", ref type, _debugger.Breakpoints.BreakpointTypeNames);
    }
}
