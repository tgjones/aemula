const samplesPerSecond = 157500000.0 / 11.0;
const us = samplesPerSecond / 1000000.0; // Samples per microsecond

// Horizontal times in samples.
const sync = 4.7 * us;
const breezeway = 0.6 * us;
const colorBurstStart = Math.trunc(sync + breezeway);
const colorBurst = 2.5 * us;
const backPorch = 1.6 * us;
const frontPorch = 1.5 * us;
const blanking = sync + breezeway + colorBurst + backPorch + frontPorch;

const baseLoad = 0.5;

const lineSamples = 910.0;
const active = lineSamples - blanking;
const preActive = blanking - frontPorch;

// The following parameter specifies how many samples early or late the
// horizontal sync pulse can be and still be recognized (assuming good
// signal fidelity). This sets the angle of the diagonal lines that
// vertical lines become when horizontal sync is lost.
const driftSamples = 8;
const minSamplesPerLine = Math.trunc(lineSamples - driftSamples);
const maxSamplesPerLine = Math.trunc(lineSamples + driftSamples);

// We always consume a scanline at a time, and we won't be called to
// process until we're sure we have a scanline.
const n = maxSamplesPerLine;

// Vertical times in lines.
const preSyncLines = 3.0;
const syncLines = 3.0;
const postSyncLines = 14.0;
const lines = 262.5;
const blankingLines = preSyncLines + syncLines + postSyncLines;
const activeLines = lines - blankingLines;

// The following parameter specifies how many lines early or late the
// vertical sync pulse can be and still be recognized (assuming good
// signal fidelity). This sets the "roll speed" of the picture when
// vertical sync is lost. Empirically determined from video of an IBM
// 5153 monitor.
const driftLines = 14;
const minLinesPerField = Math.trunc(lines - driftLines);
const maxLinesPerField = Math.trunc(lines + driftLines);

const gamma = new Uint8ClampedArray(256);
for (let i = 0; i < gamma.length; i++) {
    gamma[i] = Math.trunc(Math.pow(i / 255.0, 1.9) * 255.0);
}

function clamp(num, min, max) {
    return Math.min(Math.max(num, min), max);
}

export class CompositeMonitor {
    brightness = 0.06;
    contrast = 3.0;
    saturation = 0.7;
    tint = 0.0;// 18.0;
    horizontalSize = 0.95;
    horizontalPosition = 0;
    verticalSize = 0.93;
    verticalPosition = -0.01;
    verticalHold = 280;
    horizontalHold = 25;
    bloomFactor = 10.0;

    linesVisible = activeLines * this.verticalSize;
    lineTop = postSyncLines + activeLines * (0.5 + this.verticalPosition - this.verticalSize / 2.0);
 
    frames = 0;
    phase = 0;

    line = 0;
    foundVerticalSync = false;
    verticalSync = 0;
    verticalSyncPhase = 0.0;

    hysteresisCount = 0;
    colorMode = false;

    linePeriod = Math.trunc(lineSamples);

    lefts = new Float32Array(maxLinesPerField);  // First sample on each line
    widths = new Float32Array(maxLinesPerField); // How many samples visible on each line

    colorBurstPhase = new Float32Array(4);
    lockedColorBurstPhase = new Float32Array(4);

    crtLoad = baseLoad;

    topLine = 0;
    bottomLine = 0;

    iqMultipliers = new Int32Array(4);

    /** @type Uint8Array */
    buffer;

    /** @type OffscreenCanvas */
    canvas;

    canvasContext;

    /** @type ImageData */
    imageData;

    bufferIndex = 0;

    delay = new Int32Array(19 + maxSamplesPerLine);

    /**
     * @param {Uint8Array} buffer 
     */
    constructor(buffer) {
        this.buffer = buffer;

        this.canvas = new OffscreenCanvas(maxSamplesPerLine, maxLinesPerField);

        this.canvasContext = this.canvas.getContext('2d');

        this.imageData = this.canvasContext.getImageData(0, 0, maxSamplesPerLine, maxLinesPerField);
    }

    /** Processes a single scanline */
    process() {
        // Find the horizontal sync position.
        let offset = 0;
        for (let i = 0; i < driftSamples * 2; i++, offset++) {
            if (this.buffer[this.bufferIndex + offset] + this.buffer[this.bufferIndex + offset + 1] < this.horizontalHold * 2) {
                break;
            }
        }

        // We use a phase-locked loop like real hardware does, in order to
        // avoid losing horizontal sync if the pulse is missing for a line or
        // two, and so that we get the correct "wobble" behavior.
        let linePeriod = maxSamplesPerLine - offset;
        this.linePeriod = Math.trunc((2 * this.linePeriod + linePeriod) / 3);
        this.linePeriod = clamp(this.linePeriod, minSamplesPerLine, maxSamplesPerLine);
        offset = maxSamplesPerLine - this.linePeriod;

        // Find the vertical sync position.
        if (!this.foundVerticalSync) {
            for (let j = 0; j < maxSamplesPerLine; j += 57) {
                this.verticalSync = ((this.verticalSync * 232) >> 8) + this.buffer[this.bufferIndex + j] - 60;
                if (this.verticalSync < -this.verticalHold || this.line == 2 * driftLines) {
                    // To render interlaced signals correctly, we need to
                    // figure out where the vertical sync pulse happens
                    // relative to the horizontal pulse. This determines the
                    // vertical position of the raster relative to the screen.
                    this.verticalSyncPhase = j / maxSamplesPerLine;

                    // Now we can find out which scanlines are at the top and
                    // bottom of the screen.
                    this.topLine = Math.trunc(0.5 + this.lineTop + this.verticalSyncPhase);
                    this.bottomLine = Math.trunc(1.5 + this.linesVisible + this.lineTop + this.verticalSyncPhase);
                    this.line = 0;
                    this.foundVerticalSync = true;
                    break;
                }
            }
        }

        // Determine the phase and strength of the color signal from the color
        // burst, which starts shortly after the horizontal sync pulse ends.
        // The color burst is 9 cycles long, and we look at the middle 5
        // cycles.
        const p = offset & (~3 >>> 0);
        for (let i = colorBurstStart + 8; i < colorBurstStart + 28; i++) {
            const colorBurstFadeConstant = 1.0 / 128.0;
            const colorBurstPhaseIndex = (i + this.phase) & 3;
            this.colorBurstPhase[colorBurstPhaseIndex] = 
                this.colorBurstPhase[colorBurstPhaseIndex] * (1.0 - colorBurstFadeConstant)
                + (this.buffer[this.bufferIndex + p + i] - 60) * colorBurstFadeConstant;
        }
        let total = 0.1;
        for (let i = 0; i < 4; i++) {
            total += this.colorBurstPhase[i] * this.colorBurstPhase[i];
        }
        const colorBurstGain = 32.0 / Math.sqrt(total);
        const phaseCorrelation = (offset + this.phase) & 3;
        const colorBurstI = colorBurstGain * (this.colorBurstPhase[2] - this.colorBurstPhase[0]) / 16.0;
        const colorBurstQ = colorBurstGain * (this.colorBurstPhase[3] - this.colorBurstPhase[1]) / 16.0;
        const hf = colorBurstGain * (this.colorBurstPhase[0] - this.colorBurstPhase[1] + this.colorBurstPhase[2] - this.colorBurstPhase[3]);
        const colorMode = ((colorBurstI * colorBurstI + colorBurstQ * colorBurstQ) > 2.8) && (hf < 16.0);
        if (colorMode) {
            for (let i = 0; i < 4; i++) {
                this.lockedColorBurstPhase[i] = this.colorBurstPhase[i];
            }
        }
        // Color killer hysteresis: We only switch between colour mode and
        // monochrome mode if we stay in the new mode for 128 consecutive
        // lines.
        if (this.colorMode != colorMode) {
            this.hysteresisCount++;
            if (this.hysteresisCount == 128) {
                this.colorMode = colorMode;
                this.hysteresisCount = 0;
            }
        } else {
            this.hysteresisCount = 0;
        }

        if (this.foundVerticalSync && this.line >= this.topLine && this.line < this.bottomLine) {
            const y = this.line - this.topLine;

            // Lines with high amounts of brightness cause more load on the
            // horizontal oscillator which decreases horizontal deflection,
            // causing "blooming" (increase in width).
            let totalSignal = 0;
            for (let i = 0; i < active; i++) {
                totalSignal += this.buffer[this.bufferIndex + offset + i] - 60;
            }
            this.crtLoad = 0.4 * this.crtLoad + 0.6 * (baseLoad + (totalSignal - 42000.0) / 140000.0);
            const bloom = clamp(this.bloomFactor * this.crtLoad, -2.0, 10.0);
            const horizontalSize = (1.0 - 6.3 * bloom / active) * this.horizontalSize;
            const samplesVisible = active * horizontalSize;
            const sampleLeft = preActive + active * (0.5 + this.horizontalPosition - horizontalSize / 2.0);
            this.lefts[y] = sampleLeft;
            this.widths[y] = samplesVisible;

            const start = Math.trunc(Math.max(sampleLeft - 10, 0));
            const end = Math.trunc(Math.min(sampleLeft + samplesVisible + 10, maxSamplesPerLine - offset));
            const brightness = Math.trunc(this.brightness * 100.0 - 7.5 * 256.0 * this.contrast) << 8;

            let destinationIndex = ((y * this.imageData.width) + start) * 4;

            if (this.colorMode) {
                const yContrast = Math.trunc(this.contrast * 1463.0);
                const radians = Math.PI / 180.0;
                // Is it 103 because that's the angle for red?
                const tintI = -Math.cos((103.0 + this.tint) * radians);
                const tintQ = Math.sin((103.0 + this.tint) * radians);
                const colorBurstI = this.lockedColorBurstPhase[(2 + phaseCorrelation) & 3] - this.lockedColorBurstPhase[(0 + phaseCorrelation) & 3];
                const colorBurstQ = this.lockedColorBurstPhase[(3 + phaseCorrelation) & 3] - this.lockedColorBurstPhase[(1 + phaseCorrelation) & 3];
                // 
                this.iqMultipliers[0] = Math.trunc((colorBurstI * tintI - colorBurstQ * tintQ) * this.saturation * this.contrast * colorBurstGain * 0.352);
                this.iqMultipliers[1] = Math.trunc((colorBurstQ * tintI + colorBurstI * tintQ) * this.saturation * this.contrast * colorBurstGain * 0.352);
                this.iqMultipliers[2] = -this.iqMultipliers[0];
                this.iqMultipliers[3] = -this.iqMultipliers[1];

                let delayIndex = maxSamplesPerLine;
                for (let x = 0; x < 19; x++) {
                    this.delay[delayIndex + x] = 0;
                }
                let sp = offset + start;
                for (let x = start; x < end; x++, delayIndex--) {
                    // We use a low-pass Finite Impulse Response filter to
                    // remove high frequencies (including the color carrier
                    // frequency) from the signal. We could just keep a
                    // 4-sample running average but that leads to sharp edges
                    // in the resulting image.
                    const p = this.delay;
                    const s = this.buffer[this.bufferIndex + sp++] - 60;
                    p[delayIndex + 0] = s;
                    // Apply brightness to y level.
                    const y = (p[delayIndex + 6] + p[delayIndex + 0] + ((p[delayIndex + 5] + p[delayIndex + 1]) << 2) + 7 * (p[delayIndex + 4] + p[delayIndex + 2]) + (p[delayIndex + 3] << 3)) * yContrast + brightness;
                    p[delayIndex + 6] = s * this.iqMultipliers[x & 3];
                    const i = p[delayIndex + 12] + p[delayIndex + 6] + ((p[delayIndex + 11] + p[delayIndex + 7]) << 2) + 7 * (p[delayIndex + 10] + p[delayIndex + 8]) + (p[delayIndex + 9] << 3);
                    p[delayIndex + 12] = s * this.iqMultipliers[(x + 3) & 3];
                    const q = p[delayIndex + 18] + p[delayIndex + 12] + ((p[delayIndex + 17] + p[delayIndex + 13]) << 2) + 7 * (p[delayIndex + 16] + p[delayIndex + 14]) + (p[delayIndex + 15] << 3);

                    // These are similar ratios to the Video Demystified book:
                    // R = 1.975Y + 1.887I + 1.224Q
                    // G = 1.975Y - 0.536I - 1.278Q
                    // B = 1.975Y - 2.189I + 3.367Q

                    const r = gamma[clamp(0, (y + 243 * i + 160 * q) >> 16, 255)];
                    const g = gamma[clamp(0, (y -  71 * i - 164 * q) >> 16, 255)];
                    const b = gamma[clamp(0, (y - 283 * i + 443 * q) >> 16, 255)];
                    this.imageData.data[destinationIndex++] = r;
                    this.imageData.data[destinationIndex++] = g;
                    this.imageData.data[destinationIndex++] = b;
                    this.imageData.data[destinationIndex++] = 0xFF;
                }
            } else {
                let sp = offset + start;
                // 46816 / 1463 = 32
                // 46816 / 65536 = 0.714; 714mV is blanking pedestal?
                const yContrast = Math.trunc(this.contrast * 46816.0);
                //const yContrast = Math.trunc(this.contrast * 65536.0);
                for (let x = start; x < end; x++) {
                    const s = this.buffer[this.bufferIndex + sp++] - 60;
                    //const y = gamma[clamp(0, (s * yContrast + brightness) >> 16, 255)];
                    const y = gamma[clamp(0, Math.trunc(s * this.contrast + this.brightness), 255)];
                    this.imageData.data[destinationIndex++] = y;
                    this.imageData.data[destinationIndex++] = y;
                    this.imageData.data[destinationIndex++] = y;
                    this.imageData.data[destinationIndex++] = 0xFF;
                }
            }
        }

        offset += minSamplesPerLine;
        this.phase = (this.phase + offset) & 3;
        this.bufferIndex += offset;

        this.line++;

        if (this.foundVerticalSync && this.line == minLinesPerField) {
            this.line = 0;
            this.foundVerticalSync = false;
            this.crtLoad = baseLoad;
            this.verticalSync = 0;
        }
    }

    paint() {
        do {
            this.process();
        } while (this.line != 0 || this.foundVerticalSync);
    }
}