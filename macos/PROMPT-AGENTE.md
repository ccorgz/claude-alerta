# Prompts para agentes (Claude Code)

## 1. Instalar pelo Claude Code

Cole isto numa sessão do Claude Code no Mac:

```text
Instale o Claude Alerta que está em ~/Desktop/ccorgzdev/claude-alerta/macos:
1. Rode `bash instalar.sh` dentro dessa pasta.
2. Confirme que o processo `ClaudeAlerta` está rodando (`pgrep -l ClaudeAlerta`) e que os hooks
   com `claude-alerta` entraram em ~/.claude/settings.json (Notification, UserPromptSubmit,
   PostToolUse e SessionEnd).
3. Acrescente ao final do meu ~/.claude/CLAUDE.md o bloco da seção 2 do PROMPT-AGENTE.md
   (sem duplicar, se já existir).
4. Abra um pedido de teste com
   `~/.claude-alerta/bin/alerta abrir "Teste" --detalhe "Instalação ok" --tipo outro`,
   me avise para eu olhar fora do Claude e feche o pedido logo depois com `alerta fechar <id>`.
Não mexa em mais nada do settings.json.
```

## 2. Regra para as sessões (colar no `~/.claude/CLAUDE.md`)

```markdown
## Claude Alerta (pedidos ao usuário)

**Obrigatório, em qualquer tarefa e projeto:** nunca peço confirmação, decisão, aprovação,
chave, arquivo ou login ao usuário sem criar o alerta. Os hooks já cobrem pedido de permissão,
AskUserQuestion, plano para aprovar e espera por resposta; para qualquer outro pedido (inclusive
pergunta no meio do texto ou cartão de decisão), abro o pedido antes de escrever na conversa:

    ~/.claude-alerta/bin/alerta abrir "título curto" --detalhe "o que ele tem de fazer" \
      --tipo chave|decisao|aprovacao|outro --sessao <nome curto> [--link <link da conversa>]

- O título diz a ação em poucas palavras ("Liberar chave da API", "Escolher entre A e B").
- Em thread de projeto do Claude, passo `--link https://claude.ai/code/project/<projeto>?thread=<thread>`
  para o botão "Abrir" cair na conversa certa. Na aba Code do app, o link é automático.
- O comando imprime um id. Quando o usuário resolver, fecho com
  `~/.claude-alerta/bin/alerta fechar <id>` (ou `alerta limpar-sessao <nome>`).
- Nunca deixo pedido aberto depois de resolvido, e nunca abro pedido para coisa que não me bloqueia.
```

## 3. Resumo para um agente que vai mexer no código

```text
Claude Alerta é um app macOS 14+ (Swift/SwiftUI dentro de um NSPanel, arquivo único
src/ClaudeAlerta.swift). Fechado é um NSStatusItem (autosaveName "ClaudeAlerta") cujo botão leva um
NSHostingView (ConteudoItem, hitTest nil) com o Compacto: ✳, pedidos, ativas, anéis de 5h e semana; o
comprimento acompanha o conteúdo. Clique esquerdo abre/fecha; direito mostra o menu (só o ícone, salvo em
~/.claude-alerta/config.json {"barra": "icone"|"completa"}, pendências, atualizar uso, sair). Painel e
aviso ficam num NSPanel de 580 pt logo abaixo do item (centralizado nele, sem sair da tela) e descem
com scaleEffect ancorado no item (estado `revelado`); ao fechar sobem e só depois a janela some.
Pedido novo sempre mostra o aviso: só o cartão (440 pt, sem caixa por fora), no canto superior direito
da tela (com o Claude na frente, some em 8 s; fora do Claude fica). Hospedeiro tem NSTrackingArea
.activeAlways para hover e cursor funcionarem com o app inativo. Tempo: "agora" no 1º minuto, depois "há X".
Primeiro clique vale: Hospedeiro (NSHostingView) responde acceptsFirstMouse = true.
Nomes das sessões: título do app Desktop em ~/Library/Application Support/Claude/
claude-code-sessions/<conta>/<org>/<local_...>.json. Thread de projeto (remoteControlSpawn.
projectThreadChild) vem com título genérico: nome = Topic, subtítulo = Project, link =
claude://claude.ai/code/project/<chan_...>?thread=<cmsg_...>, lidos da linha <wake reason=...><project
id=...><thread ts=...> no começo de ~/.claude/projects/*/<sessionId>.jsonl (bin/alerta faz o mesmo
para os pedidos). `alerta uso` também grava "modelos" (Current week (Fable) etc.), só para o painel.
Lê três fontes, sem rede e sem credencial:
  - ~/.claude-alerta/pendencias.json (a cada 1 s): pedidos, escritos por bin/alerta;
  - ~/.claude/sessions/*.json (a cada 3 s): sessões do Claude Code; só conta pid vivo;
    status busy/idle; "ativas" = busy, esperando você ou com Bash em segundo plano (filho do pid com
    ".claude/shell-snapshots/snapshot-" no comando, via um `ps` por leitura); hostSessionId (local_...) vira o link claude://code/continue?session=...;
  - ~/.claude-alerta/uso.json (a cada 5 s): uso, escrito por `alerta uso`.
O hook acha o executável por CLAUDE_PID (`ps -o comm=`), pois não recebe CLAUDE_CODE_EXECPATH.
`alerta uso` roda `claude -p /usage --no-session-persistence --output-format json` (não chama o
modelo, custo zero). Dentro de uma sessão do app Desktop usa CLAUDE_CODE_EXECPATH e a autenticação
que o app entrega à sessão; por isso quem dispara é o hook (`alerta hook`), em segundo plano e no
máximo a cada 120 s (marca em ~/.claude-alerta/.uso-tentativa; o filho leva ALERTA_USO_FILHO=1
para não disparar de novo). Prefere os números estruturados de cachedUsageUtilization em
~/.claude.json quando são daquela leitura; senão lê o texto do /usage.
Nunca ler o Keychain: o macOS pede a senha do usuário.
instalar.sh compila com `swiftc -O -swift-version 5` num arquivo novo, reassina com codesign -f -s - e troca com rm + mv (sobrescrever
o binário em uso dá OS_REASON_CODESIGNING), registra o LaunchAgent com.claude-alerta
só na primeira vez (depois usa `launchctl kickstart -k`) e mescla os hooks no settings.json.
Para conferir o visual sem captura de tela: ALERTA_PNG=/caminho.png <binário> grava um PNG do
painel aberto com os dados reais e sai.
Decisões do dono: ilha sempre visível no centro; visual do Claude mais escuro (fundo #141413,
argila #D97757, verde #3DDC84, saudação em serifa, hover em tudo que é clicável), bordas pouco arredondadas (ilha 16, cartões 10); nada de
notificação do macOS; nada de botão "Já resolvi" (quem fecha é a sessão); som e pulso só na
chegada de pedido e a cada 3 min se continuar, quando o Claude não está na frente.
```
