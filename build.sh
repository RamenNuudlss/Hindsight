#!/usr/bin/env bash
# Builds Hindsight.exe (a single file for Windows 10/11, .NET Framework 4.8).
# Works on Linux with Mono's compiler, or on Windows from Git Bash with Mono installed.
set -euo pipefail
cd "$(dirname "$0")"

API=/usr/lib/mono/4.8-api
OUT=dist
mkdir -p "$OUT" obj

# Windows resources: icon, version info and the app manifest
if command -v x86_64-w64-mingw32-windres >/dev/null; then
  x86_64-w64-mingw32-windres --preprocessor=cpp -O res -i app.rc -o obj/app.res
  WIN32RES="-win32res:obj/app.res"
else
  echo "windres not found: building without embedded version info/manifest"
  WIN32RES="-win32icon:assets/icon.ico"
fi

mcs -nologo -target:winexe -platform:anycpu -optimize+ -langversion:7.2 -codepage:utf8 \
  -nostdlib -noconfig \
  -r:$API/mscorlib.dll -r:$API/System.dll -r:$API/System.Core.dll \
  -r:$API/System.Drawing.dll -r:$API/System.Windows.Forms.dll \
  -r:$API/System.Web.Extensions.dll -r:$API/System.Management.dll \
  -r:$API/Microsoft.VisualBasic.dll -r:$API/Facades/System.Runtime.dll \
  -r:lib/Microsoft.Web.WebView2.Core.dll -r:lib/Microsoft.Web.WebView2.WinForms.dll \
  $WIN32RES \
  -resource:lib/Microsoft.Web.WebView2.Core.dll,lib.Microsoft.Web.WebView2.Core.dll \
  -resource:lib/Microsoft.Web.WebView2.WinForms.dll,lib.Microsoft.Web.WebView2.WinForms.dll \
  -resource:lib/WebView2Loader.x64.dll,lib.WebView2Loader.x64.dll \
  -resource:lib/WebView2Loader.arm64.dll,lib.WebView2Loader.arm64.dll \
  -resource:assets/icon.ico,app.ico \
  -resource:ui/index.html,ui.index.html \
  -resource:ui/bricolage.woff2,ui.bricolage.woff2 \
  -resource:ui/icon.png,ui.icon.png \
  -out:"$OUT/Hindsight.exe" src/*.cs

echo "built $OUT/Hindsight.exe ($(du -h "$OUT/Hindsight.exe" | cut -f1))"
