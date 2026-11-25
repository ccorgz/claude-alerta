// Claude Alerta (Windows): bloco na barra de tarefas, ao lado da área de notificação, com pedidos do
// Claude Code, sessões ativas e uso do plano. Clicando, o painel sobe do bloco. Modo "só o ícone": o ✳ vai
// para a bandeja do sistema. Mesmos arquivos e mesmo visual da versão macOS.
// Compilado na instalação pelo csc.exe do .NET Framework 4 (C# 5, WPF + Windows Forms), que já vem no Windows.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Automation;
using WPath = System.Windows.Shapes.Path;
using WEllipse = System.Windows.Shapes.Ellipse;
using WRect = System.Windows.Shapes.Rectangle;
using IOPath = System.IO.Path;

namespace ClaudeAlertaWin
{
    // ------------------------------------------------------------------ dados

    public class Janela { public double Pct; public DateTime? Renova; public bool Renovou; }

    public class Uso
    {
        public Janela Sessao, Semana;
        public List<KeyValuePair<string, Janela>> Modelos = new List<KeyValuePair<string, Janela>>();
        public DateTime Lido;
    }

    public class Pendencia { public string Id, Titulo, Detalhe, Tipo, Sessao, Link; public double Criado; }

    public class Sessao
    {
        public string Id, Nome, Sub, Link;
        public bool Ocupada, Fundo;
        public double Desde;
    }

    public static class Dados
    {
        static readonly JavaScriptSerializer js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        public static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        public static readonly string Pasta = IOPath.Combine(Home, ".claude-alerta");
        public static readonly string ArqPendencias = IOPath.Combine(Pasta, "pendencias.json");
        public static readonly string ArqUso = IOPath.Combine(Pasta, "uso.json");
        public static readonly string ArqConfig = IOPath.Combine(Pasta, "config.json");
        static readonly string PastaSessoes = IOPath.Combine(Home, ".claude", "sessions");
        static readonly string PastaProjetos = IOPath.Combine(Home, ".claude", "projects");
        static readonly string PastaTitulos = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude-code-sessions");

        public static object Ler(string caminho)
        {
            try { return File.Exists(caminho) ? js.DeserializeObject(File.ReadAllText(caminho, Encoding.UTF8)) : null; }
            catch { return null; }
        }
        public static Dictionary<string, object> Obj(object o) { return o as Dictionary<string, object>; }
        public static string Str(Dictionary<string, object> d, string k)
        {
            object v;
            return d != null && d.TryGetValue(k, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : "";
        }
        public static double? Num(Dictionary<string, object> d, string k)
        {
            object v;
            if (d == null || !d.TryGetValue(k, out v) || v == null) return null;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { return null; }
        }
        public static IEnumerable<object> Lista(object o)
        {
            var e = o as IEnumerable;
            if (e == null || o is string || o is IDictionary) yield break;
            foreach (var x in e) yield return x;
        }
        public static DateTime DeEpoch(double s) { return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(s).ToLocalTime(); }
        public static double Agora() { return (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds; }

        public static List<Pendencia> LerPendencias()
        {
            return Lista(Ler(ArqPendencias)).Select(o => Obj(o)).Where(d => d != null).Select(d => new Pendencia
            {
                Id = Str(d, "id"), Titulo = Str(d, "titulo"), Detalhe = Str(d, "detalhe"), Tipo = Str(d, "tipo"),
                Sessao = Str(d, "sessao"), Link = Str(d, "link"), Criado = Num(d, "criado") ?? 0
            }).OrderByDescending(p => p.Criado).ToList();
        }

        static Janela LerJanela(object o)
        {
            var d = Obj(o);
            var pct = Num(d, "pct");
            if (pct == null) return null;
            var r = Num(d, "renova");
            var j = new Janela { Pct = Math.Max(0, Math.Min(100, pct.Value)) };
            if (r != null) j.Renova = DeEpoch(r.Value);
            if (j.Renova != null && j.Renova.Value < DateTime.Now) { j.Pct = 0; j.Renova = null; j.Renovou = true; }   // janela já renovou
            return j;
        }

        public static Uso LerUso()
        {
            var d = Obj(Ler(ArqUso));
            if (d == null) return null;
            var u = new Uso();
            object v;
            if (d.TryGetValue("sessao", out v)) u.Sessao = LerJanela(v);
            if (d.TryGetValue("semana", out v)) u.Semana = LerJanela(v);
            if (d.TryGetValue("modelos", out v))
                foreach (var m in Lista(v))
                {
                    var j = LerJanela(m);
                    if (j != null) u.Modelos.Add(new KeyValuePair<string, Janela>(Str(Obj(m), "nome"), j));
                }
            u.Lido = DeEpoch(Num(d, "lido") ?? 0);
            return u;
        }

        public static string Config(string chave)
        {
            return Str(Obj(Ler(ArqConfig)), chave);
        }
        public static void GravarConfig(string chave, string valor)
        {
            var d = Obj(Ler(ArqConfig)) ?? new Dictionary<string, object>();
            d[chave] = valor;
            Directory.CreateDirectory(Pasta);
            File.WriteAllText(ArqConfig, js.Serialize(d), new UTF8Encoding(false));
        }

        // Título que o app Desktop mostra (claude-code-sessions\<conta>\<org>\<local_...>.json)
        static Dictionary<string, object> RegistroDesktop(string id)
        {
            if (!id.StartsWith("local_") || !Directory.Exists(PastaTitulos)) return null;
            foreach (var conta in Directory.GetDirectories(PastaTitulos))
                foreach (var org in Directory.GetDirectories(conta))
                {
                    var f = IOPath.Combine(org, id + ".json");
                    if (File.Exists(f)) return Obj(Ler(f));
                }
            return null;
        }

        // Thread de projeto: a conversa traz no começo <project id="chan_..."> <thread ts="cmsg_...">, Project: e Topic:
        static readonly Dictionary<string, string[]> cacheProjetos = new Dictionary<string, string[]>();
        static string[] InfoProjeto(string sessionId)
        {
            string[] c;
            if (cacheProjetos.TryGetValue(sessionId, out c)) return c;
            if (!Directory.Exists(PastaProjetos)) return null;
            foreach (var dir in Directory.GetDirectories(PastaProjetos))
            {
                var f = IOPath.Combine(dir, sessionId + ".jsonl");
                if (!File.Exists(f)) continue;
                List<string> linhas;
                try { linhas = File.ReadLines(f, Encoding.UTF8).Take(80).ToList(); } catch { return null; }
                var abertura = linhas.Where(l => l.Contains("<wake reason=")).ToList();
                Func<string, IEnumerable<string>, string> achar = (padrao, em) =>
                {
                    foreach (var t in em) { var m = Regex.Match(t, padrao); if (m.Success) return m.Groups[1].Value; }
                    return "";
                };
                var proj = achar("<project id=\\\\*\"(chan_[A-Za-z0-9_]{6,120})", abertura);
                if (proj == "") return null;
                var r = new[] {
                    proj,
                    achar("<thread ts=\\\\*\"(cmsg_[A-Za-z0-9_]{6,120})", abertura),
                    achar("Project: \\\\\"([^\"\\\\]{1,120})\\\\\"", linhas),
                    achar("Topic: \\\\\"([^\"\\\\]{1,160})\\\\\"", linhas)
                };
                cacheProjetos[sessionId] = r;
                return r;
            }
            return null;
        }

        // Sessões com comando do Claude em segundo plano: o Bash do Claude Code roda (via Git Bash) como
        // descendente do processo da sessão, carregando ".claude/shell-snapshots/snapshot-".
        public static HashSet<int> PidsComFundo()
        {
            var r = new HashSet<int>();
            try
            {
                var pai = new Dictionary<int, int>();
                using (var q = new ManagementObjectSearcher("SELECT ProcessId, ParentProcessId FROM Win32_Process"))
                    foreach (ManagementObject p in q.Get())
                        pai[Convert.ToInt32(p["ProcessId"])] = Convert.ToInt32(p["ParentProcessId"]);
                using (var q = new ManagementObjectSearcher("SELECT ParentProcessId FROM Win32_Process WHERE CommandLine LIKE '%shell-snapshots%'"))
                    foreach (ManagementObject p in q.Get())
                    {
                        int x = Convert.ToInt32(p["ParentProcessId"]);
                        for (int i = 0; i < 4 && x > 4; i++)        // o bash do Git passa por um lançador
                        {
                            r.Add(x);
                            int y;
                            if (!pai.TryGetValue(x, out y)) break;
                            x = y;
                        }
                    }
            }
            catch { }
            return r;
        }

        public static List<Sessao> LerSessoes(HashSet<int> comFundo)
        {
            var r = new List<Sessao>();
            if (!Directory.Exists(PastaSessoes)) return r;
            foreach (var f in Directory.GetFiles(PastaSessoes, "*.json"))
            {
                var j = Obj(Ler(f));
                var pidN = Num(j, "pid");
                if (pidN == null) continue;
                int pid = (int)pidN.Value;
                try { if (Process.GetProcessById(pid).HasExited) continue; } catch { continue; }
                string cwd = Str(j, "cwd");
                string pasta = IOPath.GetFileName(cwd.TrimEnd('\\', '/'));
                string id = Str(j, "hostSessionId");
                if (id == "") id = Str(j, "sessionId");
                string nomeCli = Str(j, "name");
                bool nomeDoUsuario = Str(j, "nameSource") == "user" && nomeCli != "";
                var reg = RegistroDesktop(id);
                string titulo = Str(reg, "title");
                var rc = reg != null && reg.ContainsKey("remoteControlSpawn") ? Obj(reg["remoteControlSpawn"]) : null;
                bool projeto = rc != null && rc.ContainsKey("projectThreadChild") && Equals(rc["projectThreadChild"], true);
                string nome = pasta;
                if (titulo != "" && !titulo.StartsWith("Local session")) nome = titulo;
                else if (nomeDoUsuario) nome = nomeCli;
                else if (projeto) nome = "Thread de projeto";
                else if (nomeCli != "") nome = nomeCli;
                string sub = pasta;
                string link = id.StartsWith("local_") ? "claude://code/continue?session=" + id : "claude://";
                var info = projeto ? InfoProjeto(Str(j, "sessionId")) : null;
                if (info != null)
                {
                    link = "claude://claude.ai/code/project/" + info[0] + (info[1] != "" ? "?thread=" + info[1] : "");
                    if (info[3] != "" && !nomeDoUsuario) nome = info[3];
                    if (info[2] != "") sub = info[2];
                }
                var ms = Num(j, "statusUpdatedAt") ?? Num(j, "startedAt") ?? 0;
                r.Add(new Sessao
                {
                    Id = id, Nome = nome, Sub = sub, Link = link, Ocupada = Str(j, "status") == "busy",
                    Fundo = comFundo.Contains(pid), Desde = ms / 1000
                });
            }
            return r.OrderByDescending(s => s.Ocupada).ThenByDescending(s => s.Desde).ToList();
        }
    }

    // ------------------------------------------------------------------ textos

    public static class Fmt
    {
        public static string Tempo(double s)
        {
            int m = (int)((Dados.Agora() - s) / 60);
            if (m < 1) return "agora";
            if (m < 60) return m + " min";
            return (m / 60) + " h " + (m % 60) + " min";
        }
        // "agora" no primeiro minuto; depois "há 3 min"
        public static string HaQuanto(double s) { var t = Tempo(s); return t == "agora" ? t : "há " + t; }
        // renovou e ainda não houve leitura nova: a janela de 5h só começa a contar no próximo uso
        public static string Renova(Janela j)
        {
            if (j == null) return "";
            return j.Renovou ? "renovou · conta no próximo uso" : Renova(j.Renova);
        }
        public static string Renova(DateTime? d)
        {
            if (d == null) return "";
            var s = (d.Value - DateTime.Now).TotalSeconds;
            if (s <= 0) return "renovando";
            if (s < 24 * 3600) { int m = (int)(s / 60); return "renova em " + (m / 60) + "h " + (m % 60) + "min"; }
            return "renova " + d.Value.ToString("ddd HH:mm", new CultureInfo("pt-BR")).Replace(".", "");
        }
        public static string RotuloTipo(string t)
        {
            switch (t) { case "chave": return "chave"; case "decisao": return "decisão"; case "aprovacao": return "aprovação"; case "pronto": return "pronto"; default: return "pedido"; }
        }
        static string nomeUsuario;
        public static string Saudacao()
        {
            int h = DateTime.Now.Hour;
            string s = h < 12 ? "Bom dia" : (h < 18 ? "Boa tarde" : "Boa noite");
            if (nomeUsuario == null) nomeUsuario = PrimeiroNome();
            return nomeUsuario == "" ? s : s + ", " + nomeUsuario;
        }
        static string PrimeiroNome()
        {
            string nome = "";
            try
            {
                using (var ctx = new System.DirectoryServices.AccountManagement.PrincipalContext(System.DirectoryServices.AccountManagement.ContextType.Machine))
                {
                    var u = System.DirectoryServices.AccountManagement.UserPrincipal.FindByIdentity(ctx, Environment.UserName);
                    if (u != null && !string.IsNullOrEmpty(u.DisplayName)) nome = u.DisplayName.Split(' ')[0];
                }
            }
            catch { }
            if (nome == "") nome = Environment.UserName ?? "";
            return nome;
        }
    }

    // ------------------------------------------------------------------ visual

    public static class C
    {
        public static SolidColorBrush B(uint argb)
        {
            var b = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
            b.Freeze();
            return b;
        }
        public static readonly SolidColorBrush Fundo = B(0xFF141413), Cartao = B(0xFF1F1E1B), Cartao2 = B(0xFF262521), Linha = B(0xFF2F2D28);
        public static readonly SolidColorBrush Tx = B(0xFFECE8E1), Tx2 = B(0xFFA39E93), Tx3 = B(0xFF6F6B62);
        public static readonly SolidColorBrush Argila = B(0xFFD97757), Argila2 = B(0xFFE8A184), Ok = B(0xFF3DDC84), OkFraco = B(0x8C3DDC84);
        public static readonly SolidColorBrush Trilho = B(0xFF33312C), Perigo = B(0xFFE5534B), Parada = B(0xFF5C584F);
        public static readonly SolidColorBrush ArgilaFundo = B(0x2ED97757), ArgilaBorda = B(0x73D97757), Transparente = B(0x01000000);
        public static readonly FontFamily Fonte = new FontFamily("Segoe UI Variable Text, Segoe UI");
        public static readonly FontFamily Serifa = new FontFamily("Georgia");
        public static SolidColorBrush Uso(double p) { return p < 60 ? Ok : (p < 85 ? Argila : Perigo); }
    }

    public static class Ui
    {
        public static TextBlock T(string s, double tam, Brush cor, bool forte = false)
        {
            return new TextBlock
            {
                Text = s, FontSize = tam, Foreground = cor, FontFamily = C.Fonte,
                FontWeight = forte ? FontWeights.SemiBold : FontWeights.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center
            };
        }

        public static void Hover(Border b, Brush cor, Brush normal)
        {
            b.Cursor = Cursors.Hand;
            b.MouseEnter += delegate { b.Background = cor; };
            b.MouseLeave += delegate { b.Background = normal; };
        }

        public static FrameworkElement Estrela(double tam)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                double m = tam / 2, r = tam / 2 - 1;
                for (int i = 0; i < 4; i++)
                {
                    double a = i * Math.PI / 4;
                    c.BeginFigure(new Point(m + r * Math.Cos(a), m + r * Math.Sin(a)), false, false);
                    c.LineTo(new Point(m - r * Math.Cos(a), m - r * Math.Sin(a)), true, false);
                }
            }
            g.Freeze();
            return new WPath
            {
                Data = g, Stroke = C.Argila, StrokeThickness = 2.2, StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round, Width = tam, Height = tam, VerticalAlignment = VerticalAlignment.Center
            };
        }

        public static FrameworkElement Anel(double pct, Brush trilho)
        {
            const double tam = 13, esp = 2.4;
            var g = new Grid { Width = tam, Height = tam, VerticalAlignment = VerticalAlignment.Center };
            g.Children.Add(new WEllipse { Stroke = trilho, StrokeThickness = esp });
            double p = Math.Max(0.02, Math.Min(0.999, pct / 100));
            double r = (tam - esp) / 2, cx = tam / 2, cy = tam / 2, a = p * 2 * Math.PI;
            var fig = new PathFigure { StartPoint = new Point(cx, cy - r), IsClosed = false };
            fig.Segments.Add(new ArcSegment(new Point(cx + r * Math.Sin(a), cy - r * Math.Cos(a)), new Size(r, r), 0, p > 0.5, SweepDirection.Clockwise, true));
            var geo = new PathGeometry();
            geo.Figures.Add(fig);
            g.Children.Add(new WPath { Data = geo, Stroke = C.Uso(pct), StrokeThickness = esp, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
            return g;
        }

        public static FrameworkElement Barra(double pct, double altura)
        {
            var g = new Grid { Height = altura, VerticalAlignment = VerticalAlignment.Center };
            g.Children.Add(new Border { Background = C.Trilho, CornerRadius = new CornerRadius(altura / 2) });
            var cheio = new Border { Background = C.Uso(pct), CornerRadius = new CornerRadius(altura / 2), HorizontalAlignment = HorizontalAlignment.Left, MinWidth = altura };
            g.SizeChanged += delegate { cheio.Width = Math.Max(altura, g.ActualWidth * pct / 100); };
            g.Children.Add(cheio);
            return g;
        }

        public static Border Cartao(UIElement filho, Brush borda = null)
        {
            return new Border
            {
                Background = C.Cartao, BorderBrush = borda ?? C.Linha, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10), Child = filho
            };
        }

        public static Border BotaoArgila(string texto, Action acao)
        {
            var b = new Border { Background = C.Argila, CornerRadius = new CornerRadius(7), Padding = new Thickness(13, 6, 13, 7), VerticalAlignment = VerticalAlignment.Center, BorderThickness = new Thickness(1), BorderBrush = C.Transparente };
            b.Child = T(texto, 12.5, Brushes.White, true);
            Hover(b, C.B(0xFFEC8A68), C.Argila);
            b.MouseEnter += delegate { b.BorderBrush = C.B(0x59FFFFFF); };
            b.MouseLeave += delegate { b.BorderBrush = C.Transparente; };
            b.MouseLeftButtonUp += delegate { acao(); };
            return b;
        }

        // Cartão de pedido: o mesmo no aviso e no painel aberto
        public static FrameworkElement CartaoPedido(Pendencia p, Action<string> abrir, Action fechar, string dica = "Esconder até o próximo pedido")
        {
            var linha = new DockPanel { LastChildFill = true };
            if (fechar != null)
            {
                var x = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(6), Background = C.Transparente, Margin = new Thickness(6, 0, 0, 0) };
                x.Child = new TextBlock { Text = "✕", FontSize = 11.5, FontWeight = FontWeights.Bold, Foreground = C.Tx2, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                Hover(x, C.B(0x1AFFFFFF), C.Transparente);
                x.ToolTip = dica;
                x.MouseLeftButtonUp += delegate { fechar(); };
                DockPanel.SetDock(x, Dock.Right);
                linha.Children.Add(x);
            }
            var btn = BotaoArgila("Abrir conversa", () => abrir(p.Link));
            btn.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(btn, Dock.Right);
            linha.Children.Add(btn);
            var textos = new StackPanel();
            textos.Children.Add(T(p.Titulo, 14, Brushes.White, true));
            var meta = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
            meta.Children.Add(new Border
            {
                Background = C.B(0x52D97757), CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 0, 6, 1), Margin = new Thickness(0, 0, 6, 0),
                Child = T(Fmt.RotuloTipo(p.Tipo), 10.5, C.B(0xFFFFC2A8), true)
            });
            meta.Children.Add(T(string.Join(" · ", new[] { p.Sessao, Fmt.HaQuanto(p.Criado) }.Where(s => !string.IsNullOrEmpty(s))), 11.5, C.B(0xFFC9C3B8)));
            textos.Children.Add(meta);
            linha.Children.Add(textos);
            // faixa argila à esquerda, borda argila forte, fundo um pouco mais quente
            var faixa = new Grid();
            faixa.Children.Add(new Border { Background = C.Argila, Width = 4, HorizontalAlignment = HorizontalAlignment.Left });
            faixa.Children.Add(new Border { Padding = new Thickness(18, 13, 12, 13), Child = linha });
            var c = new Border { Background = C.B(0xFF1C1A18), BorderBrush = C.B(0xD9D97757), BorderThickness = new Thickness(1.2), CornerRadius = new CornerRadius(10), Child = faixa, ClipToBounds = true };
            if (!string.IsNullOrEmpty(p.Detalhe)) c.ToolTip = p.Detalhe;
            return c;
        }
    }

    // ------------------------------------------------------------------ Win32

    public static class W
    {
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string nome);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr pai, IntPtr depois, string cls, string nome);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr h, int i, int v);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr depois, int x, int y, int cx, int cy, uint f);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr h, uint f);
        [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFO mi);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
        public const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;

        public static void FerramentaSemAtivar(Window w)
        {
            var h = new WindowInteropHelper(w).Handle;
            SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        }
        public static void PorCima(Window w)
        {
            var h = new WindowInteropHelper(w).Handle;
            if (h != IntPtr.Zero) SetWindowPos(h, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);   // TOPMOST, sem mover/redimensionar/ativar
        }

        // app em tela cheia na frente (vídeo, jogo, apresentação): o bloco some
        public static bool TelaCheia()
        {
            var h = GetForegroundWindow();
            if (h == IntPtr.Zero) return false;
            var cls = new StringBuilder(64);
            GetClassName(h, cls, 64);
            var c = cls.ToString();
            if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd") return false;
            RECT r;
            if (!GetWindowRect(h, out r)) return false;
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            if (!GetMonitorInfo(MonitorFromWindow(h, 2), ref mi)) return false;
            return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top && r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
        }

        public static bool ClaudeNaFrente()
        {
            try
            {
                uint pid;
                GetWindowThreadProcessId(GetForegroundWindow(), out pid);
                return Process.GetProcessById((int)pid).ProcessName.Equals("claude", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // barra de tarefas (px) e onde começa a área de notificação
        public static bool BarraDeTarefas(out RECT barra, out int inicioBandeja)
        {
            inicioBandeja = 0;
            var tb = FindWindow("Shell_TrayWnd", null);
            if (tb == IntPtr.Zero || !GetWindowRect(tb, out barra)) { barra = new RECT(); return false; }
            var nt = FindWindowEx(tb, IntPtr.Zero, "TrayNotifyWnd", null);
            RECT rn;
            if (nt != IntPtr.Zero && GetWindowRect(nt, out rn) && rn.Right > rn.Left && rn.Left > barra.Left && rn.Left < barra.Right)
                inicioBandeja = rn.Left;
            else
                inicioBandeja = barra.Right - 230;                      // sem a área de notificação: estimativa
            return true;
        }

        public static bool BarraClara()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    var v = k == null ? null : k.GetValue("SystemUsesLightTheme");
                    return v is int && (int)v == 1;
                }
            }
            catch { return false; }
        }
    }

    // ------------------------------------------------------------------ bloco da barra de tarefas

    public class Bloco : Window
    {
        readonly Controle ctl;
        public Bloco(Controle c)
        {
            ctl = c;
            WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
            ShowInTaskbar = false; Topmost = true; ShowActivated = false; ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            SourceInitialized += delegate { W.FerramentaSemAtivar(this); };
            MouseLeftButtonUp += delegate { ctl.Alternar(); };
            ContextMenu = ctl.MenuWpf();
        }

        public void Desenhar()
        {
            bool clara = W.BarraClara();
            Brush tx = clara ? C.B(0xFF1A1A1A) : Brushes.White, tx2 = clara ? C.B(0xFF5A5A5A) : C.B(0xFFC9C9C9), trilho = clara ? C.B(0x33000000) : C.B(0x33FFFFFF);
            var p = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            p.Children.Add(Ui.Estrela(14));
            if (ctl.Pendencias.Count > 0)
            {
                var badge = new StackPanel { Orientation = Orientation.Horizontal };
                badge.Children.Add(new WEllipse { Width = 6, Height = 6, Fill = C.Argila, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
                badge.Children.Add(Ui.T(ctl.Pendencias.Count.ToString(), 12.5, C.Argila, true));
                p.Children.Add(new Border { Background = C.B(0x33D97757), CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 0, 6, 1), Margin = new Thickness(6, 0, 0, 0), Child = badge, VerticalAlignment = VerticalAlignment.Center, ToolTip = ctl.Pendencias.Count == 1 ? "1 pedido esperando você" : ctl.Pendencias.Count + " pedidos esperando você" });
            }
            var ativas = Ui.T(ctl.Ativas.Count.ToString(), 12.5, tx, true);
            ativas.Margin = new Thickness(6, 0, 0, 0);
            ativas.ToolTip = ctl.Ativas.Count == 1 ? "1 sessão trabalhando" : ctl.Ativas.Count + " sessões trabalhando";
            p.Children.Add(ativas);
            foreach (var par in new[] { new KeyValuePair<string, Janela>("Sessão de 5 horas", ctl.Uso == null ? null : ctl.Uso.Sessao), new KeyValuePair<string, Janela>("Semana", ctl.Uso == null ? null : ctl.Uso.Semana) })
            {
                var g = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                g.Children.Add(Ui.Anel(par.Value == null ? 0 : par.Value.Pct, trilho));
                var n = Ui.T(par.Value == null ? "–" : Math.Round(par.Value.Pct) + "%", 12.5, par.Value == null ? tx2 : tx, true);
                n.Margin = new Thickness(4, 0, 0, 0);
                g.Children.Add(n);
                g.ToolTip = par.Value == null ? par.Key : par.Key + ": " + Math.Round(par.Value.Pct) + "% · " + Fmt.Renova(par.Value);
                p.Children.Add(g);
            }
            var borda = new Border { Background = C.Transparente, CornerRadius = new CornerRadius(5), Padding = new Thickness(9, 6, 9, 6), Child = p };
            Ui.Hover(borda, clara ? C.B(0x14000000) : C.B(0x14FFFFFF), C.Transparente);
            Content = borda;
        }
    }

    // ------------------------------------------------------------------ painel e aviso

    public class Painel : Window
    {
        readonly Controle ctl;
        readonly Border raiz;
        readonly ScaleTransform escala = new ScaleTransform(1, 1);
        public bool ModoAberto;
        public double AncoraX = 0.5;
        public bool Sobe = true;                            // barra embaixo: sobe; barra em cima: desce
        public const double Largura = 560, LarguraAviso = 440, Margem = 16;

        public Painel(Controle c)
        {
            ctl = c;
            WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
            ShowInTaskbar = false; Topmost = true; ShowActivated = false; ResizeMode = ResizeMode.NoResize;
            Width = Largura + 2 * Margem; SizeToContent = SizeToContent.Height;
            raiz = new Border
            {
                Background = C.Fundo, BorderBrush = C.B(0x14FFFFFF), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
                Margin = new Thickness(Margem), RenderTransform = escala,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Opacity = 0.5, Direction = 270 }
            };
            Content = raiz;
            Deactivated += delegate { if (ModoAberto && IsVisible) ctl.FecharPainel(); };
        }

        public void Desenhar()
        {
            // aviso: o próprio cartão é a janela (sem caixa por fora nem padding extra), e mais estreito
            raiz.Background = ModoAberto ? (Brush)C.Fundo : Brushes.Transparent;
            raiz.BorderThickness = new Thickness(ModoAberto ? 1 : 0);
            Width = (ModoAberto ? Largura : LarguraAviso) + 2 * Margem;
            raiz.Child = ModoAberto ? Completo() : Aviso();
        }

        FrameworkElement Aviso()
        {
            var p = new StackPanel();
            var noAviso = ctl.NoAviso;
            foreach (var x in noAviso.Take(3))
            {
                var pedido = x;
                var c = Ui.CartaoPedido(x, link => ctl.AbrirPedido(pedido), () => ctl.EsconderAviso(pedido.Id));
                c.Margin = new Thickness(0, 0, 0, 8);
                p.Children.Add(c);
            }
            if (noAviso.Count > 3) p.Children.Add(Ui.T("+ " + (noAviso.Count - 3) + " pedidos", 11.5, C.Tx2));
            if (p.Children.Count > 0 && p.Children[p.Children.Count - 1] is FrameworkElement) ((FrameworkElement)p.Children[p.Children.Count - 1]).Margin = new Thickness(0);
            return p;
        }

        FrameworkElement Rotulo(string t, int n)
        {
            var d = new DockPanel { Margin = new Thickness(2, 16, 2, 8) };
            var num = Ui.T(n.ToString(), 12, C.Tx2);
            DockPanel.SetDock(num, Dock.Right);
            d.Children.Add(num);
            d.Children.Add(Ui.T(t, 12, C.Tx2));
            return d;
        }

        FrameworkElement LinhaUso(string t, Janela j)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 13) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
            var k = new StackPanel();
            k.Children.Add(Ui.T(t, 13, C.Tx));
            k.Children.Add(Ui.T(Fmt.Renova(j), 11, C.Tx3));
            g.Children.Add(k);
            var b = Ui.Barra(j == null ? 0 : j.Pct, 7);
            b.Margin = new Thickness(12, 0, 0, 0);
            Grid.SetColumn(b, 1);
            g.Children.Add(b);
            var v = Ui.T(j == null ? "–" : Math.Round(j.Pct) + "%", 13, C.Tx, true);
            v.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(v, 2);
            g.Children.Add(v);
            return g;
        }

        FrameworkElement LinhaSessao(Sessao s)
        {
            bool esp = ctl.Esperando(s);
            var d = new DockPanel { LastChildFill = true };
            var ponto = new WEllipse { Width = 7, Height = 7, Margin = new Thickness(0, 0, 11, 0), VerticalAlignment = VerticalAlignment.Center, Fill = esp ? C.Argila : (s.Ocupada ? C.Ok : (s.Fundo ? C.OkFraco : C.Parada)) };
            DockPanel.SetDock(ponto, Dock.Left);
            d.Children.Add(ponto);
            var estado = Ui.T(esp ? "esperando você" : (s.Ocupada ? "trabalhando" : (s.Fundo ? "em segundo plano" : "parada")), 11.5, C.Tx2);
            estado.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(estado, Dock.Right);
            d.Children.Add(estado);
            var t = new StackPanel();
            t.Children.Add(Ui.T(s.Nome, 13, C.Tx));
            t.Children.Add(Ui.T(s.Sub, 11, C.Tx3));
            d.Children.Add(t);
            var b = new Border { Background = C.Transparente, Padding = new Thickness(14, 10, 14, 10), Child = d };
            Ui.Hover(b, C.Cartao2, C.Transparente);
            b.MouseLeftButtonUp += delegate { ctl.Abrir(s.Link); };
            return b;
        }

        FrameworkElement Completo()
        {
            var p = new StackPanel { Margin = new Thickness(22, 14, 22, 16) };
            var topo = new DockPanel { Margin = new Thickness(2, 0, 2, 14) };
            var at = Ui.T(ctl.Uso == null ? "" : "atualizado " + Fmt.HaQuanto((ctl.Uso.Lido.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds), 11.5, C.Tx3);
            DockPanel.SetDock(at, Dock.Right);
            topo.Children.Add(at);
            var ola = new TextBlock { Text = Fmt.Saudacao(), FontFamily = C.Serifa, FontSize = 21, Foreground = C.Tx };
            topo.Children.Add(ola);
            p.Children.Add(topo);

            var uso = new StackPanel { Margin = new Thickness(16, 14, 16, 1) };
            if (ctl.Uso != null)
            {
                uso.Children.Add(LinhaUso("Sessão", ctl.Uso.Sessao));
                uso.Children.Add(LinhaUso("Semana", ctl.Uso.Semana));
                foreach (var m in ctl.Uso.Modelos) uso.Children.Add(LinhaUso("Semana · " + m.Key, m.Value));
            }
            else
            {
                var t = Ui.T("O uso aparece quando uma sessão do Claude Code rodar", 12.5, C.Tx2);
                t.Margin = new Thickness(0, 0, 0, 13);
                uso.Children.Add(t);
            }
            p.Children.Add(Ui.Cartao(uso));

            if (ctl.Pendencias.Count > 0)
            {
                var cab = (DockPanel)Rotulo("Precisa de você", ctl.Pendencias.Count);
                var todos = new TextBlock { Text = "Fechar todos", FontSize = 12, FontFamily = C.Fonte, Foreground = C.Tx2, Margin = new Thickness(10, 0, 0, 0), Cursor = Cursors.Hand, ToolTip = "Fecha todos os pedidos sem abrir as conversas" };
                todos.MouseEnter += delegate { todos.Foreground = Brushes.White; };
                todos.MouseLeave += delegate { todos.Foreground = C.Tx2; };
                todos.MouseLeftButtonUp += delegate { ctl.DescartarTodas(); };
                DockPanel.SetDock(todos, Dock.Right);
                cab.Children.Insert(0, todos);
                p.Children.Add(cab);
                foreach (var x in ctl.Pendencias.Take(4))
                {
                    var pedido = x;
                    var c = Ui.CartaoPedido(x, link => ctl.AbrirPedido(pedido), () => ctl.Descartar(pedido), "Fechar este pedido sem abrir a conversa");
                    c.Margin = new Thickness(0, 0, 0, 8);
                    p.Children.Add(c);
                }
            }

            p.Children.Add(Rotulo("Sessões ativas", ctl.Ativas.Count));
            var lista = new StackPanel();
            if (ctl.Ativas.Count == 0)
            {
                var t = Ui.T("Nenhuma sessão trabalhando agora", 12.5, C.Tx2);
                t.Margin = new Thickness(14);
                lista.Children.Add(t);
            }
            int i = 0;
            foreach (var s in ctl.Ativas.Take(6))
            {
                if (i++ > 0) lista.Children.Add(new WRect { Height = 1, Fill = C.Linha });
                lista.Children.Add(LinhaSessao(s));
            }
            var cl = Ui.Cartao(lista);
            cl.ClipToBounds = true;
            p.Children.Add(cl);

            var abrir = new TextBlock { Text = "Abrir Claude ↗", FontSize = 11, FontFamily = C.Fonte, Foreground = C.Tx3, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 13, 4, 0), Cursor = Cursors.Hand };
            abrir.MouseEnter += delegate { abrir.Foreground = C.Tx; };
            abrir.MouseLeave += delegate { abrir.Foreground = C.Tx3; };
            abrir.MouseLeftButtonUp += delegate { ctl.Abrir("claude://"); };
            p.Children.Add(abrir);
            return p;
        }

        // sobe (ou desce, com a barra em cima) a partir do bloco clicado
        public void Revelar()
        {
            raiz.RenderTransformOrigin = new Point(AncoraX, Sobe ? 1 : 0);
            var dur = new Duration(TimeSpan.FromMilliseconds(240));
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            escala.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.35, 1, dur) { EasingFunction = ease });
            escala.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.03, 1, dur) { EasingFunction = ease });
            raiz.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = ease });
        }

        public void Recolher(Action depois)
        {
            raiz.RenderTransformOrigin = new Point(AncoraX, Sobe ? 1 : 0);
            var dur = new Duration(TimeSpan.FromMilliseconds(150));
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            escala.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0.35, dur) { EasingFunction = ease });
            escala.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 0.03, dur) { EasingFunction = ease });
            var a = new DoubleAnimation(1, 0, dur) { EasingFunction = ease };
            a.Completed += delegate { depois(); };
            raiz.BeginAnimation(OpacityProperty, a);
        }

        public void SalvarPng(string caminho)
        {
            raiz.Measure(new Size(Largura, double.PositiveInfinity));
            raiz.Arrange(new Rect(0, 0, Largura, raiz.DesiredSize.Height));
            var bmp = new RenderTargetBitmap((int)(Largura * 2), (int)(raiz.DesiredSize.Height * 2), 192, 192, PixelFormats.Pbgra32);
            bmp.Render(raiz);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (var f = File.Create(caminho)) enc.Save(f);
        }
    }

    // ------------------------------------------------------------------ controle

    // Threads e coordenador de projetos rodam na nuvem: os cartões "Allow Claude to…" não passam por hook nem
    // arquivo. O leitor lê a janela do app Claude pela UI Automation do Windows (sem pedir permissão) a cada 3 s
    // e vira pedido todo cartão de permissão visível (texto "Allow Claude…" seguido de um botão "Deny").
    public class LeitorTela
    {
        public List<Pendencia> Achados = new List<Pendencia>();
        public HashSet<string> Presentes = new HashSet<string>();   // ids vistos na última leitura
        Dictionary<string, double> desde = new Dictionary<string, double>();
        Dictionary<string, int> faltas = new Dictionary<string, int>();

        static string Chave(string s)
        {
            ulong h = 1469598103934665603;   // FNV-1a: id igual entre reinícios do app
            foreach (var b in Encoding.UTF8.GetBytes(s)) { h ^= b; h *= 1099511628211; }
            return h.ToString("x");
        }

        // roda fora da thread da interface
        public static List<string[]> Ler()
        {
            var r = new List<string[]>();
            foreach (var pr in Process.GetProcessesByName("Claude"))
            {
                IntPtr h;
                try { h = pr.MainWindowHandle; } catch { continue; }
                if (h == IntPtr.Zero) continue;
                AutomationElement raiz;
                try { raiz = AutomationElement.FromHandle(h); } catch { continue; }
                var textos = new List<string>();
                var walker = TreeWalker.RawViewWalker;
                var pilha = new Stack<KeyValuePair<AutomationElement, int>>();
                pilha.Push(new KeyValuePair<AutomationElement, int>(raiz, 0));
                int n = 0;
                while (pilha.Count > 0 && n < 60000)
                {
                    var par = pilha.Pop(); n++;
                    string t = "";
                    try { t = (par.Key.Current.Name ?? "").Trim(); } catch { }
                    if (t != "" && (textos.Count == 0 || textos[textos.Count - 1] != t)) textos.Add(t);
                    if (par.Value >= 120) continue;
                    var filhos = new List<AutomationElement>();
                    try
                    {
                        var f = walker.GetFirstChild(par.Key);
                        while (f != null) { filhos.Add(f); f = walker.GetNextSibling(f); }
                    }
                    catch { }
                    for (int i = filhos.Count - 1; i >= 0; i--) pilha.Push(new KeyValuePair<AutomationElement, int>(filhos[i], par.Value + 1));   // ordem de leitura
                }
                for (int i = 0; i < textos.Count; i++)
                {
                    var t = textos[i];
                    if (t.Length >= 160 || !(t.StartsWith("Allow Claude") || t.StartsWith("Permitir que o Claude") || t.StartsWith("Permitir ao Claude"))) continue;
                    int k = -1;
                    for (int j = i + 1; j < Math.Min(textos.Count, i + 14); j++)
                        if (textos[j] == "Deny" || textos[j] == "Negar" || textos[j] == "Recusar") { k = j; break; }
                    if (k < 0) continue;
                    // o título às vezes vem partido ("Allow Claude to connect to" + "… in this thread?")
                    string titulo = t; int ini = i + 1;
                    while (!(titulo.EndsWith("?") || titulo.EndsWith(".")) && ini < k && ini < i + 4) { titulo += " " + textos[ini]; ini++; }
                    var det = string.Join(" ", textos.Skip(ini).Take(k - ini).Where(x => x.Length > 3));
                    r.Add(new[] { titulo, det.Length > 220 ? det.Substring(0, 220) : det });
                }
            }
            return r;
        }

        // aplica uma leitura (na thread da interface)
        public void Aplicar(List<string[]> cartoes)
        {
            double agora = Dados.Agora();
            var novos = new List<Pendencia>();
            var vistos = new HashSet<string>();
            foreach (var c in cartoes)
            {
                var id = "tela-" + Chave(c[0] + "|" + c[1]);
                if (!vistos.Add(id)) continue;
                faltas[id] = 0;
                double t0;
                if (!desde.TryGetValue(id, out t0)) { t0 = agora; desde[id] = t0; }
                novos.Add(new Pendencia { Id = id, Titulo = c[0], Detalhe = c[1], Tipo = "aprovacao", Sessao = "Claude · thread na nuvem", Link = "", Criado = t0 });
            }
            // tolera 1 leitura sem o cartão (janela redesenhando) antes de tirar o pedido
            foreach (var p in Achados)
            {
                if (vistos.Contains(p.Id)) continue;
                int f; faltas.TryGetValue(p.Id, out f); f++; faltas[p.Id] = f;
                if (f <= 1) novos.Add(p); else { desde.Remove(p.Id); faltas.Remove(p.Id); }
            }
            Presentes = vistos;
            Achados = novos;
        }
    }

    public class Controle
    {
        public List<Pendencia> Pendencias = new List<Pendencia>();
        public List<Sessao> Sessoes = new List<Sessao>();
        public Uso Uso;
        public bool SoIcone;
        Bloco bloco;
        Painel painel;
        System.Windows.Forms.NotifyIcon bandeja;
        DateTime? mtime;
        HashSet<string> vistos = new HashSet<string>();
        bool primeira = true, aviso, lendoSessoes;
        // ✕ num cartão do aviso esconde só aquele pedido (continua no painel e no contador)
        readonly HashSet<string> escondidos = new HashSet<string>();
        public List<Pendencia> NoAviso { get { return Pendencias.Where(p => !escondidos.Contains(p.Id)).ToList(); } }
        DateTime avisoAte = DateTime.MinValue, ultimoSom = DateTime.MinValue;
        double escalaDpi = 1;
        static System.Drawing.Icon iconeNormal, iconePedido;
        LeitorTela tela = new LeitorTela();
        HashSet<string> fechadasTela = new HashSet<string>();
        bool lendoTela;

        public List<Sessao> Ativas { get { return Sessoes.Where(s => s.Ocupada || s.Fundo || Esperando(s)).ToList(); } }
        public bool Esperando(Sessao s) { return Pendencias.Any(p => (p.Link ?? "").Contains(s.Id)); }

        public void Iniciar()
        {
            SoIcone = Dados.Config("barra") == "icone";
            bloco = new Bloco(this);
            painel = new Painel(this);
            bandeja = new System.Windows.Forms.NotifyIcon { Text = "Claude Alerta", ContextMenuStrip = MenuForms() };
            bandeja.MouseUp += (o, e) => { if (e.Button == System.Windows.Forms.MouseButtons.Left) Alternar(); };
            LerPendencias(); Uso = Dados.LerUso();
            Sessoes = Dados.LerSessoes(new HashSet<int>());
            Redesenhar();

            Repetir(1, delegate { LerPendencias(); Posicionar(); });
            Repetir(3, delegate { LerSessoesFundo(); LerTelaFundo(); });
            // sem hook rodando ninguém relia o uso e o número congelava: o app pede leitura nova se a última tem > 3 min
            Repetir(5, delegate {
                Uso = Dados.LerUso();
                var idade = Uso == null ? double.MaxValue : (DateTime.Now - Uso.Lido).TotalSeconds;
                if (idade > 180 && (DateTime.UtcNow - ultimoPedidoUso).TotalSeconds > 120) { ultimoPedidoUso = DateTime.UtcNow; AtualizarUsoAgora(); }
                Redesenhar();
            });
            Repetir(30, delegate { Redesenhar(); });

            var png = Environment.GetEnvironmentVariable("ALERTA_PNG");
            if (!string.IsNullOrEmpty(png))
            {
                painel.ModoAberto = Environment.GetEnvironmentVariable("ALERTA_PNG_AVISO") == null;
                painel.Desenhar();
                painel.SalvarPng(png);
                Environment.Exit(0);
            }
        }

        void Repetir(double s, EventHandler f)
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(s) };
            t.Tick += f;
            t.Start();
        }

        void LerTelaFundo()
        {
            if (lendoTela) return;
            lendoTela = true;
            Task.Factory.StartNew(() => LeitorTela.Ler()).ContinueWith(t =>
            {
                lendoTela = false;
                tela.Aplicar(t.Exception == null ? t.Result : new List<string[]>());
                LerPendencias();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void LerSessoesFundo()
        {
            if (lendoSessoes) return;
            lendoSessoes = true;
            Task.Factory.StartNew(() => Dados.LerSessoes(Dados.PidsComFundo())).ContinueWith(t =>
            {
                lendoSessoes = false;
                if (t.Exception == null) { Sessoes = t.Result; Redesenhar(); }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        // Pedido só aparece depois de `atraso` segundos (config.json "atraso", padrão 6): a sessão abre o alerta
        // logo antes de escrever a pergunta, e o aviso não pode chegar antes da mensagem. Resolvido antes disso, nem aparece.
        List<Pendencia> todas = new List<Pendencia>();
        Dictionary<string, double> mostradoEm = new Dictionary<string, double>();   // "Terminou" some 10 s depois de aparecer
        static double Atraso()
        {
            double v;
            return double.TryParse(Dados.Config("atraso"), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 6;
        }

        void LerPendencias()
        {
            DateTime? m = File.Exists(Dados.ArqPendencias) ? File.GetLastWriteTimeUtc(Dados.ArqPendencias) : (DateTime?)null;
            if (primeira || m != mtime) { mtime = m; todas = Dados.LerPendencias(); }
            double agora = Dados.Agora(), espera = Atraso();
            // cartão fechado com "Abrir conversa" fica escondido só enquanto continuar na tela; se sair e voltar, avisa de novo
            fechadasTela.RemoveWhere(id => !tela.Presentes.Contains(id) && !tela.Achados.Any(p => p.Id == id));
            // "Terminou: …" é menos importante: some sozinho 10 s depois de aparecer
            foreach (var p in Pendencias.Where(x => x.Tipo == "pronto").ToList())
            {
                double t;
                if (mostradoEm.TryGetValue(p.Id, out t) && agora - t >= 10) { mostradoEm.Remove(p.Id); Resolver(p); }
            }
            var mostrando = new HashSet<string>(Pendencias.Select(p => p.Id));
            // pedido da tela só aparece se ainda estiver na tela agora (aprovado durante a espera, nem aparece)
            var visiveis = todas.Concat(tela.Achados.Where(p => !fechadasTela.Contains(p.Id)))
                .Where(p => agora - p.Criado >= espera && (!p.Id.StartsWith("tela-") || mostrando.Contains(p.Id) || tela.Presentes.Contains(p.Id)))
                .OrderByDescending(p => p.Criado).ToList();
            if (!primeira && visiveis.Select(p => p.Id).SequenceEqual(Pendencias.Select(p => p.Id)))
            {
                // lembrete a cada 3 min enquanto houver pedido e o Claude não estiver na frente
                if (Pendencias.Count > 0 && (DateTime.Now - ultimoSom).TotalSeconds > 180)
                {
                    ultimoSom = DateTime.Now;
                    if (!W.ClaudeNaFrente()) Som();
                }
                AtualizarAviso();
                return;
            }
            Pendencias = visiveis;
            foreach (var p in visiveis) if (!mostradoEm.ContainsKey(p.Id)) mostradoEm[p.Id] = agora;
            escondidos.IntersectWith(Pendencias.Select(p => p.Id).ToList());
            if (!primeira && Pendencias.Any(p => !vistos.Contains(p.Id)))
            {
                avisoAte = DateTime.Now.AddSeconds(8);
                if (!W.ClaudeNaFrente()) Som();
                ultimoSom = DateTime.Now;
            }
            foreach (var p in Pendencias) vistos.Add(p.Id);
            primeira = false;
            Redesenhar();
            AtualizarAviso();
        }

        static void Som()
        {
            try
            {
                var f = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", "Windows Notify Messaging.wav");
                if (File.Exists(f)) new System.Media.SoundPlayer(f).Play();
                else System.Media.SystemSounds.Asterisk.Play();
            }
            catch { }
        }

        // pedido novo sempre sobe o aviso; com o Claude na frente, recolhe sozinho em 8 s; fora do Claude, fica
        void AtualizarAviso()
        {
            bool mostrar = NoAviso.Count > 0 && (!W.ClaudeNaFrente() || DateTime.Now < avisoAte);
            if (mostrar == aviso) return;
            aviso = mostrar;
            if (painel.ModoAberto && painel.IsVisible) { painel.Desenhar(); return; }
            if (mostrar) MostrarPainel(false);
            else if (painel.IsVisible) painel.Recolher(() => painel.Hide());
        }

        public void EsconderAviso(string id) { escondidos.Add(id); Redesenhar(); AtualizarAviso(); }

        public void Redesenhar()
        {
            bloco.Desenhar();
            if (painel.IsVisible) painel.Desenhar();
            AtualizarBandeja();
            Posicionar();
        }

        void AtualizarBandeja()
        {
            W.RECT r; int x;
            bool barraHorizontal = W.BarraDeTarefas(out r, out x) && (r.Right - r.Left) > (r.Bottom - r.Top);
            bandeja.Visible = SoIcone || !barraHorizontal;          // barra vertical: só o ícone da bandeja
            if (!bandeja.Visible) return;
            if (iconeNormal == null) { iconeNormal = IconeBandeja(false); iconePedido = IconeBandeja(true); }
            var novo = Pendencias.Count > 0 ? iconePedido : iconeNormal;
            if (bandeja.Icon != novo) bandeja.Icon = novo;
            string dica = "Claude Alerta";
            if (Uso != null && Uso.Sessao != null) dica += "\n5h " + Math.Round(Uso.Sessao.Pct) + "%";
            if (Uso != null && Uso.Semana != null) dica += " · semana " + Math.Round(Uso.Semana.Pct) + "%";
            if (Pendencias.Count > 0) dica += "\n" + Pendencias.Count + " pedido(s) esperando você";
            bandeja.Text = dica.Length > 63 ? dica.Substring(0, 63) : dica;
        }

        static System.Drawing.Icon IconeBandeja(bool pedido)
        {
            using (var bmp = new System.Drawing.Bitmap(32, 32))
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            using (var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(0xD9, 0x77, 0x57), 3.4f))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                pen.StartCap = pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                for (int i = 0; i < 4; i++)
                {
                    double a = i * Math.PI / 4;
                    float dx = (float)(12 * Math.Cos(a)), dy = (float)(12 * Math.Sin(a));
                    g.DrawLine(pen, 15 + dx, 17 + dy, 15 - dx, 17 - dy);
                }
                if (pedido)
                {
                    using (var b = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(0xD9, 0x77, 0x57)))
                    using (var borda = new System.Drawing.Pen(System.Drawing.Color.FromArgb(0x1C, 0x1C, 0x1C), 2.5f))
                    {
                        g.FillEllipse(b, 21, 1, 10, 10);
                        g.DrawEllipse(borda, 21, 1, 10, 10);
                    }
                }
                return System.Drawing.Icon.FromHandle(bmp.GetHicon());
            }
        }

        // bloco: na barra de tarefas, logo à esquerda da área de notificação
        void Posicionar()
        {
            W.RECT r; int inicio;
            bool ok = W.BarraDeTarefas(out r, out inicio) && (r.Right - r.Left) > (r.Bottom - r.Top);
            if (!ok || SoIcone || W.TelaCheia())
            {
                if (bloco.IsVisible) bloco.Hide();
                return;
            }
            if (!bloco.IsVisible) { bloco.Show(); }
            var src = PresentationSource.FromVisual(bloco);
            if (src != null && src.CompositionTarget != null) escalaDpi = src.CompositionTarget.TransformToDevice.M11;
            double e = escalaDpi;
            bloco.UpdateLayout();
            double w = bloco.ActualWidth, h = bloco.ActualHeight;
            bloco.Left = inicio / e - w - 4;
            bloco.Top = r.Top / e + ((r.Bottom - r.Top) / e - h) / 2;
            W.PorCima(bloco);
        }

        void PosicionarPainel()
        {
            W.RECT r; int inicio;
            bool temBarra = W.BarraDeTarefas(out r, out inicio);
            double e = escalaDpi;
            var area = SystemParameters.WorkArea;
            double ax;                                   // de onde o painel sai (centro do bloco ou do cursor na bandeja)
            if (bloco.IsVisible) ax = bloco.Left + bloco.ActualWidth / 2;
            else ax = System.Windows.Forms.Cursor.Position.X / e;
            painel.UpdateLayout();
            double w = painel.Width, h = painel.ActualHeight;
            // painel aberto: sobe do bloco; aviso: no canto inferior direito, acima da barra (onde o Windows mostra notificações)
            double left = painel.ModoAberto ? Math.Max(area.Left + 4 - Painel.Margem, Math.Min(ax - w / 2, area.Right - w + Painel.Margem - 4))
                                            : area.Right - w + Painel.Margem - 12;
            painel.Sobe = !temBarra || r.Top > 0;
            painel.Left = left;
            painel.Top = painel.Sobe ? area.Bottom - h + Painel.Margem - 8 : area.Top - Painel.Margem + 8;
            double larg = painel.ModoAberto ? Painel.Largura : Painel.LarguraAviso;
            painel.AncoraX = painel.ModoAberto ? Math.Max(0, Math.Min(1, (ax - left - Painel.Margem) / larg)) : 1;
        }

        void MostrarPainel(bool aberto)
        {
            painel.ModoAberto = aberto;
            painel.Desenhar();
            if (!painel.IsVisible) { painel.Opacity = 1; painel.Show(); }
            PosicionarPainel();
            painel.Dispatcher.BeginInvoke(new Action(() => { PosicionarPainel(); painel.Revelar(); }), DispatcherPriority.Loaded);
            if (aberto) painel.Activate();
        }

        public void Alternar()
        {
            if (painel.IsVisible && painel.ModoAberto) FecharPainel();
            else MostrarPainel(true);
        }

        public void FecharPainel()
        {
            painel.Recolher(() =>
            {
                painel.ModoAberto = false;
                if (aviso && Pendencias.Count > 0) { painel.Desenhar(); PosicionarPainel(); painel.Revelar(); }
                else painel.Hide();
            });
        }

        // "Abrir conversa" resolve o pedido: some da tela na hora e sai do arquivo pelo próprio `alerta fechar`
        public void AbrirPedido(Pendencia p)
        {
            Resolver(p);
            Abrir(p.Link);
        }

        // tira o pedido da tela na hora e do arquivo pelo próprio `alerta fechar`
        // ✕ do painel e "Fechar todos": fecham sem abrir a conversa
        public void Descartar(Pendencia p) { Resolver(p); }
        public void DescartarTodas() { foreach (var p in Pendencias.ToList()) Resolver(p); }

        void Resolver(Pendencia p)
        {
            Pendencias.RemoveAll(x => x.Id == p.Id);
            todas.RemoveAll(x => x.Id == p.Id);
            vistos.Add(p.Id);
            if (p.Id.StartsWith("tela-")) fechadasTela.Add(p.Id);
            else try
            {
                var exe = IOPath.Combine(Dados.Pasta, "bin", "alerta.exe");
                Process.Start(new ProcessStartInfo(exe, "fechar " + p.Id) { UseShellExecute = false, CreateNoWindow = true });
            }
            catch { }
            Redesenhar();
            AtualizarAviso();
        }

        public void Abrir(string link)
        {
            if (painel.IsVisible && painel.ModoAberto) FecharPainel();
            try { Process.Start(new ProcessStartInfo(string.IsNullOrEmpty(link) ? "claude://" : link) { UseShellExecute = true }); }
            catch { }
        }

        void AlternarIcone()
        {
            SoIcone = !SoIcone;
            Dados.GravarConfig("barra", SoIcone ? "icone" : "completa");
            bloco.ContextMenu = MenuWpf();
            bandeja.ContextMenuStrip = MenuForms();
            Redesenhar();
        }

        DateTime ultimoPedidoUso = DateTime.MinValue;
        void AtualizarUsoAgora()
        {
            try
            {
                var exe = IOPath.Combine(Dados.Pasta, "bin", "alerta.exe");
                Process.Start(new ProcessStartInfo(exe, "uso") { UseShellExecute = false, CreateNoWindow = true });
            }
            catch { }
        }

        public ContextMenu MenuWpf()
        {
            var m = new ContextMenu();
            var a = new MenuItem { Header = "Mostrar só o ícone", IsCheckable = true, IsChecked = SoIcone };
            a.Click += delegate { AlternarIcone(); };
            m.Items.Add(a);
            m.Items.Add(new Separator());
            var b = new MenuItem { Header = "Abrir arquivo de pendências" };
            b.Click += delegate { try { Process.Start(Dados.ArqPendencias); } catch { } };
            m.Items.Add(b);
            var c = new MenuItem { Header = "Atualizar uso agora" };
            c.Click += delegate { AtualizarUsoAgora(); };
            m.Items.Add(c);
            m.Items.Add(new Separator());
            var d = new MenuItem { Header = "Sair do Claude Alerta" };
            d.Click += delegate { Sair(); };
            m.Items.Add(d);
            return m;
        }

        System.Windows.Forms.ContextMenuStrip MenuForms()
        {
            var m = new System.Windows.Forms.ContextMenuStrip();
            var a = new System.Windows.Forms.ToolStripMenuItem("Mostrar só o ícone") { Checked = SoIcone };
            a.Click += delegate { AlternarIcone(); };
            m.Items.Add(a);
            m.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            m.Items.Add("Abrir arquivo de pendências", null, delegate { try { Process.Start(Dados.ArqPendencias); } catch { } });
            m.Items.Add("Atualizar uso agora", null, delegate { AtualizarUsoAgora(); });
            m.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            m.Items.Add("Sair do Claude Alerta", null, delegate { Sair(); });
            return m;
        }

        void Sair()
        {
            bandeja.Visible = false;
            bandeja.Dispose();
            Application.Current.Shutdown();
        }
    }

    public static class Programa
    {
        [STAThread]
        public static void Main()
        {
            bool novo;
            using (var unico = new Mutex(true, "Local\\ClaudeAlertaWin", out novo))
            {
                if (!novo && Environment.GetEnvironmentVariable("ALERTA_PNG") == null) return;   // já está rodando
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var ctl = new Controle();
                app.Startup += delegate { ctl.Iniciar(); };
                app.DispatcherUnhandledException += (o, e) =>
                {
                    try { File.AppendAllText(IOPath.Combine(Dados.Pasta, "app", "erro.log"), DateTime.Now + " " + e.Exception + "\n"); } catch { }
                    e.Handled = true;
                };
                app.Run();
            }
        }
    }
}
