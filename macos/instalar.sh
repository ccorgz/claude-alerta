#!/bin/bash
# Instala o Claude Alerta para o usuário atual (macOS). Pode rodar de novo para atualizar.
set -e
cd "$(dirname "$0")"
BASE="$HOME/.claude-alerta"
PLIST="$HOME/Library/LaunchAgents/com.claude-alerta.plist"
command -v swiftc >/dev/null || { echo "Falta o compilador Swift: rode  xcode-select --install"; exit 1; }
[ "$(sw_vers -productVersion | cut -d. -f1)" -ge 14 ] || { echo "Precisa do macOS 14 (Sonoma) ou mais novo."; exit 1; }
mkdir -p "$BASE/bin" "$BASE/app" "$HOME/Library/LaunchAgents"
[ -f "$BASE/pendencias.json" ] || echo '[]' > "$BASE/pendencias.json"
cp bin/alerta "$BASE/bin/alerta"; chmod +x "$BASE/bin/alerta"
cp src/ClaudeAlerta.swift "$BASE/app/"
echo "Compilando..."
# compila num arquivo novo e troca: sobrescrever o binário em uso faz o macOS matar o app (OS_REASON_CODESIGNING)
swiftc -O -swift-version 5 "$BASE/app/ClaudeAlerta.swift" -o "$BASE/app/ClaudeAlerta.novo"
codesign --force --sign - "$BASE/app/ClaudeAlerta.novo" 2>/dev/null || true
rm -f "$BASE/app/ClaudeAlerta"
mv -f "$BASE/app/ClaudeAlerta.novo" "$BASE/app/ClaudeAlerta"

[ -f "$PLIST" ] || cat > "$PLIST" <<P
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
 <key>Label</key><string>com.claude-alerta</string>
 <key>ProgramArguments</key><array><string>$BASE/app/ClaudeAlerta</string></array>
 <key>RunAtLoad</key><true/>
 <key>KeepAlive</key><dict><key>SuccessfulExit</key><false/></dict>
 <key>LimitLoadToSessionType</key><string>Aqua</string>
 <key>StandardErrorPath</key><string>$BASE/app/erro.log</string>
</dict></plist>
P
# Só registra o item de login na primeira vez (registrar de novo faz o macOS mostrar o aviso de "Background Activity")
if launchctl print "gui/$(id -u)/com.claude-alerta" >/dev/null 2>&1; then
  launchctl kickstart -k "gui/$(id -u)/com.claude-alerta"
else
  launchctl bootstrap "gui/$(id -u)" "$PLIST"
fi

# Hooks globais do Claude Code (~/.claude/settings.json), sem mexer no resto do arquivo
/usr/bin/python3 - "$BASE/bin/alerta" <<'PY'
import json, os, sys, shutil, time
cmd = sys.argv[1] + " hook"
p = os.path.expanduser("~/.claude/settings.json")
os.makedirs(os.path.dirname(p), exist_ok=True)
s = json.load(open(p)) if os.path.exists(p) and os.path.getsize(p) else {}
if os.path.exists(p) and not os.path.exists(p + ".antes-claude-alerta"): shutil.copy(p, p + ".antes-claude-alerta")
h = s.setdefault("hooks", {})
for ev in ["Notification", "UserPromptSubmit", "PreToolUse", "PostToolUse", "SessionEnd"]:
    lst = h.setdefault(ev, [])
    lst[:] = [g for g in lst if not any("claude-alerta" in (x.get("command") or "") for x in g.get("hooks", []))]
    if ev == "PreToolUse":
        lst.append({"matcher": "AskUserQuestion|ExitPlanMode", "hooks": [{"type": "command", "command": cmd, "timeout": 5}]})
        continue
    lst.append({"matcher": "", "hooks": [{"type": "command", "command": cmd, "timeout": 5}]} if ev in ("PostToolUse", "Notification")
               else {"hooks": [{"type": "command", "command": cmd, "timeout": 5}]})
json.dump(s, open(p, "w"), indent=2, ensure_ascii=False)
print("Hooks ligados em", p)
PY
# Primeira leitura do uso (5h/semana). Fora de uma sessão do Claude Code só funciona se o comando
# `claude` estiver no PATH; senão, a barra preenche sozinha na primeira vez que uma sessão rodar.
"$BASE/bin/alerta" uso >/dev/null 2>&1 && echo "Uso lido." || echo "O uso aparece na primeira vez que uma sessão do Claude Code rodar."
echo "Pronto. Comando: $BASE/bin/alerta  (teste:  $BASE/bin/alerta abrir \"Teste\" --detalhe \"Olá\")"
