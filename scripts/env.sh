#!/usr/bin/env bash
# Source this file before running any dotnet/npm command in this repo:
#   source scripts/env.sh
# The SDK lives in ~/.dotnet (win-arm64 build on this machine); the copy under
# "C:\Program Files\dotnet" only ships the 8.0 runtime and has no SDK.
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
