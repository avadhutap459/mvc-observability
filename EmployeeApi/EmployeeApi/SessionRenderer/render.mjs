import { chromium } from 'playwright';

import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const rendererDirectory = path.dirname(
    fileURLToPath(import.meta.url)
);

const [inputFile, outputFile] = process.argv.slice(2);

if (!inputFile || !outputFile) {
    throw new Error(
        'Usage: node render.mjs events.json session.webm'
    );
}

const absoluteInput = path.resolve(inputFile);
const absoluteOutput = path.resolve(outputFile);

console.log('Reading rrweb events...');

const events = JSON.parse(
    await fs.readFile(absoluteInput, 'utf8')
);

if (!Array.isArray(events) || events.length === 0) {
    throw new Error('No rrweb events found.');
}

if (!events.some(event => event.type === 2)) {
    throw new Error('Full snapshot event is missing.');
}

const timestamps = events.map(event => event.timestamp);

if (timestamps.some(t => !Number.isFinite(t))) {
    throw new Error('Invalid event timestamps.');
}

const firstTimestamp = Math.min(...timestamps);
const lastTimestamp = Math.max(...timestamps);

const duration = lastTimestamp - firstTimestamp + 3000;

const maxDuration = 30 * 60 * 1000;

if (duration <= 0 || duration > maxDuration) {
    throw new Error(
        'Recording duration is outside the configured limit.'
    );
}

console.log('Total events:', events.length);
console.log('Replay duration:', duration, 'ms');

const replayJs = path.join(
    rendererDirectory,
    'replay-bundle.js'
);

const replayCss = path.join(
    rendererDirectory,
    'replay-bundle.css'
);

await fs.access(replayJs);
await fs.access(replayCss);

await fs.mkdir(
    path.dirname(absoluteOutput),
    { recursive: true }
);

const browser = await chromium.launch({
    headless: true
});

let context;

try {

    const videoDirectory = path.join(
        path.dirname(absoluteOutput),
        'playwright-video-' + Date.now()
    );

    await fs.mkdir(videoDirectory, {
        recursive: true
    });

    context = await browser.newContext({
        viewport: {
            width: 1366,
            height: 768
        },

        recordVideo: {
            dir: videoDirectory,
            size: {
                width: 1366,
                height: 768
            }
        }
    });

    const page = await context.newPage();

    page.on('pageerror', error => {
        console.error('Browser error:', error.message);
    });

    page.on('console', message => {
        if (message.type() === 'error') {
            console.error(
                'Browser console error:',
                message.text()
            );
        }
    });

    await page.setContent(`
        <!DOCTYPE html>
        <html>
        <head>
            <meta charset="UTF-8">

            <title>Session Replay</title>

            <style>
                html,
                body {
                    width: 100%;
                    height: 100%;
                    margin: 0;
                    padding: 0;
                    overflow: hidden;
                    background: white;
                }

                #replay-root {
                    width: 100%;
                    height: 100%;
                }
            </style>
        </head>

        <body>
            <div id="replay-root"></div>
        </body>
        </html>
    `);

    // Load rrweb replay CSS.
    await page.addStyleTag({
        path: replayCss
    });

    // Load bundled rrweb replay JavaScript.
    await page.addScriptTag({
        path: replayJs
    });

    console.log('Starting browser replay...');

    // Start replay with actual events.
    await page.evaluate(recordedEvents => {

        if (!window.startRrwebReplay) {
            throw new Error(
                'rrweb replay function not loaded.'
            );
        }

        window.startRrwebReplay(recordedEvents);

    }, events);

    // Wait while rrweb events are replayed.
    await page.waitForTimeout(duration);

    // Save reference to video before closing context.
    const video = page.video();

    if (!video) {
        throw new Error('Playwright video recording unavailable.');
    }

    // Closing context finalizes the recorded video.
    await context.close();
    context = null;

    await video.saveAs(absoluteOutput);

    console.log('WebM successfully generated.');
    console.log('Output:', absoluteOutput);

} finally {

    if (context) {
        await context.close();
    }

    await browser.close();
}