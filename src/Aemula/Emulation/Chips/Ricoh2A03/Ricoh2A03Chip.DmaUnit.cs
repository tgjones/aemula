using Aemula.Emulation.Chips.Mos6502;

namespace Aemula.Emulation.Chips.Ricoh2A03;

partial class Ricoh2A03Chip
{
    // When 4014 is written to, pause CPU and do 256 * read/write cycles from specified page to 2004
    // https://forums.nesdev.com/viewtopic.php?f=3&t=14120
    private sealed class DmaUnit
    {
        public DmaState DmaState;

        private bool _isDmaReadCycle;
        private byte _dmaHiByte;
        private byte _dmaLoByte;

        public void Cycle(ref Mos6502Pins pins)
        {
            // DMA transfer can only become active on a read cycle.
            if (DmaState == DmaState.Pending && _isDmaReadCycle)
            {
                DmaState = DmaState.Active;
            }

            // DMA flips between read / write cycles. This is happening all the time,
            // even if a DMA transfer is not in progress.

            if (_isDmaReadCycle)
            {
                // Only do a read if a DMA transfer is actually active.
                if (DmaState == DmaState.Active)
                {
                    pins.Address = (ushort)((_dmaHiByte << 8) | _dmaLoByte);
                    pins.RW = true;
                }
                _isDmaReadCycle = false;
            }
            else
            {
                // Only do a write if a DMA transfer is actually active.
                if (DmaState == DmaState.Active)
                {
                    pins.Address = 0x2004;
                    pins.RW = false;

                    // Check if we have finished the DMA transfer.
                    if (_dmaLoByte == 0xFF)
                    {
                        DmaState = DmaState.Inactive;

                        // Let CPU continue on next clock cycle.
                        pins.Rdy = false;
                    }
                    else
                    {
                        _dmaLoByte++;
                    }
                }

                _isDmaReadCycle = true;
            }
        }

        public void SetHiByte(byte value)
        {
            _dmaHiByte = value;
            _dmaLoByte = 0;
        }
    }

    private enum DmaState
    {
        Inactive,
        Pending,
        Active,
    }
}
