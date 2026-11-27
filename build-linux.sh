#!/bin/sh
# Builds LOTUS for Linux and packs it into dist/lotus-<version>-linux-x64.tar.gz.
#
#   ./build-linux.sh
#   ./build-linux.sh --skip-tests
#
# Needs the .NET SDK (8 or newer). Run it on Linux.
set -eu
cd "$(dirname "$0")"

version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props | head -n 1)
[ -n "$version" ] || { echo "Could not read the version from Directory.Build.props" >&2; exit 1; }
name="lotus-$version-linux-x64"
stage="dist/linux/$name"

if [ "${1:-}" != "--skip-tests" ]; then
    echo "== Running the tests"
    dotnet test tests/Lotus.Linux.Tests/Lotus.Linux.Tests.csproj -c Release -nologo -v minimal
fi

echo "== Building the program"
rm -rf dist/linux
mkdir -p "$stage"
# One file that carries its own runtime, so the computer it runs on needs nothing installed.
dotnet publish src/Lotus.Linux/Lotus.Linux.csproj -c Release -r linux-x64 --self-contained true \
    -p:PublishSingleFile=true -p:PublishTrimmed=true -p:TrimmerSingleWarn=false \
    -p:DebugType=none -p:IncludeNativeLibrariesForSelfExtract=false \
    -nologo -v minimal -o dist/linux/publish

echo "== Packing"
cp dist/linux/publish/lotus "$stage/lotus"
cp packaging/linux/install.sh packaging/linux/uninstall.sh packaging/linux/lotus.service "$stage/"
cp LICENSE "$stage/LICENSE"
chmod 0755 "$stage/lotus" "$stage/install.sh" "$stage/uninstall.sh"
chmod 0644 "$stage/lotus.service" "$stage/LICENSE"
tar -C dist/linux --owner=0 --group=0 --numeric-owner -czf "dist/$name.tar.gz" "$name"

echo ""
echo "Done: dist/$name.tar.gz"
sha256sum "dist/$name.tar.gz"
