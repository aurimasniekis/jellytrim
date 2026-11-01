// Bundles the UI into the single ES module that Jellyfin serves through its plugin-page mechanism.
import { build } from 'esbuild';

await build({
    entryPoints: ['src/index.tsx'],
    bundle: true,
    format: 'esm',
    target: 'es2020',
    minify: true,
    legalComments: 'inline',
    jsx: 'automatic',
    jsxImportSource: 'preact',
    loader: { '.css': 'text' },
    outfile: '../src/Jellyfin.Plugin.Jellytrim/Web/jellytrim.js',
    logLevel: 'warning',
});
