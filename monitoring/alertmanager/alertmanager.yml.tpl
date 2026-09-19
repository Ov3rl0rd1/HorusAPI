# ==============================================================================
#  Template. am-init renders this into the real alertmanager.yml at start-up,
#  substituting __BOT_TOKEN__ / __CHAT_ID__ from .env — Alertmanager itself does
#  not expand environment variables, and the token must not live in git.
#
#  Why Alertmanager at all, when vmalert could in principle just shout: because
#  of grouping, inhibition and repeat_interval. Without them a flapping node
#  sends a Telegram message every evaluation, and a dead server sends one per
#  rule — twelve messages saying the same thing, which trains you to ignore them.
# ==============================================================================

global:
  resolve_timeout: 5m

# ── Routing ───────────────────────────────────────────────────────────────────
# Grouped by server first: one incident on one server is one conversation, even
# when six rules fire at once.
route:
  receiver: telegram
  group_by: [server, alertname]
  group_wait: 30s
  group_interval: 5m
  repeat_interval: 12h
  routes:
    # Critical jumps the queue and nags harder. Everything else can wait half a
    # minute and be reminded twice a day.
    - matchers: ['severity="critical"']
      receiver: telegram
      group_wait: 10s
      group_interval: 2m
      repeat_interval: 2h

# ── Inhibition ────────────────────────────────────────────────────────────────
# A dead server fails every check on that server. Saying so once is informative;
# saying so nine times is noise that hides the next real alert.
inhibit_rules:
  - source_matchers: ['alertname="ServerUnreachable"']
    target_matchers: ['alertname!="ServerUnreachable"']
    equal: [server]

  # Same condition at two thresholds (disk 12% / disk 5%) — send the worse one.
  - source_matchers: ['severity="critical"']
    target_matchers: ['severity="warning"']
    equal: [server, alertname]

  - source_matchers: ['alertname="XrayDown"']
    target_matchers: ['alertname=~"XrayHoldsNoUsers|NoTrafficWhileOnline|Olcrtc.*"']
    equal: [server]

receivers:
  - name: telegram
    telegram_configs:
      - bot_token: __BOT_TOKEN__
        chat_id: __CHAT_ID__
        parse_mode: HTML
        send_resolved: true
        # Deliberately plain: a Telegram alert is read on a phone, usually in a
        # hurry. Server, what broke, what to do — nothing else.
        message: |-
          {{ if eq .Status "firing" }}{{ if eq .CommonLabels.severity "critical" }}🔴{{ else }}🟡{{ end }}{{ else }}✅{{ end }} <b>{{ .CommonLabels.server }}</b> — {{ .CommonLabels.alertname }}
          {{ range .Alerts }}
          {{ .Annotations.summary }}
          <i>{{ .Annotations.description }}</i>
          {{ end }}
