using Aemula.Debugging;
using Hexa.NET.ImGui;

namespace Aemula.UI.Oscilloscope;

/// <summary>
/// Phase 0: proves the recording plumbing end-to-end by listing channels and the
/// running sample count. Waveform rendering (via Hexa.NET.ImPlot) comes in later
/// phases - see docs/oscilloscope-plan.md.
/// </summary>
public sealed class OscilloscopeWindow : DebuggerWindow
{
    private readonly Debugger _debugger;
    private readonly ScopeRecorder _recorder;

    public override string DisplayName => "Oscilloscope";

    public override Pane PreferredPane => Pane.Bottom;

    public OscilloscopeWindow(Debugger debugger, ScopeChannelNode channels)
    {
        _debugger = debugger;
        _recorder = new ScopeRecorder(channels);

        _debugger.Ticked += OnTicked;
    }

    private void OnTicked()
    {
        if (!IsOpen)
        {
            return;
        }

        _recorder.Sample();
    }

    protected override void DrawOverride(EmulatorTime time)
    {
        ImGui.Text($"{_recorder.Channels.Length} channel(s), {_recorder.TotalSamples} sample(s) recorded");

        ImGui.Spacing();

        foreach (var channel in _recorder.Channels)
        {
            ImGui.BulletText($"{channel.Name} ({channel.Kind}, {channel.BitWidth}-bit)");
        }
    }

    public override void Dispose()
    {
        base.Dispose();

        _debugger.Ticked -= OnTicked;
    }
}
