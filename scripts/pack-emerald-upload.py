"""Package the built Emerald theme for SNM installations where `emerald` is already built in."""
import argparse
import json
from pathlib import Path
from zipfile import ZIP_DEFLATED, ZipFile


def main():
    root = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, default=root / 'web/themes/emerald')
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    source = args.source.resolve()
    manifest = json.loads((source / 'theme.json').read_text(encoding='utf-8'))
    if manifest['id'] != 'emerald' or manifest['entry'] != 'index.html':
        parser.error('expected a built SNM Emerald theme')
    html = (source / 'index.html').read_text(encoding='utf-8')
    if html.count('data-snm-theme="emerald"') != 1:
        parser.error('rebuild Emerald before packaging')
    manifest.update(id='emerald-snm', name='Emerald 绿宝石 · 独立版')
    out = (args.output or root / 'artifacts' / f'snm-theme-emerald-snm-{manifest["version"]}.zip').resolve()
    if out.is_relative_to(source):
        parser.error('output must be outside the built theme directory')
    out.parent.mkdir(parents=True, exist_ok=True)
    with ZipFile(out, 'w', ZIP_DEFLATED) as archive:
        for item in sorted(source.rglob('*')):
            if not item.is_file():
                continue
            name = item.relative_to(source).as_posix()
            if name == 'theme.json':
                archive.writestr(name, json.dumps(manifest, ensure_ascii=False, indent=2) + '\n')
            elif name == 'index.html':
                archive.writestr(name, html.replace('data-snm-theme="emerald"', 'data-snm-theme="emerald-snm"'))
            else:
                archive.write(item, name)
    print(out)


if __name__ == '__main__':
    main()
