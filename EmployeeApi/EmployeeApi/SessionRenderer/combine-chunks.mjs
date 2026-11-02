import fs from 'node:fs/promises';
import path from 'node:path';

const inputDirectory = process.argv[2];
const outputFile = process.argv[3];

if (!inputDirectory || !outputFile) {
    throw new Error(
        'Usage: node combine-chunks.mjs <chunks-folder> <output-file>'
    );
}

const files = (await fs.readdir(inputDirectory))
    .filter(file => /^\d+\.json$/i.test(file))
    .sort((a, b) => {
        const numberA = Number.parseInt(a, 10);
        const numberB = Number.parseInt(b, 10);
        return numberA - numberB;
    });

if (files.length === 0) {
    throw new Error('No chunk JSON files found.');
}

const allEvents = [];

for (const file of files) {

    const filePath = path.join(inputDirectory, file);

    console.log('Reading:', file);

    const content = await fs.readFile(filePath, 'utf8');

    const chunk = JSON.parse(content);

    if (!Array.isArray(chunk.events)) {
        throw new Error(
            `${file} does not contain an events array.`
        );
    }

    console.log(
        `Chunk ${file}: ${chunk.events.length} events`
    );

    allEvents.push(...chunk.events);
}

if (allEvents.length === 0) {
    throw new Error('All chunks are empty.');
}

await fs.mkdir(path.dirname(outputFile), {
    recursive: true
});

await fs.writeFile(
    outputFile,
    JSON.stringify(allEvents),
    'utf8'
);

console.log('--------------------------------');
console.log('Chunk combination successful.');
console.log('Total chunks:', files.length);
console.log('Total events:', allEvents.length);
console.log('Output:', outputFile);