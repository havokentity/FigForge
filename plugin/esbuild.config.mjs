// Build script: bundles main.ts → dist/main.js and inlines ui.ts into dist/ui.html.
import { build, context } from 'esbuild';
import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { dirname } from 'node:path';

const watch = process.argv.includes('--watch');

const common = {
  bundle: true,
  format: 'iife',
  target: 'es2017',
  logLevel: 'info',
};

async function buildUiHtml() {
  // Bundle the UI script to a string, then inline it into the HTML shell so
  // Figma loads a single self-contained ui.html.
  const result = await build({
    ...common,
    entryPoints: ['src/ui.ts'],
    write: false,
  });
  const js = result.outputFiles[0].text;
  const shell = await readFile('src/ui.html', 'utf8');
  const html = shell.replace('<!-- FIGFORGE_UI_SCRIPT -->', `<script>${js}</script>`);
  await mkdir(dirname('dist/ui.html'), { recursive: true });
  await writeFile('dist/ui.html', html, 'utf8');
}

async function run() {
  await mkdir('dist', { recursive: true });
  if (watch) {
    const ctx = await context({ ...common, entryPoints: ['src/main.ts'], outfile: 'dist/main.js' });
    await ctx.watch();
    await buildUiHtml();
    console.log('FigForge: watching… (re-run for ui.html changes)');
  } else {
    await build({ ...common, entryPoints: ['src/main.ts'], outfile: 'dist/main.js' });
    await buildUiHtml();
    console.log('FigForge: build complete → dist/main.js + dist/ui.html');
  }
}

run().catch((e) => {
  console.error(e);
  process.exit(1);
});
