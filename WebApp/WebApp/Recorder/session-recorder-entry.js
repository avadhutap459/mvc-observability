import { record } from '@rrweb/record';

(() => {
    if (window.__rrwebCaptureStarted) return;
    window.__rrwebCaptureStarted = true;

    let sessionId = sessionStorage.getItem('rrweb-session-id');

    if (!sessionId) {
        sessionId = crypto.randomUUID();
        sessionStorage.setItem('rrweb-session-id', sessionId);
    }

    let chunkNumber = Number(
        sessionStorage.getItem('rrweb-chunk-number') || '0'
    );

    let queue = [];
    let sending = false;

    async function flush() {
        if (sending || queue.length === 0) return;

        sending = true;
        const batch = queue;
        queue = [];

        try {
            const response = await fetch(
                `https://localhost:7150/api/session-capture/${sessionId}/chunks`,
                {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json'
                    },
                    credentials: 'same-origin',
                    body: JSON.stringify({
                        chunkNumber,
                        events: batch
                    })
                }
            );

            if (!response.ok) {
                throw new Error(`Upload failed: ${response.status}`);
            }

            chunkNumber++;

            sessionStorage.setItem(
                'rrweb-chunk-number',
                String(chunkNumber)
            );
        } catch (error) {
            // Preserve order and retry during the next flush.
            queue = batch.concat(queue);
            console.error('Session upload failed.', error);
        } finally {
            sending = false;
        }
    }

    const stopRecording = record({
        emit(event) {
            queue.push(event);

            if (queue.length >= 100) {
                void flush();
            }
        },

        // Mask inputs and exclude sensitive sections.
        maskAllInputs: true,
        blockSelector: '.replay-block, [data-replay-block]',
        ignoreSelector: '.replay-ignore, [data-replay-ignore]'
    });

    const timer = setInterval(() => void flush(), 5000);

    window.__rrwebCapture = {
        sessionId,
        stop: () => {
            clearInterval(timer);
            stopRecording?.();
        }
    };

    console.info('rrweb session ID:', sessionId);

    window.addEventListener('pagehide', () => {
        clearInterval(timer);
        stopRecording?.();
        void flush();
    }, { once: true });
})();