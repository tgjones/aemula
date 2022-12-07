import { CompositeMonitor } from './monitor.js';

const canvas = document.getElementById("my-canvas");

/** @type HTMLInputElement */
const brightnessSlider = document.getElementById("brightness-slider");

/** @type HTMLSpanElement */
const brightnessText = document.getElementById("brightness-text");

function onBrightnessChange() {
    const newValue = brightnessSlider.valueAsNumber;
    brightnessText.textContent = newValue;

    monitor.brightness = newValue;
}

brightnessSlider.addEventListener("change", onBrightnessChange);
brightnessSlider.addEventListener("input", onBrightnessChange);

/** @type CompositeMonitor */
let monitor = null;

const ctx = canvas.getContext("2d");

let buffer = null;

document.getElementById('file-selector').addEventListener('change', async event => {
    const file = event.target.files[0];
    buffer = new Uint8Array(await file.arrayBuffer());

    requestAnimationFrame(startDecode);
});

function startDecode() {
    monitor = new CompositeMonitor(buffer);

    brightnessSlider.value = monitor.brightness;
    onBrightnessChange();

    decode();
}

function decode() {
    // Process one frame.
    monitor.paint();
    monitor.canvasContext.putImageData(monitor.imageData, 0, 0);

    // Update image.
    ctx.drawImage(monitor.canvas, 0, 0, canvas.scrollWidth, canvas.scrollHeight);

    // Schedule next frame.
    requestAnimationFrame(decode);
}