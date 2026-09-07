#!/bin/bash
#
# Installs the toolchain a Claude Code on the web session needs to actually run the game's
# self-test, which is the only way anything here gets verified: `--selftest` drives every screen
# with scripted devices and then simulates thirteen bot matches, and a change that builds is not
# the same as a change that works.
#
# The repository deliberately does not carry the toolchain - see "Setting up a machine that has
# never run this" in README.md - so a fresh container has neither half of it. This is the web
# equivalent of double-clicking Setup.cmd.
#
# Local machines are left alone. A developer's own Godot and SDK are their business, and a hook
# that installed a second copy of both on every session would be an unpleasant surprise.
set -euo pipefail

if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  exit 0
fi

GODOT_VERSION="4.7.1-stable"
GODOT_DIR="/opt/godot"
GODOT_BIN="$GODOT_DIR/Godot_v${GODOT_VERSION}_mono_linux.x86_64"

# ---------------------------------------------------------------------------
# .NET 8 SDK.
#
# From Ubuntu's own archive rather than dot.net, which the sandbox network policy does not allow
# through. The apt index in the base image is stale enough that the dotnet8 packages 404 on a
# straight install, so the update is not optional.
# ---------------------------------------------------------------------------
if ! dotnet --list-sdks 2>/dev/null | grep -q '^8\.'; then
  echo "Installing the .NET 8 SDK..."
  apt-get update -qq
  DEBIAN_FRONTEND=noninteractive apt-get install -y -qq dotnet-sdk-8.0
fi

# ---------------------------------------------------------------------------
# Godot 4.7.1, the .NET/mono build.
#
# It must be the mono build: the plain one cannot run C# at all and fails with a wall of script
# errors that never mention that as the cause.
#
# Fetched from the GitHub release rather than godotengine.org, which the network policy blocks.
# The release redirects to the assets CDN, which it allows - so this works where the obvious URL
# does not, and that is worth writing down because it is not guessable.
# ---------------------------------------------------------------------------
if [ ! -x "$GODOT_BIN" ]; then
  echo "Installing Godot ${GODOT_VERSION} (mono)..."
  mkdir -p "$GODOT_DIR"
  tmp="$(mktemp -d)"
  curl -sSL --retry 3 --retry-delay 2 -o "$tmp/godot.zip" \
    "https://github.com/godotengine/godot/releases/download/${GODOT_VERSION}/Godot_v${GODOT_VERSION}_mono_linux_x86_64.zip"
  unzip -q -o "$tmp/godot.zip" -d "$tmp"
  cp -r "$tmp/Godot_v${GODOT_VERSION}_mono_linux_x86_64/." "$GODOT_DIR/"
  chmod +x "$GODOT_BIN"
  rm -rf "$tmp"
fi

# GODOT_HOME is the same variable Play.cmd and Setup.cmd look for on Windows, so the two halves of
# the project agree on one name for "where Godot is".
{
  echo "export GODOT_HOME=\"$GODOT_DIR\""
  echo "export GODOT=\"$GODOT_BIN\""
} >> "${CLAUDE_ENV_FILE:-/dev/null}"

# ---------------------------------------------------------------------------
# Import the assets once, here, where the cost is paid by the container rather than by the first
# thing that tries to run a test.
#
# Godot builds a .godot/ cache from every model in assets/ on first launch. It takes minutes, it is
# derived data, and it is deliberately not in the repository - so without this the first --selftest
# of every session looks like a hang for five minutes before printing anything.
# ---------------------------------------------------------------------------
if [ ! -d "$CLAUDE_PROJECT_DIR/.godot/imported" ]; then
  echo "Importing assets (first run only, this takes a few minutes)..."
  "$GODOT_BIN" --headless --path "$CLAUDE_PROJECT_DIR" --import >/dev/null 2>&1 || true
fi

echo "Toolchain ready: $(dotnet --version), Godot ${GODOT_VERSION}. Run ./Test.sh"
