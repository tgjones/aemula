using System.Collections.Generic;
using Aemula.Debugging;
using Aemula.Debugging.LogicAnalyzer;
using Aemula.Emulation.Chips.Mos6502.Debugging;

namespace Aemula.Emulation.Systems.AppleII.Debugging;

public sealed class AppleIIDebugger : Debugger
{
    private readonly AppleIISystem _appleII;

    public AppleIIDebugger(AppleIISystem appleII)
        : base(appleII, CreateMemoryCallbacks(appleII))
    {
        _appleII = appleII;

        AttachCpuDebugger(new Mos6502Debugger(appleII.Cpu));
    }

    protected override void AddChannelNodes(List<ChannelNode> nodes)
    {
        base.AddChannelNodes(nodes);

        nodes.Add(new ChannelGroup("Video Timing",
        [
            Channel.Digital("HBL", () => _appleII.Hbl),
            Channel.Digital("VBL", () => _appleII.Vbl),
            Channel.Digital("Color Burst Gate", () => _appleII.ColorBurstGate),
            Channel.Digital("Phase 0", () => _appleII.Phase0),
            Channel.Digital("HSync", () => _appleII.HSyncPulse),
            Channel.Digital("VSync", () => _appleII.VSyncPulse),
            Channel.Digital("Video Data", () => _appleII.VideoDataBit),
            Channel.Analog("Composite Video", () => _appleII.CurrentCompositeVideoSample, 0, AppleIISystem.WhiteVoltage, "V"),
        ]));
        nodes.Add(new ChannelGroup("Game I/O",
        [
            Channel.Digital("Speaker", () => _appleII.SpeakerBit),
            Channel.Digital("AN0", () => _appleII.Annunciator0),
            Channel.Digital("AN1", () => _appleII.Annunciator1),
            Channel.Digital("AN2", () => _appleII.Annunciator2),
            Channel.Digital("AN3", () => _appleII.Annunciator3),
            Channel.Digital("PB0", () => _appleII.PushButton(0)),
            Channel.Digital("PB1", () => _appleII.PushButton(1)),
            Channel.Digital("PB2", () => _appleII.PushButton(2)),
            Channel.Digital("PDL0 Timer", () => _appleII.PaddleTimerOut(0)),
            Channel.Digital("PDL1 Timer", () => _appleII.PaddleTimerOut(1)),
            Channel.Digital("PDL2 Timer", () => _appleII.PaddleTimerOut(2)),
            Channel.Digital("PDL3 Timer", () => _appleII.PaddleTimerOut(3)),
        ]));
    }

    private static DebuggerMemoryCallbacks CreateMemoryCallbacks(AppleIISystem appleII)
    {
        return new DebuggerMemoryCallbacks(appleII.ReadByteDebug, appleII.WriteByteDebug);
    }

    protected override Disassembler CreateDisassembler()
    {
        return new Mos6502Disassembler(
            MemoryCallbacks,
            new Dictionary<ushort, string>(),
            registerCallbacks: new Mos6502RegisterCallbacks(() => _appleII.Cpu.X, () => _appleII.Cpu.Y));
    }
}
