#!/usr/bin/env bash
# Builds whisper.cpp for Android phones (arm64) into native/out/arm64-v8a, two variants of the same library:
#   libcuepilot_whisper_v8.so  - any 64-bit ARM phone (ARMv8.0 NEON);
#   libcuepilot_whisper_v82.so - phones since about 2019 (ARMv8.2 with dot product and FP16), 2-3 times faster.
# The app picks the variant by the CPU features of the phone (Platform/LocalWhisper.cs).
# Needs the Android NDK (ANDROID_NDK_ROOT, ANDROID_NDK_HOME or ANDROID_NDK_LATEST_HOME), cmake and git.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
WHISPER_TAG="${WHISPER_TAG:-v1.7.4}"
NDK="${ANDROID_NDK_ROOT:-${ANDROID_NDK_HOME:-${ANDROID_NDK_LATEST_HOME:-}}}"
# Outside the project folder: the .NET Android build would otherwise pick up whisper.cpp's Java and C# bindings.
WORK="${WORK:-${RUNNER_TEMP:-${TMPDIR:-/tmp}}/cuepilot-whisper}"
OUT="$HERE/out/arm64-v8a"

if [ -z "$NDK" ] || [ ! -f "$NDK/build/cmake/android.toolchain.cmake" ]; then
  echo "[whisper] Android NDK not found: set ANDROID_NDK_ROOT" >&2
  exit 1
fi

mkdir -p "$WORK" "$OUT"
SRC="$WORK/whisper.cpp-$WHISPER_TAG"
if [ ! -d "$SRC" ]; then
  git clone --depth 1 --branch "$WHISPER_TAG" https://github.com/ggml-org/whisper.cpp.git "$SRC"
fi

STRIP="$(ls "$NDK"/toolchains/llvm/prebuilt/*/bin/llvm-strip | head -1)"

build() {
  local name="$1" arch="$2"
  echo "[whisper] $name ($arch), whisper.cpp $WHISPER_TAG"
  cmake -S "$HERE" -B "$WORK/$name" \
    -DCMAKE_TOOLCHAIN_FILE="$NDK/build/cmake/android.toolchain.cmake" \
    -DANDROID_ABI=arm64-v8a -DANDROID_PLATFORM=android-26 -DANDROID_STL=c++_static \
    -DCMAKE_BUILD_TYPE=Release -DWHISPER_DIR="$SRC" -DCPW_OUTPUT_NAME="$name" -DGGML_CPU_ARM_ARCH="$arch" > /dev/null
  cmake --build "$WORK/$name" --config Release -j "$(nproc)" --target cuepilot_whisper > /dev/null
  local lib
  lib="$(find "$WORK/$name" -name "lib$name.so" | head -1)"
  cp "$lib" "$OUT/"
  "$STRIP" --strip-unneeded "$OUT/lib$name.so"
  ls -la "$OUT/lib$name.so"
}

build cuepilot_whisper_v8 armv8-a
build cuepilot_whisper_v82 armv8.2-a+dotprod+fp16
