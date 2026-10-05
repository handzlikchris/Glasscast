#!/bin/sh
# Starts the Glasscast phone relay from its own folder (where relay.json, relay.Local.json and
# wwwroot are). Copied next to the program by scripts/publish-relay.ps1 (-Runtime linux-x64).
# It listens on 127.0.0.1:5090 only; Caddy (deploy/Caddyfile.relay) is the public side.
# As a service: deploy/relay/glasscast-relay.service.
cd "$(dirname "$0")" || exit 1
exec ./GlassesRemote.RelayServer "$@"
