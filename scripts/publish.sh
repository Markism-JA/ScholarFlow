#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd -P "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT_DIR/src/ScholarFlow.Desktop/ScholarFlow.Desktop.csproj"
DIST_DIR="$ROOT_DIR/dist"
VERSION="${1:-v1.0.0}"

rm -rf "$DIST_DIR/linux-x64" "$DIST_DIR/win-x64"
mkdir -p "$DIST_DIR/linux-x64" "$DIST_DIR/win-x64"

echo "==> Publishing ScholarFlow $VERSION for Linux (linux-x64)..."
dotnet publish "$PROJECT" \
  --disable-build-servers \
  -m:1 \
  -c Release \
  -r linux-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -o "$DIST_DIR/linux-x64"

cp -f "$ROOT_DIR/assets/scholarflow.svg" "$DIST_DIR/linux-x64/scholarflow.svg"
cp -f "$ROOT_DIR/scripts/install-linux.sh" "$DIST_DIR/linux-x64/install.sh"
cp -f "$ROOT_DIR/scripts/uninstall-linux.sh" "$DIST_DIR/linux-x64/uninstall.sh"
chmod +x "$DIST_DIR/linux-x64/ScholarFlow" "$DIST_DIR/linux-x64/install.sh" "$DIST_DIR/linux-x64/uninstall.sh"

echo "==> Publishing ScholarFlow $VERSION for Windows (win-x64)..."
dotnet publish "$PROJECT" \
  --disable-build-servers \
  -m:1 \
  -c Release \
  -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -o "$DIST_DIR/win-x64"

cp -f "$ROOT_DIR/scripts/install-windows.ps1" "$DIST_DIR/win-x64/install.ps1"
cp -f "$ROOT_DIR/scripts/uninstall-windows.ps1" "$DIST_DIR/win-x64/uninstall.ps1"

echo "==> Verifying critical managed & native libraries inside single-file bundles..."
verify_bundle() {
  local bin="$1"
  shift
  for tok in "$@"; do
    if ! grep -a -q "$tok" "$bin"; then
      echo "ERROR: Missing '$tok' in published bundle: $bin" >&2
      exit 1
    fi
  done
}

verify_bundle "$DIST_DIR/linux-x64/ScholarFlow" \
  Mermaider.dll Svg.Skia.dll ExCSS.dll ShimSkiaSharp.dll Svg.Model.dll \
  CSharpMath.SkiaSharp.dll AngleSharp.dll Microsoft.Playwright.dll \
  ScholarFlow.Core.dll ScholarFlow.Elms.dll ScholarFlow.Elms.Sti.dll \
  libSkiaSharp.so libHarfBuzzSharp.so libpdfium.so libe_sqlite3.so

verify_bundle "$DIST_DIR/win-x64/ScholarFlow.exe" \
  Mermaider.dll Svg.Skia.dll ExCSS.dll ShimSkiaSharp.dll Svg.Model.dll \
  CSharpMath.SkiaSharp.dll AngleSharp.dll Microsoft.Playwright.dll \
  ScholarFlow.Core.dll ScholarFlow.Elms.dll ScholarFlow.Elms.Sti.dll \
  libSkiaSharp.dll libHarfBuzzSharp.dll pdfium.dll e_sqlite3.dll

echo "==> All required managed & native dependencies verified!"

echo "==> Copying target-specific Playwright drivers..."
PW_NUGET_DIR="$(find "${NUGET_PACKAGES:-$HOME/.nuget/packages}/microsoft.playwright" -maxdepth 1 -mindepth 1 -type d 2>/dev/null | sort -V | tail -n1)"
if [ -n "$PW_NUGET_DIR" ] && [ -d "$PW_NUGET_DIR/.playwright" ]; then
  # Linux x64 driver
  mkdir -p "$DIST_DIR/linux-x64/.playwright/node/linux-x64"
  rm -rf "$DIST_DIR/linux-x64/.playwright/package"
  cp -a "$PW_NUGET_DIR/.playwright/package" "$DIST_DIR/linux-x64/.playwright/package"
  cp -f "$PW_NUGET_DIR/.playwright/node/LICENSE" "$DIST_DIR/linux-x64/.playwright/node/LICENSE"
  cp -f "$PW_NUGET_DIR/.playwright/node/linux-x64/node" "$DIST_DIR/linux-x64/.playwright/node/linux-x64/node"
  chmod +x "$DIST_DIR/linux-x64/.playwright/node/linux-x64/node"

  # Windows x64 driver (remove any host linux-x64 binary copied by MSBuild on Linux)
  rm -rf "$DIST_DIR/win-x64/.playwright/node" "$DIST_DIR/win-x64/.playwright/package"
  mkdir -p "$DIST_DIR/win-x64/.playwright/node/win32_x64"
  cp -a "$PW_NUGET_DIR/.playwright/package" "$DIST_DIR/win-x64/.playwright/package"
  cp -f "$PW_NUGET_DIR/.playwright/node/LICENSE" "$DIST_DIR/win-x64/.playwright/node/LICENSE"
  cp -f "$PW_NUGET_DIR/.playwright/node/win32_x64/node.exe" "$DIST_DIR/win-x64/.playwright/node/win32_x64/node.exe"
fi

echo "==> Creating release archives..."
LINUX_ITEMS=(ScholarFlow scholarflow.svg install.sh uninstall.sh)
if [ -d "$DIST_DIR/linux-x64/.playwright" ]; then
  LINUX_ITEMS+=(.playwright)
fi
tar -C "$DIST_DIR/linux-x64" -czf "$DIST_DIR/ScholarFlow-$VERSION-linux-x64.tar.gz" "${LINUX_ITEMS[@]}"

python3 - "$DIST_DIR/win-x64" "$DIST_DIR/ScholarFlow-$VERSION-win-x64.zip" <<'PY'
import os, sys, zipfile
src_dir, out_zip = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(out_zip, "w", zipfile.ZIP_DEFLATED) as zf:
    for name in ("ScholarFlow.exe", "install.ps1", "uninstall.ps1"):
        p = os.path.join(src_dir, name)
        if os.path.exists(p):
            zf.write(p, arcname=name)
    pw_dir = os.path.join(src_dir, ".playwright")
    if os.path.isdir(pw_dir):
        for root, _, files in os.walk(pw_dir):
            for f in files:
                full = os.path.join(root, f)
                rel = os.path.relpath(full, src_dir)
                zf.write(full, arcname=rel)
PY

echo "==> Done! Release artifacts:"
ls -lh "$DIST_DIR/ScholarFlow-$VERSION-linux-x64.tar.gz" "$DIST_DIR/ScholarFlow-$VERSION-win-x64.zip"
