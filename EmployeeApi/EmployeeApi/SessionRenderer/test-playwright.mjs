import { chromium } from 'playwright';

async function main() {

    console.log('Starting Playwright test...');

    const browser = await chromium.launch({
        headless: true
    });

    console.log('Chromium launched successfully.');

    const page = await browser.newPage({
        viewport: {
            width: 1366,
            height: 768
        }
    });

    await page.setContent(`
        <!DOCTYPE html>
        <html>
        <head>
            <title>Session Replay Test</title>
        </head>
        <body>
            <h1>Playwright Installation Successful</h1>
            <p>Testing browser rendering.</p>
        </body>
        </html>
    `);

    console.log('Browser page created.');

    console.log(
        'Page title:',
        await page.title()
    );

    await page.screenshot({
        path: 'playwright-test.png',
        fullPage: true
    });

    console.log('Screenshot generated successfully.');

    await browser.close();

    console.log('Playwright test completed.');
}

main().catch(error => {
    console.error('Playwright test failed:', error);
    process.exitCode = 1;
});