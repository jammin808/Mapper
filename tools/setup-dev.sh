#!/usr/bin/env bash
# A PlayMap build and test machine (Ubuntu 24.04, run as root; safe to run again): the .NET 10 SDK, headless OpenGL (Mesa
# llvmpipe under Xvfb), FFmpeg 9.0's shared libraries in /opt/ffmpeg9, and a sound stack (PulseAudio, sox) so audio capture runs
# against a real device. `tools/setup-dev.sh` installs; `tools/setup-dev.sh start` also starts the display and the sound card for
# this session and prints the variables the tests read. PulseAudio pulls Ubuntu's FFmpeg and with it libva, which a bare machine
# (and CI) lacks: `tools/without-libs.sh <command>` runs one command as on such a machine.
set -euo pipefail

FFMPEG_DIR=/opt/ffmpeg9
FFMPEG_URL="https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n9.0-latest-linux64-gpl-shared-9.0.tar.xz"

install() {
  export DEBIAN_FRONTEND=noninteractive
  apt-get update -qq
  apt-get install -y -qq --no-install-recommends \
    dotnet-sdk-10.0 \
    xvfb libgl1-mesa-dri libegl1 libgles2 libgl1 libglx-mesa0 libglfw3 libfontconfig1 fonts-dejavu-core \
    pulseaudio pulseaudio-utils sox libsox-fmt-base alsa-utils \
    curl xz-utils ca-certificates
  if [ ! -e "$FFMPEG_DIR/lib/libavcodec.so" ]; then
    local tmp
    tmp=$(mktemp -d)
    curl -sSL -o "$tmp/ff.tar.xz" "$FFMPEG_URL"
    mkdir -p "$FFMPEG_DIR"
    tar -xJf "$tmp/ff.tar.xz" -C "$FFMPEG_DIR" --strip-components=1
    rm -rf "$tmp"
  fi
}

# The display the GL tests draw on (:99) and a sound card with one virtual output (playmap_test) whose monitor is the input the
# audio tests capture from.
start() {
  if ! pgrep -f "^Xvfb :99( |$)" >/dev/null; then
    (Xvfb :99 -screen 0 1920x1080x24 >/dev/null 2>&1 &)
  fi
  if ! pactl info >/dev/null 2>&1; then
    pulseaudio -D --exit-idle-time=-1 2>/dev/null || true
    for _ in 1 2 3 4 5 6 7 8 9 10; do pactl info >/dev/null 2>&1 && break; sleep 0.5; done
  fi
  if ! pactl list short sinks | grep -q playmap_test; then
    pactl load-module module-null-sink sink_name=playmap_test sink_properties=device.description=PlayMapTest >/dev/null
  fi
  pactl set-default-sink playmap_test
  echo "export DISPLAY=:99 PLAYMAP_FFMPEG_DIR=$FFMPEG_DIR/lib PLAYMAP_TEST_AUDIO=pulse:playmap_test.monitor"
}

install
if [ "${1:-}" = "start" ]; then
  start
fi
