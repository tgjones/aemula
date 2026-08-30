using System.Threading.Tasks;

namespace Aemula.Tests.Emulation.Systems.Nes;

internal class Ricoh2C02Tests
{
    // The orphaned Ppu/Ricoh2C02 prototype this class used to exercise has been
    // folded into Ricoh2C02Chip and deleted. Step 6 of the NES composite-video
    // work rewrites this against the transistor-level Flawless2C02 oracle:
    // it drives Ricoh2C02Chip and Flawless2C02 in lockstep with rendering
    // enabled and asserts the behavioural vid_sync_* / vid_burst_* / vid_luma*_*
    // outputs match node-for-node each half-cycle.
    // TODO(step 6): rewrite as the Flawless2C02 comparison test.
    [Test, Skip("Rewritten against Flawless2C02 in a later change")]
    public async Task PlaceholderUntilFlawless2C02ComparisonExists()
    {
        await Task.CompletedTask;
    }
}
