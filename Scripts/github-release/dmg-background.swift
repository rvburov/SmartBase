// Фон окна установочного образа .dmg — та самая инструкция, которую человек читает
// РАНЬШЕ, чем увидит предупреждение Gatekeeper. Приложение само её показать не может:
// Gatekeeper блокирует запуск до первой строчки нашего кода.
//
//   swift Scripts/github-release/dmg-background.swift <папка-вывода>
//
// Пишет background.png (640×420) и background@2x.png (1280×840); в образ кладётся
// склеенный из них TIFF, иначе на retina текст выходит мылом.
//
// Рисуется кодом, а не лежит картинкой в репозитории: текст инструкции правится как
// обычный исходник, в диффах видно, что именно поменялось, и бинарных блобов не заводится.
// Из инструментов нужен только swift — он есть и на macOS-раннере GitHub, и локально.
import AppKit

let W: CGFloat = 640, H: CGFloat = 470

// Верхняя треть отдана значку установщика — его туда ставит Finder (см. osascript
// в шаге упаковки). Рисовать здесь ничего нельзя, иначе перекроется.
let iconZoneBottom: CGFloat = 165

let ink       = NSColor(calibratedRed: 0.11, green: 0.12, blue: 0.14, alpha: 1)
let inkSoft   = NSColor(calibratedRed: 0.40, green: 0.42, blue: 0.46, alpha: 1)
let accent    = NSColor(calibratedRed: 0.15, green: 0.42, blue: 0.85, alpha: 1)
let hairline  = NSColor(calibratedRed: 0.85, green: 0.86, blue: 0.88, alpha: 1)
let backdrop  = NSColor(calibratedRed: 0.98, green: 0.98, blue: 0.99, alpha: 1)

// Координаты в тексте задаются от ВЕРХА окна — так же, как их задаёт Finder значку.
// NSGraphicsContext считает от низа, поэтому каждая координата переворачивается.
func fromTop(_ y: CGFloat) -> CGFloat { H - y }

func text(_ s: String, _ font: NSFont, _ color: NSColor,
          x: CGFloat, top: CGFloat, width: CGFloat, centered: Bool = false) {
    let p = NSMutableParagraphStyle()
    p.alignment = centered ? .center : .left
    p.lineSpacing = 3
    let attrs: [NSAttributedString.Key: Any] = [.font: font, .foregroundColor: color, .paragraphStyle: p]
    let a = NSAttributedString(string: s, attributes: attrs)
    let h = a.boundingRect(with: NSSize(width: width, height: 400), options: [.usesLineFragmentOrigin]).height
    a.draw(with: NSRect(x: x, y: fromTop(top) - h, width: width, height: h),
           options: [.usesLineFragmentOrigin])
}

func step(_ n: Int, _ s: String, top: CGFloat) {
    let d: CGFloat = 22, x: CGFloat = 56
    let circle = NSRect(x: x, y: fromTop(top + 18), width: d, height: d)
    accent.setFill()
    NSBezierPath(ovalIn: circle).fill()

    // Цифру нельзя просто положить по центру строки: в строку заложено место под нижние
    // выносные элементы (у «р», «у»), а у цифр их нет — глиф всплывает над серединой.
    // Считаем по метрикам шрифта: низ цифры — на базовой линии, верх — на высоте прописной,
    // значит видимый центр лежит на capHeight/2 выше базовой линии.
    let f = NSFont.systemFont(ofSize: 12, weight: .semibold)
    let p = NSMutableParagraphStyle(); p.alignment = .center
    let glyph = NSAttributedString(string: "\(n)",
        attributes: [.font: f, .foregroundColor: NSColor.white, .paragraphStyle: p])
    let lineHeight = glyph.size().height
    glyph.draw(in: NSRect(x: circle.minX, y: circle.midY + f.descender - f.capHeight / 2,
                          width: d, height: lineHeight))

    text(s, .systemFont(ofSize: 14, weight: .regular), ink,
         x: x + d + 14, top: top, width: W - x - d - 14 - 56)
}

func render(scale: CGFloat) -> Data {
    let rep = NSBitmapImageRep(
        bitmapDataPlanes: nil, pixelsWide: Int(W * scale), pixelsHigh: Int(H * scale),
        bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
        colorSpaceName: .calibratedRGB, bytesPerRow: 0, bitsPerPixel: 0)!
    rep.size = NSSize(width: W, height: H)

    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)

    backdrop.setFill()
    NSRect(x: 0, y: 0, width: W, height: H).fill()

    hairline.setFill()
    NSRect(x: 56, y: fromTop(iconZoneBottom), width: W - 112, height: 1).fill()

    text("Как запустить", .systemFont(ofSize: 17, weight: .semibold), ink,
         x: 56, top: iconZoneBottom + 30, width: W - 112)

    // Формулировки и названия кнопок — дословно те, что показывает macOS 26.
    step(1, "Двойной клик по значку выше.", top: 244)
    step(2, "Появится окно про проверку программы.\nНажмите «Готово».", top: 280)
    step(3, "Откройте Системные настройки ▸ Конфиденциальность\nи безопасность и прокрутите вниз до «Безопасность».", top: 340)
    step(4, "Там будет строка с названием программы —\nнажмите рядом «Все равно открыть» и подтвердите.", top: 400)

    NSGraphicsContext.restoreGraphicsState()
    return rep.representation(using: .png, properties: [:])!
}

let out = CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : "."
for (scale, name) in [(CGFloat(1), "background.png"), (CGFloat(2), "background@2x.png")] {
    let url = URL(fileURLWithPath: out).appendingPathComponent(name)
    try! render(scale: scale).write(to: url)
    print("нарисовано: \(url.path)")
}
