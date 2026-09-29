#!/usr/bin/env python3
"""Partial semantic check on macOS. Does NOT replace WinUI XAML compilation or Windows tests.
Requires restored WinUI input.json from an attempted Windows-targeted app build.
"""
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parent.parent
dotnet = root / '.tools/dotnet/dotnet'
inputs = list((root / 'src/Glide.App/obj').rglob('input.json'))
if not inputs:
    sys.exit('First restore/build src/Glide.App with EnableWindowsTargeting=true and Platform=x64.')
config = max(inputs, key=lambda p: p.stat().st_mtime)
metadata = json.loads(config.read_text())
refs = [Path(x['FullPath']) for x in metadata['ReferenceAssemblies'] if x['FullPath'].endswith('.dll')]
if not refs or any(not p.exists() for p in refs):
    sys.exit('References from XAML compiler input are missing.')
output = root / '.artifacts/managed-ui-check'
output.mkdir(parents=True, exist_ok=True)
xml = ET.parse(root / 'src/Glide.App/MainWindow.xaml')
ET.parse(root / 'src/Glide.App/App.xaml')
fields, hooks = [], []
events = {'Click', 'ValueChanged', 'SelectionChanged', 'SizeChanged', 'PointerPressed', 'PointerMoved', 'PointerReleased', 'PointerCanceled', 'KeyDown', 'DragStarted', 'DragDelta', 'DragCompleted'}
for element in xml.iter():
    name = element.get('{http://schemas.microsoft.com/winfx/2006/xaml}Name')
    kind = element.tag.split('}')[-1]
    ns = 'Microsoft.UI.Xaml.Controls.Primitives' if kind == 'Thumb' else 'Microsoft.UI.Xaml.Controls'
    if name:
        fields.append(f'private {ns}.{kind} {name} = null!;')
    for event in events:
        if event in element.attrib:
            receiver = name if name else f'(new {ns}.{kind}())'
            hooks.append(f'{receiver}.{event} += {element.attrib[event]};')
code = '#pragma warning disable CS0414\nnamespace Glide.App { public sealed partial class MainWindow {\n'
code += '\n'.join(fields) + '\nprivate void InitializeComponent() {\n' + '\n'.join(hooks) + '\n} }\n'
code += 'public partial class App { private void InitializeComponent() {} } }\n'
(output / 'XamlNames.cs').write_text(code)
globals_file = next(config.parent.glob('Glide.App.GlobalUsings.g.cs'))
sources = [*sorted((root / 'src/Glide.App').glob('*.cs')), output / 'XamlNames.cs', globals_file]
options = ['-nologo', '-target:library', '-nullable:enable', '-langversion:latest', '-warnaserror+', '-nowarn:1701,1702', f'-out:"{output / "Glide.UI.Check.dll"}"']
options += [f'-r:"{p}"' for p in refs] + [f'"{p}"' for p in sources]
response = output / 'compile.rsp'
response.write_text('\n'.join(options))
sdk = root / '.tools/dotnet/sdk' / subprocess.check_output([str(dotnet), '--version'], cwd=root, text=True).strip()
result = subprocess.run([str(dotnet), str(sdk / 'Roslyn/bincore/csc.dll'), '@' + str(response)], cwd=root, text=True, capture_output=True)
(output / 'compile.log').write_text(result.stdout + result.stderr)
print(result.stdout + result.stderr, end='')
source_files = [*sorted((root / 'src/Glide.App').glob('*.cs')), root / 'src/Glide.App/MainWindow.xaml', root / 'src/Glide.App/App.xaml']
report = {'passed': result.returncode == 0, 'scope': 'C# type/event signature checks and XML syntax only. Generated controls are stubs. NO WinUI XAML compiler, resource resolution, native build or Windows runtime coverage.', 'sourceSha256': {str(p.relative_to(root)): hashlib.sha256(p.read_bytes()).hexdigest() for p in source_files}}
(output / 'result.json').write_text(json.dumps(report, indent=2))
print('PASS partial managed UI semantics' if result.returncode == 0 else 'FAIL partial managed UI semantics')
sys.exit(result.returncode)
