namespace Aemula.Emulation.Output;

// Which color-TV standard a composite signal is being decoded as. Only Ntsc
// is implemented today (see docs/television-plan.md) - Pal is a real future
// goal here, not speculative scaffolding, which is why this already exists
// as an enum (and why the Ntsc-prefixed classes under Emulation/Output/Ntsc/
// are named that way) rather than only being introduced once Pal support
// starts.
public enum TelevisionStandard
{
    Ntsc,
}
