// Claude Alerta / alerta-tela: lê a janela do app Claude e grava em ~/.claude-alerta/tela.json os cartões de permissão.
//
// Threads e coordenador de projetos rodam na nuvem: os cartões "Allow Claude to…"
// não passam por hook nem arquivo no Mac. Este leitor usa a Acessibilidade para ler a janela do Claude.
//
// Ele não pede permissão nenhuma: é ligado pelo hook (`alerta hook`) de dentro de uma sessão do Claude Code, e o
// macOS atribui o processo ao próprio app Claude, que já tem a Acessibilidade ligada. Sem essa permissão, grava
// "permissao": false e sai; o próximo hook tenta de novo.
//
// Uma instância só (trava em ~/.claude-alerta/.tela.lock). `alerta-tela --uma` lê uma vez e imprime.
import AppKit
import ApplicationServices

let pasta = (NSHomeDirectory() as NSString).appendingPathComponent(".claude-alerta")
let arquivo = (pasta as NSString).appendingPathComponent("tela.json")

func texto(_ el: AXUIElement, _ attr: String) -> String? {
    var v: CFTypeRef?
    guard AXUIElementCopyAttributeValue(el, attr as CFString, &v) == .success, let s = v as? String else { return nil }
    let t = s.trimmingCharacters(in: .whitespacesAndNewlines)
    return t.isEmpty ? nil : t
}

func ler() -> [String: Any] {
    let confiavel = AXIsProcessTrusted()
    var d: [String: Any] = ["lido": Date().timeIntervalSince1970, "permissao": confiavel]
    guard confiavel else { return d }
    guard let app = NSRunningApplication.runningApplications(withBundleIdentifier: "com.anthropic.claudefordesktop").first
    else { d["claude"] = false; return d }
    d["claude"] = true
    let a = AXUIElementCreateApplication(app.processIdentifier)
    AXUIElementSetMessagingTimeout(a, 0.5)
    AXUIElementSetAttributeValue(a, "AXManualAccessibility" as CFString, kCFBooleanTrue)  // Electron expõe a página
    var v: CFTypeRef?
    guard AXUIElementCopyAttributeValue(a, kAXWindowsAttribute as CFString, &v) == .success,
          let janelas = v as? [AXUIElement] else { return d }
    var cartoes: [[String: String]] = []
    var total = 0
    for j in janelas {
        var nome = texto(j, kAXTitleAttribute) ?? "Claude"
        if nome.hasSuffix(" - Claude Code") { nome = String(nome.dropLast(" - Claude Code".count)) }
        var textos: [String] = []
        var pilha: [(AXUIElement, Int)] = [(j, 0)]
        var n = 0
        while let (el, p) = pilha.popLast(), n < 60000 {
            n += 1
            if let t = texto(el, kAXValueAttribute) ?? texto(el, kAXTitleAttribute) ?? texto(el, kAXDescriptionAttribute) {
                if textos.last != t { textos.append(t) }
            }
            guard p < 120 else { continue }
            var c: CFTypeRef?
            if AXUIElementCopyAttributeValue(el, kAXChildrenAttribute as CFString, &c) == .success,
               let filhos = c as? [AXUIElement] {
                for f in filhos.reversed() { pilha.append((f, p + 1)) }   // mantém a ordem de leitura
            }
        }
        total += textos.count
        aprenderProjeto(j)
        if NSWorkspace.shared.frontmostApplication?.processIdentifier == app.processIdentifier { resolverVistos(Set(textos)) }
        if let t = textos.first(where: { $0.hasSuffix(" - Claude Code") }) { nome = String(t.dropLast(" - Claude Code".count)) }
        for (i, t) in textos.enumerated() where t.count < 160
            && (t.hasPrefix("Allow Claude") || t.hasPrefix("Permitir que o Claude") || t.hasPrefix("Permitir ao Claude")) {
            let resto = textos[(i + 1)..<min(textos.count, i + 14)]
            guard let k = resto.firstIndex(where: { $0 == "Deny" || $0 == "Negar" || $0 == "Recusar" }) else { continue }
            // o título às vezes vem partido ("Allow Claude to connect to" + "Carlos’s MacBook Air in this thread?")
            var titulo = t, ini = i + 1
            while !(titulo.hasSuffix("?") || titulo.hasSuffix(".")) && ini < k && ini < i + 4 {
                titulo += " " + textos[ini]; ini += 1
            }
            let det = textos[ini..<k].filter { $0.count > 3 }.joined(separator: " ")
            cartoes.append(["titulo": titulo, "detalhe": String(det.prefix(220)), "janela": nome])
        }
    }
    d["cartoes"] = cartoes; d["janelas"] = janelas.count; d["textos"] = total
    return d
}

func json(_ d: [String: Any]) -> Data? { try? JSONSerialization.data(withJSONObject: d, options: [.prettyPrinted, .sortedKeys]) }


// MARK: - Banners do Claude (Central de Notificações)
// O app Claude manda banner do macOS quando uma thread de QUALQUER projeto pede algo ou termina, com o app em
// segundo plano. O banner some em segundos, mas a Central de Notificações expõe o texto pela Acessibilidade.
// Cada banner do Claude vira um pedido (`alerta abrir`), com o link do projeto quando ele é conhecido.
let arqProjetos = (pasta as NSString).appendingPathComponent("projetos.json")   // nome do projeto → chan_...
var projetos: [String: String] = {
    guard let d = FileManager.default.contents(atPath: arqProjetos),
          let j = (try? JSONSerialization.jsonObject(with: d)) as? [String: String] else { return [:] }
    return j
}()
var bannersVistos = Set<String>()
var abertosPorBanner: [(id: String, nome: String)] = []   // pedido aberto ← título da thread do banner
let log = (pasta as NSString).appendingPathComponent("tela.log")

func anotar(_ t: String) {
    let f = DateFormatter(); f.dateFormat = "dd/MM HH:mm:ss"
    let linha = f.string(from: Date()) + "  " + t + "\n"
    if let h = FileHandle(forWritingAtPath: log) { h.seekToEndOfFile(); h.write(linha.data(using: .utf8)!); try? h.close() }
    else { try? linha.write(toFile: log, atomically: true, encoding: .utf8) }
}

// página aberta na janela do Claude: guarda nome do projeto → chan_ (para montar o link dos banners)
func aprenderProjeto(_ janela: AXUIElement) {
    var pilha: [(AXUIElement, Int)] = [(janela, 0)]
    var n = 0
    while let (el, p) = pilha.popLast(), n < 400 {
        n += 1
        var v: CFTypeRef?
        if AXUIElementCopyAttributeValue(el, "AXURL" as CFString, &v) == .success, let u = v as? URL,
           let r = u.absoluteString.range(of: "/project/chan_") {
            let resto = u.absoluteString[r.upperBound...]
            let chan = "chan_" + resto.prefix { $0.isLetter || $0.isNumber || $0 == "_" }
            if var nome = texto(el, kAXTitleAttribute) ?? texto(el, kAXDescriptionAttribute) {
                if nome.hasSuffix(" - Claude Code") { nome = String(nome.dropLast(" - Claude Code".count)) }
                if projetos[nome] != chan {
                    projetos[nome] = chan
                    if let d = try? JSONSerialization.data(withJSONObject: projetos, options: [.prettyPrinted, .sortedKeys]) {
                        try? d.write(to: URL(fileURLWithPath: arqProjetos), options: .atomic)
                    }
                }
            }
            return
        }
        guard p < 14 else { continue }
        var c: CFTypeRef?
        if AXUIElementCopyAttributeValue(el, kAXChildrenAttribute as CFString, &c) == .success, let f = c as? [AXUIElement] {
            for x in f { pilha.append((x, p + 1)) }
        }
    }
}

func pendenciasAtuais() -> [[String: Any]] {
    guard let d = FileManager.default.contents(atPath: (pasta as NSString).appendingPathComponent("pendencias.json")),
          let j = (try? JSONSerialization.jsonObject(with: d)) as? [[String: Any]] else { return [] }
    return j
}

func alerta(_ args: [String]) -> String {
    let p = Process()
    p.executableURL = URL(fileURLWithPath: (pasta as NSString).appendingPathComponent("bin/alerta"))
    p.arguments = args
    // sem os ids da sessão que ligou o leitor: o pedido não é dela
    var env = ProcessInfo.processInfo.environment
    for k in ["CLAUDE_CODE_SESSION_ID", "CLAUDE_CODE_HOST_SESSION_ID", "CLAUDE_PID"] { env[k] = nil }
    p.environment = env
    let saida = Pipe(); p.standardOutput = saida; p.standardError = FileHandle.nullDevice
    do { try p.run(); p.waitUntilExit() } catch { return "" }
    return String(decoding: saida.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
}

func lerBanners() {
    guard let nc = NSRunningApplication.runningApplications(withBundleIdentifier: "com.apple.notificationcenterui").first else { return }
    let a = AXUIElementCreateApplication(nc.processIdentifier)
    AXUIElementSetMessagingTimeout(a, 0.3)
    var v: CFTypeRef?
    guard AXUIElementCopyAttributeValue(a, kAXWindowsAttribute as CFString, &v) == .success, let js = v as? [AXUIElement] else { return }
    for j in js {
        var pilha: [(AXUIElement, Int)] = [(j, 0)]
        var n = 0
        while let (el, p) = pilha.popLast(), n < 300 {
            n += 1
            let desc = texto(el, kAXDescriptionAttribute) ?? ""
            if desc.hasPrefix("Claude, ") {
                let ident = texto(el, "AXIdentifier") ?? desc
                if !bannersVistos.contains(ident) {
                    bannersVistos.insert(ident)
                    var partes: [String: String] = [:]
                    var c: CFTypeRef?
                    if AXUIElementCopyAttributeValue(el, kAXChildrenAttribute as CFString, &c) == .success, let f = c as? [AXUIElement] {
                        for x in f { if let id = texto(x, "AXIdentifier"), let t = texto(x, kAXValueAttribute) ?? texto(x, kAXTitleAttribute) ?? texto(x, kAXDescriptionAttribute) { partes[id] = t } }
                    }
                    novoBanner(titulo: partes["title"] ?? "", sub: partes["subtitle"] ?? "", corpo: partes["body"] ?? "", desc: desc)
                }
                continue
            }
            guard p < 6 else { continue }
            var c: CFTypeRef?
            if AXUIElementCopyAttributeValue(el, kAXChildrenAttribute as CFString, &c) == .success, let f = c as? [AXUIElement] {
                for x in f { pilha.append((x, p + 1)) }
            }
        }
    }
}

func novoBanner(titulo: String, sub: String, corpo: String, desc: String) {
    anotar("banner: " + desc)
    let nome = titulo.isEmpty ? "Claude" : titulo
    // já avisado pelos hooks (sessão local): não repete
    let agora = Date().timeIntervalSince1970
    for p in pendenciasAtuais() where agora - ((p["criado"] as? Double) ?? 0) < 180 {
        let t = ((p["titulo"] as? String) ?? "") + " " + ((p["detalhe"] as? String) ?? "")
        if t.contains(nome) || (!sub.isEmpty && t.contains(sub)) { anotar("  já avisado pelos hooks"); return }
    }
    let baixo = (titulo + " " + sub + " " + corpo).lowercased()
    let tipo = baixo.contains("permission") || baixo.contains("allow") || baixo.contains("connect") || baixo.contains("approv") ? "aprovacao"
        : (baixo.contains("question") || corpo.contains("?") ? "decisao" : "pronto")
    let projeto = [sub, titulo].first { projetos[$0] != nil } ?? ""
    let link = projeto.isEmpty ? "" : "claude://claude.ai/code/project/" + projetos[projeto]!
    var args = ["abrir", nome, "--detalhe", corpo.isEmpty ? sub : (sub.isEmpty ? corpo : sub + " · " + corpo),
                "--tipo", tipo, "--sessao", projeto.isEmpty ? "Claude · projeto" : projeto]
    if !link.isEmpty { args += ["--link", link] }
    let id = alerta(args)
    if !id.isEmpty { abertosPorBanner.append((id, nome)); anotar("  pedido " + id + " (" + tipo + ", link " + (link.isEmpty ? "-" : link) + ")") }
}

// você abriu a thread do banner na janela do Claude: o pedido está resolvido
func resolverVistos(_ textosDaJanela: Set<String>) {
    abertosPorBanner.removeAll { b in
        if textosDaJanela.contains(b.nome) { _ = alerta(["fechar", b.id]); anotar("resolvido na tela: " + b.nome); return true }
        return false
    }
}

if CommandLine.arguments.contains("--uma") {
    if let data = json(ler()) { print(String(data: data, encoding: .utf8) ?? "") }
    exit(0)
}

let trava = open((pasta as NSString).appendingPathComponent(".tela.lock"), O_CREAT | O_RDWR, 0o644)
guard trava >= 0, flock(trava, LOCK_EX | LOCK_NB) == 0 else { exit(0) }   // já tem um rodando
var volta = 0
while true {
    var sair = false
    autoreleasepool {
        if volta % 3 == 0 {   // janela do Claude a cada 3 s
            let d = ler()
            if let data = json(d) { try? data.write(to: URL(fileURLWithPath: arquivo), options: .atomic) }
            sair = (d["permissao"] as? Bool) != true
        }
        if !sair { lerBanners() }   // banners a cada 1 s (eles somem em poucos segundos)
    }
    if sair { exit(0) }
    volta += 1
    Thread.sleep(forTimeInterval: 1)
}
