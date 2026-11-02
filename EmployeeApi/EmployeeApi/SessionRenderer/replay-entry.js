import { Replayer } from '@rrweb/replay';
import '@rrweb/replay/dist/style.css';

window.startRrwebReplay = function (events) {

    console.log('Starting rrweb replay...');

    if (!Array.isArray(events) || events.length === 0) {
        throw new Error('No rrweb events received.');
    }

    // Validate that the initial full snapshot exists.
    const hasFullSnapshot = events.some(
        event => event.type === 2
    );

    if (!hasFullSnapshot) {
        throw new Error(
            'Initial rrweb full snapshot is missing.'
        );
    }

    const root = document.getElementById('replay-root');

    if (!root) {
        throw new Error('Replay root element not found.');
    }

    // Create the rrweb replay instance.
    const replayer = new Replayer(events, {
        root: root,
        speed: 1,
        skipInactive: false
    });

    // Start playback.
    replayer.play();

    console.log('rrweb replay started successfully.');

    return true;
};