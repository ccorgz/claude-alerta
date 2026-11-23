# Prompts para agentes (Claude Code no Windows)

## 1. Instalar pelo Claude Code

Cole isto numa sessão do Claude Code no Windows:

```text
Instale o Claude Alerta que está na pasta windows deste projeto:
1. Rode `powershell -NoProfile -ExecutionPolicy Bypass -File instalar.ps1` dentro dessa pasta.
   Se o csc.exe acusar erro de compilação, me mostre a mensagem inteira.
2. Confirme que o processo ClaudeAlerta.exe está rodando e que os hooks com `claude-alerta`
   entraram em %USERPROFILE%\.claude\settings.json (Notification, UserPromptSubmit, PreToolUse,
   PostToolUse e SessionEnd).
3. Acrescente ao final do meu %USERPROFILE%\.claude\CLAUDE.md o bloco da seção 2 deste arquivo
   (sem duplicar, se já existir).
4. Abra um pedido de teste com
   `"$HOME/.claude-alerta/bin/alerta.exe" abrir "Teste" --detalhe "Instalação ok" --tipo outro`,
   me avise para eu olhar a barra de tarefas e feche o pedido logo depois com `alerta.exe fechar <id>`.
Não mexa em mais nada do settings.json.
```

## 2. Regra para as sessões (colar no `%USERPROFILE%\.claude\CLAUDE.md`)

```markdown
## Claude Alerta (pedidos ao usuário)

**Obrigatório, em qualquer tarefa e projeto:** nunca peço confirmação, decisão, aprovação,
chave, arquivo ou login ao usuário sem criar o alerta. Os hooks já cobrem pedido de permissão,
AskUserQuestion, plano para aprovar e espera por resposta; para qualquer outro pedido (inclusive
pergunta no meio do texto ou cartão de decisão), abro o pedido antes de escrever na conversa:

    "$HOME/.claude-alerta/bin/alerta.exe" abrir "título curto" --detalhe "o que ele tem de fazer" \
      --tipo chave|decisao|aprovacao|outro --sessao <nome curto> [--link <link da conversa>]

- O título diz a ação em poucas palavras ("Liberar chave da API", "Escolher entre A e B").
- Em thread de projeto do Claude, passo `--link https://claude.ai/code/project/<projeto>?thread=<thread>`
  para o botão "Abrir" cair na conversa certa. Na aba Code do app, o link é automático.
- O comando imprime um id. Quando o usuário resolver, fecho com
  `"$HOME/.claude-alerta/bin/alerta.exe" fechar <id>` (ou `alerta limpar-sessao <nome>`).
- Nunca deixo pedido aberto depois de resolvido, e nunca abro pedido para coisa que não me bloqueia.
```

## 3. Resumo para um agente que vai mexer no código

```text
Claude Alerta para Windows 10/11: src/ClaudeAlerta.cs (app WPF + Windows Forms, C# 5) e
src/alerta.cs (comando), compilados na instalação pelo csc.exe do .NET Framework 4
(%WINDIR%\Microsoft.NET\Framework64\v4.0.30319) com /codepage:65001, porque os fontes são UTF-8
sem BOM. Só C# 5: nada de $"", ?., nameof, => em membros, out var nem tuplas (C#).
Mesmos arquivos e formatos da versão macOS, trocando ~ por %USERPROFILE%:
  - .claude-alerta\pendencias.json, uso.json, config.json ({"barra": "icone"|"completa"});
  - .claude\sessions\*.json (pid vivo; status busy/idle; hostSessionId local_...);
  - %APPDATA%\Claude\claude-code-sessions\<conta>\<org>\<local_...>.json (título; thread de projeto
    em remoteControlSpawn.projectThreadChild; projeto e thread na linha <wake ...> do começo de
    .claude\projects\*\<sessionId>.jsonl, viram claude://claude.ai/code/project/<chan>?thread=<cmsg>).
Bloco: janela WPF transparente, topmost, WS_EX_TOOLWINDOW|WS_EX_NOACTIVATE, posta sobre a barra de
tarefas logo à esquerda do TrayNotifyWnd (filho de Shell_TrayWnd; sem ele, estima 230 px antes da
borda); reafirma o topmost a cada 1 s; some com app em tela cheia; cores pelo SystemUsesLightTheme.
Painel: janela WPF de 560 DIP que sobe do bloco (ScaleTransform ancorado nele), ativa a janela e fecha
no Deactivated. Aviso: só o cartão (440 DIP, sem caixa por fora), no canto inferior direito da área
de trabalho. Textos de tempo: "agora" no primeiro minuto, depois "há X min". Só ícone: NotifyIcon com o ✳ (32 px, ícones criados uma vez).
"Ativas" = busy, esperando você ou com Bash em segundo plano (processo com "shell-snapshots" na linha
de comando, até 4 níveis abaixo do pid da sessão, via WMI Win32_Process, fora da thread da interface).
alerta.exe uso: claude.exe -p /usage --no-session-persistence --output-format json (custo zero);
o hook dispara em segundo plano no máximo a cada 120 s; claude.exe achado por CLAUDE_PID, PATH ou
%APPDATA%\Claude\claude-code. Horários do /usage vêm no fuso local do usuário.
Hooks: comando "C:/Users/.../.claude-alerta/bin/alerta.exe" hook (barras normais, roda no Git Bash e no cmd).
Login: HKCU\Software\Microsoft\Windows\CurrentVersion\Run\ClaudeAlerta.
Decisões do dono (iguais ao macOS): visual do Claude mais escuro (fundo #141413, argila #D97757,
verde #3DDC84, saudação em serifa), bordas pouco arredondadas, hover em tudo que é clicável,
pedido novo sempre aparece sozinho, nada de notificação do sistema.
```
