using System.IO;

namespace Aemula.Tests.Emulation.Output;

internal static class SmpteAsset
{
    // smpte.ntsc's raw bytes are on a 0-200 scale - its own capture's own
    // calibration, where the 100 IRE white bar sits at raw 200 - not the
    // shared byte scale every producer and Television now use (sync tip 0,
    // blanking 64, reference white 224). The asset's 100 IRE white bar is
    // reference white, so it is mapped to 224: raw * 224 / 200. Sync and
    // blanking then follow the asset's own ratio, which is already ~spec.
    // Rescaling once here, at the point the asset is loaded, keeps
    // Television itself agnostic to the fact that differently-calibrated
    // producers exist.
    public static byte[] LoadNormalized()
    {
        var filePath = Path.GetFullPath(Path.Combine("Emulation", "Output", "Assets", "smpte.ntsc"));
        var rawBytes = File.ReadAllBytes(filePath);

        var normalized = new byte[rawBytes.Length];
        for (var i = 0; i < rawBytes.Length; i++)
        {
            normalized[i] = (byte)(rawBytes[i] * 224 / 200);
        }

        CorrectNonStandardBurstPhase(normalized);

        return normalized;
    }

    // This asset's color burst is transmitted exactly 180 degrees away from
    // where RS-170A puts it, and this rotates it back.
    //
    // That's a claim about the asset, not about this project's decoder, and
    // it was measured directly out of the raw bytes rather than inferred
    // from how anything decodes: correlating each of the seven bars'
    // chroma against the same line's burst window (a phase *difference*, so
    // no phase convention, axis assignment or signal polarity enters into
    // it at all) gives 167.5 / 283.9 / 241.3 / 60.9 / 103.9 / 347.5 degrees
    // for yellow / cyan / green / magenta / red / blue. Those are, to
    // better than half a degree, the standard NTSC vectorscope bar targets
    // (167.1 / 283.5 / 240.7 / 60.9 / 103.5 / 347.1) - but those targets are
    // defined with *burst at 180 degrees*, on the -(B'-Y') axis, not with
    // burst at zero. So the bars in this file sit at their correct absolute
    // angles while its burst sits half a turn from where a real encoder
    // would have put it: a bug in whatever synthesized the file (and it is
    // synthesized - every line is byte-identical to the last), not a
    // property of NTSC.
    //
    // Correcting it here, at the asset boundary, rather than anywhere in
    // the decoder, is the point: NtscYiqDecoder's burst-to-I-axis rotation
    // is now the plain spec figure with no per-signal calibration on top
    // (see that constant's remarks for how a compensating 180 degrees there
    // used to hide this), so a signal that doesn't conform to the spec has
    // to be brought back to it before it reaches Television, exactly as a
    // real receiver's burst-locked oscillator would be entitled to assume.
    //
    // Mechanically: rotating a sinusoid 180 degrees is just negating it, and
    // burst swings symmetrically about the blanking level, so reflecting
    // each burst window's samples about that window's own mean rotates the
    // burst without touching its amplitude, the blanking level it rides on,
    // or one single sample of picture, sync or chroma anywhere else in the
    // line.
    private static void CorrectNonStandardBurstPhase(byte[] samples)
    {
        // Between this asset's sync tip (raw 4 -> 4 here) and its blanking
        // level (raw 60 -> 67 here) - the same "is this sample sync or not"
        // question NtscSyncSeparator answers, asked much more crudely,
        // since all this needs is to find the trailing edges, not to
        // classify or measure the pulses.
        const byte SyncThreshold = 40;

        // Saturated dark bars swing their chroma below SyncThreshold for a
        // single sample per subcarrier cycle, so "the previous sample was
        // low and this one isn't" on its own finds a false trailing edge on
        // every such cycle, right in the middle of the picture. A real sync
        // pulse is ~67 samples wide (and even the vertical interval's
        // equalizing pulses are ~33), so requiring a run this long ahead of
        // the edge separates the two with enormous margin in both
        // directions.
        const int MinimumSyncRunLength = 20;

        // Measured from the sync trailing edge found below, and deliberately
        // wider than the burst itself (NtscTiming's own window is 8.6
        // samples in, 36 long): the extra margin either side is blanking,
        // where reflecting about the window mean - which *is* the blanking
        // level, since burst averages to it - is exactly a no-op, so
        // widening costs nothing and guarantees the real burst's ramp-in and
        // ramp-out are rotated along with its body rather than left behind
        // as a discontinuity for the PLL to integrate over.
        const int WindowStart = 6;
        const int WindowLength = 40;

        // Real burst on this asset swings roughly +/-26 on the normalized
        // scale (raw +/-23 * 224/200); anything flatter than this is a line
        // that simply has no burst (the vertical interval), which needs no
        // correction.
        const int MinimumBurstSwing = 20;

        var syncRunLength = 0;

        for (var i = 0; i < samples.Length; i++)
        {
            if (samples[i] < SyncThreshold)
            {
                syncRunLength++;
                continue;
            }

            // A sync trailing edge: this sample is the first non-sync one
            // after a run long enough to have been a real pulse.
            var wasSyncTrailingEdge = syncRunLength >= MinimumSyncRunLength;
            syncRunLength = 0;

            if (!wasSyncTrailingEdge)
            {
                continue;
            }

            var start = i + WindowStart;
            if (start + WindowLength > samples.Length)
            {
                break;
            }

            var sum = 0;
            var min = byte.MaxValue;
            var max = byte.MinValue;

            for (var j = start; j < start + WindowLength; j++)
            {
                sum += samples[j];
                if (samples[j] < min) min = samples[j];
                if (samples[j] > max) max = samples[j];
            }

            // Sync level inside the window means this "line" is really part
            // of the vertical interval's own faster pulse train, and the
            // window has run into the *next* pulse rather than sitting in
            // one line's back porch. Those lines carry no burst to rotate
            // (and the PLL flywheels through them - see
            // NtscColorBurstPll.FinishBurstWindow), so leave them alone.
            if (min < SyncThreshold || max - min < MinimumBurstSwing)
            {
                continue;
            }

            var mean = sum / (double)WindowLength;

            for (var j = start; j < start + WindowLength; j++)
            {
                samples[j] = (byte)System.Math.Clamp((int)System.Math.Round(2.0 * mean - samples[j]), 0, 255);
            }
        }
    }
}
