import AppKit
import SwiftUI

// Claude Alerta: ilha fixa no centro da barra de menus (encaixada no notch, quando há).
// Fechada: pendências, sessões ativas e uso de 5h/semana. Clicando, desce o painel completo.
// Fontes: ~/.claude-alerta/pendencias.json (comando `alerta` e hooks), ~/.claude/sessions/*.json,
// e ~/.claude-alerta/uso.json (gravado por `alerta uso`, disparado pelos hooks).
// NUVEM (não remover): threads/coordenador de projetos do claude.ai rodam na nuvem, sem hooks aqui. Eles gravam
// ~/.claude-alerta/nuvem/<chave>.json pelo `bin/nuvem`; viram pedidos e "Sessões ativas" (pendenciasNuvem/sessoesNuvem).

// MARK: - Dados

struct Pendencia: Codable, Identifiable {
    var id: String
    var titulo: String
    var detalhe: String?
    var tipo: String?
    var sessao: String?
    var criado: Double
    var link: String?
    var app: String?
}

struct Sessao: Identifiable {
    var id: String          // hostSessionId (local_...) ou sessionId
    var nome: String
    var pasta: String
    var ocupada: Bool
    var desde: Double       // segundos
    var link: String = ""   // para onde o clique leva
    var fundo = false       // turno terminou, mas há comando do Claude rodando em segundo plano (tarefa, monitor)
    var aguardando = false  // thread da nuvem marcada como esperando você
}

// Sessões com comando em segundo plano: o Bash do Claude Code roda como filho do processo da sessão,
// carregando o snapshot do shell (".claude/shell-snapshots/snapshot-"). Um `ps` por leitura, sem permissão.
func pidsComFundo() -> Set<Int32> {
    let p = Process()
    p.executableURL = URL(fileURLWithPath: "/bin/ps")
    p.arguments = ["-A", "-o", "ppid=,command="]
    let saida = Pipe(); p.standardOutput = saida; p.standardError = Pipe()
    guard (try? p.run()) != nil else { return [] }
    let d = saida.fileHandleForReading.readDataToEndOfFile(); p.waitUntilExit()
    var r = Set<Int32>()
    for l in String(decoding: d, as: UTF8.self).split(separator: "\n") where l.contains(".claude/shell-snapshots/snapshot-") {
        if let ppid = Int32(l.split(separator: " ", omittingEmptySubsequences: true).first ?? "") { r.insert(ppid) }
    }
    return r
}

// Thread de projeto: a conversa traz no começo o projeto e a thread
// (<project id="chan_..."> <thread ts="cmsg_...">, Project: "..." e Topic: "...").
// Com isso o link abre o projeto com a thread ao lado, e o nome vira o tópico da thread.
struct InfoProjeto { var projeto: String; var thread: String; var nomeProjeto: String; var topico: String }
var cacheProjetos: [String: InfoProjeto] = [:]
func infoProjeto(sessionId: String) -> InfoProjeto? {
    if let c = cacheProjetos[sessionId] { return c }
    let base = home.appendingPathComponent(".claude/projects")
    let fm = FileManager.default
    for pasta in (try? fm.contentsOfDirectory(atPath: base)) ?? [] {
        let f = ((base as NSString).appendingPathComponent(pasta) as NSString).appendingPathComponent(sessionId + ".jsonl")
        guard let h = FileHandle(forReadingAtPath: f) else { continue }
        let d = h.readData(ofLength: 600_000); try? h.close()
        // só as primeiras linhas, e o id só da mensagem de abertura da thread (<wake ...><project id=...>)
        let linhas = String(decoding: d, as: UTF8.self).split(separator: "\n", maxSplits: 80).prefix(80).map(String.init)
        func achar(_ padrao: String, _ em: [String]) -> String? {
            guard let r = try? NSRegularExpression(pattern: padrao) else { return nil }
            for t in em {
                if let m = r.firstMatch(in: t, range: NSRange(t.startIndex..., in: t)), m.numberOfRanges > 1,
                   let g = Range(m.range(at: 1), in: t) { return String(t[g]) }
            }
            return nil
        }
        let abertura = linhas.filter { $0.contains("<wake reason=") }
        guard let proj = achar("<project id=\\\\*\"(chan_[A-Za-z0-9_]{6,120})", abertura) else { return nil }
        let info = InfoProjeto(projeto: proj,
                               thread: achar("<thread ts=\\\\*\"(cmsg_[A-Za-z0-9_]{6,120})", abertura) ?? "",
                               nomeProjeto: achar("Project: \\\\\"([^\"\\\\]{1,120})\\\\\"", linhas) ?? "",
                               topico: achar("Topic: \\\\\"([^\"\\\\]{1,160})\\\\\"", linhas) ?? "")
        cacheProjetos[sessionId] = info
        return info
    }
    return nil
}

struct Janela { var pct: Double; var renova: Date?; var renovou = false }
struct Uso { var sessao: Janela?; var semana: Janela?; var sonnet: Janela?; var lido: Date
    var modelos: [(nome: String, janela: Janela)] = []   // limites semanais por modelo (ex.: Fable), só no painel
}

let home = (ProcessInfo.processInfo.environment["HOME"] ?? NSHomeDirectory()) as NSString
let pastaAlerta = home.appendingPathComponent(".claude-alerta")
let arqPendencias = (pastaAlerta as NSString).appendingPathComponent("pendencias.json")
let arqUso = (pastaAlerta as NSString).appendingPathComponent("uso.json")
let arqConfig = (pastaAlerta as NSString).appendingPathComponent("config.json")

// Preferências do usuário: {"barra": "completa" | "icone"}
func lerConfig() -> [String: Any] {
    guard let d = FileManager.default.contents(atPath: arqConfig),
          let j = try? JSONSerialization.jsonObject(with: d) as? [String: Any] else { return [:] }
    return j
}
func gravarConfig(_ c: [String: Any]) {
    if let d = try? JSONSerialization.data(withJSONObject: c, options: [.prettyPrinted, .sortedKeys]) {
        try? d.write(to: URL(fileURLWithPath: arqConfig))
    }
}
let pastaSessoes = home.appendingPathComponent(".claude/sessions")
let pastaTitulos = home.appendingPathComponent("Library/Application Support/Claude/claude-code-sessions")

// Título que o app Desktop mostra na barra lateral (claude-code-sessions/<conta>/<org>/<local_...>.json)
var cacheTitulos: [String: (String, Bool)] = [:]
func tituloDesktop(_ id: String) -> (titulo: String, projeto: Bool)? {
    guard id.hasPrefix("local_") else { return nil }
    if let c = cacheTitulos[id] { return c }
    let fm = FileManager.default
    for conta in (try? fm.contentsOfDirectory(atPath: pastaTitulos)) ?? [] {
        let pc = (pastaTitulos as NSString).appendingPathComponent(conta)
        for org in (try? fm.contentsOfDirectory(atPath: pc)) ?? [] {
            let f = ((pc as NSString).appendingPathComponent(org) as NSString).appendingPathComponent(id + ".json")
            guard let d = fm.contents(atPath: f), let j = try? JSONSerialization.jsonObject(with: d) as? [String: Any] else { continue }
            let rc = j["remoteControlSpawn"] as? [String: Any]
            let r = ((j["title"] as? String) ?? "", (rc?["projectThreadChild"] as? Bool) ?? false)
            cacheTitulos[id] = r
            return r
        }
    }
    return nil
}

func lerPendencias() -> [Pendencia] {
    guard let d = FileManager.default.contents(atPath: arqPendencias), !d.isEmpty else { return [] }
    return ((try? JSONDecoder().decode([Pendencia].self, from: d)) ?? []).sorted { $0.criado > $1.criado }
}

// MARK: - Tela do Claude (~/.claude-alerta/tela.json, gravado por bin/alerta-tela)
// Threads e coordenador de projetos rodam na nuvem: cartões "Allow Claude to…" não
// passam por hook nem arquivo no Mac. O leitor bin/alerta-tela (ligado pelo hook, com a permissão do próprio app
// Claude) lê a janela e grava tela.json. Aqui isso vira pedido, que some quando sai da tela. Este app não pede
// permissão nenhuma.
final class LeitorTela {
    var achados: [Pendencia] = []
    var presentes = Set<String>()                       // ids vistos na última leitura
    private var desde: [String: Double] = [:]
    private var faltas: [String: Int] = [:]
    private let arquivo = (pastaAlerta as NSString).appendingPathComponent("tela.json")

    private func chave(_ s: String) -> String {
        var h: UInt64 = 1469598103934665603            // FNV-1a: id igual entre reinícios do app
        for b in s.utf8 { h = (h ^ UInt64(b)) &* 1099511628211 }
        return String(h, radix: 36)
    }

    func atualizar() {
        let agora = Date().timeIntervalSince1970
        var itens: [(id: String, titulo: String, detalhe: String, tipo: String, onde: String)] = []
        if let data = FileManager.default.contents(atPath: arquivo),
           let j = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any],
           agora - ((j["lido"] as? Double) ?? 0) < 20 {            // leitura velha (leitor parado) não vale
            for c in (j["cartoes"] as? [[String: Any]]) ?? [] {
                let t = (c["titulo"] as? String) ?? "", d = (c["detalhe"] as? String) ?? "", w = (c["janela"] as? String) ?? "Claude"
                if !t.isEmpty { itens.append(("tela-" + chave(t + "|" + d), t, d, "aprovacao", w)) }
            }
        }
        var novos: [Pendencia] = []
        var vistos = Set<String>()
        for i in itens where !vistos.contains(i.id) {
            vistos.insert(i.id); faltas[i.id] = 0
            let t0 = desde[i.id] ?? agora; desde[i.id] = t0
            novos.append(Pendencia(id: i.id, titulo: i.titulo, detalhe: i.detalhe.isEmpty ? nil : i.detalhe, tipo: i.tipo,
                                   sessao: i.onde, criado: t0, link: nil, app: nil))
        }
        // tolera 1 leitura sem o item (janela redesenhando) antes de tirar o pedido
        for p in achados where !vistos.contains(p.id) {
            let f = (faltas[p.id] ?? 0) + 1; faltas[p.id] = f
            if f <= 1 { novos.append(p) } else { desde[p.id] = nil; faltas[p.id] = nil }
        }
        presentes = vistos
        achados = novos
    }
}

// MARK: - Nuvem (~/.claude-alerta/nuvem/<chave>.json, um arquivo por thread, gravado por `bin/nuvem`)
// {"chave": "cmsg_...", "projeto": "...", "link": "claude://...",
//  "sessao": {"nome": "...", "estado": "trabalhando|esperando|parada", "desde": epoch, "ate": epoch},
//  "pedido": {"titulo": "...", "detalhe": "...", "tipo": "decisao", "criado": epoch, "expira": epoch} | null}
let pastaNuvem = (pastaAlerta as NSString).appendingPathComponent("nuvem")

func lerNuvem() -> [(arquivo: String, j: [String: Any])] {
    let fm = FileManager.default
    var r: [(arquivo: String, j: [String: Any])] = []
    for n in (try? fm.contentsOfDirectory(atPath: pastaNuvem)) ?? [] where n.hasSuffix(".json") {
        let f = (pastaNuvem as NSString).appendingPathComponent(n)
        guard let d = fm.contents(atPath: f), let j = try? JSONSerialization.jsonObject(with: d) as? [String: Any] else { continue }
        r.append((arquivo: f, j: j))
    }
    return r
}

// id muda a cada pedido novo da mesma thread, para tocar o som de novo
func idNuvem(_ j: [String: Any]) -> String? {
    guard let p = j["pedido"] as? [String: Any], let c = p["criado"] as? Double else { return nil }
    return "nuvem-\(j["chave"] as? String ?? "")-\(Int(c))"
}

func pendenciasNuvem() -> [Pendencia] {
    let agora = Date().timeIntervalSince1970
    var r: [Pendencia] = []
    for item in lerNuvem() {
        let j = item.j
        guard let p = j["pedido"] as? [String: Any], let titulo = p["titulo"] as? String, !titulo.isEmpty,
              let id = idNuvem(j), ((p["expira"] as? Double) ?? Double.infinity) > agora else { continue }
        let det = (p["detalhe"] as? String) ?? ""
        r.append(Pendencia(id: id, titulo: titulo, detalhe: det.isEmpty ? nil : det, tipo: p["tipo"] as? String,
                           sessao: (j["projeto"] as? String) ?? "Nuvem", criado: (p["criado"] as? Double) ?? agora,
                           link: j["link"] as? String, app: nil))
    }
    return r
}

func sessoesNuvem() -> [Sessao] {
    let agora = Date().timeIntervalSince1970
    var r: [Sessao] = []
    for item in lerNuvem() {
        let j = item.j
        // arquivo parado há 2 dias: limpa
        if let m = (try? FileManager.default.attributesOfItem(atPath: item.arquivo))?[.modificationDate] as? Date,
           agora - m.timeIntervalSince1970 > 48 * 3600 { try? FileManager.default.removeItem(atPath: item.arquivo); continue }
        guard let s = j["sessao"] as? [String: Any], let estado = s["estado"] as? String,
              estado == "trabalhando" || estado == "esperando", ((s["ate"] as? Double) ?? 0) > agora,
              let chave = j["chave"] as? String else { continue }
        var se = Sessao(id: chave, nome: (s["nome"] as? String) ?? "Thread da nuvem",
                        pasta: ((j["projeto"] as? String) ?? "Projeto") + " · nuvem",
                        ocupada: estado == "trabalhando", desde: (s["desde"] as? Double) ?? agora,
                        link: (j["link"] as? String) ?? "claude://")
        se.aguardando = estado == "esperando"
        r.append(se)
    }
    return r
}

// "Abrir conversa" num pedido da nuvem: tira o pedido do arquivo da thread (a thread segue como esperando até responder)
func fecharNuvem(_ id: String) {
    for item in lerNuvem() where idNuvem(item.j) == id {
        var n = item.j
        n["pedido"] = NSNull()
        if let d = try? JSONSerialization.data(withJSONObject: n, options: [.prettyPrinted]) {
            try? d.write(to: URL(fileURLWithPath: item.arquivo), options: .atomic)
        }
    }
}

func lerSessoes() -> [Sessao] {
    cacheTitulos = [:]
    let comFundo = pidsComFundo()
    let fm = FileManager.default
    let nomes = (try? fm.contentsOfDirectory(atPath: pastaSessoes)) ?? []
    var r: [Sessao] = []
    for n in nomes where n.hasSuffix(".json") {
        guard let d = fm.contents(atPath: (pastaSessoes as NSString).appendingPathComponent(n)),
              let j = try? JSONSerialization.jsonObject(with: d) as? [String: Any],
              let pid = j["pid"] as? Int32, kill(pid, 0) == 0 else { continue }
        let pasta = (j["cwd"] as? String ?? "") as NSString
        let id = (j["hostSessionId"] as? String) ?? (j["sessionId"] as? String) ?? n
        let nomeCLI = (j["name"] as? String) ?? ""
        let nomeDoUsuario = (j["nameSource"] as? String) == "user" && !nomeCLI.isEmpty
        var nome = pasta.lastPathComponent
        if let t = tituloDesktop(id), !t.titulo.isEmpty, !t.titulo.hasPrefix("Local session") {
            nome = t.titulo
        } else if nomeDoUsuario {
            nome = nomeCLI
        } else if tituloDesktop(id)?.projeto == true {
            nome = "Thread de projeto"
        } else if !nomeCLI.isEmpty {
            nome = nomeCLI
        }
        let ms = (j["statusUpdatedAt"] as? Double) ?? (j["startedAt"] as? Double) ?? 0
        var sub = pasta.lastPathComponent
        var link = id.hasPrefix("local_") ? "claude://code/continue?session=" + id : "claude://"
        if tituloDesktop(id)?.projeto == true, let cli = j["sessionId"] as? String, let p = infoProjeto(sessionId: cli) {
            link = "claude://claude.ai/code/project/" + p.projeto + (p.thread.isEmpty ? "" : "?thread=" + p.thread)
            if !p.topico.isEmpty && !nomeDoUsuario { nome = p.topico }
            if !p.nomeProjeto.isEmpty { sub = p.nomeProjeto }
        }
        r.append(Sessao(id: id, nome: nome, pasta: sub,
                        ocupada: (j["status"] as? String) == "busy", desde: ms / 1000, link: link,
                        fundo: comFundo.contains(pid)))
    }
    r += sessoesNuvem()
    return r.sorted { ($0.ocupada ? 1 : 0, $0.desde) > ($1.ocupada ? 1 : 0, $1.desde) }
}

// Uso de 5h/semana: ~/.claude-alerta/uso.json, gravado por `alerta uso`. Os hooks do Claude Code disparam
// essa leitura em segundo plano (no máximo a cada 2 min), dentro das sessões do app Desktop ou do terminal.
func lerJanela(_ v: Any?) -> Janela? {
    guard let o = v as? [String: Any], let pct = o["pct"] as? Double else { return nil }
    let r = (o["renova"] as? Double).map { Date(timeIntervalSince1970: $0) }
    // janela já renovou e ninguém usou desde então: está zerada
    if let r = r, r < Date() { return Janela(pct: 0, renova: nil, renovou: true) }
    return Janela(pct: max(0, min(100, pct)), renova: r)
}

// Sem hook rodando (só threads na nuvem, ou nada aberto), ninguém relia o uso e o número congelava.
// O próprio app pede uma leitura nova quando a última tem mais de 3 min (no máximo uma a cada 2 min).
var ultimoPedidoUso = Date.distantPast
func pedirLeituraUso(_ lido: Date?) {
    let agora = Date()
    if let l = lido, agora.timeIntervalSince(l) < 180 { return }
    if agora.timeIntervalSince(ultimoPedidoUso) < 120 { return }
    ultimoPedidoUso = agora
    let p = Process()
    p.executableURL = URL(fileURLWithPath: (pastaAlerta as NSString).appendingPathComponent("bin/alerta"))
    p.arguments = ["uso"]
    p.currentDirectoryURL = URL(fileURLWithPath: pastaAlerta)
    var env = ProcessInfo.processInfo.environment
    env["ALERTA_USO_FILHO"] = "1"
    env["PATH"] = (env["PATH"] ?? "/usr/bin:/bin") + ":/opt/homebrew/bin:/usr/local/bin:" + NSHomeDirectory() + "/.local/bin"
    p.environment = env
    p.standardOutput = FileHandle.nullDevice; p.standardError = FileHandle.nullDevice
    try? p.run()
}

func buscarUso(_ fim: @escaping (Uso?, String?) -> Void) {
    let lidoAntes = FileManager.default.contents(atPath: arqUso)
        .flatMap { try? JSONSerialization.jsonObject(with: $0) as? [String: Any] }
        .flatMap { $0["lido"] as? Double }.map { Date(timeIntervalSince1970: $0) }
    pedirLeituraUso(lidoAntes)
    guard let d = FileManager.default.contents(atPath: arqUso),
          let j = try? JSONSerialization.jsonObject(with: d) as? [String: Any] else {
        return fim(nil, "O uso aparece quando uma sessão do Claude Code rodar")
    }
    let lido = Date(timeIntervalSince1970: (j["lido"] as? Double) ?? 0)
    var u = Uso(sessao: lerJanela(j["sessao"]), semana: lerJanela(j["semana"]), sonnet: nil, lido: lido)
    for m in (j["modelos"] as? [[String: Any]]) ?? [] {
        if let nome = m["nome"] as? String, let jan = lerJanela(m) { u.modelos.append((nome, jan)) }
    }
    fim(u, nil)
}

// MARK: - Visual

extension Color {
    init(_ h: UInt32, _ a: Double = 1) {
        self.init(.sRGB, red: Double((h >> 16) & 255) / 255, green: Double((h >> 8) & 255) / 255, blue: Double(h & 255) / 255, opacity: a)
    }
}
enum C {
    static let fundo = Color(0x141413), cartao = Color(0x1F1E1B), cartao2 = Color(0x262521), linha = Color(0x2F2D28)
    static let tx = Color(0xECE8E1), tx2 = Color(0xA39E93), tx3 = Color(0x6F6B62)
    static let argila = Color(0xD97757), argila2 = Color(0xE8A184), ok = Color(0x3DDC84), trilho = Color(0x33312C)
    static let perigo = Color(0xE5534B), parada = Color(0x5C584F)
}
let raioIlha: CGFloat = 16, raioCartao: CGFloat = 10

func corUso(_ p: Double) -> Color { p < 60 ? C.ok : (p < 85 ? C.argila : C.perigo) }

func tempo(_ s: Double) -> String {
    let m = Int((Date().timeIntervalSince1970 - s) / 60)
    if m < 1 { return "agora" }
    if m < 60 { return "\(m) min" }
    return "\(m / 60) h \(m % 60) min"
}

// "agora" no primeiro minuto; depois "há 3 min", "há 1 h 5 min"
func haQuanto(_ s: Double) -> String { let t = tempo(s); return t == "agora" ? t : "há " + t }

func renova(_ d: Date?) -> String {
    guard let d = d else { return "" }
    let s = d.timeIntervalSinceNow
    if s <= 0 { return "renovando" }
    if s < 24 * 3600 { let m = Int(s / 60); return "renova em \(m / 60)h \(m % 60)min" }
    let f = DateFormatter(); f.locale = Locale(identifier: "pt_BR"); f.dateFormat = "EEE HH:mm"
    return "renova " + f.string(from: d).replacingOccurrences(of: ".", with: "")
}

func rotuloTipo(_ t: String?) -> String {
    switch t ?? "" { case "chave": return "chave"; case "decisao": return "decisão"; case "aprovacao": return "aprovação"; case "pronto": return "pronto"; default: return "pedido" }
}

func saudacao() -> String {
    let h = Calendar.current.component(.hour, from: Date())
    let nome = NSFullUserName().split(separator: " ").first.map(String.init) ?? ""
    let s = h < 12 ? "Bom dia" : (h < 18 ? "Boa tarde" : "Boa noite")
    return nome.isEmpty ? s : "\(s), \(nome)"
}

final class Estado: ObservableObject {
    @Published var pendencias: [Pendencia] = []
    @Published var sessoes: [Sessao] = []
    @Published var uso: Uso? = nil
    @Published var erroUso: String? = nil
    @Published var aberto = false
    @Published var aviso = false      // pedidos à mostra sob a ilha (Claude fora da frente)
    @Published var pulso = false
    @Published var tique = 0          // força refazer textos de tempo
    var notch: CGFloat = 0
    @Published var largura: CGFloat = 300
    @Published var revelado = false        // o que desce (painel ou aviso) está à mostra; anima de cima para baixo
    var ancoraX: CGFloat = 0.5              // de onde desce: o meio do bloco clicado, em fração da largura
    @Published var soIcone = false          // barra fechada mostra só o ✳ (opção do botão direito)   // onde o bloco fechado começa, dentro da janela
    var alturaBarra: CGFloat = 37
    var abrir: (String) -> Void = { _ in }
    var abrirPedido: (Pendencia) -> Void = { _ in }   // abre a conversa e dá o pedido por resolvido
    var alternar: () -> Void = {}
    // ✕ num cartão do aviso esconde só aquele pedido (continua no painel e no contador)
    @Published var escondidos = Set<String>()
    var esconderAviso: (String) -> Void = { _ in }
    var noAviso: [Pendencia] { pendencias.filter { !escondidos.contains($0.id) } }

    func esperando(_ s: Sessao) -> Bool { s.aguardando || pendencias.contains { ($0.link ?? "").contains(s.id) } }
    // ativa = trabalhando agora ou esperando você; sessão que terminou o turno não conta,
    // mesmo com o processo ainda aberto (o app Desktop mantém o processo vivo)
    var ativas: [Sessao] { sessoes.filter { $0.ocupada || $0.fundo || esperando($0) } }
}

struct Hover: ViewModifier {
    var cor: Color; var raio: CGFloat = raioCartao
    @State private var dentro = false
    func body(content: Content) -> some View {
        content
            .background(RoundedRectangle(cornerRadius: raio).fill(dentro ? cor : .clear))
            .onHover { h in
                withAnimation(.easeOut(duration: 0.12)) { dentro = h }
                if h { NSCursor.pointingHand.set() } else { NSCursor.arrow.set() }
            }
    }
}
extension View { func hover(_ cor: Color, raio: CGFloat = raioCartao) -> some View { modifier(Hover(cor: cor, raio: raio)) } }

struct BotaoArgila: ButtonStyle {
    @State private var dentro = false
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.system(size: 12.5, weight: .semibold)).foregroundColor(.white)
            .padding(.horizontal, 13).padding(.vertical, 7)
            .background(RoundedRectangle(cornerRadius: 7).fill(dentro ? Color(0xEC8A68) : C.argila))
            .overlay(RoundedRectangle(cornerRadius: 7).stroke(Color.white.opacity(dentro ? 0.35 : 0), lineWidth: 1))
            .scaleEffect(configuration.isPressed ? 0.96 : (dentro ? 1.03 : 1))
            .onHover { h in
                withAnimation(.easeOut(duration: 0.12)) { dentro = h }
                if h { NSCursor.pointingHand.set() } else { NSCursor.arrow.set() }
            }
    }
}

struct BotaoTexto: ButtonStyle {
    @State private var dentro = false
    func makeBody(configuration: Configuration) -> some View {
        configuration.label.foregroundColor(dentro ? .white : C.tx2)
            .onHover { h in dentro = h; if h { NSCursor.pointingHand.set() } else { NSCursor.arrow.set() } }
    }
}

// Cartão de pedido: o mesmo no aviso (ilha com pedidos) e no painel aberto
struct CartaoPedido: View {
    let p: Pendencia
    var abrir: (String) -> Void
    var fechar: (() -> Void)? = nil      // com ✕ = cartão sozinho (aviso): é a própria janela
    var body: some View {
        HStack(spacing: 12) {
            VStack(alignment: .leading, spacing: 4) {
                Text(p.titulo).font(.system(size: 14, weight: .semibold)).foregroundColor(.white).lineLimit(1)
                HStack(spacing: 6) {
                    Text(rotuloTipo(p.tipo)).font(.system(size: 10.5, weight: .semibold)).foregroundColor(Color(0xFFC2A8))
                        .padding(.horizontal, 6).padding(.vertical, 1)
                        .background(RoundedRectangle(cornerRadius: 4).fill(C.argila.opacity(0.32)))
                    Text([p.sessao ?? "", haQuanto(p.criado)].filter { !$0.isEmpty }.joined(separator: " · "))
                        .font(.system(size: 11.5)).foregroundColor(Color(0xC9C3B8)).lineLimit(1)
                }
            }
            Spacer(minLength: 8)
            Button(action: { abrir(p.link ?? "") }) { Text("Abrir conversa") }.buttonStyle(BotaoArgila())
            if let fechar = fechar {
                Button(action: fechar) {
                    Image(systemName: "xmark").font(.system(size: 10.5, weight: .bold)).frame(width: 26, height: 26)
                        .contentShape(Rectangle())
                }.buttonStyle(BotaoTexto()).hover(Color.white.opacity(0.1), raio: 6).help("Esconder até o próximo pedido")
            }
        }
        .padding(.leading, 18).padding(.trailing, 12).padding(.vertical, 13)
        .background(
            ZStack(alignment: .leading) {
                RoundedRectangle(cornerRadius: raioCartao).fill(Color(0x1C1A18))
                Rectangle().fill(C.argila).frame(width: 4)          // faixa argila à esquerda
            }
            .clipShape(RoundedRectangle(cornerRadius: raioCartao))
            .overlay(RoundedRectangle(cornerRadius: raioCartao).stroke(C.argila.opacity(0.85), lineWidth: 1.2)))
        .help(p.detalhe ?? "")
    }
}

struct Barrinha: View {
    var pct: Double; var largura: CGFloat; var altura: CGFloat
    var body: some View {
        ZStack(alignment: .leading) {
            Capsule().fill(C.trilho)
            Capsule().fill(corUso(pct)).frame(width: max(altura, largura * pct / 100))
        }.frame(width: largura, height: altura)
    }
}

struct Anel: View {
    var pct: Double
    var body: some View {
        ZStack {
            Circle().stroke(Color.primary.opacity(0.2), lineWidth: 2.4)
            Circle().trim(from: 0, to: max(0.02, pct / 100))
                .stroke(corUso(pct), style: StrokeStyle(lineWidth: 2.4, lineCap: .round))
                .rotationEffect(.degrees(-90))
        }.frame(width: 13, height: 13)
    }
}

struct Estrela: View {
    var body: some View {
        Canvas { ctx, sz in
            let c = CGPoint(x: sz.width / 2, y: sz.height / 2), r = sz.width / 2 - 1
            var p = Path()
            for i in 0..<4 {
                let a = Double(i) * .pi / 4
                p.move(to: CGPoint(x: c.x + r * cos(a), y: c.y + r * sin(a)))
                p.addLine(to: CGPoint(x: c.x - r * cos(a), y: c.y - r * sin(a)))
            }
            ctx.stroke(p, with: .color(C.argila), style: StrokeStyle(lineWidth: 2.2, lineCap: .round))
        }.frame(width: 14, height: 14)
    }
}

// Ilha fechada: um bloco só, enxuto. Com notch, fica logo à direita dele (à esquerda do notch ficam os
// menus do app, que no MacBook chegam até o notch); sem notch, no centro da barra.
struct Compacto: View {
    @ObservedObject var e: Estado
    var body: some View {
        if e.soIcone { icone } else { completo }
    }
    // só o ícone: ✳ e, havendo pedido, o ponto argila
    var icone: some View {
        ZStack(alignment: .topTrailing) {
            Estrela().frame(width: 16, height: 16)
            if !e.pendencias.isEmpty {
                Circle().fill(C.argila).frame(width: 7, height: 7)
                    .overlay(Circle().stroke(Color(nsColor: .windowBackgroundColor), lineWidth: 1.5))
                    .scaleEffect(e.pulso ? 1.4 : 1)
                    .offset(x: 3, y: -2)
            }
        }
        .help(e.pendencias.isEmpty ? "Claude Alerta" : (e.pendencias.count == 1 ? "1 pedido esperando você" : "\(e.pendencias.count) pedidos esperando você"))
        .padding(.horizontal, 7).padding(.vertical, 4)
    }
    var completo: some View {
        HStack(spacing: 9) {
            HStack(spacing: 5) {
                Estrela()
                if !e.pendencias.isEmpty {
                    HStack(spacing: 4) {
                        Circle().fill(C.argila).frame(width: 6, height: 6)
                            .scaleEffect(e.pulso ? 1.5 : 1).opacity(e.pulso ? 1 : 0.85)
                        Text("\(e.pendencias.count)")
                    }
                    .foregroundColor(C.argila)
                    .padding(.horizontal, 6).padding(.vertical, 1)
                    .background(RoundedRectangle(cornerRadius: 5).fill(C.argila.opacity(0.18)))
                    .help(e.pendencias.count == 1 ? "1 pedido esperando você" : "\(e.pendencias.count) pedidos esperando você")
                }
                Text("\(e.ativas.count)").foregroundStyle(.primary)
                    .help(e.ativas.count == 1 ? "1 sessão trabalhando" : "\(e.ativas.count) sessões trabalhando")
            }
            anel(e.uso?.sessao, "Sessão de 5 horas")
            anel(e.uso?.semana, "Semana")
        }
        .font(.system(size: 12.5, weight: .medium)).monospacedDigit()
        .lineLimit(1).fixedSize()
        .padding(.horizontal, 7).padding(.vertical, 4)
    }
    func anel(_ j: Janela?, _ dica: String) -> some View {
        HStack(spacing: 4) {
            if let j = j {
                Anel(pct: j.pct)
                Text("\(Int(j.pct.rounded()))%").foregroundStyle(.primary)
            } else { Anel(pct: 0); Text("–").foregroundStyle(.tertiary) }
        }.help(j.map { "\(dica): \(Int($0.pct.rounded()))% · \($0.renovou ? "renovou" : renova($0.renova))" } ?? dica)
    }
}

struct Painel: View {
    @ObservedObject var e: Estado
    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(alignment: .firstTextBaseline) {
                Text(saudacao()).font(.system(size: 21, weight: .regular, design: .serif)).foregroundColor(C.tx)
                Spacer()
                Text(e.uso.map { "atualizado " + haQuanto($0.lido.timeIntervalSince1970) } ?? "").font(.system(size: 11.5)).foregroundColor(C.tx3)
            }.padding(.top, 10).padding(.bottom, 14).padding(.horizontal, 2)

            VStack(spacing: 13) {
                if let u = e.uso {
                    linhaUso("Sessão", u.sessao)
                    linhaUso("Semana", u.semana)
                    ForEach(Array(u.modelos.enumerated()), id: \.offset) { _, m in linhaUso("Semana · " + m.nome, m.janela) }
                                    } else {
                    Text(e.erroUso ?? "lendo uso…").font(.system(size: 12.5)).foregroundColor(C.tx2).frame(maxWidth: .infinity, alignment: .leading)
                }
            }
            .padding(.horizontal, 16).padding(.vertical, 14)
            .background(cartao())

            if !e.pendencias.isEmpty {
                rotulo("Precisa de você", e.pendencias.count)
                VStack(spacing: 8) { ForEach(e.pendencias.prefix(4)) { p in CartaoPedido(p: p, abrir: { _ in e.abrirPedido(p) }) } }
            }

            rotulo("Sessões ativas", e.ativas.count)
            VStack(spacing: 0) {
                if e.ativas.isEmpty {
                    Text("Nenhuma sessão trabalhando agora").font(.system(size: 12.5)).foregroundColor(C.tx2)
                        .frame(maxWidth: .infinity, alignment: .leading).padding(14)
                }
                ForEach(Array(e.ativas.prefix(6).enumerated()), id: \.element.id) { i, s in
                    if i > 0 { Rectangle().fill(C.linha).frame(height: 1) }
                    linhaSessao(s)
                }
            }.background(cartao())

            HStack {
                Spacer()
                Button(action: { e.abrir("claude://") }) { Text("Abrir Claude ↗") }.buttonStyle(BotaoTexto())
            }.font(.system(size: 11)).foregroundColor(C.tx3).padding(.top, 13).padding(.horizontal, 4)
        }
        .padding(.horizontal, 22).padding(.top, 4).padding(.bottom, 18)
    }

    func cartao() -> some View {
        RoundedRectangle(cornerRadius: raioCartao).fill(C.cartao)
            .overlay(RoundedRectangle(cornerRadius: raioCartao).stroke(C.linha, lineWidth: 1))
    }
    func rotulo(_ t: String, _ n: Int) -> some View {
        HStack { Text(t); Spacer(); Text("\(n)") }.font(.system(size: 12)).foregroundColor(C.tx2)
            .padding(.top, 16).padding(.bottom, 8).padding(.horizontal, 2)
    }
    func linhaUso(_ t: String, _ j: Janela?) -> some View {
        HStack(spacing: 12) {
            VStack(alignment: .leading, spacing: 1) {
                Text(t).font(.system(size: 13)).foregroundColor(C.tx)
                // renovou e ainda não houve leitura nova: a janela de 5h só começa a contar no próximo uso
                Text(j?.renovou == true ? "renovou · conta no próximo uso" : renova(j?.renova))
                    .font(.system(size: 11)).foregroundColor(C.tx3)
            }.frame(width: 140, alignment: .leading)
            GeometryReader { g in Barrinha(pct: j?.pct ?? 0, largura: g.size.width, altura: 7) }.frame(height: 7)
            Text(j.map { "\(Int($0.pct.rounded()))%" } ?? "–").font(.system(size: 13, weight: .medium)).monospacedDigit()
                .foregroundColor(C.tx).frame(width: 44, alignment: .trailing)
        }
    }
    func linhaSessao(_ s: Sessao) -> some View {
        let esp = e.esperando(s)
        return Button(action: { e.abrir(s.link) }) {
            HStack(spacing: 11) {
                Circle().fill(esp ? C.argila : (s.ocupada ? C.ok : (s.fundo ? C.ok.opacity(0.55) : C.parada))).frame(width: 7, height: 7)
                VStack(alignment: .leading, spacing: 1) {
                    Text(s.nome).font(.system(size: 13)).foregroundColor(C.tx).lineLimit(1)
                    Text(s.pasta).font(.system(size: 11)).foregroundColor(C.tx3).lineLimit(1)
                }
                Spacer(minLength: 8)
                Text(esp ? "esperando você" : (s.ocupada ? "trabalhando" : (s.fundo ? "em segundo plano" : "parada")))
                    .font(.system(size: 11.5)).foregroundColor(C.tx2)
            }
            .padding(.horizontal, 14).padding(.vertical, 10).contentShape(Rectangle())
        }.buttonStyle(.plain).hover(C.cartao2, raio: 0)
    }
}

struct Ilha: View {
    @ObservedObject var e: Estado
    var body: some View {
        VStack(spacing: 0) {
            Group {
                if e.aberto { Painel(e: e) }
                else if e.aviso && !e.noAviso.isEmpty {
                    VStack(spacing: 8) {
                        ForEach(e.noAviso.prefix(3)) { p in CartaoPedido(p: p, abrir: { _ in e.abrirPedido(p) }, fechar: { e.esconderAviso(p.id) }) }
                        if e.noAviso.count > 3 {
                            Text("+ \(e.noAviso.count - 3) pedidos").font(.system(size: 11.5)).foregroundColor(C.tx2)
                                .padding(.horizontal, 10).padding(.vertical, 4).background(Capsule().fill(C.fundo))
                        }
                    }
                }
            }
            .frame(width: e.largura)
            .background(RoundedRectangle(cornerRadius: raioIlha).fill(e.aberto ? C.fundo : Color.clear)
                .overlay(RoundedRectangle(cornerRadius: raioIlha).stroke(Color.white.opacity(e.aberto ? 0.08 : 0), lineWidth: 1)))
            .environment(\.colorScheme, .dark)
            .scaleEffect(x: e.revelado ? 1 : 0.35, y: e.revelado ? 1 : 0.02, anchor: UnitPoint(x: e.ancoraX, y: 0))
            .opacity(e.revelado ? 1 : 0)
        }
        .frame(width: e.largura)
        .tint(C.argila)
    }
}

// MARK: - Janela

final class JanelaIlha: NSPanel {
    override var canBecomeKey: Bool { true }
}

// Conteúdo do item da barra: desenha, mas deixa o clique para o botão do item
final class ConteudoItem: NSHostingView<Compacto> {
    override func hitTest(_ point: NSPoint) -> NSView? { nil }
}

// O primeiro clique já age (sem isso, a janela que não está ativa engole o clique e pede um segundo)
final class Hospedeiro<V: View>: NSHostingView<V> {
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    // o app nunca fica ativo (é um acessório); sem esta área, o SwiftUI não recebe o mouse e não há hover nem mãozinha
    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        for t in trackingAreas where t.userInfo?["claudeAlerta"] != nil { removeTrackingArea(t) }
        addTrackingArea(NSTrackingArea(rect: .zero, options: [.mouseEnteredAndExited, .mouseMoved, .activeAlways, .inVisibleRect],
                                       owner: self, userInfo: ["claudeAlerta": true]))
    }
}

final class App: NSObject, NSApplicationDelegate {
    let e = Estado()
    var janela: JanelaIlha!
    var hospedeiro: Hospedeiro<Ilha>!
    var item: NSStatusItem!            // o bloco: item nativo da barra, logo antes dos outros ícones
    var conteudoItem: ConteudoItem!
    var avisoAte = Date.distantPast   // pedido novo desce sempre; com o Claude na frente, sobe sozinho depois disso
    var mtime: Date? = nil
    var vistos = Set<String>()
    var primeira = true
    var ultimoSom = Date.distantPast
    var pulsarAte = Date.distantPast
    var monitorFora: Any?
    let menu = NSMenu()

    func applicationDidFinishLaunching(_ n: Notification) {
        e.abrir = { [weak self] l in self?.abrirLink(l) }
        e.abrirPedido = { [weak self] p in self?.resolver(p); self?.abrirLink(p.link ?? "") }
        e.alternar = { [weak self] in self?.alternar() }
        e.esconderAviso = { [weak self] id in self?.e.escondidos.insert(id); self?.atualizarAviso(); self?.ajustarAltura() }

        janela = JanelaIlha(contentRect: .zero, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        janela.level = NSWindow.Level(rawValue: Int(CGWindowLevelForKey(.mainMenuWindow)) + 2)
        janela.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
        janela.backgroundColor = .clear
        janela.isOpaque = false
        janela.hasShadow = false
        janela.hidesOnDeactivate = false
        janela.isMovable = false
        janela.acceptsMouseMovedEvents = true
        hospedeiro = Hospedeiro(rootView: Ilha(e: e))
        janela.contentView = hospedeiro

        let itemIcone = menu.addItem(withTitle: "Mostrar só o ícone", action: #selector(alternarIcone(_:)), keyEquivalent: "")
        e.soIcone = (lerConfig()["barra"] as? String) == "icone"
        itemIcone.state = e.soIcone ? .on : .off
        menu.addItem(.separator())
        menu.addItem(withTitle: "Abrir arquivo de pendências", action: #selector(abrirArquivo), keyEquivalent: "")
        menu.addItem(withTitle: "Atualizar uso agora", action: #selector(atualizarUso), keyEquivalent: "")
        menu.addItem(.separator())
        menu.addItem(withTitle: "Sair do Claude Alerta", action: #selector(sair), keyEquivalent: "")
        for i in menu.items { i.target = self }
        hospedeiro.menu = menu

        item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        item.autosaveName = "ClaudeAlerta"
        if let b = item.button {
            conteudoItem = ConteudoItem(rootView: Compacto(e: e))
            conteudoItem.translatesAutoresizingMaskIntoConstraints = false
            b.addSubview(conteudoItem)
            NSLayoutConstraint.activate([
                conteudoItem.centerXAnchor.constraint(equalTo: b.centerXAnchor),
                conteudoItem.centerYAnchor.constraint(equalTo: b.centerYAnchor),
            ])
            b.target = self
            b.action = #selector(cliqueItem)
            b.sendAction(on: [.leftMouseUp, .rightMouseUp])
        }

        NotificationCenter.default.addObserver(self, selector: #selector(posicionar), name: NSApplication.didChangeScreenParametersNotification, object: nil)

        lerArquivos(); e.sessoes = lerSessoes(); atualizarUso()
        posicionar()

        Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in self?.lerArquivos() }
        Timer.scheduledTimer(withTimeInterval: 3, repeats: true) { [weak self] _ in self?.e.sessoes = lerSessoes(); self?.ajustarAltura() }
        Timer.scheduledTimer(withTimeInterval: 5, repeats: true) { [weak self] _ in self?.atualizarUso() }
        Timer.scheduledTimer(withTimeInterval: 30, repeats: true) { [weak self] _ in self?.e.tique += 1 }
        Timer.scheduledTimer(withTimeInterval: 0.6, repeats: true) { [weak self] _ in self?.pulsar() }

        if let png = ProcessInfo.processInfo.environment["ALERTA_PNG"] { capturar(png) }
    }

    var tela: NSScreen { item.button?.window?.screen ?? NSScreen.screens.first ?? NSScreen.main! }

    @objc func posicionar() {
        let t = tela
        let barra = t.frame.maxY - t.visibleFrame.maxY
        e.alturaBarra = (barra > 20 && barra < 60) ? barra : max(t.safeAreaInsets.top, 32)
        ajustarAltura(animar: false)
    }

    @objc func cliqueItem() {
        if NSApp.currentEvent?.type == .rightMouseUp {
            item.menu = menu
            item.button?.performClick(nil)
            item.menu = nil
        } else { alternar() }
    }

    func ajustarAltura(animar: Bool = true) {
        // largura do item = a do conteúdo (muda com pedidos, números e o modo só ícone)
        let wc = ceil(conteudoItem.fittingSize.width)
        if abs(item.length - wc) > 0.5 { item.length = wc }
        let desce = e.aberto || (e.aviso && !e.noAviso.isEmpty)
        guard desce else { janela.orderOut(nil); return }
        // painel e aviso descem logo abaixo do item, centralizados nele (sem sair da tela)
        let t = tela.frame
        let r = item.button?.window?.frame ?? NSRect(x: t.maxX - 300, y: t.maxY - e.alturaBarra, width: wc, height: e.alturaBarra)
        let largura: CGFloat = e.aberto ? 580 : 440   // aviso: só o cartão, mais estreito
        // painel aberto: centralizado no item; aviso: no canto superior direito da tela, saindo do canto
        let x = e.aberto ? min(max(r.midX - largura / 2, t.minX + 8), t.maxX - largura - 8) : t.maxX - largura - 10
        e.ancoraX = e.aberto ? (r.midX - x) / largura : 1
        if e.largura != largura { e.largura = largura }
        hospedeiro.layoutSubtreeIfNeeded()
        let h = hospedeiro.fittingSize.height
        janela.hasShadow = true
        let f = NSRect(x: x, y: r.minY - h - (e.aberto ? 4 : 8), width: largura, height: h)
        if janela.frame != f { janela.setFrame(f, display: true) }
        if !janela.isVisible { janela.orderFrontRegardless() }
    }

    func alternar() {
        if !e.aberto {
            e.aberto = true
            descer()
            monitorFora = NSEvent.addGlobalMonitorForEvents(matching: [.leftMouseDown, .rightMouseDown]) { [weak self] _ in
                if self?.e.aberto == true { self?.alternar() }
            }
        } else {
            if let m = monitorFora { NSEvent.removeMonitor(m); monitorFora = nil }
            let ficaAviso = e.aviso && !e.pendencias.isEmpty
            subir { self.e.aberto = false; if ficaAviso { self.descer() } }
        }
    }

    // janela no tamanho final na hora (invisível), depois o conteúdo desce a partir do bloco clicado
    func descer() {
        e.revelado = false
        ajustarAltura()
        DispatchQueue.main.async {
            self.ajustarAltura()
            withAnimation(.spring(response: 0.32, dampingFraction: 0.86)) { self.e.revelado = true }
        }
    }
    // o conteúdo sobe de volta para o bloco; só depois a janela encolhe
    func subir(_ depois: @escaping () -> Void) {
        withAnimation(.easeIn(duration: 0.16)) { e.revelado = false }
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.17) { depois(); self.ajustarAltura() }
    }

    func claudeNaFrente() -> Bool {
        NSWorkspace.shared.frontmostApplication?.bundleIdentifier?.hasPrefix("com.anthropic.claude") ?? false
    }

    func atualizarAviso() {
        let mostrar = !e.noAviso.isEmpty && (!claudeNaFrente() || Date() < avisoAte)
        guard mostrar != e.aviso else { return }
        if e.aberto { e.aviso = mostrar; return }       // painel aberto já mostra os pedidos
        if mostrar { e.aviso = true; descer() } else { subir { self.e.aviso = false } }
    }

    // Pedido só aparece depois de `atraso` segundos (config.json "atraso", padrão 6): a sessão abre o alerta
    // logo antes de escrever a pergunta, e o aviso não pode chegar antes da mensagem. Resolvido antes disso, nem aparece.
    var todas: [Pendencia] = []
    var mostradoEm: [String: Double] = [:]   // quando cada pedido apareceu ("Terminou" some 10 s depois)
    var locais: [Pendencia] = []
    var fechadasNuvem = Set<String>()
    let leitorTela = LeitorTela()
    var atraso: Double {
        let v = lerConfig()["atraso"]
        return (v as? Double) ?? (v as? Int).map(Double.init) ?? 6
    }

    func lerArquivos() {
        defer { atualizarAviso() }
        let m = (try? FileManager.default.attributesOfItem(atPath: arqPendencias))?[.modificationDate] as? Date
        if primeira || m != mtime { mtime = m; locais = lerPendencias() }
        leitorTela.atualizar()
        // pedido da tela fechado com "Abrir conversa" fica escondido só enquanto continuar na tela; se sair e voltar, avisa de novo
        let naTela = Set(leitorTela.achados.map { $0.id })
        fechadasNuvem = fechadasNuvem.filter { !$0.hasPrefix("tela-") || naTela.contains($0) }
        todas = (locais + pendenciasNuvem() + leitorTela.achados).filter { !fechadasNuvem.contains($0.id) }.sorted { $0.criado > $1.criado }
        let agora = Date().timeIntervalSince1970, espera = atraso
        // pedido da tela só aparece se ainda estiver na tela agora (aprovado durante a espera, nem aparece)
        let mostrando = Set(e.pendencias.map { $0.id })
        // "Terminou: …" é menos importante: some sozinho 10 s depois de aparecer
        for p in e.pendencias where p.tipo == "pronto" {
            if let t = mostradoEm[p.id], agora - t >= 10 { mostradoEm[p.id] = nil; resolver(p) }
        }
        let visiveis = todas.filter { agora - $0.criado >= espera &&
            (!$0.id.hasPrefix("tela-") || mostrando.contains($0.id) || leitorTela.presentes.contains($0.id)) }
        if !primeira && visiveis.map({ $0.id }) == e.pendencias.map({ $0.id }) {
            // lembrete a cada 3 min enquanto houver pedido e o Claude não estiver na frente
            if !e.pendencias.isEmpty && Date().timeIntervalSince(ultimoSom) > 180 {
                ultimoSom = Date()
                if !claudeNaFrente() { NSSound(named: "Sosumi")?.play(); pulsarAte = Date().addingTimeInterval(4) }
            }
            return
        }
        e.pendencias = visiveis
        for p in visiveis where mostradoEm[p.id] == nil { mostradoEm[p.id] = agora }
        e.escondidos.formIntersection(Set(e.pendencias.map { $0.id }))
        if !primeira && e.pendencias.contains(where: { !vistos.contains($0.id) }) {
            avisoAte = Date().addingTimeInterval(8)
            if !claudeNaFrente() { NSSound(named: "Sosumi")?.play() }
            ultimoSom = Date(); pulsarAte = Date().addingTimeInterval(4)
        }
        vistos.formUnion(e.pendencias.map { $0.id })
        primeira = false
        ajustarAltura()
    }

    func pulsar() {
        let ativo = Date() < pulsarAte
        withAnimation(.easeInOut(duration: 0.5)) { e.pulso = ativo ? !e.pulso : false }
    }

    @objc func atualizarUso() {
        buscarUso { u, erro in
            DispatchQueue.main.async {
                if let u = u { self.e.uso = u; self.e.erroUso = nil } else if self.e.uso == nil { self.e.erroUso = erro }
                self.ajustarAltura()
            }
        }
    }

    // "Abrir conversa" resolve o pedido: some da tela na hora e sai do arquivo pelo próprio `alerta fechar`
    func resolver(_ p: Pendencia) {
        e.pendencias.removeAll { $0.id == p.id }
        todas.removeAll { $0.id == p.id }
        locais.removeAll { $0.id == p.id }
        vistos.insert(p.id)
        if p.id.hasPrefix("tela-") {
            fechadasNuvem.insert(p.id); atualizarAviso(); ajustarAltura(); return
        }
        if p.id.hasPrefix("nuvem-") {
            fechadasNuvem.insert(p.id); fecharNuvem(p.id)
            atualizarAviso(); ajustarAltura(); return
        }
        let proc = Process()
        proc.executableURL = URL(fileURLWithPath: (pastaAlerta as NSString).appendingPathComponent("bin/alerta"))
        proc.arguments = ["fechar", p.id]
        proc.standardOutput = FileHandle.nullDevice; proc.standardError = FileHandle.nullDevice
        try? proc.run()
        atualizarAviso()
        ajustarAltura()
    }

    func abrirLink(_ l: String) {
        if e.aberto { alternar() }
        if !l.isEmpty, let u = URL(string: l) { NSWorkspace.shared.open(u); return }
        if let u = NSWorkspace.shared.urlForApplication(withBundleIdentifier: "com.anthropic.claudefordesktop") {
            NSWorkspace.shared.openApplication(at: u, configuration: NSWorkspace.OpenConfiguration())
        }
    }

    // ALERTA_PNG=arquivo.png: grava o painel aberto, com os dados reais, e sai (para conferir o visual)
    func capturar(_ png: String) {
        e.aberto = ProcessInfo.processInfo.environment["ALERTA_PNG_AVISO"] == nil
        DispatchQueue.main.asyncAfter(deadline: .now() + 4) {
            if !self.e.aberto { self.e.aviso = true }
            self.e.revelado = true
            if ProcessInfo.processInfo.environment["ALERTA_PNG_ICONE"] != nil { self.e.soIcone = true; self.e.aviso = false; self.e.revelado = false }
            self.ajustarAltura()
            let r = ImageRenderer(content: VStack(spacing: 6) { Compacto(e: self.e); Ilha(e: self.e) }
                .environment(\.colorScheme, .dark).padding(30).background(Color(0x1E1E20)))
            r.scale = 2
            if let img = r.nsImage, let tiff = img.tiffRepresentation, let rep = NSBitmapImageRep(data: tiff) {
                try? rep.representation(using: .png, properties: [:])?.write(to: URL(fileURLWithPath: png))
            }
            exit(0)
        }
    }

    @objc func alternarIcone(_ item: NSMenuItem) {
        e.soIcone.toggle()
        item.state = e.soIcone ? .on : .off
        var c = lerConfig(); c["barra"] = e.soIcone ? "icone" : "completa"; gravarConfig(c)
        DispatchQueue.main.async { self.ajustarAltura(animar: false) }
    }
    @objc func abrirArquivo() { NSWorkspace.shared.open(URL(fileURLWithPath: arqPendencias)) }
    @objc func sair() { NSApp.terminate(nil) }
}

let app = NSApplication.shared
app.setActivationPolicy(.accessory)
let delegado = App()
app.delegate = delegado
app.run()
