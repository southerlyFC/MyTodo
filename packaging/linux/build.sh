#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/../.."
version=1.1.0~preview.1
dotnet publish src/MyTodo.Linux -c Release -r linux-x64 --self-contained true \
  -p:EnableHeadless=false -p:DebugType=None -p:DebugSymbols=false \
  -o artifacts/linux/portable
install -m 644 docs/linux-usage.md artifacts/linux/portable/README.md
mkdir -p artifacts/linux
tar -czf artifacts/linux/MyTodo-linux-x64-portable.tar.gz -C artifacts/linux/portable .
stage=$(mktemp -d -t mytodo-deb-XXXXXXXX)
install -d "$stage/DEBIAN" "$stage/opt/mytodo" "$stage/usr/share/applications" "$stage/usr/share/icons/hicolor/scalable/apps"
cp -a artifacts/linux/portable/. "$stage/opt/mytodo/"
install -m 644 packaging/linux/control "$stage/DEBIAN/control"
install -m 644 packaging/linux/mytodo.desktop "$stage/usr/share/applications/mytodo.desktop"
install -m 644 src/MyTodo/Assets/MyTodo.svg "$stage/usr/share/icons/hicolor/scalable/apps/mytodo.svg"
dpkg-deb --root-owner-group --build "$stage" "artifacts/linux/MyTodo_${version}_amd64.deb"
(cd artifacts/linux && sha256sum MyTodo-linux-x64-portable.tar.gz "MyTodo_${version}_amd64.deb" > SHA256SUMS)
