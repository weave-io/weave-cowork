#!/usr/bin/env bash
# Weave CoWork on one Linux machine (Ubuntu 24.04 tested): the server, a pool for team templates, and Dex for sign-in, as
# systemd user services of the user who runs this. Experimental: for trying CoWork out, not for keeping anything you can't lose.
#
#   deploy/single-host/cowork-host.sh install --url https://cowork.example.ts.net   # once; the address people open
#   deploy/single-host/cowork-host.sh person add alex@lumen.example "Alex Morgan"  # a sign-in for each person (prints a password)
#   deploy/single-host/cowork-host.sh pool XXXX-XXXX                               # after Where agents run → Add a pool
#   deploy/single-host/cowork-host.sh copilot-login                                # optional: GitHub Copilot as the model provider
#   LIVEKIT_URL=wss://… LIVEKIT_API_KEY=… LIVEKIT_API_SECRET=… deploy/single-host/cowork-host.sh voice   # optional: voice channels
#   deploy/single-host/cowork-host.sh status | logs [server|dex|pool] | stop | start | uninstall
#
# What it sets up:
# - Dex (an OpenID Connect provider) with one sign-in per person you add; it listens on 127.0.0.1:5556 under <url>/dex.
# - The CoWork server on 127.0.0.1:5880, data in ~/.local/share/cowork-host/data, signing people in through Dex.
# - A pool (rootless podman) that starts a sandbox per thread from cowork-sandbox-opencode:2.4.1, built here.
# Nothing listens on a public interface. Put HTTPS in front that serves <url>/ from 127.0.0.1:5880 and <url>/dex from
# 127.0.0.1:5556/dex and sets X-Forwarded-Proto: for example `cowork-host.sh tailscale-serve` on a Tailscale node, or Caddy.
set -euo pipefail

REPO="$(cd "$(dirname "$0")/../.." && pwd)"
CONF="$HOME/.config/cowork-host"
DATA="$HOME/.local/share/cowork-host"
UNITS="$HOME/.config/systemd/user"
DEX_IMAGE="ghcr.io/dexidp/dex:v2.44.0"
export XDG_RUNTIME_DIR="${XDG_RUNTIME_DIR:-/run/user/$(id -u)}"
export DOTNET_ROOT="$HOME/.dotnet" DOTNET_CLI_TELEMETRY_OPTOUT=1
export PATH="$HOME/.dotnet:$HOME/.bun/bin:$HOME/.local/bin:$PATH"

say() { printf '\n== %s\n' "$*"; }
die() { echo "$*" >&2; exit 1; }
need_conf() { [ -f "$CONF/host.env" ] || die "Not installed yet: run $0 install --url https://…"; . "$CONF/host.env"; }

dex_config() {   # writes Dex's config from host.env and the people file
  need_conf
  {
    echo "issuer: ${COWORK_URL}/dex"
    echo "storage: { type: memory }"
    echo "web: { http: 127.0.0.1:5556 }"
    echo "oauth2: { skipApprovalScreen: true }"
    echo "enablePasswordDB: true"
    echo "staticClients:"
    echo "  - { id: cowork, name: Weave CoWork, secret: '${CLIENT_SECRET}', redirectURIs: ['${COWORK_URL}/signin-oidc'] }"
    echo "staticPasswords:"
    if [ -s "$CONF/people.tsv" ]; then
      while IFS=$'\t' read -r email name id hash; do
        echo "  - { email: '${email}', username: '${name//\'/}', userID: '${id}', hash: '${hash}' }"
      done < "$CONF/people.tsv"
    else
      echo "  []"
    fi
  } > "$CONF/dex.yaml"
  chmod 600 "$CONF/dex.yaml"
}

write_units() {
  mkdir -p "$UNITS"
  cat > "$UNITS/cowork-dex.service" <<EOF
[Unit]
Description=Weave CoWork sign-in (Dex)
[Service]
ExecStartPre=-/usr/bin/podman rm -f cowork-dex
ExecStart=/usr/bin/podman run --rm --name cowork-dex --label cowork.host=dex --network host --user 0:0 -v %h/.config/cowork-host/dex.yaml:/etc/dex/config.yaml:ro ${DEX_IMAGE} dex serve /etc/dex/config.yaml
ExecStop=/usr/bin/podman stop -t 5 cowork-dex
Restart=on-failure
[Install]
WantedBy=default.target
EOF
  cat > "$UNITS/cowork-server.service" <<EOF
[Unit]
Description=Weave CoWork server
After=cowork-dex.service
[Service]
EnvironmentFile=%h/.config/cowork-host/server.env
WorkingDirectory=%h/.local/share/cowork-host/app
ExecStart=%h/.dotnet/dotnet %h/.local/share/cowork-host/app/CoWork.Server.dll
Restart=on-failure
MemoryMax=4G
[Install]
WantedBy=default.target
EOF
  cat > "$UNITS/cowork-pool.service" <<EOF
[Unit]
Description=Weave CoWork pool
After=cowork-server.service
[Service]
Environment=COWORK_RUNNER_CONFIG=%h/.config/cowork-host/runner
ExecStart=%h/.local/share/cowork-host/runner/cowork-runner pool run
Restart=on-failure
MemoryMax=1G
[Install]
WantedBy=default.target
EOF
  systemctl --user daemon-reload
}

build() {
  say "Building the web app, the server and the runner from $REPO"
  (cd "$REPO/web" && bun install --frozen-lockfile >/dev/null && bun run build >/dev/null)
  rm -rf "$DATA/app.new" "$DATA/runner.new"
  dotnet publish "$REPO/src/CoWork.Server/CoWork.Server.csproj" -c Release -o "$DATA/app.new" --nologo -v quiet
  dotnet publish "$REPO/src/CoWork.Runner/CoWork.Runner.csproj" -c Release -r linux-x64 --self-contained true \
    -p:PublishSingleFile=true -o "$DATA/runner.new" --nologo -v quiet
  rm -rf "$DATA/app" "$DATA/runner"; mv "$DATA/app.new" "$DATA/app"; mv "$DATA/runner.new" "$DATA/runner"
  say "Building the sandbox image (OpenCode 2.4.1 from npm, checked)"
  "$REPO/deploy/sandbox/build.sh"
  "$REPO/deploy/sandbox/fetch-opencode.sh" "$HOME/.local/bin/opencode"   # the host's own copy, for copilot-login
}

cmd_install() {
  local url=""
  while [ $# -gt 0 ]; do case "$1" in --url) url="${2%/}"; shift 2;; *) die "Unknown option $1";; esac; done
  [ -n "$url" ] || [ -f "$CONF/host.env" ] || die "Give the address people will open: $0 install --url https://…"
  mkdir -p "$CONF" "$DATA/data" "$CONF/runner"; chmod 700 "$CONF"

  say "Packages (sudo): podman, git, curl, python3-bcrypt, unzip; and lingering, so the services run without a login"
  sudo apt-get update -qq
  sudo DEBIAN_FRONTEND=noninteractive apt-get install -y -qq podman uidmap passt slirp4netns git curl ca-certificates python3-bcrypt unzip >/dev/null
  sudo loginctl enable-linger "$USER"

  if ! dotnet --list-sdks 2>/dev/null | grep -q '^10\.'; then
    say ".NET 10 SDK into ~/.dotnet"
    curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$DATA/dotnet-install.sh"
    bash "$DATA/dotnet-install.sh" --channel 10.0 --install-dir "$HOME/.dotnet" >/dev/null
  fi
  if ! command -v bun >/dev/null; then
    say "bun into ~/.bun"
    curl -fsSL https://bun.sh/install | bash >/dev/null
  fi

  if [ ! -f "$CONF/host.env" ]; then
    printf 'COWORK_URL=%s\nCLIENT_SECRET=%s\n' "$url" "$(head -c 24 /dev/urandom | base64 | tr -dc 'A-Za-z0-9')" > "$CONF/host.env"
  elif [ -n "$url" ]; then
    sed -i "s|^COWORK_URL=.*|COWORK_URL=$url|" "$CONF/host.env"
  fi
  chmod 600 "$CONF/host.env"; . "$CONF/host.env"
  local voice=""; voice="$(grep '^LIVEKIT_' "$CONF/server.env" 2>/dev/null || true)"
  cat > "$CONF/server.env" <<EOF
COWORK_DATA=$DATA/data
ASPNETCORE_URLS=http://127.0.0.1:5880
COWORK_OIDC__AUTHORITY=$COWORK_URL/dex
COWORK_OIDC__CLIENTID=cowork
COWORK_OIDC__CLIENTSECRET=$CLIENT_SECRET
DOTNET_ROOT=$HOME/.dotnet
EOF
  [ -z "$voice" ] || echo "$voice" >> "$CONF/server.env"
  chmod 600 "$CONF/server.env"
  touch "$CONF/people.tsv"; chmod 600 "$CONF/people.tsv"

  build
  podman pull -q "$DEX_IMAGE" >/dev/null
  dex_config
  write_units
  systemctl --user enable --now cowork-dex.service cowork-server.service >/dev/null
  for _ in $(seq 60); do curl -fs -o /dev/null http://127.0.0.1:5880/api/setup/status && break; sleep 1; done
  curl -fs -o /dev/null http://127.0.0.1:5880/api/setup/status || die "The server didn't start: $0 logs server"

  say "Installed. Next:"
  cat <<EOF
  1. HTTPS in front, serving $COWORK_URL/ from 127.0.0.1:5880 and $COWORK_URL/dex from 127.0.0.1:5556/dex
     (on a Tailscale node: $0 tailscale-serve).
  2. A sign-in for yourself: $0 person add you@example.com "Your Name"
  3. Open $COWORK_URL and set the server up: the first person to arrive is its admin. Invite the others with links,
     and add each of them here too ($0 person add …).
  4. For team templates: server menu → Where agents run → Add a pool, then $0 pool XXXX-XXXX
  5. For the agents' models: $0 copilot-login (GitHub Copilot signed in here), or Server settings → Models with an API key.
EOF
}

cmd_person() {
  [ "${1:-}" = "add" ] || die "Usage: $0 person add <email> [\"Display name\"]"
  local email="${2:?email}" name="${3:-${2%@*}}"
  need_conf
  grep -q "^${email}"$'\t' "$CONF/people.tsv" 2>/dev/null && die "$email can already sign in."
  local pw hash id
  pw="$(head -c 18 /dev/urandom | base64 | tr -dc 'A-Za-z0-9' | head -c 16)"
  hash="$(PW="$pw" python3 -c 'import bcrypt,os; print(bcrypt.hashpw(os.environ["PW"].encode(), bcrypt.gensalt(10)).decode())')"
  id="$(cat /proc/sys/kernel/random/uuid)"
  printf '%s\t%s\t%s\t%s\n' "$email" "$name" "$id" "$hash" >> "$CONF/people.tsv"
  dex_config
  systemctl --user restart cowork-dex.service
  echo "$email can sign in at $COWORK_URL with the password: $pw"
  echo "(Shown once. They still need an invite link from CoWork, except the first person, who sets the server up.)"
}

cmd_pool() {
  local code="${1:?Usage: $0 pool XXXX-XXXX [slots] [memory]}" slots="${2:-4}" memory="${3:-1500M}"
  need_conf
  COWORK_RUNNER_CONFIG="$CONF/runner" "$DATA/runner/cowork-runner" pool connect http://127.0.0.1:5880 --code "$code" \
    --slots "$slots" --memory "$memory" --host "$(hostname -s)" --enrol-only
  systemctl --user enable --now cowork-pool.service >/dev/null
  echo "The pool is running. It shows under Where agents run."
}

cmd_copilot_login() {
  echo "Sign this machine's OpenCode in to GitHub Copilot. CoWork's gateway then forwards every sandbox's model calls to"
  echo "that Copilot account (Server settings → Models → GitHub Copilot). Use a server only you, or a short demo, relies on."
  opencode auth login github-copilot
}

cmd_voice() {   # voice channels through a LiveKit server (LiveKit Cloud, or your own): the values come from the environment, not argv
  need_conf
  [ -n "${LIVEKIT_URL:-}" ] && [ -n "${LIVEKIT_API_KEY:-}" ] && [ -n "${LIVEKIT_API_SECRET:-}" ] \
    || die "Usage: LIVEKIT_URL=wss://… LIVEKIT_API_KEY=… LIVEKIT_API_SECRET=… $0 voice"
  case "$LIVEKIT_URL" in wss://*|ws://*) ;; *) die "LIVEKIT_URL is the address browsers use: wss://…";; esac
  sed -i '/^LIVEKIT_/d' "$CONF/server.env"
  printf 'LIVEKIT_URL=%s\nLIVEKIT_API_KEY=%s\nLIVEKIT_API_SECRET=%s\n' "$LIVEKIT_URL" "$LIVEKIT_API_KEY" "$LIVEKIT_API_SECRET" >> "$CONF/server.env"
  chmod 600 "$CONF/server.env"
  systemctl --user restart cowork-server.service
  for _ in $(seq 60); do curl -fs -o /dev/null http://127.0.0.1:5880/api/setup/status && break; sleep 1; done
  echo "Voice is on: voice channels use $LIVEKIT_URL. Browsers need the server's HTTPS address to use the microphone."
}

cmd_tailscale_serve() {
  need_conf
  command -v tailscale >/dev/null || die "Tailscale isn't installed on this machine."
  # tailscale serve waits forever when the tailnet has Serve or HTTPS certificates off: check first, and say what's missing
  local certs
  certs="$(tailscale status --json 2>/dev/null | python3 -c 'import json,sys; print(" ".join(json.load(sys.stdin).get("CertDomains") or []))' 2>/dev/null || true)"
  [ -n "$certs" ] || die "This tailnet has no HTTPS certificates, so tailscale serve can't give $COWORK_URL a certificate.
A tailnet admin turns on MagicDNS, HTTPS Certificates (admin console, DNS) and Serve, then run this again."
  case " $certs " in *" ${COWORK_URL#https://} "*) ;; *) echo "Note: $COWORK_URL isn't this machine's certificate name ($certs)." >&2;; esac
  timeout 60 sudo tailscale serve --bg --https=443 http://127.0.0.1:5880 \
    || die "tailscale serve didn't finish. If it asked for Serve to be turned on, a tailnet admin does that at the link it printed."
  timeout 60 sudo tailscale serve --bg --https=443 --set-path=/dex http://127.0.0.1:5556/dex || die "tailscale serve for /dex didn't finish."
  sudo tailscale serve status
}

case "${1:-}" in
  install) shift; cmd_install "$@";;
  rebuild) need_conf; build; systemctl --user restart cowork-server.service; systemctl --user try-restart cowork-pool.service;;
  person) shift; cmd_person "$@";;
  pool) shift; cmd_pool "$@";;
  copilot-login) cmd_copilot_login;;
  voice) cmd_voice;;
  tailscale-serve) cmd_tailscale_serve;;
  status) systemctl --user --no-pager status cowork-dex cowork-server cowork-pool 2>/dev/null | grep -E '●|Active:' || true;;
  logs) journalctl --user --no-pager -n 80 -u "cowork-${2:-server}";;
  stop) systemctl --user stop cowork-pool cowork-server cowork-dex 2>/dev/null || true;;
  start) systemctl --user start cowork-dex cowork-server; systemctl --user try-restart cowork-pool 2>/dev/null || true;;
  uninstall)
    systemctl --user disable --now cowork-pool cowork-server cowork-dex 2>/dev/null || true
    podman ps -aq --filter label=cowork.pool | xargs -r podman rm -f >/dev/null
    podman volume ls -q --filter label=cowork.pool | xargs -r podman volume rm >/dev/null
    rm -f "$UNITS"/cowork-{dex,server,pool}.service; systemctl --user daemon-reload
    echo "Services and sandboxes removed. Data is still in $DATA and settings in $CONF: delete them to start again.";;
  *) sed -n '2,10p' "$0" | sed 's/^# \{0,1\}//'; exit 2;;
esac
