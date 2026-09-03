namespace Aemula.Emulation.Chips.Ricoh2A03;

partial class Ricoh2A03Chip
{
    // When 4014 is written to, pause CPU and do 256 * read/write cycles from specified page to 2004
    // https://forums.nesdev.com/viewtopic.php?f=3&t=14120
    private sealed class DmaUnit
    {
        // Phase the get / put divider comes out of reset on, matched against the
        // transistor-level reference chip (Ricoh2A03ChipTests.OamDma).
        private const bool GetCycleAfterReset = true;

        private DmaState _state;

        // The unit alternates between a "get" (read from the source page) and a
        // "put" (write to $2004) cycle, and that divider free-runs whether or
        // not a transfer is in progress - it only stops while /RES is low. It is
        // what makes a transfer take 513 or 514 cycles: a request landing on a
        // put waits one more cycle for the next get.
        private bool _isGetCycle = GetCycleAfterReset;

        private byte _page;
        private byte _offset;

        /// <summary>
        /// Latches the source page from a $4014 write and asks the core to halt.
        /// The core doesn't stop dead: it finishes this write cycle, then the
        /// opcode fetch after it, and only then freezes - repeating that fetch
        /// until RDY is released again.
        /// </summary>
        public void Request(Ricoh2A03Chip chip, byte page)
        {
            _page = page;
            _offset = 0;
            _state = DmaState.Pending;

            chip.Rdy = true;
        }

        /// <summary>
        /// Runs one CPU cycle's worth of the transfer. Called at Phi0 falling,
        /// just after the core has put its own address on the pins, so that a
        /// cycle the DMA unit claims overrides them for the whole cycle.
        /// </summary>
        public void Cycle(Ricoh2A03Chip chip)
        {
            // Stop driving the pins; the transfer takes them back below for the
            // cycles it actually owns.
            chip._address = null;
            chip._rw = null;

            if (!chip._cpuCore.Res)
            {
                _isGetCycle = GetCycleAfterReset;
                return;
            }

            switch (_state)
            {
                case DmaState.Pending:
                    // Sync && Rdy is the last fetch the core gets to make before
                    // it freezes, so the bus is ours from the cycle after it.
                    if (chip._cpuCore.Sync && chip._cpuCore.Rdy)
                    {
                        _state = DmaState.Halted;
                    }
                    break;

                case DmaState.Halted:
                    if (_isGetCycle)
                    {
                        _state = DmaState.Active;
                    }
                    break;

                case DmaState.Finishing:
                    // One cycle after the last put. The core is released here,
                    // not on the put itself, so that the fetch it repeats this
                    // cycle is the one it goes on to execute - released a cycle
                    // earlier it would latch the last DMA byte as its opcode.
                    _state = DmaState.Inactive;
                    chip.Rdy = false;
                    break;
            }

            if (_state == DmaState.Active)
            {
                if (_isGetCycle)
                {
                    chip._address = (ushort)((_page << 8) | _offset);
                    chip._rw = true;
                }
                else
                {
                    chip._address = OamDataAddress;
                    chip._rw = false;

                    if (_offset == 0xFF)
                    {
                        _state = DmaState.Finishing;
                    }
                    else
                    {
                        _offset++;
                    }
                }
            }

            _isGetCycle = !_isGetCycle;
        }
    }

    private enum DmaState
    {
        Inactive,

        /// <summary>$4014 has been written; waiting for the core to freeze.</summary>
        Pending,

        /// <summary>Core frozen; waiting for a get cycle to start on.</summary>
        Halted,

        Active,

        /// <summary>Last put done; releasing the core on the next cycle.</summary>
        Finishing,
    }
}
