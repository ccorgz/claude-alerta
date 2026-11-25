// Claude Alerta (Windows): comando `alerta.exe`.
// Mesma lógica do `alerta` do macOS: pedidos (pendencias.json), hooks do Claude Code, uso do plano e links.
// Compilado na instalação pelo csc.exe do .NET Framework 4 (C# 5), que já vem no Windows 10/11.
//
//   alerta abrir "título" [--detalhe "..."] [--tipo chave|decisao|aprovacao|outro] [--sessao nome] [--link url]
//   alerta fechar <id>
//   alerta limpar-sessao <nome>
//   alerta listar
//   alerta uso            lê o uso do plano (5h e semana) e grava em %USERPROFILE%\.claude-alerta\uso.json
//   alerta atividade      soma os tokens de cada dia no histórico local (.claude\projects) e grava atividade.json
//   alerta hook           usado pelos hooks do Claude Code (lê o JSON do evento na entrada padrão)
//   alerta hooks ligar|desligar   liga ou tira os hooks em %USERPROFILE%\.claude\settings.json

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace ClaudeAlertaCli
{
    public static class Json
    {
        static readonly JavaScriptSerializer js = CriarSerializador();
        static JavaScriptSerializer CriarSerializador()
        {
            var s = new JavaScriptSerializer();
            s.MaxJsonLength = int.MaxValue;
            s.RecursionLimit = 512;
            return s;
        }

        public static object Ler(string texto)
        {
            if (string.IsNullOrWhiteSpace(texto)) return null;
            return js.DeserializeObject(texto);
        }

        public static object LerArquivo(string caminho)
        {
            try
            {
                if (!File.Exists(caminho)) return null;
                return Ler(File.ReadAllText(caminho, Encoding.UTF8));
            }
            catch { return null; }
        }

        public static Dictionary<string, object> Obj(object o)
        {
            return o as Dictionary<string, object>;
        }

        public static string Str(Dictionary<string, object> d, string chave)
        {
            object v;
            if (d == null || !d.TryGetValue(chave, out v) || v == null) return "";
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static double Num(Dictionary<string, object> d, string chave, double padrao)
        {
            object v;
            if (d == null || !d.TryGetValue(chave, out v) || v == null) return padrao;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { return padrao; }
        }

        public static List<object> Lista(object o)
        {
            var r = new List<object>();
            var arr = o as IEnumerable;
            if (arr == null || o is string || o is IDictionary) return r;
            foreach (var x in arr) r.Add(x);
            return r;
        }

        // escreve JSON identado (o JavaScriptSerializer só escreve tudo numa linha)
        public static string Escrever(object o)
        {
            var sb = new StringBuilder();
            Valor(sb, o, 0);
            return sb.ToString();
        }

        static void Valor(StringBuilder sb, object o, int nivel)
        {
            if (o == null) { sb.Append("null"); return; }
            if (o is string) { Texto(sb, (string)o); return; }
            if (o is bool) { sb.Append((bool)o ? "true" : "false"); return; }
            if (o is double) { sb.Append(((double)o).ToString("R", CultureInfo.InvariantCulture)); return; }
            if (o is float) { sb.Append(((float)o).ToString("R", CultureInfo.InvariantCulture)); return; }
            if (o is decimal || o is int || o is long || o is short || o is byte || o is uint || o is ulong)
            {
                sb.Append(Convert.ToString(o, CultureInfo.InvariantCulture)); return;
            }
            var d = o as IDictionary;
            if (d != null)
            {
                if (d.Count == 0) { sb.Append("{}"); return; }
                sb.Append("{\n");
                int i = 0;
                foreach (DictionaryEntry e in d)
                {
                    sb.Append(' ', (nivel + 1) * 2);
                    Texto(sb, Convert.ToString(e.Key, CultureInfo.InvariantCulture));
                    sb.Append(": ");
                    Valor(sb, e.Value, nivel + 1);
                    if (++i < d.Count) sb.Append(',');
                    sb.Append('\n');
                }
                sb.Append(' ', nivel * 2).Append('}');
                return;
            }
            var l = o as IEnumerable;
            if (l != null)
            {
                var itens = new List<object>();
                foreach (var x in l) itens.Add(x);
                if (itens.Count == 0) { sb.Append("[]"); return; }
                sb.Append("[\n");
                for (int i = 0; i < itens.Count; i++)
                {
                    sb.Append(' ', (nivel + 1) * 2);
                    Valor(sb, itens[i], nivel + 1);
                    if (i < itens.Count - 1) sb.Append(',');
                    sb.Append('\n');
                }
                sb.Append(' ', nivel * 2).Append(']');
                return;
            }
            Texto(sb, Convert.ToString(o, CultureInfo.InvariantCulture));
        }

        static void Texto(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        public static void GravarAtomico(string caminho, string conteudo)
        {
            var tmp = caminho + ".tmp";
            File.WriteAllText(tmp, conteudo, new UTF8Encoding(false));
            if (File.Exists(caminho)) File.Replace(tmp, caminho, null);
            else File.Move(tmp, caminho);
        }
    }

    public static class Programa
    {
        static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        static readonly string Pasta = System.IO.Path.Combine(Home, ".claude-alerta");
        static readonly string Arq = System.IO.Path.Combine(Pasta, "pendencias.json");
        static readonly string Trava = System.IO.Path.Combine(Pasta, ".lock");
        static readonly string ArqUso = System.IO.Path.Combine(Pasta, "uso.json");
        static readonly string ArqUsoTentativa = System.IO.Path.Combine(Pasta, ".uso-tentativa");
        const int UsoIntervalo = 120;

        public static int Main(string[] args)
        {
            try { Console.OutputEncoding = new UTF8Encoding(false); } catch { }
            if (args.Length == 0) { Ajuda(); return 1; }
            string cmd = args[0];
            try
            {
                if (cmd == "hook")
                {
                    try { Hook(); } catch { }
                    return 0;                       // nunca atrapalhar a sessão
                }
                if (cmd == "uso") return AtualizarUso();
                if (cmd == "atividade") return AtualizarAtividade();
                if (cmd == "hooks") return Hooks(args.Length > 1 ? args[1] : "");
                if (cmd == "abrir") return Abrir(args);
                if (cmd == "fechar")
                {
                    if (args.Length < 2) { Ajuda(); return 1; }
                    string id = args[1];
                    string saida = Mexer(d =>
                    {
                        var r = d.Where(p => Json.Str(Json.Obj(p), "id") != id).ToList();
                        return Tuple.Create(r, r.Count < d.Count ? "fechada" : "id não encontrado");
                    });
                    Console.WriteLine(saida);
                    return 0;
                }
                if (cmd == "limpar-sessao")
                {
                    if (args.Length < 2) { Ajuda(); return 1; }
                    string s = args[1];
                    string saida = Mexer(d =>
                    {
                        var r = d.Where(p => Json.Str(Json.Obj(p), "sessao") != s).ToList();
                        return Tuple.Create(r, (d.Count - r.Count) + " fechada(s)");
                    });
                    Console.WriteLine(saida);
                    return 0;
                }
                if (cmd == "listar")
                {
                    var d = LerPendencias();
                    if (d.Count == 0) Console.WriteLine("nenhuma pendência");
                    foreach (var o in d)
                    {
                        var p = Json.Obj(o);
                        string origem = Json.Str(p, "origem");
                        Console.WriteLine(Json.Str(p, "id") + "  [" + Json.Str(p, "tipo") + "] " + Json.Str(p, "titulo") +
                                          "  (" + Json.Str(p, "sessao") + ", " + (origem == "" ? "manual" : origem) + ")");
                    }
                    return 0;
                }
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("erro: " + e.Message);
                return 1;
            }
            Ajuda();
            return 1;
        }

        static void Ajuda()
        {
            Console.WriteLine("uso: alerta abrir \"título\" [--detalhe \"...\"] [--tipo chave|decisao|aprovacao|outro] [--sessao nome] [--link url]");
            Console.WriteLine("     alerta fechar <id> | limpar-sessao <nome> | listar | uso | hook | hooks ligar|desligar");
        }

        // ---------- pendências (com trava de arquivo) ----------

        static List<object> LerPendencias()
        {
            return Json.Lista(Json.LerArquivo(Arq));
        }

        static string Mexer(Func<List<object>, Tuple<List<object>, string>> f)
        {
            Directory.CreateDirectory(Pasta);
            FileStream trava = null;
            for (int i = 0; i < 100 && trava == null; i++)
            {
                try { trava = new FileStream(Trava, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) { Thread.Sleep(50); }
            }
            try
            {
                var dados = LerPendencias();
                var antes = Json.Escrever(dados);
                var r = f(dados);
                if (r.Item1 != null)
                {
                    var depois = Json.Escrever(r.Item1);
                    if (depois != antes) Json.GravarAtomico(Arq, depois);
                }
                return r.Item2;
            }
            finally { if (trava != null) trava.Dispose(); }
        }

        static string LinkProjeto(string sid, string transcript)
        {
            // Thread de projeto: a conversa traz <project id="chan_..."> e <thread ts="cmsg_..."> logo no começo.
            // O link do projeto com ?thread= abre o projeto com a thread ao lado.
            var caminhos = new List<string>();
            if (!string.IsNullOrEmpty(transcript)) caminhos.Add(transcript);
            if (!string.IsNullOrEmpty(sid))
            {
                var base_ = System.IO.Path.Combine(Home, ".claude", "projects");
                if (Directory.Exists(base_))
                    foreach (var dir in Directory.GetDirectories(base_))
                    {
                        var f = System.IO.Path.Combine(dir, sid + ".jsonl");
                        if (File.Exists(f)) caminhos.Add(f);
                    }
            }
            foreach (var c in caminhos)
            {
                List<string> linhas;
                try { linhas = File.ReadLines(c, Encoding.UTF8).Take(80).ToList(); }
                catch { continue; }
                foreach (var l in linhas)
                {
                    if (!l.Contains("<wake reason=")) continue;
                    var p = Regex.Match(l, "<project id=\\\\*\"(chan_[A-Za-z0-9_]{6,120})");
                    if (p.Success)
                    {
                        var th = Regex.Match(l, "<thread ts=\\\\*\"(cmsg_[A-Za-z0-9_]{6,120})");
                        return "claude://claude.ai/code/project/" + p.Groups[1].Value + (th.Success ? "?thread=" + th.Groups[1].Value : "");
                    }
                }
                return "";
            }
            return "";
        }

        // (nome, onde) da sessão: thread de projeto → (tópico, projeto); senão → (título da barra lateral do Desktop, "")
        static Tuple<string, string> QuemE(string sid, string transcript)
        {
            var caminhos = new List<string>();
            if (!string.IsNullOrEmpty(transcript)) caminhos.Add(transcript);
            if (!string.IsNullOrEmpty(sid))
            {
                var base_ = System.IO.Path.Combine(Home, ".claude", "projects");
                if (Directory.Exists(base_))
                    foreach (var dir in Directory.GetDirectories(base_))
                    {
                        var f = System.IO.Path.Combine(dir, sid + ".jsonl");
                        if (File.Exists(f)) caminhos.Add(f);
                    }
            }
            if (caminhos.Count > 0)
            {
                try
                {
                    var linhas = File.ReadLines(caminhos[0], Encoding.UTF8).Take(80).ToList();
                    if (linhas.Any(l => l.Contains("<wake reason=")))
                    {
                        var txt = string.Join("\n", linhas);
                        var pj = Regex.Match(txt, "Project: \\\\\"([^\"\\\\]{1,120})\\\\\"");
                        var tp = Regex.Match(txt, "Topic: \\\\\"([^\"\\\\]{1,160})\\\\\"");
                        if (pj.Success || tp.Success)
                            return Tuple.Create(tp.Success ? tp.Groups[1].Value : "Thread de projeto", pj.Success ? pj.Groups[1].Value : "");
                    }
                }
                catch { }
            }
            var host = Environment.GetEnvironmentVariable("CLAUDE_CODE_HOST_SESSION_ID") ?? "";
            if (host.StartsWith("local_"))
            {
                var raiz = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude-code-sessions");
                try
                {
                    if (Directory.Exists(raiz))
                        foreach (var f in Directory.GetFiles(raiz, host + ".json", SearchOption.AllDirectories))
                        {
                            var t = Json.Str(Json.Obj(Json.LerArquivo(f)), "title").Trim();
                            if (t != "") return Tuple.Create(t, "");
                        }
                }
                catch { }
            }
            return Tuple.Create("", "");
        }

        // começo da última resposta do Claude na sessão (detalhe do aviso de "terminou")
        static string UltimaResposta(Dictionary<string, object> ev)
        {
            string t = Json.Str(ev, "last_assistant_message");
            string tr = Json.Str(ev, "transcript_path");
            if (t == "" && tr != "" && File.Exists(tr))
            {
                try
                {
                    string tudo;
                    using (var fs = new FileStream(tr, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        long ini = Math.Max(0, fs.Length - 400000);
                        fs.Seek(ini, SeekOrigin.Begin);
                        using (var sr = new StreamReader(fs, Encoding.UTF8)) tudo = sr.ReadToEnd();
                    }
                    var linhas = tudo.Split('\n');
                    for (int i = linhas.Length - 1; i >= 0 && t == ""; i--)
                    {
                        Dictionary<string, object> j = null;
                        try { j = Json.Obj(Json.Ler(linhas[i])); } catch { }
                        if (j == null || Json.Str(j, "type") != "assistant") continue;
                        var msg = Json.Obj(j.ContainsKey("message") ? j["message"] : null);
                        var partes = Json.Lista(msg != null && msg.ContainsKey("content") ? msg["content"] : null)
                            .Select(c => Json.Obj(c)).Where(c => c != null && Json.Str(c, "type") == "text")
                            .Select(c => Json.Str(c, "text")).Where(x => x != "");
                        t = string.Join(" ", partes).Trim();
                    }
                }
                catch { }
            }
            t = Regex.Replace(Regex.Replace(t, "\\s+", " "), "[`*_#>\\[\\]]", "").Trim();
            return t.Length > 160 ? t.Substring(0, 157) + "…" : t;
        }

        static string LinkDaSessao(string sid, string transcript)
        {
            if (string.IsNullOrEmpty(sid)) sid = Environment.GetEnvironmentVariable("CLAUDE_CODE_SESSION_ID") ?? "";
            var lp = LinkProjeto(sid, transcript);
            if (lp != "") return lp;
            var host = Environment.GetEnvironmentVariable("CLAUDE_CODE_HOST_SESSION_ID") ?? "";
            if (host.StartsWith("local_")) return "claude://code/continue?session=" + host;
            return "";
        }

        static Dictionary<string, object> NovoItem(string titulo, string detalhe, string tipo, string sessao, string link,
                                                   string origem, string sessaoId, string transcript)
        {
            var d = new Dictionary<string, object>();
            d["id"] = Guid.NewGuid().ToString("N").Substring(0, 8);
            d["titulo"] = titulo;
            d["detalhe"] = detalhe;
            d["tipo"] = tipo;
            d["sessao"] = sessao;
            d["sessao_id"] = sessaoId;
            d["link"] = string.IsNullOrEmpty(link) ? LinkDaSessao(sessaoId, transcript) : link;
            d["app"] = "";
            d["origem"] = origem;
            d["criado"] = (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            return d;
        }

        static int Abrir(string[] args)
        {
            if (args.Length < 2) { Ajuda(); return 1; }
            string titulo = args[1], detalhe = "", tipo = "outro", sessao = "", link = "";
            for (int i = 2; i < args.Length - 1; i++)
            {
                if (args[i] == "--detalhe") detalhe = args[++i];
                else if (args[i] == "--tipo") tipo = args[++i];
                else if (args[i] == "--sessao") sessao = args[++i];
                else if (args[i] == "--link") link = args[++i];
            }
            if (tipo != "chave" && tipo != "decisao" && tipo != "aprovacao" && tipo != "pronto") tipo = "outro";
            // guarda a sessão que abriu: quando você responder nela, o pedido fecha sozinho
            var it = NovoItem(titulo, detalhe, tipo, sessao, link, "manual", Environment.GetEnvironmentVariable("CLAUDE_CODE_SESSION_ID") ?? "", "");
            Console.WriteLine(Mexer(d => { d.Add(it); return Tuple.Create(d, (string)it["id"]); }));
            return 0;
        }

        // ---------- hooks do Claude Code ----------

        static void Hook()
        {
            try { Console.InputEncoding = new UTF8Encoding(false); } catch { }
            var ev = Json.Obj(Json.Ler(Console.In.ReadToEnd()));
            if (ev == null) return;
            try { TalvezAtualizarUso(); } catch { }
            string nome = Json.Str(ev, "hook_event_name");
            string sid = Json.Str(ev, "session_id");
            string cwd = Json.Str(ev, "cwd");
            string pasta = cwd == "" ? "Claude Code" : System.IO.Path.GetFileName(cwd.TrimEnd('\\', '/'));
            string transcript = Json.Str(ev, "transcript_path");
            Func<object, bool> doHookDaSessao = p => Json.Str(Json.Obj(p), "origem") == "hook" && Json.Str(Json.Obj(p), "sessao_id") == sid;

            if (nome == "Notification")
            {
                string msg = Json.Str(ev, "message");
                string tn = Json.Str(ev, "notification_type");
                string titulo, tipo;
                if (tn == "permission_prompt" || msg.ToLowerInvariant().Contains("permission")) { titulo = "Claude pede sua permissão"; tipo = "aprovacao"; }
                else if (tn == "idle_prompt" || msg.ToLowerInvariant().Contains("waiting")) { titulo = "Claude está esperando sua resposta"; tipo = "outro"; }
                else { titulo = "Claude precisa de você"; tipo = "outro"; }
                var item = NovoItem(titulo, msg, tipo, pasta, "", "hook", sid, transcript);
                Mexer(d =>
                {
                    // "esperando sua resposta" logo depois de terminar: o aviso de "terminou" já diz mais, fica ele
                    if (tipo == "outro" && d.Any(p => doHookDaSessao(p) && Json.Str(Json.Obj(p), "tipo") == "pronto")) return Tuple.Create(d, "");
                    var r = d.Where(p => !doHookDaSessao(p)).ToList(); r.Add(item); return Tuple.Create(r, "");
                });
            }
            else if (nome == "Stop")
            {
                // terminou de responder: avisa com o nome da conversa/thread, um trecho da resposta e o link certo
                if (ev.ContainsKey("stop_hook_active") && ev["stop_hook_active"] is bool && (bool)ev["stop_hook_active"]) return;
                var q = QuemE(sid, transcript);
                var item = NovoItem("Terminou: " + (q.Item1 != "" ? q.Item1 : pasta), UltimaResposta(ev), "pronto",
                                    q.Item2 != "" ? q.Item2 : pasta, "", "hook", sid, transcript);
                Mexer(d => { var r = d.Where(p => !doHookDaSessao(p)).ToList(); r.Add(item); return Tuple.Create(r, ""); });
            }
            else if (nome == "PreToolUse" && (Json.Str(ev, "tool_name") == "AskUserQuestion" || Json.Str(ev, "tool_name") == "ExitPlanMode"))
            {
                var ti = Json.Obj(ev.ContainsKey("tool_input") ? ev["tool_input"] : null);
                string det, titulo, tipo;
                if (Json.Str(ev, "tool_name") == "AskUserQuestion")
                {
                    var qs = Json.Lista(ti != null && ti.ContainsKey("questions") ? ti["questions"] : null);
                    det = qs.Count > 0 ? Json.Str(Json.Obj(qs[0]), "question") : "";
                    titulo = "Claude tem uma pergunta para você"; tipo = "decisao";
                }
                else { det = "Plano pronto esperando sua aprovação"; titulo = "Claude quer aprovar um plano"; tipo = "aprovacao"; }
                var item = NovoItem(titulo, det, tipo, pasta, "", "hook", sid, transcript);
                Mexer(d => { var r = d.Where(p => !doHookDaSessao(p)).ToList(); r.Add(item); return Tuple.Create(r, ""); });
            }
            else if (nome == "UserPromptSubmit" || (nome == "PostToolUse" && (Json.Str(ev, "tool_name") == "AskUserQuestion" || Json.Str(ev, "tool_name") == "ExitPlanMode")))
            {
                // você respondeu nessa sessão: fecham os pedidos dela que eram pergunta (automáticos e chave/decisão/aprovação
                // abertos à mão). Avisos informativos ("outro") ficam até você fechar cada um.
                if (sid != "") Mexer(d => Tuple.Create(d.Where(p => !(Json.Str(Json.Obj(p), "sessao_id") == sid &&
                    (Json.Str(Json.Obj(p), "origem") == "hook" || new[] { "chave", "decisao", "aprovacao" }.Contains(Json.Str(Json.Obj(p), "tipo"))))).ToList(), ""));
            }
            else if (nome == "PostToolUse")
            {
                // aprovou uma permissão e a ferramenta rodou: fecha só o pedido automático
                Mexer(d => Tuple.Create(d.Where(p => !doHookDaSessao(p)).ToList(), ""));
            }
            else if (nome == "SessionEnd")
            {
                Mexer(d => Tuple.Create(d.Where(p => Json.Str(Json.Obj(p), "sessao_id") != sid).ToList(), ""));
            }
        }

        static int Hooks(string acao)
        {
            if (acao != "ligar" && acao != "desligar") { Ajuda(); return 1; }
            string p = System.IO.Path.Combine(Home, ".claude", "settings.json");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p));
            var s = Json.Obj(Json.LerArquivo(p));
            if (s == null)
            {
                if (File.Exists(p) && new FileInfo(p).Length > 0)
                {
                    Console.Error.WriteLine("~/.claude/settings.json não é um JSON válido; nada foi alterado.");
                    return 1;
                }
                s = new Dictionary<string, object>();
            }
            if (File.Exists(p) && !File.Exists(p + ".antes-claude-alerta")) File.Copy(p, p + ".antes-claude-alerta");

            string exe = System.IO.Path.Combine(Pasta, "bin", "alerta.exe").Replace('\\', '/');
            string cmd = "\"" + exe + "\" hook";
            var hooks = Json.Obj(s.ContainsKey("hooks") ? s["hooks"] : null) ?? new Dictionary<string, object>();
            foreach (var ev in new[] { "Notification", "UserPromptSubmit", "PreToolUse", "PostToolUse", "Stop", "SessionEnd" })
            {
                var lst = Json.Lista(hooks.ContainsKey(ev) ? hooks[ev] : null);
                lst = lst.Where(g => !Json.Lista(Json.Obj(g) != null && Json.Obj(g).ContainsKey("hooks") ? Json.Obj(g)["hooks"] : null)
                                         .Any(x => Json.Str(Json.Obj(x), "command").Contains("claude-alerta"))).ToList();
                if (acao == "ligar")
                {
                    var h = new Dictionary<string, object>();
                    h["type"] = "command"; h["command"] = cmd; h["timeout"] = 5;
                    var grupo = new Dictionary<string, object>();
                    if (ev == "PreToolUse") grupo["matcher"] = "AskUserQuestion|ExitPlanMode";
                    else if (ev == "PostToolUse" || ev == "Notification") grupo["matcher"] = "";
                    grupo["hooks"] = new List<object> { h };
                    lst.Add(grupo);
                }
                if (lst.Count > 0) hooks[ev] = lst; else hooks.Remove(ev);
            }
            if (hooks.Count > 0) s["hooks"] = hooks; else s.Remove("hooks");
            Json.GravarAtomico(p, Json.Escrever(s) + "\n");
            Console.WriteLine(acao == "ligar" ? "Hooks ligados em " + p : "Hooks removidos de " + p);
            return 0;
        }

        // ---------- uso do plano (5h e semana) ----------
        // `claude -p /usage` não chama o modelo (custo zero). Dentro de uma sessão do app Desktop usa a
        // autenticação que o app entrega à sessão; por isso quem dispara é o hook, em segundo plano.

        static string ExecutavelClaude()
        {
            var exe = Environment.GetEnvironmentVariable("CLAUDE_CODE_EXECPATH") ?? "";
            if (exe != "" && File.Exists(exe)) return exe;
            int pid;
            if (int.TryParse(Environment.GetEnvironmentVariable("CLAUDE_PID") ?? "", out pid))
            {
                try
                {
                    var f = Process.GetProcessById(pid).MainModule.FileName;
                    if (System.IO.Path.GetFileName(f).Equals("claude.exe", StringComparison.OrdinalIgnoreCase)) return f;
                }
                catch { }
            }
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                try
                {
                    var f = System.IO.Path.Combine(dir.Trim(), "claude.exe");
                    if (dir.Trim() != "" && File.Exists(f)) return f;
                }
                catch { }
            }
            var app = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude-code");
            if (Directory.Exists(app))
            {
                var achados = Directory.GetFiles(app, "claude.exe", SearchOption.AllDirectories);
                if (achados.Length > 0) return achados.OrderByDescending(f => File.GetLastWriteTimeUtc(f)).First();
            }
            return "";
        }

        static void TalvezAtualizarUso()
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ALERTA_USO_FILHO")) || ExecutavelClaude() == "") return;
            if (File.Exists(ArqUsoTentativa) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(ArqUsoTentativa)).TotalSeconds < UsoIntervalo) return;
            Directory.CreateDirectory(Pasta);
            File.WriteAllText(ArqUsoTentativa, "");
            File.SetLastWriteTimeUtc(ArqUsoTentativa, DateTime.UtcNow);
            var psi = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, "uso");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.WorkingDirectory = Pasta;
            psi.EnvironmentVariables["ALERTA_USO_FILHO"] = "1";
            Process.Start(psi);
        }

        static readonly string[] Meses = { "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec" };

        // "Jan 5 at 3:15pm (Europe/Lisbon)" ou "3:15pm (...)": o horário vem no fuso do usuário (o local)
        static object RenovaDoTexto(string txt)
        {
            var m = Regex.Match(txt ?? "", "(?:([A-Za-z]{3})\\w*\\s+(\\d{1,2})\\s+at\\s+)?(\\d{1,2})(?::(\\d{2}))?\\s*(am|pm)");
            if (!m.Success) return null;
            var agora = DateTime.Now;
            int mes = agora.Month, dia = agora.Day;
            if (m.Groups[1].Success)
            {
                int i = Array.IndexOf(Meses, m.Groups[1].Value.ToLowerInvariant());
                if (i >= 0) mes = i + 1;
                dia = int.Parse(m.Groups[2].Value);
            }
            int h = int.Parse(m.Groups[3].Value) % 12 + (m.Groups[5].Value == "pm" ? 12 : 0);
            int mi = m.Groups[4].Success ? int.Parse(m.Groups[4].Value) : 0;
            try
            {
                var d = new DateTime(agora.Year, mes, dia, h, mi, 0, DateTimeKind.Local);
                if (d < agora && m.Groups[1].Success && d.Month < agora.Month) d = d.AddYears(1);
                return (d.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            }
            catch { return null; }
        }

        static Dictionary<string, object> JanelaDoTexto(string rotulo, string txt)
        {
            var m = Regex.Match(txt, Regex.Escape(rotulo) + ":\\s*(\\d+(?:\\.\\d+)?)%\\s*used(?:\\s*·\\s*resets\\s*([^\\n]+))?");
            if (!m.Success) return null;
            var d = new Dictionary<string, object>();
            d["pct"] = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            d["renova"] = RenovaDoTexto(m.Groups[2].Success ? m.Groups[2].Value : "");
            return d;
        }

        static object IsoParaEpoch(string s)
        {
            DateTime d;
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out d))
                return (d - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            return null;
        }

        // Grid de uso por dia: tokens (entrada + saída + cache criado; leitura de cache não conta) e respostas.
        // Lê só o que cresceu em cada transcrição desde a última vez (posição guardada em .atividade-pos.json).
        static long N(Dictionary<string, object> d, string k)
        {
            object v;
            if (d == null || !d.TryGetValue(k, out v) || v == null) return 0;
            try { return Convert.ToInt64(v, CultureInfo.InvariantCulture); } catch { return 0; }
        }

        static int AtualizarAtividade()
        {
            var raiz = System.IO.Path.Combine(Home, ".claude", "projects");
            var arqPos = System.IO.Path.Combine(Pasta, ".atividade-pos.json");
            var estado = Json.Obj(Json.LerArquivo(arqPos));
            if (estado == null || Json.Num(estado, "versao", 0) != 1)
            {
                estado = new Dictionary<string, object>();
                estado["versao"] = 1;
                estado["arquivos"] = new Dictionary<string, object>();
                estado["dias"] = new Dictionary<string, object>();
            }
            var arqs = Json.Obj(estado["arquivos"]);
            var diasJ = Json.Obj(estado["dias"]);
            var dias = new Dictionary<string, long[]>();
            foreach (var kv in diasJ)
            {
                var l = Json.Lista(kv.Value);
                if (l.Count == 2) dias[kv.Key] = new[] { Convert.ToInt64(l[0]), Convert.ToInt64(l[1]) };
            }
            string[] todos = new string[0];
            try { if (Directory.Exists(raiz)) todos = Directory.GetFiles(raiz, "*.jsonl", SearchOption.AllDirectories); } catch { }
            foreach (var f in todos)
            {
                long tam;
                try { tam = new FileInfo(f).Length; } catch { continue; }
                var info = arqs.ContainsKey(f) ? Json.Obj(arqs[f]) : null;
                long pos = info == null ? 0 : N(info, "pos");
                var vistos = new HashSet<string>(info == null ? new List<string>() : Json.Lista(info["ids"]).Select(x => Convert.ToString(x)));
                var ordem = new List<string>(vistos);
                if (tam < pos) { pos = 0; vistos.Clear(); ordem.Clear(); }
                if (tam == pos) continue;
                try
                {
                    using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        fs.Seek(pos, SeekOrigin.Begin);
                        var buf = new byte[4 << 20];
                        var resto = new MemoryStream();
                        int lidos;
                        while ((lidos = fs.Read(buf, 0, buf.Length)) > 0)
                        {
                            int ini = 0;
                            for (int i = 0; i < lidos; i++)
                            {
                                if (buf[i] != (byte)'\n') continue;
                                resto.Write(buf, ini, i - ini + 1);
                                var bytes = resto.ToArray();
                                resto.SetLength(0);
                                ini = i + 1;
                                pos += bytes.Length;
                                var linha = Encoding.UTF8.GetString(bytes);
                                if (linha.IndexOf("\"assistant\"", StringComparison.Ordinal) < 0 || linha.IndexOf("\"usage\"", StringComparison.Ordinal) < 0) continue;
                                Dictionary<string, object> j;
                                try { j = Json.Obj(Json.Ler(linha)); } catch { continue; }
                                if (j == null || Json.Str(j, "type") != "assistant") continue;
                                var m = j.ContainsKey("message") ? Json.Obj(j["message"]) : null;
                                var u = m != null && m.ContainsKey("usage") ? Json.Obj(m["usage"]) : null;
                                if (u == null) continue;
                                var mid = Json.Str(m, "id");
                                if (mid == "") mid = Json.Str(j, "uuid");
                                if (vistos.Contains(mid)) continue;
                                vistos.Add(mid); ordem.Add(mid);
                                DateTime t;
                                if (!DateTime.TryParse(Json.Str(j, "timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t)) continue;
                                var dia = t.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                                long[] d;
                                if (!dias.TryGetValue(dia, out d)) dias[dia] = d = new long[2];
                                d[0] += N(u, "input_tokens") + N(u, "output_tokens") + N(u, "cache_creation_input_tokens");
                                d[1] += 1;
                            }
                            resto.Write(buf, ini, lidos - ini);   // linha ainda sendo escrita: fica para a próxima
                        }
                    }
                }
                catch { continue; }
                var novo = new Dictionary<string, object>();
                novo["pos"] = pos;
                novo["ids"] = ordem.Skip(Math.Max(0, ordem.Count - 50)).ToList();   // o mesmo id se repete em linhas seguidas
                arqs[f] = novo;
            }
            var dj = new Dictionary<string, object>();
            var saida = new Dictionary<string, object>();
            foreach (var kv in dias)
            {
                dj[kv.Key] = new List<object> { kv.Value[0], kv.Value[1] };
                var o = new Dictionary<string, object>();
                o["tokens"] = kv.Value[0]; o["respostas"] = kv.Value[1];
                saida[kv.Key] = o;
            }
            estado["dias"] = dj;
            Directory.CreateDirectory(Pasta);
            Json.GravarAtomico(arqPos, Json.Escrever(estado));
            var fim = new Dictionary<string, object>();
            fim["dias"] = saida;
            fim["lido"] = (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            Json.GravarAtomico(System.IO.Path.Combine(Pasta, "atividade.json"), Json.Escrever(fim));
            return 0;
        }

        static int AtualizarUso()
        {
            var exe = ExecutavelClaude();
            if (exe == "") { Console.WriteLine("Claude Code não encontrado"); return 1; }
            double inicio = (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            var psi = new ProcessStartInfo(exe, "-p /usage --no-session-persistence --output-format json");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.WorkingDirectory = Pasta;
            psi.EnvironmentVariables["ALERTA_USO_FILHO"] = "1";
            Directory.CreateDirectory(Pasta);
            string saida;
            using (var p = Process.Start(psi))
            {
                var t = p.StandardOutput.ReadToEndAsync();
                p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(90000)) { try { p.Kill(); } catch { } Console.WriteLine("Claude Code não respondeu"); return 1; }
                saida = t.Result;
            }
            string txt = Json.Str(Json.Obj(Json.Ler(saida)), "result");
            if (txt == "") { Console.WriteLine("resposta inesperada do Claude Code"); return 1; }
            var sessao = JanelaDoTexto("Current session", txt);
            var semana = JanelaDoTexto("Current week (all models)", txt);
            // números estruturados em ~/.claude.json, quando são desta leitura
            try
            {
                var c = Json.Obj(Json.Obj(Json.LerArquivo(System.IO.Path.Combine(Home, ".claude.json"))) ["cachedUsageUtilization"]);
                if (c != null && Json.Num(c, "fetchedAtMs", 0) / 1000 >= inicio - 5)
                {
                    var u = Json.Obj(c["utilization"]);
                    foreach (var par in new[] { new[] { "five_hour", "sessao" }, new[] { "seven_day", "semana" } })
                    {
                        var j = Json.Obj(u != null && u.ContainsKey(par[0]) ? u[par[0]] : null);
                        if (j != null && j.ContainsKey("utilization") && j["utilization"] != null)
                        {
                            var v = new Dictionary<string, object>();
                            v["pct"] = Json.Num(j, "utilization", 0);
                            v["renova"] = IsoParaEpoch(Json.Str(j, "resets_at"));
                            if (par[1] == "sessao") sessao = v; else semana = v;
                        }
                    }
                }
            }
            catch { }
            var modelos = new List<object>();
            foreach (Match m in Regex.Matches(txt, "Current week \\(([^)]+)\\):\\s*(\\d+(?:\\.\\d+)?)%\\s*used(?:\\s*·\\s*resets\\s*([^\\n]+))?"))
            {
                if (m.Groups[1].Value.Trim().ToLowerInvariant() == "all models") continue;
                var d = new Dictionary<string, object>();
                d["nome"] = m.Groups[1].Value.Trim();
                d["pct"] = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                d["renova"] = RenovaDoTexto(m.Groups[3].Success ? m.Groups[3].Value : "");
                modelos.Add(d);
            }
            if (sessao == null && semana == null) { Console.WriteLine("uso não disponível nesta conta"); return 1; }
            var o = new Dictionary<string, object>();
            o["sessao"] = sessao;
            o["semana"] = semana;
            o["modelos"] = modelos;
            o["lido"] = (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            var texto = Json.Escrever(o);
            Json.GravarAtomico(ArqUso, texto);
            Console.WriteLine(texto);
            return 0;
        }
    }
}
