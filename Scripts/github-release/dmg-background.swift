// Фон окна установочного образа .dmg — та самая инструкция, которую человек читает
// РАНЬШЕ, чем увидит предупреждение Gatekeeper. Приложение само её показать не может:
// Gatekeeper блокирует запуск до первой строчки нашего кода.
//
//   swift Scripts/github-release/dmg-background.swift <папка-вывода>
//
// Пишет background.png (640×470) и background@2x.png (1280×940); в образ кладётся
// склеенный из них TIFF, иначе на retina текст выходит мылом.
//
// Рисуется кодом, а не лежит картинкой в репозитории: текст инструкции правится как
// обычный исходник, в диффах видно, что именно поменялось, и бинарных блобов не заводится.
// Из инструментов нужен только swift — он есть и на macOS-раннере GitHub, и локально.
import AppKit

let W: CGFloat = 640, H: CGFloat = 470

// Значок ставит Finder (см. osascript в шаге упаковки), позиция {320, 78}. При размере 128
// он занимает примерно 14…142, подпись под ним — до 164. Рисовать здесь нечего.
let iconLabelBottom: CGFloat = 164

// Разделитель и заголовок отбиты от подписи значка на одинаковое расстояние — иначе линия
// наезжает на «SmartBase-Setup.app», а блоки выглядят слипшимися.
let gap: CGFloat        = 24
let hairlineTop         = iconLabelBottom + gap        // 188
let headingTop          = hairlineTop + gap            // 212

// Весь текст умещается в верхние ~360 точек, ниже — namеренно пустой запас.
// Причина: Finder показывает панель вкладок и панель пути, если они включены у пользователя,
// и они съедают высоту окна. Управлять ими из AppleScript нельзя. Поэтому картинка выше
// видимой области, и обрезается только пустое место, а не инструкция.
let firstStepTop: CGFloat = 252
let stepStride: CGFloat   = 30

let ink      = NSColor(calibratedRed: 0.11, green: 0.12, blue: 0.14, alpha: 1)
let hairline = NSColor(calibratedRed: 0.85, green: 0.86, blue: 0.88, alpha: 1)
let backdrop = NSColor(calibratedRed: 0.98, green: 0.98, blue: 0.99, alpha: 1)

// Координаты задаются от ВЕРХА окна — так же, как их задаёт Finder значку.
// NSGraphicsContext считает от низа, поэтому каждая переворачивается.
func fromTop(_ y: CGFloat) -> CGFloat { H - y }

func text(_ s: String, _ font: NSFont, x: CGFloat, top: CGFloat, width: CGFloat) {
    let p = NSMutableParagraphStyle()
    p.lineSpacing = 3
    let a = NSAttributedString(string: s, attributes: [
        .font: font, .foregroundColor: ink, .paragraphStyle: p,
    ])
    let h = a.boundingRect(with: NSSize(width: width, height: 400),
                           options: [.usesLineFragmentOrigin]).height
    a.draw(with: NSRect(x: x, y: fromTop(top) - h, width: width, height: h),
           options: [.usesLineFragmentOrigin])
}

// Обычный нумерованный список: «1.» и текст, без кружков и прочей графики.
// Номер и текст рисуются отдельно, чтобы перенос строки вставал под текст, а не под номер.
func step(_ n: Int, _ s: String, top: CGFloat) {
    let x: CGFloat = 56, numWidth: CGFloat = 22
    let font = NSFont.systemFont(ofSize: 14, weight: .regular)
    text("\(n).", font, x: x, top: top, width: numWidth)
    text(s, font, x: x + numWidth, top: top, width: W - x - numWidth - 56)
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
    NSRect(x: 56, y: fromTop(hairlineTop), width: W - 112, height: 1).fill()

    text("Как запустить", .systemFont(ofSize: 17, weight: .semibold),
         x: 56, top: headingTop, width: W - 112)

    // Каждый пункт — одна строка: так список не разъезжается по высоте и целиком
    // помещается в видимую часть окна. Названия кнопок дословно из интерфейса macOS 26.
    let steps = [
        "Двойной клик по значку выше.",
        "Появится окно про проверку — нажмите «Готово».",
        "Системные настройки ▸ Конфиденциальность и безопасность.",
        "Прокрутите вниз до «Безопасность» → «Все равно открыть».",
    ]
    for (i, s) in steps.enumerated() {
        step(i + 1, s, top: firstStepTop + CGFloat(i) * stepStride)
    }

    NSGraphicsContext.restoreGraphicsState()
    return rep.representation(using: .png, properties: [:])!
}

let out = CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : "."
for (scale, name) in [(CGFloat(1), "background.png"), (CGFloat(2), "background@2x.png")] {
    let url = URL(fileURLWithPath: out).appendingPathComponent(name)
    try! render(scale: scale).write(to: url)
    print("нарисовано: \(url.path)")
}
