#!/bin/bash
# Remove o Claude Alerta: para o app, tira do login, tira os hooks do ~/.claude/settings.json
# e apaga ~/.claude-alerta (pendências e uso salvo).
launchctl bootout "gui/$(id -u)/com.claude-alerta" 2>/dev/null
rm -f "$HOME/Library/LaunchAgents/com.claude-alerta.plist"
/usr/bin/python3 - <<'PY'
import json, os
p = os.path.expanduser("~/.claude/settings.json")
if os.path.exists(p):
    s = json.load(open(p)); h = s.get("hooks", {})
    for ev in list(h):
        h[ev] = [g for g in h[ev] if not any("claude-alerta" in (x.get("command") or "") for x in g.get("hooks", []))]
        if not h[ev]: del h[ev]
    if not h: s.pop("hooks", None)
    json.dump(s, open(p, "w"), indent=2, ensure_ascii=False)
PY
rm -rf "$HOME/.claude-alerta"
echo "Claude Alerta removido."
