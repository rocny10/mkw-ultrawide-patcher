#!/bin/sh
# Builds UltrawidePatcher.exe with Mono (Linux/macOS). Requires: mono-mcs, mono-devel
set -e
mkdir -p dist
mcs -sdk:4.5 -target:winexe -optimize+ -win32icon:src/icon.ico \
    -r:System.Windows.Forms.dll -r:System.Drawing.dll \
    -out:dist/UltrawidePatcher.exe src/UltrawidePatcher.cs
echo "Built dist/UltrawidePatcher.exe"
