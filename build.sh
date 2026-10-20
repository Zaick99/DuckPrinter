#!/usr/bin/env bash
# Build incrociata da Linux: duck_core.dll (mingw) + publish win-x64 + test sul core nativo Linux.
# Requisiti: rustup target x86_64-pc-windows-gnu, gcc-mingw-w64-x86-64, .NET 8 SDK con WindowsDesktop.
set -euo pipefail
cd "$(dirname "$0")"
OUT="${1:-dist/DuckPrinterManager}"
cargo test --manifest-path native/duck_core/Cargo.toml
cargo build --release --manifest-path native/duck_core/Cargo.toml
cargo build --release --manifest-path native/duck_core/Cargo.toml --target x86_64-pc-windows-gnu
dotnet test tests/DuckPrinterManager.Tests
dotnet publish src/DuckPrinterManager -c Release -o "$OUT"
echo "Pronto: $OUT"
