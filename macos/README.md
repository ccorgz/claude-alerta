# Claude Alerta — macOS

Um item fixo na barra de menus do Mac, no canto superior direito, logo antes dos outros ícones (Wi-Fi, bateria, relógio). Ele mostra, sempre à vista:

- **pedidos do Claude Code**: quando uma sessão precisa de você (permissão, resposta, chave, decisão, aprovação);
- **sessões ativas**: as sessões do Claude Code que estão trabalhando agora ou esperando você;
- **uso do plano**: quanto da sessão de 5 horas e da semana você já gastou, e quando renovam.

Clicando no bloco, o painel completo desce dele:

![Painel aberto](docs/exemplo.png)

Quando chega um pedido e você está em outro app, o cartão desce sozinho:

![Aviso de pedido](docs/aviso.png)

Funciona com o **app Claude Desktop** (aba Code) e com o Claude Code no terminal. Não pede senha, não lê o Keychain e não guarda token nenhum.

## O que ele faz

- **Item nativo da barra de menus** (`NSStatusItem`): o macOS coloca o item logo antes dos outros ícones do canto direito, abre espaço para ele (nunca fica por cima de menu de app nem de outro ícone), usa as cores da barra (clara ou escura) e mostra o item em todos os monitores. Dá para mudar a posição arrastando com ⌘ pressionado, e o macOS lembra dela. Num MacBook com muitos ícones, o macOS esconde atrás do notch os que não cabem, como faz com qualquer item.
- **Fechado**, ocupa só a largura do conteúdo: `✳ 2 ◯ 28% ◯ 55%`, que são as sessões ativas, o anel da sessão de 5h e o anel da semana. Com pedido aberto, aparece `● 1` em argila ao lado do ✳. Passe o mouse em cada parte para ver o que é e quando renova.
- **Só o ícone:** botão direito no bloco, depois *Mostrar só o ícone*. Fica só o ✳, como o ícone nativo do Claude, com um ponto argila quando há pedido. A escolha fica salva em `~/.claude-alerta/config.json` (`{"barra": "icone"}` ou `{"barra": "completa"}`).
- **Aviso de pedido**: todo pedido novo aparece sozinho no canto superior direito da tela, logo abaixo da barra, num cartão com borda e faixa argila (até 3 cartões; o resto vira "+ N pedidos"). Se você está em outro app, o aviso fica até o pedido ser resolvido ou você clicar em **✕**, que esconde até o próximo pedido. Se o Claude está na frente, ele sobe sozinho depois de 8 segundos.
- **Aberta (clique)**, mostra:
  - **Uso**: sessão (5h), semana e os limites semanais por modelo (por exemplo, "Semana · Fable"), com barra, porcentagem e "renova em…". Os limites por modelo aparecem só no painel aberto. A cor fica verde abaixo de 60%, argila até 85% e vermelha acima disso.
  - **Precisa de você**: cada pedido com tipo, projeto e há quanto tempo espera. **Abrir conversa** leva direto à conversa no app do Claude.
  - **Sessões ativas**: as que estão trabalhando, esperando você ou com tarefa em segundo plano (comando ou monitor rodando depois do fim do turno), com nome, pasta e estado. Sessão parada sai da lista, mesmo com o processo ainda aberto. Clicar numa sessão abre a conversa dela. O nome é o título que o app Desktop mostra na barra lateral. Threads de projeto aparecem com o tópico da thread e o nome do projeto embaixo, e o clique abre o projeto com a thread ao lado (`claude://claude.ai/code/project/chan_…?thread=cmsg_…`). O projeto e a thread vêm da mensagem de abertura da própria conversa (`~/.claude/projects/*/<sessão>.jsonl`).
- **O painel desce de cima para baixo, logo abaixo do item**, e sobe de volta para ele ao fechar. O aviso sai do canto superior direito.
- **Hover e cursor de mão** em tudo que é clicável, mesmo com o app inativo (ele nunca fica na frente).
- **Tudo que é clicável reage ao mouse** (fundo claro só em volta do conteúdo, e cursor de mão). Um clique só basta, mesmo com outro app em foco. **Clicar fora** fecha o painel. **Botão direito** na ilha: abrir o arquivo de pedidos, atualizar o uso agora, sair.
- **Chama atenção sem irritar.** Na chegada de um pedido, quando o Claude não está na frente, toca um som e o ponto pulsa. Se o pedido continuar aberto, repete a cada 3 minutos.
- **Automático**: hooks globais do Claude Code abrem os pedidos (permissão, pergunta, plano para aprovar, espera por resposta) e fecham quando você responde ou aprova.

## Requisitos

- **macOS 14 (Sonoma) ou mais novo.**
- **Ferramentas de linha de comando da Apple**: o instalador usa `swiftc` e `/usr/bin/python3`. Se não tiver, rode `xcode-select --install`.
- **Claude Code no próprio Mac**: pelo app Claude Desktop (aba Code), pelo terminal ou por IDE. Sessões na nuvem não alcançam os arquivos locais.
- **Para o uso de 5h/semana**: conta do Claude com plano (Pro, Max, Team ou Enterprise). Com chave de API, Bedrock ou Vertex não existe limite de plano, e essa parte fica vazia.

Não precisa instalar mais nada: sem Homebrew, sem Node, sem pacote Python.

## Instalar

```bash
bash ~/Desktop/ccorgzdev/claude-alerta/macos/instalar.sh
```

O instalador:

1. copia o comando para `~/.claude-alerta/bin/alerta`;
2. compila o app em `~/.claude-alerta/app/ClaudeAlerta`;
3. registra o app para abrir no login (`~/Library/LaunchAgents/com.claude-alerta.plist`). Isso só acontece na primeira vez, para o macOS não repetir o aviso de "Background Activity";
4. liga os hooks no `~/.claude/settings.json` sem mexer no resto do arquivo. Na primeira instalação, guarda uma cópia em `settings.json.antes-claude-alerta`;
5. tenta ler o uso pela primeira vez. Se não conseguir, a barra preenche sozinha na primeira vez que uma sessão do Claude Code rodar.

Para atualizar, basta rodar de novo: ele recompila e reinicia o app. O binário novo é compilado à parte, assinado de novo (`codesign --force --sign -`) e só depois trocado, porque sobrescrever o binário em uso faz o macOS matar o app.

Depois de instalar, para as sessões também abrirem pedidos manuais (chave, decisão…), cole a regra de [PROMPT-AGENTE.md](PROMPT-AGENTE.md) no seu `~/.claude/CLAUDE.md`. Também dá para pedir ao próprio Claude Code com o prompt de instalação que está no mesmo arquivo.

## Testar

```bash
~/.claude-alerta/bin/alerta abrir "Teste" --detalhe "Olá" --tipo decisao
```

A pílula "1 pede você" aparece na ilha. Feche o teste com o id que o comando imprimiu:

```bash
~/.claude-alerta/bin/alerta fechar <id>
```

Para conferir o uso na hora:

```bash
~/.claude-alerta/bin/alerta uso
```

Fora de uma sessão do Claude Code, esse comando só funciona se o `claude` estiver no PATH. Rodando dentro de uma sessão (inclusive do app Desktop), funciona sempre.

## De onde vem cada informação

| Na ilha | Fonte | Atualiza |
|---|---|---|
| Pedidos | `~/.claude-alerta/pendencias.json`, escrito pelo comando `alerta` (hooks e sessões) | a cada 1 s |
| Sessões ativas | `~/.claude/sessions/*.json`, que o próprio Claude Code mantém (só conta processo vivo); título em `~/Library/Application Support/Claude/claude-code-sessions/` | a cada 3 s |
| Uso 5h / semana | `~/.claude-alerta/uso.json`, escrito por `alerta uso` | o app relê a cada 5 s; o arquivo é renovado no máximo a cada 2 min, enquanto houver sessão em uso |

### Como o uso é lido (e por que não pede senha)

O próprio Claude Code tem o comando `/usage`. Rodado como `claude -p /usage --no-session-persistence`, ele **não chama o modelo** (custo zero, nenhum token) e devolve a sessão de 5h e a semana em cerca de 2 segundos.

O detalhe importante é que, no app Desktop, a autenticação vive dentro do app e é entregue às sessões que ele abre. Por isso a leitura é disparada **de dentro das sessões**, pelo hook:

```
sessão do Claude Code (Desktop ou terminal)
   └─ hook PostToolUse / UserPromptSubmit / Notification ...
        └─ alerta hook ── (no máximo a cada 2 min, em segundo plano) ──> alerta uso
                                                                           └─ claude -p /usage
                                                                                └─> ~/.claude-alerta/uso.json ──> ilha
```

- O hook volta em ~50 ms. A leitura roda solta e nunca atrasa a sessão.
- O hook acha o executável do Claude Code pelo processo da própria sessão (`CLAUDE_PID`). Se não achar, procura o `claude` no PATH e, por último, o Claude Code que vem dentro do app Desktop.
- Quando uma janela renova (por exemplo, passou o horário da sessão de 5h) e ninguém usou o Claude depois disso, a ilha mostra 0%, que é o valor real.
- **Nada de Keychain.** Ler o login direto do Keychain faz o macOS pedir a senha do usuário. Por isso esse caminho não é usado.

## Comando `alerta`

| Comando | O que faz |
|---|---|
| `alerta abrir "título" [--detalhe "..."] [--tipo chave\|decisao\|aprovacao\|outro] [--sessao nome] [--link url]` | Abre um pedido e imprime o id |
| `alerta fechar <id>` | Fecha um pedido |
| `alerta limpar-sessao <nome>` | Fecha todos os pedidos de uma sessão |
| `alerta listar` | Lista os pedidos abertos |
| `alerta uso` | Lê o uso do plano e grava em `~/.claude-alerta/uso.json` |
| `alerta hook` | Usado pelos hooks: lê o evento do Claude Code na entrada padrão |

Os pedidos abertos dentro de uma sessão da aba Code do app ganham sozinhos o link `claude://code/continue?session=local_…`, que leva direto àquela conversa. Em thread de projeto, o link abre o projeto com a thread ao lado.

## Hooks instalados

| Evento do Claude Code | Efeito |
|---|---|
| `Notification` (pede permissão ou espera resposta) | abre um pedido automático da sessão (um por sessão) |
| `PreToolUse` de `AskUserQuestion` ou `ExitPlanMode` | abre o pedido na hora |
| `UserPromptSubmit` (você respondeu) | fecha o pedido automático da sessão |
| `PostToolUse` (você aprovou e a ferramenta rodou) | fecha o pedido automático da sessão |
| `SessionEnd` | fecha todos os pedidos da sessão |
| todos acima | disparam a leitura do uso, no máximo a cada 2 min |

Os hooks nunca bloqueiam a sessão: qualquer erro é engolido.

## Estrutura

```
claude-alerta/macos/
├── README.md                este arquivo
├── PROMPT-AGENTE.md         prompt de instalação, regra para as sessões e resumo técnico
├── instalar.sh              instala ou atualiza
├── desinstalar.sh           remove tudo (app, login, hooks, pedidos e uso salvo)
├── bin/alerta               comando (Python 3, sem dependências)
├── src/ClaudeAlerta.swift   app (Swift/SwiftUI, arquivo único, sem dependências)
└── docs/                    prints do app (barra, painel, aviso)
```

Arquivos criados na máquina:

```
~/.claude-alerta/bin/alerta            comando instalado
~/.claude-alerta/app/ClaudeAlerta      app compilado (+ erro.log)
~/.claude-alerta/pendencias.json       pedidos abertos
~/.claude-alerta/uso.json              último uso lido
~/.claude-alerta/config.json           preferências (barra completa ou só o ícone)
~/Library/LaunchAgents/com.claude-alerta.plist
~/.claude/settings.json                hooks (o resto do arquivo não é tocado)
```

## Desinstalar

```bash
bash ~/Desktop/ccorgzdev/claude-alerta/macos/desinstalar.sh
```

Esse script fecha o app, tira ele do login, remove os hooks do `~/.claude/settings.json` e apaga a pasta `~/.claude-alerta`. A regra no `~/.claude/CLAUDE.md`, se você colou, precisa ser tirada à mão.

## Problemas comuns

- **A ilha não aparece:** veja se o app está rodando com `pgrep -l ClaudeAlerta`. Para reiniciar, rode `launchctl kickstart -k gui/$(id -u)/com.claude-alerta`. Se `launchctl print gui/$(id -u)/com.claude-alerta` mostrar `OS_REASON_CODESIGNING`, o binário foi sobrescrito em uso: rode o `instalar.sh` de novo.
- **O uso mostra "–" ou "O uso aparece quando uma sessão do Claude Code rodar":** ainda não houve leitura. Use qualquer sessão do Claude Code por um instante, ou rode `alerta uso` de dentro de uma sessão. Com chave de API (sem plano), não existe uso de plano para mostrar.
- **O uso parece velho:** o painel mostra "atualizado há X". Ele só renova enquanto alguma sessão está em uso, e no máximo a cada 2 min. Para forçar, use o botão direito na ilha e *Atualizar uso agora*, depois rode `alerta uso` numa sessão.
- **"Abrir conversa" abre só o app:** o pedido não tem link. Em threads de projeto, passe `--link https://claude.ai/code/project/<projeto>?thread=<thread>`.
- **Conferir o visual sem olhar a tela:** `ALERTA_PNG=/tmp/ilha.png ~/.claude-alerta/app/ClaudeAlerta` grava um PNG do painel aberto e sai. Com `ALERTA_PNG_AVISO=1`, grava o aviso de pedido; com `ALERTA_PNG_AVISO=1 ALERTA_PNG_ICONE=1`, o modo só ícone.
- **Erros do app:** veja `~/.claude-alerta/app/erro.log`.
