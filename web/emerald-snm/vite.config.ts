import { execFileSync } from 'node:child_process'
import { copyFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { fileURLToPath, URL } from 'node:url'
import tailwindcss from '@tailwindcss/vite'
import vue from '@vitejs/plugin-vue'
import { defineConfig } from 'vite'
import pkg from './package.json'
const root = fileURLToPath(new URL('.', import.meta.url))
const output = resolve(root, '../themes/emerald')
let hash = 'local'
try { hash = execFileSync('git', ['rev-parse', '--short', 'HEAD'], { encoding: 'utf8' }).trim() } catch {}
export default defineConfig({
  base: './',
  define: { __BUILD_VERSION__: JSON.stringify(pkg.version), __BUILD_GIT_HASH__: JSON.stringify(hash) },
  plugins: [vue(), tailwindcss(), {
    name: 'snm-theme-files', closeBundle() {
      for (const name of ['theme.json', 'preview.png', 'README.md']) copyFileSync(resolve(root, name), resolve(output, name))
      // The upload service permits .txt, but not extensionless files. Keep the upstream MIT notice in every package.
      copyFileSync(resolve(root, 'LICENSE'), resolve(output, 'LICENSE.txt'))
    },
  }],
  resolve: { alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) } },
  build: {
    outDir: output, emptyOutDir: true, chunkSizeWarningLimit: 1100,
    rollupOptions: {
      external: ['/vendor/snm-client.js'],
      output: { manualChunks: { 'vue-vendor': ['vue', 'vue-router', 'pinia'], 'echarts': ['echarts', 'vue-echarts'], 'reka-ui': ['reka-ui'] } },
    },
  },
})
