using Aemula.Emulation.Chips;

namespace Aemula.Emulation.Systems.BbcMicro;

public sealed partial class BbcMicroSystem
{
    // The SAA5050 (IC5) reads its character codes through IC15, a latch on the
    // RAM data bus that is held clear unless MA13 (TTXVDU) is set. Its data
    // inputs D0-D5 are the latch's outputs for BD0-BD5, and D6 comes from the
    // third gate of IC36 (pins 1, 2 and 13, the last tied high): the inverse
    // of the latched BD6, from IC37's first inverter, and the 6845's DISPEN.
    // The gate makes D6 high whenever DISPEN is low, so the codes the chip is
    // given outside the displayed area are all printable ones and no control
    // code can disturb a row's state there. LOSE (pin 26) is the DISPEN the
    // latch took with the character, and CRS (pin 14) the 6845's RA0 inverted
    // by the fourth gate of IC43.
    //
    // IC37's third gate inverts the video ULA's 1MHz clock (pin 4) into _N352,
    // which clocks both the latch (IC15) and the chip's F1 input (pin 20) on its
    // rising edge. The chip sees the latch's outputs from before the edge, so
    // a character reaches the screen one character time after it was fetched.
    //
    // The chip's dot clock TR6 (pin 19) is made by a ring of IC40's gates and
    // IC37's with resistors and capacitors setting their delays (R94, R102,
    // R105, R119, C41, C42, C44, C48), XORed by IC38's second gate with the 2MHz
    // clock. That analogue circuit is not modelled. TR6 is taken to be a square
    // wave that starts again at each rising edge of _N352 and has 12 edges in
    // the 1us until the next, which the 16MHz tick cannot space evenly, so
    // they fall on three of every four ticks.
    //
    // The chip draws a cell from every code it is given, so outside the
    // displayed area it would draw the screen memory that follows the row.
    // The skew in R8 makes DISPEN rise just before the edge that starts the
    // first character's cell, so the cell is visible if DISPEN was high at
    // the edge that started it.
    private bool _n352;
    private bool _tr6;
    private int _teletextTicks;
    private bool _teletextVisible;

    private void StepTeletext()
    {
        var teletextAddress = (Crtc.MA & 0x2000) != 0;

        _ic15.Clr = teletextAddress;
        _ic43.A4 = (Crtc.RA & 1) != 0;
        Teletext.Crs = _ic43.Y4;
        Teletext.Dew = Crtc.VSync;

        _ic37.A3 = VideoUla.Clock1MHz;
        var n352 = _ic37.Y3;

        if (n352 && !_n352)
        {
            // The chip takes the code the latch holds from the last edge.
            _ic37.A1 = _ic15.Q7;
            _ic36.A3 = _ic37.Y1;
            _ic36.B3 = Crtc.DispEn;
            _ic36.C3 = true;

            Teletext.Data = (byte)(
                (_ic15.Q3 ? 0x01 : 0)
                | (_ic15.Q4 ? 0x02 : 0)
                | (_ic15.Q2 ? 0x04 : 0)
                | (_ic15.Q5 ? 0x08 : 0)
                | (_ic15.Q1 ? 0x10 : 0)
                | (_ic15.Q6 ? 0x20 : 0)
                | (_ic36.Y3 ? 0x40 : 0));
            Teletext.F1 = true;

            _ic15.D0 = Crtc.DispEn;
            _ic15.D1 = (_bd & 0x10) != 0;
            _ic15.D2 = (_bd & 0x04) != 0;
            _ic15.D3 = (_bd & 0x01) != 0;
            _ic15.D4 = (_bd & 0x02) != 0;
            _ic15.D5 = (_bd & 0x08) != 0;
            _ic15.D6 = (_bd & 0x20) != 0;
            _ic15.D7 = (_bd & 0x40) != 0;
            _ic15.Clk = true;
            Teletext.Lose = _ic15.Q0;

            _teletextTicks = 0;
            _teletextVisible = Crtc.DispEn;
        }
        else
        {
            _ic15.Clk = false;
            Teletext.F1 = false;
            _teletextTicks++;

            if (_teletextTicks < 16 && _teletextTicks * 3 / 4 != (_teletextTicks - 1) * 3 / 4)
            {
                _tr6 = !_tr6;
                Teletext.Tr6 = _tr6;
            }
        }

        _n352 = n352;

        VideoUla.RedInput = _teletextVisible && Teletext.Red;
        VideoUla.GreenInput = _teletextVisible && Teletext.Green;
        VideoUla.BlueInput = _teletextVisible && Teletext.Blue;
    }
}
