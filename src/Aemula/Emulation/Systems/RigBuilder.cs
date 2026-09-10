using System;
using System.Collections.Generic;
using Aemula.Emulation.Peripherals;

namespace Aemula.Emulation.Systems;

// Assembles a Rig generically: build the system from its resolved slot
// configuration, then build and cable every peripheral the system and its
// fitted cards asked for. This is the one place a system's peripherals are
// materialised - the per-(port, device) wiring lives in each PeripheralRequest,
// declared next to the card or the motherboard port that needs it, so nothing
// here names a concrete card or peripheral type.
public static class RigBuilder
{
    public static Rig Build(
        Func<ExpansionSlotConfiguration, EmulatedSystem> createSystem,
        ExpansionSlotConfiguration slotConfiguration,
        IReadOnlyList<ExpansionSlot> slots)
    {
        var system = createSystem(slotConfiguration.ResolvedAgainst(slots));

        var context = new PeripheralContext(system);
        var peripherals = new List<IPeripheral>();
        foreach (var request in system.PeripheralRequests)
        {
            var peripheral = request.Create(context);
            request.Attach(peripheral);
            peripherals.Add(peripheral);
        }

        return new Rig(system, peripherals);
    }
}
