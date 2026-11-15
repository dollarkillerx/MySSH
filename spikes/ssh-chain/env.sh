#!/usr/bin/env bash
# Throwaway test environment for the ssh-chain spike.
# Runs NON-ROOT sshd instances on 127.0.0.1 only + an OpenSSH SOCKS5 proxy.
# All state lives in ./.work (gitignored). Nothing outside this folder is touched.
#
#   ./env.sh start         generate keys/configs, start sshd x3 + SOCKS5
#   ./env.sh stop          stop everything started by this script
#   ./env.sh status        show processes
#   ./env.sh regen-target  regenerate the target host keys and restart it (TOFU mismatch test)
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
W="$HERE/.work"
USER_NAME="$(id -un)"
PASS="spike-pass"

# name:port
INSTANCES=("bastion:2222" "target:2223" "hop2:2224")
SOCKS_PORT=1080

mk_client_keys() {
  mkdir -p "$W/keys"
  [ -f "$W/keys/id_ed25519_pass" ] || ssh-keygen -q -t ed25519 -N "$PASS" -C spike-ed25519-pass -f "$W/keys/id_ed25519_pass"
  [ -f "$W/keys/id_ed25519" ]      || ssh-keygen -q -t ed25519 -N ""      -C spike-ed25519 -f "$W/keys/id_ed25519"
  [ -f "$W/keys/id_rsa" ]          || ssh-keygen -q -t rsa -b 3072 -N ""  -C spike-rsa -f "$W/keys/id_rsa"
  [ -f "$W/keys/id_rsa_pass" ]     || ssh-keygen -q -t rsa -b 3072 -N "$PASS" -C spike-rsa-pass -f "$W/keys/id_rsa_pass"
  # legacy PEM ("BEGIN RSA PRIVATE KEY", encrypted) variant of id_rsa_pass
  if [ ! -f "$W/keys/id_rsa_pem_pass" ]; then
    cp "$W/keys/id_rsa_pass" "$W/keys/id_rsa_pem_pass"
    ssh-keygen -q -p -m PEM -P "$PASS" -N "$PASS" -f "$W/keys/id_rsa_pem_pass" >/dev/null
  fi
  cat "$W/keys/"id_ed25519_pass.pub "$W/keys/"id_ed25519.pub "$W/keys/"id_rsa.pub "$W/keys/"id_rsa_pass.pub > "$W/authorized_keys"
  chmod 600 "$W/authorized_keys" "$W/keys/"id_*
  printf '%s' "$PASS" > "$W/keys/passphrase.txt"
}

mk_instance() {
  local name="$1" port="$2" d="$W/$1"
  mkdir -p "$d"
  [ -f "$d/ssh_host_ed25519_key" ] || ssh-keygen -q -t ed25519 -N "" -f "$d/ssh_host_ed25519_key"
  [ -f "$d/ssh_host_rsa_key" ]     || ssh-keygen -q -t rsa -b 3072 -N "" -f "$d/ssh_host_rsa_key"
  cat > "$d/sshd_config" <<EOF
Port $port
ListenAddress 127.0.0.1
HostKey $d/ssh_host_ed25519_key
HostKey $d/ssh_host_rsa_key
PidFile $d/sshd.pid
AuthorizedKeysFile $W/authorized_keys
PubkeyAuthentication yes
PasswordAuthentication no
KbdInteractiveAuthentication no
UsePAM no
StrictModes no
AllowTcpForwarding yes
PermitTTY yes
AllowUsers $USER_NAME
LogLevel DEBUG1
Subsystem sftp internal-sftp
ClientAliveInterval 0
PerSourcePenalties no
MaxStartups 100:30:200
EOF
}

start() {
  mkdir -p "$W"
  mk_client_keys
  for i in "${INSTANCES[@]}"; do
    local name="${i%%:*}" port="${i##*:}"
    mk_instance "$name" "$port"
    if [ -f "$W/$name/sshd.pid" ] && kill -0 "$(cat "$W/$name/sshd.pid")" 2>/dev/null; then
      echo "$name already running"; continue
    fi
    /usr/sbin/sshd -t -f "$W/$name/sshd_config"
    # -D: no daemonize, -e: log to stderr. Background it ourselves.
    nohup /usr/sbin/sshd -D -e -f "$W/$name/sshd_config" >"$W/$name/sshd.log" 2>&1 &
    echo $! > "$W/$name/launcher.pid"
    echo "started $name on 127.0.0.1:$port (pid $!)"
  done
  sleep 1
  # SOCKS5 via OpenSSH dynamic forward through the bastion.
  if [ -f "$W/socks.pid" ] && kill -0 "$(cat "$W/socks.pid")" 2>/dev/null; then
    echo "socks already running"
  else
    nohup /usr/bin/ssh -N -D 127.0.0.1:$SOCKS_PORT -p 2222 -i "$W/keys/id_ed25519" \
      -F /dev/null -o IdentitiesOnly=yes -o BatchMode=yes \
      -o UserKnownHostsFile="$W/kh" -o StrictHostKeyChecking=accept-new \
      -o ExitOnForwardFailure=yes \
      "$USER_NAME@127.0.0.1" >"$W/socks.log" 2>&1 &
    echo $! > "$W/socks.pid"
    echo "started SOCKS5 on 127.0.0.1:$SOCKS_PORT (pid $!)"
  fi
  sleep 1
  status
}

stop_pidfile() {
  local f="$1"
  if [ -f "$f" ]; then
    local p; p="$(cat "$f")"
    if kill -0 "$p" 2>/dev/null; then kill "$p" 2>/dev/null || true; echo "killed $p ($f)"; fi
    rm -f "$f"
  fi
}

stop() {
  stop_pidfile "$W/socks.pid"
  for i in "${INSTANCES[@]}"; do
    local name="${i%%:*}"
    stop_pidfile "$W/$name/sshd.pid"
    stop_pidfile "$W/$name/launcher.pid"
  done
  sleep 1
  # Safety net: anything still referencing our .work dir (sshd-session children etc.)
  pkill -f "$W/" 2>/dev/null || true
  status
}

status() {
  echo "--- processes referencing $W ---"
  pgrep -fl "$W/" || echo "(none)"
}

regen_target() {
  local d="$W/target"
  stop_pidfile "$d/sshd.pid"; stop_pidfile "$d/launcher.pid"
  sleep 0.5
  rm -f "$d"/ssh_host_*
  mk_instance target 2223
  nohup /usr/sbin/sshd -D -e -f "$d/sshd_config" >>"$d/sshd.log" 2>&1 &
  echo $! > "$d/launcher.pid"
  sleep 1
  echo "target host keys regenerated:"
  ssh-keygen -lf "$d/ssh_host_ed25519_key.pub"
}

case "${1:-}" in
  start) start ;;
  stop) stop ;;
  status) status ;;
  regen-target) regen_target ;;
  *) echo "usage: $0 start|stop|status|regen-target"; exit 2 ;;
esac
