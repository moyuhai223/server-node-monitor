import { readFile } from 'node:fs/promises';
import { createRequire, Module } from 'node:module';
import path from 'node:path';

// Compile and mount the real SFC; feed it pointer events without contacting a running dashboard.
export async function tooltipBehavior(source, dependencies = source) {
  const require = createRequire(path.resolve(dependencies, 'package.json'));
  const vue = require('vue'), { parse, compileScript } = require('vue/compiler-sfc');
  const ts = require('typescript');
  const file = path.resolve(source, 'src/components/ui/data-tooltip/DataTooltip.vue');
  const { descriptor } = parse(await readFile(file, 'utf8'), { filename: file });
  const code = compileScript(descriptor, { id: 'tooltip-test', inlineTemplate: true }).content;
  const module = new Module(file);
  module.require = id => id === '@/lib/utils' ? { cn: (...v) => v.filter(Boolean).join(' ') } : require(id);
  module._compile(ts.transpileModule(code, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText, file);
  const element = () => ({ props: {}, children: [] });
  const renderer = vue.createRenderer({
    createElement: element, createText: element, createComment: element, setText() {}, setElementText() {},
    patchProp(el, key, _, value) { el.props[key] = value; },
    parentNode: el => el.parent, nextSibling: el => el.parent?.children[el.parent.children.indexOf(el) + 1] ?? null,
    insert(el, parent, anchor) { el.parent = parent; const i = parent.children.indexOf(anchor); parent.children.splice(i < 0 ? parent.children.length : i, 0, el); },
    remove(el) { const a = el.parent?.children; if (a?.includes(el)) a.splice(a.indexOf(el), 1); },
  });
  async function scenario(props, pointer, cancel = false) {
    const host = element(), app = renderer.createApp(module.exports.default, { content: 'info', ...props }); app.mount(host);
    const el = host.children[0]; let stopped = false;
    const event = { pointerType: pointer, stopPropagation() { stopped = true; } };
    el.props.onPointerenter?.(event); await vue.nextTick();
    const hover = el.children.some(child => child.props?.role === 'tooltip');
    el.props.onPointerdownCapture?.(event); el.props.onPointerupCapture?.(event);
    if (cancel) el.props.onPointercancelCapture?.(event);
    el.props.onClickCapture?.(event); el.props.onClick?.(event);
    await vue.nextTick(); const open = el.props['data-state'] === 'open'; app.unmount();
    return { hover, open, stopped };
  }
  return { ordinary: await scenario({}, 'touch'), info: await scenario({ touchTrigger: 'tap' }, 'touch'),
    cancelled: await scenario({ touchTrigger: 'tap' }, 'touch', true), disabled: await scenario({ disabled: true, touchTrigger: 'tap' }, 'touch'),
    mouse: await scenario({}, 'mouse') };
}
