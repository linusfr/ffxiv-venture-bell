#!/usr/bin/env just --justfile
# ==========================================
# DEFAULT
# ==========================================

# Show all available commands
@default:
    just --list

server := "server"
plugin := "plugin/VentureBell.csproj"
image_name := "ffxiv-venture-bell"

dev_plugins := env_var('HOME') / ".xlcore/devPlugins/VentureBell"
# Dalamud's SDK needs the game's assemblies; XIVLauncher.Core already has them.
dalamud_home := env_var('HOME') / ".xlcore/dalamud/Hooks/dev"
# NixOS has no system dotnet — pull the SDK from nixpkgs for the duration.
dotnet := "nix shell nixpkgs#dotnet-sdk_10 -c env DALAMUD_HOME=" + dalamud_home + " DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 dotnet"

# ==========================================
# SERVER
# ==========================================

# Build the server binary into dist/
build-server:
    mkdir -p dist
    cd {{server}} && go build -trimpath -o ../dist/venturebell .

# Run with a throwaway token and no notifier: completions are only logged
run token="dev-token":
    mkdir -p dist
    cd {{server}} && BELL_TOKEN={{token}} BELL_STATE=../dist/state.json BELL_DEBUG=1 go run .

test:
    cd {{server}} && go test -race ./...

# Pretend a summoning bell: sync one retainer finishing in `secs` seconds.
sync secs="10" token="dev-token" addr="127.0.0.1:8770":
    curl -sS -X POST http://{{addr}}/sync \
      -H "Authorization: Bearer {{token}}" \
      -H "Content-Type: application/json" \
      -d "{\"character\":\"Test@Phoenix\",\"retainers\":[{\"name\":\"Sultana\",\"venture\":\"Quick Exploration\",\"done_at\":$(($(date +%s)+{{secs}}))}]}" \
      -w "%{http_code}\n"

# What the server currently believes
state token="dev-token" addr="127.0.0.1:8770":
    curl -sS http://{{addr}}/state -H "Authorization: Bearer {{token}}"

# Build the container image
image:
    docker build -t {{image_name}}:dev {{server}}

# ==========================================
# CHART
# ==========================================

helm := "nix shell nixpkgs#kubernetes-helm -c helm"

chart-lint:
    {{helm}} lint chart --set secret.bellToken=lint

# Render every shape the values can take, and check the result is a manifest
# Kubernetes would accept.
chart-check: chart-lint
    #!/usr/bin/env bash
    set -euo pipefail
    nix shell nixpkgs#kubernetes-helm nixpkgs#kubeconform -c bash -c '
      helm template vb chart --set secret.bellToken=x \
        --set ingress.enabled=true --set ingress.host=bell.example.com \
        | kubeconform -strict -summary -
      helm template vb chart --set existingSecret=bell --set persistence.enabled=false \
        | kubeconform -strict -summary -'

# What the release workflow does: the tag becomes both version and appVersion,
# so the chart and the image it pulls carry the same number.
chart-package version="0.0.0":
    mkdir -p dist
    {{helm}} package chart --version {{version}} --app-version {{version}} -d dist

# ==========================================
# PLUGIN
# ==========================================

restore:
    {{dotnet}} restore {{plugin}}

# Debug build
build-plugin: restore
    {{dotnet}} build {{plugin}} --configuration Debug --no-restore

# Release build (what CI ships)
build-release: restore
    {{dotnet}} build {{plugin}} --configuration Release --no-restore

# Build and drop the plugin into XIVLauncher's devPlugins for /xlplugins dev mode
install: build-plugin
    rm -rf {{dev_plugins}}
    mkdir -p {{dev_plugins}}
    # The whole build output, not a hand-picked subset: Dalamud identifies a dev
    # plugin by the VentureBell.json manifest next to the DLL, and needs the
    # .deps.json to resolve assemblies.
    cp -r plugin/bin/Debug/. {{dev_plugins}}/
    @echo "Installed to {{dev_plugins}}."
    @echo "Now: /xlplugins -> Dev Tools -> reload, or restart the game."

# ==========================================
# QUALITY
# ==========================================

fmt:
    cd {{server}} && gofmt -w .
    {{dotnet}} format style {{plugin}}
    {{dotnet}} format analyzers {{plugin}}

fmt-check:
    #!/usr/bin/env bash
    set -euo pipefail
    unformatted=$(cd {{server}} && gofmt -l .)
    if [ -n "$unformatted" ]; then echo "needs gofmt:"; echo "$unformatted"; exit 1; fi
    {{dotnet}} format style {{plugin}} --verify-no-changes
    {{dotnet}} format analyzers {{plugin}} --verify-no-changes

vet:
    cd {{server}} && go vet ./...

# Everything CI runs
check: fmt-check vet test chart-check build-plugin
    prek run --all-files

# Install the git hooks
hooks:
    prek install --install-hooks
    prek install --hook-type commit-msg

clean:
    rm -rf dist plugin/bin plugin/obj VentureBell.zip pack/
