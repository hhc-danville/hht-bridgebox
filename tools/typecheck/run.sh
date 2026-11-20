#!/bin/bash
# Type-checks the WPF console code on Linux/macOS (no WPF available) against minimal API stubs.
# This catches wrong member names, Core API misuse, missing XAML-named controls and handler signatures.
# It does NOT replace a real Windows build (the stubs mirror the WPF/OpenCvSharp APIs as used here).
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/../.." && pwd)
TMP=$(mktemp -d)
dotnet build "$ROOT/src/HHT.Bridge.Core" -nologo -v q -o "$TMP/core" >/dev/null
CSC=$(ls /usr/lib/dotnet/sdk/*/Roslyn/bincore/csc.dll /usr/share/dotnet/sdk/*/Roslyn/bincore/csc.dll "$HOME"/.dotnet/sdk/*/Roslyn/bincore/csc.dll 2>/dev/null | head -1 || true)
REF=$(ls -d /usr/lib/dotnet/packs/Microsoft.NETCore.App.Ref/*/ref/net10.0 /usr/share/dotnet/packs/Microsoft.NETCore.App.Ref/*/ref/net10.0 "$HOME"/.dotnet/packs/Microsoft.NETCore.App.Ref/*/ref/net10.0 2>/dev/null | head -1 || true)
python3 - "$ROOT" "$TMP" <<'EOF'
import re, sys
root, tmp = sys.argv[1], sys.argv[2]
x = open(f"{root}/src/HHT.Bridge.TechConsole/MainWindow.xaml").read()
m = {'ColumnDefinition': 'System.Windows.Controls.ColumnDefinition', 'Ellipse': 'System.Windows.Shapes.Ellipse'}
out = ["namespace HHT.Bridge.TechConsole { public partial class MainWindow {"]
for t, n in re.findall(r'<(\w+)\s[^>]*?x:Name="(\w+)"', x):
    out.append(f"  internal {m.get(t, 'System.Windows.Controls.' + t)} {n} = null!;")
out.append("  public void InitializeComponent(){} } }")
open(f"{tmp}/xamlfields.cs", "w").write("\n".join(out))
open(f"{tmp}/gu.cs", "w").write("".join(f"global using {n};\n" for n in ["System", "System.Linq", "System.Collections.Generic", "System.Threading", "System.Threading.Tasks"]))
EOF
REFS=$(ls "$REF"/*.dll | sed 's/^/-r:/' | tr '\n' ' ')
cd "$ROOT/src/HHT.Bridge.TechConsole"
# shellcheck disable=SC2086
dotnet "$CSC" -nologo -langversion:latest -nullable:enable -t:library -noconfig -nostdlib \
  -nowarn:CS8618,CS0169,CS0414,CS0649,CS0067 $REFS -r:"$TMP/core/HHT.Bridge.Core.dll" -out:"$TMP/x.dll" \
  "$TMP/gu.cs" "$TMP/xamlfields.cs" "$ROOT"/tools/typecheck/*.cs $(find . -name "*.cs" -not -path "./obj/*" -not -path "./bin/*")
echo "typecheck OK"
