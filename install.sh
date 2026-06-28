#!/usr/bin/env bash
# ==============================================================================
#  HorusAPI — Server Installation Script
#  Tested on: Debian 11 (Bullseye) · Debian 12 (Bookworm)
#  Run as root: sudo bash install.sh
# ==============================================================================
set -euo pipefail

# ── Output helpers ─────────────────────────────────────────────────────────────
RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'
BLUE='\033[0;34m'; CYAN='\033[0;36m'; BOLD='\033[1m'; NC='\033[0m'

info()    { echo -e "${BLUE}  →${NC} $*"; }
success() { echo -e "${GREEN}  ✓${NC} $*"; }
warn()    { echo -e "${YELLOW}  ⚠${NC} $*"; }
error()   { echo -e "${RED}  ✗ ERROR:${NC} $*" >&2; exit 1; }
section() { echo -e "\n${BOLD}${CYAN}━━  $*  ━━${NC}"; }
ask()     { echo -ne "${YELLOW}  ?${NC} $* "; }

# ── Root check ──────────────────────────────────────────────────────────────────
check_root() {
    [[ "$EUID" -eq 0 ]] && return 0
    if command -v sudo &>/dev/null; then
        error "This script must run as root. Try: sudo bash $0"
    else
        error "This script must run as root. sudo is not installed — log in as root or use: su -c 'bash $0'"
    fi
}
check_root

# ── run_as: run a command as another user without requiring sudo ───────────────
# Priority: sudo → runuser (util-linux, standard on Debian) → su fallback
run_as() {
    local user="$1"; shift
    if command -v sudo &>/dev/null; then
        sudo -u "$user" "$@"
    elif command -v runuser &>/dev/null; then
        runuser -u "$user" -- "$@"
    else
        # Build a properly shell-quoted command string for su -c
        su -s /bin/bash "$user" -c "$(printf '%q ' "$@")"
    fi
}

export DEBIAN_FRONTEND=noninteractive

# ── Pre-flight ──────────────────────────────────────────────────────────────────
section "Pre-flight"
info "Checking internet connectivity..."
curl -sf --max-time 10 https://github.com > /dev/null \
    || error "Cannot reach github.com. Check network / DNS."
success "Internet OK"

# ==============================================================================
#  COLLECT ALL CONFIGURATION UP FRONT
# ==============================================================================
section "Configuration"

echo ""
echo -e "${BOLD}── GitHub ──────────────────────────────────────────${NC}"

ask "GitHub username:"
read -r GH_USER

ask "Repository name (e.g. HorusAPI):"
read -r REPO_NAME

echo ""
echo "  Create a Personal Access Token at:"
echo -e "  ${CYAN}https://github.com/settings/tokens/new${NC}"
echo "  Required scopes: ${BOLD}repo${NC} (includes deploy keys + runner token)"
echo "  You can delete the token after this script finishes."
ask "GitHub PAT:"
read -rs GH_PAT
echo ""

echo ""
echo -e "${BOLD}── Application ─────────────────────────────────────${NC}"

ask "Public domain for this server (e.g. vpn.example.com):"
read -r DOMAIN
[[ -z "$DOMAIN" ]] && error "Domain is required."

ask "Let's Encrypt / certbot email:"
read -r CERTBOT_EMAIL
[[ -z "$CERTBOT_EMAIL" ]] && error "Email is required."

ask "Use Let's Encrypt STAGING mode for testing? [y/N]:"
read -r _staging
CERTBOT_STAGING=0
[[ "$_staging" =~ ^[Yy]$ ]] && CERTBOT_STAGING=1

echo ""
echo -e "${BOLD}── Database ────────────────────────────────────────${NC}"

ask "PostgreSQL database name [horus]:"
read -r POSTGRES_DB;  POSTGRES_DB="${POSTGRES_DB:-horus}"

ask "PostgreSQL username [horus]:"
read -r POSTGRES_USER; POSTGRES_USER="${POSTGRES_USER:-horus}"

_pg_gen=$(openssl rand -base64 24 | tr -d '+/=\n' | head -c 32)
ask "PostgreSQL password [auto-generated, press ENTER to accept]:"
read -r _pg_input
POSTGRES_PASSWORD="${_pg_input:-$_pg_gen}"

echo ""
echo -e "${BOLD}── Server ──────────────────────────────────────────${NC}"

ask "SSH port [22]:"
read -r SSH_PORT; SSH_PORT="${SSH_PORT:-22}"

# ── Derived constants ───────────────────────────────────────────────────────────
APP_DIR="/opt/${REPO_NAME}"
RUNNER_USER="github-runner"
RUNNER_DIR="/opt/actions-runner"
DEPLOY_KEY="/home/${RUNNER_USER}/.ssh/github_deploy"

echo ""
info "All configuration collected. Starting installation."

# ==============================================================================
#  STEP 1 — SYSTEM UPDATE & BASE PACKAGES
# ==============================================================================
section "Step 1/11 — System update & base packages"

apt-get update -y
apt-get upgrade -y
apt-get install -y \
    curl wget git openssl ca-certificates gnupg \
    lsb-release software-properties-common apt-transport-https \
    ufw iptables-persistent fail2ban unattended-upgrades \
    jq tmux htop net-tools

success "Base packages installed"

# ==============================================================================
#  STEP 2 — DOCKER
# ==============================================================================
section "Step 2/11 — Docker"

if command -v docker &>/dev/null; then
    warn "Docker already installed — skipping."
else
    install -m 0755 -d /etc/apt/keyrings
    curl -fsSL https://download.docker.com/linux/debian/gpg \
        | gpg --dearmor -o /etc/apt/keyrings/docker.gpg
    chmod a+r /etc/apt/keyrings/docker.gpg

    echo \
      "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.gpg] \
      https://download.docker.com/linux/debian $(lsb_release -cs) stable" \
      | tee /etc/apt/sources.list.d/docker.list > /dev/null

    apt-get update -y
    apt-get install -y \
        docker-ce docker-ce-cli containerd.io \
        docker-buildx-plugin docker-compose-plugin

    systemctl enable --now docker
    success "Docker installed and started"
fi

# Limit container log size — prevents disk exhaustion on busy servers
mkdir -p /etc/docker
cat > /etc/docker/daemon.json << 'EOF'
{
  "log-driver": "json-file",
  "log-opts": { "max-size": "10m", "max-file": "3" }
}
EOF
systemctl reload docker 2>/dev/null || systemctl restart docker
success "Docker log rotation configured (10 MB × 3 files)"

# ==============================================================================
#  STEP 3 — GITHUB CLI
# ==============================================================================
section "Step 3/11 — GitHub CLI"

if ! command -v gh &>/dev/null; then
    curl -fsSL https://cli.github.com/packages/githubcli-archive-keyring.gpg \
        | dd of=/usr/share/keyrings/githubcli-archive-keyring.gpg 2>/dev/null
    chmod go+r /usr/share/keyrings/githubcli-archive-keyring.gpg

    echo \
      "deb [arch=$(dpkg --print-architecture) signed-by=/usr/share/keyrings/githubcli-archive-keyring.gpg] \
      https://cli.github.com/packages stable main" \
      | tee /etc/apt/sources.list.d/github-cli.list > /dev/null

    apt-get update -y
    apt-get install -y gh
    success "GitHub CLI installed"
else
    warn "GitHub CLI already installed — skipping."
fi

echo "$GH_PAT" | gh auth login --with-token
success "Authenticated with GitHub"

# ==============================================================================
#  STEP 4 — FIREWALL (UFW)
# ==============================================================================
section "Step 4/11 — Firewall (UFW)"

ufw --force reset
ufw default deny incoming
ufw default allow outgoing
ufw allow "${SSH_PORT}"/tcp comment "SSH"
ufw allow 80/tcp              comment "HTTP — ACME challenge"
ufw allow 443/tcp             comment "HTTPS"
ufw --force enable

success "UFW enabled — open ports: SSH:${SSH_PORT}, HTTP:80, HTTPS:443"

# ==============================================================================
#  STEP 5 — BLOCK ICMP PINGS (iptables-persistent)
# ==============================================================================
section "Step 5/11 — Block ICMP pings"

# Drop inbound echo-requests (ping) for both IPv4 and IPv6.
# Rules are appended only if not already present.
iptables  -C INPUT -p icmp    --icmp-type   echo-request -j DROP 2>/dev/null \
    || iptables  -A INPUT -p icmp    --icmp-type   echo-request -j DROP
ip6tables -C INPUT -p icmpv6 --icmpv6-type echo-request -j DROP 2>/dev/null \
    || ip6tables -A INPUT -p icmpv6 --icmpv6-type echo-request -j DROP

# Persist rules across reboots
netfilter-persistent save
success "ICMP echo-requests blocked (IPv4 + IPv6, persistent)"

# ==============================================================================
#  STEP 6 — SECURITY HARDENING
# ==============================================================================
section "Step 6/11 — Security hardening"

# fail2ban — bans IPs with repeated SSH failures
cat > /etc/fail2ban/jail.d/sshd-custom.conf << EOF
[sshd]
enabled  = true
port     = ${SSH_PORT}
maxretry = 5
bantime  = 3600
findtime = 600
EOF
systemctl enable --now fail2ban
success "fail2ban enabled (SSH port ${SSH_PORT}, 5 retries → 1 h ban)"

# Automatic security updates
dpkg-reconfigure -f noninteractive unattended-upgrades
success "Unattended security updates enabled"

# Secure shared memory (prevent privilege escalation exploits)
if ! grep -q '/run/shm' /etc/fstab; then
    echo "tmpfs /run/shm tmpfs defaults,noexec,nosuid,nodev 0 0" >> /etc/fstab
    mount -o remount /run/shm 2>/dev/null || true
    success "Shared memory secured (noexec, nosuid, nodev)"
fi

# Kernel hardening via sysctl
cat > /etc/sysctl.d/99-hardening.conf << 'EOF'
# Ignore ICMP redirects (prevent routing table manipulation)
net.ipv4.conf.all.accept_redirects     = 0
net.ipv6.conf.all.accept_redirects     = 0
net.ipv4.conf.all.send_redirects       = 0
# Prevent SYN flood
net.ipv4.tcp_syncookies                = 1
# Disable IP source routing
net.ipv4.conf.all.accept_source_route  = 0
net.ipv6.conf.all.accept_source_route  = 0
# Log suspicious martian packets
net.ipv4.conf.all.log_martians         = 1
EOF
sysctl -p /etc/sysctl.d/99-hardening.conf > /dev/null
success "Kernel hardening applied (sysctl)"

# ==============================================================================
#  STEP 7 — SWAP FILE (create if absent — useful for 1-2 GB VPS)
# ==============================================================================
section "Step 7/11 — Swap"

if swapon --show | grep -q .; then
    warn "Swap already exists — skipping."
else
    info "Creating 2 GB swap file at /swapfile..."
    fallocate -l 2G /swapfile
    chmod 600 /swapfile
    mkswap /swapfile
    swapon /swapfile
    echo "/swapfile none swap sw 0 0" >> /etc/fstab
    # Prefer RAM; only use swap under real pressure
    echo "vm.swappiness=10" >> /etc/sysctl.d/99-swap.conf
    sysctl -p /etc/sysctl.d/99-swap.conf > /dev/null
    success "2 GB swap created (swappiness=10)"
fi

# ==============================================================================
#  STEP 8 — APP USER & DEPLOY SSH KEY
# ==============================================================================
section "Step 8/11 — App user & deploy SSH key"

# Dedicated unprivileged user for the runner and app
if ! id "$RUNNER_USER" &>/dev/null; then
    useradd -m -s /bin/bash "$RUNNER_USER"
    success "User '${RUNNER_USER}' created"
else
    warn "User '${RUNNER_USER}' already exists — skipping creation."
fi

usermod -aG docker "$RUNNER_USER"
success "'${RUNNER_USER}' added to docker group"

# SSH deploy key — used by runner for git pull
mkdir -p "/home/${RUNNER_USER}/.ssh"
chmod 700 "/home/${RUNNER_USER}/.ssh"

if [[ ! -f "$DEPLOY_KEY" ]]; then
    ssh-keygen -t ed25519 -C "deploy@$(hostname)" -f "$DEPLOY_KEY" -N ""
    success "Deploy key generated"
fi

# Trust GitHub's host key (no StrictHostKeyChecking prompts)
ssh-keyscan -t ed25519 github.com >> "/home/${RUNNER_USER}/.ssh/known_hosts" 2>/dev/null

# SSH client config for the runner user
cat > "/home/${RUNNER_USER}/.ssh/config" << EOF
Host github.com
    HostName        github.com
    User            git
    IdentityFile    ${DEPLOY_KEY}
    StrictHostKeyChecking yes
EOF

chown -R "${RUNNER_USER}:${RUNNER_USER}" "/home/${RUNNER_USER}/.ssh"
chmod 600 "/home/${RUNNER_USER}/.ssh/config"

# Register deploy key on GitHub automatically via API
info "Registering deploy key on GitHub..."
gh api "repos/${GH_USER}/${REPO_NAME}/keys" \
    --method POST \
    --field title="$(hostname) — $(date +%Y-%m-%d)" \
    --field key="$(cat ${DEPLOY_KEY}.pub)" \
    --field read_only=true 2>/dev/null \
    && success "Deploy key registered on GitHub" \
    || warn "Deploy key may already be registered (safe to ignore)."

# ==============================================================================
#  STEP 9 — CLONE REPOSITORY
# ==============================================================================
section "Step 9/11 — Clone repository"

# Allow git to trust the app directory when run as RUNNER_USER
run_as "$RUNNER_USER" git config --global --add safe.directory "$APP_DIR"

if [[ -d "${APP_DIR}/.git" ]]; then
    warn "Repository already exists at ${APP_DIR} — pulling latest."
    run_as "$RUNNER_USER" git -C "$APP_DIR" pull origin main || true
else
    info "Cloning ${GH_USER}/${REPO_NAME} → ${APP_DIR}"
    run_as "$RUNNER_USER" bash -c \
        "GIT_SSH_COMMAND='ssh -i ${DEPLOY_KEY} -o StrictHostKeyChecking=yes' \
         git clone 'git@github.com:${GH_USER}/${REPO_NAME}.git' '${APP_DIR}'"
    success "Repository cloned"
fi

chown -R "${RUNNER_USER}:${RUNNER_USER}" "$APP_DIR"

# ==============================================================================
#  STEP 10 — WRITE .env FILE
# ==============================================================================
section "Step 10/11 — Application .env"

ENV_FILE="${APP_DIR}/.env"

cat > "$ENV_FILE" << EOF
# HorusAPI environment — generated by install.sh on $(date)
# DO NOT commit this file to version control.

# ── Domain & TLS (Let's Encrypt) ─────────────────────────────
DOMAIN=${DOMAIN}
CERTBOT_EMAIL=${CERTBOT_EMAIL}
CERTBOT_STAGING=${CERTBOT_STAGING}

# ── PostgreSQL ────────────────────────────────────────────────
POSTGRES_DB=${POSTGRES_DB}
POSTGRES_USER=${POSTGRES_USER}
POSTGRES_PASSWORD=${POSTGRES_PASSWORD}

# ── JWT ───────────────────────────────────────────────────────
Jwt__Secret=${JWT_SECRET}
Jwt__Issuer=horus-auth-api
Jwt__Audience=horus-clients
Jwt__ExpiryMinutes=${JWT_EXPIRY}

# ── Socks5 ────────────────────────────────────────────────────
Socks5__Port=${SOCKS5_PORT}
Socks5__Username=${SOCKS5_USER}
Socks5__Password=${SOCKS5_PASS}
EOF

chmod 600 "$ENV_FILE"
chown "${RUNNER_USER}:${RUNNER_USER}" "$ENV_FILE"
success ".env written to ${ENV_FILE}"

# ==============================================================================
#  STEP 11 — GITHUB ACTIONS SELF-HOSTED RUNNER
# ==============================================================================
section "Step 11/11 — GitHub Actions self-hosted runner"

# Get registration token automatically (requires 'repo' scope PAT)
info "Requesting runner registration token..."
RUNNER_TOKEN=$(gh api \
    "repos/${GH_USER}/${REPO_NAME}/actions/runners/registration-token" \
    --method POST --jq .token)
[[ -z "$RUNNER_TOKEN" ]] && \
    error "Could not get runner token. Make sure the PAT has 'repo' scope."
success "Runner token obtained"

# Fetch latest stable runner release
info "Fetching latest runner version..."
RUNNER_VERSION=$(curl -sf \
    https://api.github.com/repos/actions/runner/releases/latest \
    | grep '"tag_name"' | grep -oP '"v\K[^"]+')
[[ -z "$RUNNER_VERSION" ]] && RUNNER_VERSION="2.317.0"
info "Runner version: ${RUNNER_VERSION}"

# Download and extract
RUNNER_ARCHIVE="actions-runner-linux-x64-${RUNNER_VERSION}.tar.gz"
mkdir -p "$RUNNER_DIR"
curl -fsSL \
    "https://github.com/actions/runner/releases/download/v${RUNNER_VERSION}/${RUNNER_ARCHIVE}" \
    -o "/tmp/${RUNNER_ARCHIVE}"
tar xzf "/tmp/${RUNNER_ARCHIVE}" -C "$RUNNER_DIR"
rm -f "/tmp/${RUNNER_ARCHIVE}"
chown -R "${RUNNER_USER}:${RUNNER_USER}" "$RUNNER_DIR"

# Configure (runs as the runner user)
run_as "$RUNNER_USER" bash -c "
    cd '$RUNNER_DIR'
    ./config.sh \
        --url 'https://github.com/${GH_USER}/${REPO_NAME}' \
        --token '${RUNNER_TOKEN}' \
        --name '$(hostname)' \
        --labels 'self-hosted,linux,x64,debian' \
        --work '_work' \
        --unattended \
        --replace
"

# Install and start as a systemd service (must run as root)
"$RUNNER_DIR/svc.sh" install "$RUNNER_USER"
"$RUNNER_DIR/svc.sh" start
success "Runner installed as a systemd service and started"

# ==============================================================================
#  DEPLOY SCRIPT  (called by CI/CD on every push)
# ==============================================================================
section "Deploy script"

cat > "${APP_DIR}/deploy.sh" << DEPLOY_SCRIPT
#!/usr/bin/env bash
# Called by GitHub Actions on every push to main.
set -euo pipefail
cd "${APP_DIR}"
git pull origin main
docker compose up -d --build vpn-api
docker image prune -f
echo "Deployed at \$(date)"
DEPLOY_SCRIPT

chmod +x "${APP_DIR}/deploy.sh"
chown "${RUNNER_USER}:${RUNNER_USER}" "${APP_DIR}/deploy.sh"
success "deploy.sh created at ${APP_DIR}/deploy.sh"

# ==============================================================================
#  CI/CD WORKFLOW  (push to GitHub repo automatically)
# ==============================================================================
section "GitHub Actions workflow"

WORKFLOW_CONTENT="name: Deploy

on:
  push:
    branches: [main]

jobs:
  deploy:
    runs-on: self-hosted
    steps:
      - name: Pull and redeploy
        run: ${APP_DIR}/deploy.sh"

ENCODED=$(printf '%s' "$WORKFLOW_CONTENT" | base64 -w 0)

# Check if the file already exists in the repo
_existing_sha=$(gh api \
    "repos/${GH_USER}/${REPO_NAME}/contents/.github/workflows/deploy.yml" \
    --jq .sha 2>/dev/null || echo "")

if [[ -n "$_existing_sha" ]]; then
    warn "Workflow file already exists in repo — skipping."
else
    gh api "repos/${GH_USER}/${REPO_NAME}/contents/.github/workflows/deploy.yml" \
        --method PUT \
        --field message="ci: add self-hosted runner deploy workflow" \
        --field content="$ENCODED" > /dev/null \
        && success "deploy.yml committed to ${GH_USER}/${REPO_NAME}" \
        || warn "Could not commit workflow file — add it manually (see summary below)."
fi

# ==============================================================================
#  OPTIONAL FIRST START
# ==============================================================================
section "First start"

ask "Start the application stack now? [Y/n]:"
read -r _start; _start="${_start:-Y}"

if [[ "$_start" =~ ^[Yy]$ ]]; then
    info "Building and starting containers..."
    docker compose -f "${APP_DIR}/docker-compose.yml" up -d --build
    success "Stack started"
    echo ""
    info "Tailing nginx logs (Ctrl+C to stop — stack keeps running)..."
    sleep 2
    docker compose -f "${APP_DIR}/docker-compose.yml" logs --tail=40 -f nginx || true
fi

# ==============================================================================
#  SUMMARY
# ==============================================================================
echo ""
echo -e "${BOLD}${GREEN}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
echo -e "${BOLD}${GREEN}  Installation complete!${NC}"
echo -e "${BOLD}${GREEN}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
echo ""
echo -e "  ${BOLD}Domain:${NC}       https://${DOMAIN}"
echo -e "  ${BOLD}App dir:${NC}      ${APP_DIR}"
echo -e "  ${BOLD}Runner dir:${NC}   ${RUNNER_DIR}"
echo -e "  ${BOLD}Runner user:${NC}  ${RUNNER_USER}"
echo ""
echo -e "${BOLD}Saved credentials (keep these safe):${NC}"
echo -e "  PostgreSQL password:  ${POSTGRES_PASSWORD}"
echo -e "  Full .env:            ${ENV_FILE}"
echo ""
echo -e "${BOLD}CI/CD workflow (.github/workflows/deploy.yml):${NC}"
echo "  Every push to main → runner pulls code → docker compose up"
echo ""
echo -e "${BOLD}Next steps:${NC}"
echo "  1. Verify runner is online:"
echo "     https://github.com/${GH_USER}/${REPO_NAME}/settings/actions/runners"
echo ""
echo "  2. Register the first admin user:"
echo "     curl -sX POST https://${DOMAIN}/auth/register \\"
echo "       -H 'Content-Type: application/json' \\"
echo "       -d '{\"username\":\"admin\",\"password\":\"...\",\"email\":\"...\"}'"
echo ""
echo "  3. Grant admin rights:"
echo "     docker compose -f ${APP_DIR}/docker-compose.yml exec postgres \\"
echo "       psql -U ${POSTGRES_USER} -d ${POSTGRES_DB} \\"
echo "       -c \"UPDATE users SET is_admin=TRUE WHERE username='admin';\""
echo ""
if [[ "$CERTBOT_STAGING" == "1" ]]; then
    echo -e "  ${YELLOW}⚠  CERTBOT_STAGING=1 is set — certs are untrusted (for testing).${NC}"
    echo "     Once verified, set CERTBOT_STAGING=0 in ${ENV_FILE} and run:"
    echo "     docker compose -f ${APP_DIR}/docker-compose.yml restart nginx"
    echo ""
fi
echo -e "  ${YELLOW}⚠  Revoke your GitHub PAT now if it was created only for this setup:${NC}"
echo "     https://github.com/settings/tokens"
echo ""
