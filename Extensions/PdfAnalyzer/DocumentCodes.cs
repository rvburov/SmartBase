using System.Text.RegularExpressions;

namespace SmartBase.Extensions.PdfAnalyzer;

/// <summary>
/// Справочные данные ЕСКД/ЕСПД (ГОСТ 2.201 / 19.101 / 19.103) и разбор обозначений документов
/// утилиты «Анализ PDF файлов». Только публичные коды/правила (не секрет):
/// коды КД/ПД и их названия, порядок сортировки, извлечение номеров КД/ПД/ТД из имени файла
/// и формирование полного наименования документа. Чистая логика без UI и состояния, кроме
/// кэша названий изделий, который передаётся явно.
///
/// <para>Как формируется децимальный номер (по нему определяется ТИП документа):</para>
/// <code>
///   ТД (технолог.):   АААА . 00000 . 00000 
///                     └─┬─┘  └──┬─┘  └──┬─┘      строгий формат, без кодов-приставок
///                     орг.    номер   номер
///
///   КД (ГОСТ 2.201):  АААА . 000000 . 000 [-исполн.] [КОД] [-ЛУ|-УЛ]
///                     └─┬─┘  └──┬──┘ └┬┘             └─┬─┘  └───┬───┘
///                     орг.    номер  версия      КОД документа  лист утв./
///                                                (СБ, Э3, ПЗ…)  удост. лист
///                                                      └ определяет тип → KdNameMapping / KdOrder
///
///   ПД (ГОСТ 19.103): КОДО . 00000 - ИИ  ВВ  ДД [-Ч] [-ЛУ|-УЛ]
///                     └─┬─┘  └──┬─┘ └┬┘ └┬┘ └┬┘  └┬┘  └───┬───┘
///                     орг.    рег.№ ред. ВИД  №   часть  лист утв./
///                     (4 симв)(5 цф)(01-99)│ док-та(1-9) удост. лист
///                                          │  (01-99)
///                                          └ КОД ВИДА документа (ГОСТ 19.101) → PdVidCodes
/// </code>
/// </summary>
public static class DocumentCodes
{
    private static readonly string[] SuffixCodes = ["-ЛУ", "-УЛ", "ЛУ", "УЛ"];

    // ══════════════════════════════════════════════════════════════════════════
    //  КД — конструкторская документация (ЕСКД, ГОСТ 2.201/2.102)
    // ──────────────────────────────────────────────────────────────────────────
    //  Тип документа задаётся КОДом-приставкой в конце номера АААА.000000.000[…КОД]
    //  (см. шпаргалку в summary класса). Ниже — коды сгруппированы по классам
    //  документов (тех. проект → чертежи → схемы → перечни → ведомости → ТУ →
    //  эксплуатационные → удостоверяющие/утверждающие листы → дополнительные).
    /// <summary>Коды КД → название документа. Индексатор-инициализатор: дубликат ключа перезаписывает (как в оригинале).</summary>
    public static readonly Dictionary<string, string> KdNameMapping = new()
    {
        // === ТЕХ. ПРОЕКТ ===
        ["ТП"] = "Ведомость технического проекта", ["ПЗ"] = "Пояснительная записка",
        ["Е1"] = "Схема деления структурная", ["ВО"] = "Чертеж общего вида",
        // === КД ===
        ["СБ"] = "Сборочный чертеж", ["ГЧ"] = "Габаритный чертеж", ["МЭ"] = "Электромонтажный чертеж",
        ["МЧ"] = "Монтажный чертеж", ["УЧ"] = "Упаковочный чертеж",
        ["Э0"] = "Схема электрическая объединения", ["Э1"] = "Схема электрическая структурная",
        ["Э2"] = "Схема электрическая функциональная", ["Э3"] = "Схема электрическая принципиальная",
        ["Э4"] = "Схема электрическая соединений", ["Э5"] = "Схема электрическая подключения",
        ["Э6"] = "Схема электрическая общая", ["Э7"] = "Схема электрическая расположения",
        ["ПЭ0"] = "Перечень элементов", ["ПЭ1"] = "Перечень элементов", ["ПЭ2"] = "Перечень элементов",
        ["ПЭ3"] = "Перечень элементов", ["ПЭ4"] = "Перечень элементов", ["ПЭ5"] = "Перечень элементов",
        ["ПЭ6"] = "Перечень элементов", ["ПЭ7"] = "Перечень элементов",
        ["С7"] = "Схема электрокинематическая расположения", ["ПС7"] = "Перечень элементов",
        ["ВС"] = "Ведомость спецификаций", ["ВД"] = "Ведомость ссылочных документов",
        ["ВП"] = "Ведомость покупных изделий", ["ДП"] = "Ведомость держателей подлинников",
        ["ВДЭ"] = "Ведомость электронных документов", ["ВЭД"] = "Ведомость электронных документов",
        ["ТБ"] = "Таблица соединений", ["Д23"] = "Технические требования", ["Д27"] = "Перечень параметров",
        ["Д70"] = "Карта прокладки кабелей", ["Д1"] = "Маркировка", ["ТУ"] = "Технические условия",
        ["ТУ1"] = "Технические условия Часть 2", ["ТУ2"] = "Технические условия Часть 3",
        ["ТУ3"] = "Технические условия Часть 4", ["ТУ4"] = "Технические условия Часть 5",
        ["ТУ5"] = "Технические условия Часть 6", ["ТУ6"] = "Технические условия Часть 7",
        ["ТУ7"] = "Технические условия Часть 8", ["ТУ8"] = "Технические условия Часть 9",
        ["ТУ9"] = "Технические условия Часть 10", ["РР"] = "Расчет надежности",
        // === ЭД ===
        ["ВЭ"] = "Ведомость эксплуатационных документов", ["РЭ"] = "Руководство по технической эксплуатации",
        ["РЭ1"] = "Руководство по технической эксплуатации Часть 2", ["РЭ2"] = "Руководство по технической эксплуатации Часть 3",
        ["РЭ3"] = "Руководство по технической эксплуатации Часть 4", ["РЭ4"] = "Руководство по технической эксплуатации Часть 5",
        ["РЭ5"] = "Руководство по технической эксплуатации Часть 6", ["РЭ6"] = "Руководство по технической эксплуатации Часть 7",
        ["РЭ7"] = "Руководство по технической эксплуатации Часть 8", ["РЭ8"] = "Руководство по технической эксплуатации Часть 9",
        ["РЭ9"] = "Руководство по технической эксплуатации Часть 10", ["РЭ10"] = "Руководство по технической эксплуатации Часть 11",
        ["РЭ11"] = "Руководство по технической эксплуатации Часть 12", ["РЭ12"] = "Руководство по технической эксплуатации Часть 13",
        ["РЭ13"] = "Руководство по технической эксплуатации Часть 14", ["РЭ14"] = "Руководство по технической эксплуатации Часть 15",
        ["РЭ15"] = "Руководство по технической эксплуатации Часть 16", ["РО"] = "Регламент технического обслуживания",
        ["ФО"] = "Формуляр", ["ФО1"] = "Формуляр Часть 2", ["ФО2"] = "Формуляр Часть 3", ["ФО3"] = "Формуляр Часть 4",
        ["ФО4"] = "Формуляр Часть 5", ["ФО5"] = "Формуляр Часть 6", ["ФО6"] = "Формуляр Часть 7",
        ["ФО7"] = "Формуляр Часть 8", ["ФО8"] = "Формуляр Часть 9", ["ФО9"] = "Формуляр Часть 10",
        ["ПС"] = "Паспорт", ["ЭТ"] = "Этикетка", ["ЗИ"] = "Ведомость ЗИП", ["И3"] = "Руководство оператора КСЗИ",
        ["ЭД"] = "Ведомость эксплуатационных документов", ["ВЗ"] = "Ведомость ЗИП",
        ["НЗ"] = "Нормы расхода запасных частей", ["НМ"] = "Нормы расхода материалов",
        ["Д90"] = "Учебный видеофильм", ["УП"] = "Учебно-технические плакаты",
        ["И10"] = "Инструкция по транспортированию", ["РЛЭ"] = "Руководство по летной эксплуатации",
        ["ВКЭ"] = "Ведомость эксплуатационных комплектов запасных частей, инструмента, принадлежностей и материалов",
        ["НЭ"] = "Нормы расхода предметов снабжения (эксплуатационные)", ["АЭ"] = "Альбом электрических схем",
        ["ИС"] = "Инструкция эксплуатационная специальная",
        ["ИС1"] = "Инструкция эксплуатационная специальная Часть 2", ["ИС2"] = "Инструкция эксплуатационная специальная Часть 3",
        ["ИС3"] = "Инструкция эксплуатационная специальная Часть 4", ["ИС4"] = "Инструкция эксплуатационная специальная Часть 5",
        ["ИС5"] = "Инструкция эксплуатационная специальная Часть 6", ["ИС6"] = "Инструкция эксплуатационная специальная Часть 7",
        ["ИС7"] = "Инструкция эксплуатационная специальная Часть 8", ["ИС8"] = "Инструкция эксплуатационная специальная Часть 9",
        ["ИС9"] = "Инструкция эксплуатационная специальная Часть 10",
        // === УДОСТОВЕРЯЮЩИЕ ЛИСТЫ ===
        ["Э3-УЛ"] = "Схема электрическая принципиальная Удостоверяющий лист",
        ["Д33-УЛ"] = "Данные проектирования Удостоверяющий лист",
        ["Д42-УЛ"] = "Данные программирования Информационно-удостоверяющий лист",
        ["Д42.1-УЛ"] = "Данные программирования Информационно-удостоверяющий лист",
        ["Д42.2-УЛ"] = "Данные программирования Информационно-удостоверяющий лист",
        ["Д50-УЛ"] = "Данные проектирования Информационно-удостоверяющий лист",
        ["МД-УЛ"] = "Электронная модель детали Информационно-удостоверяющий лист",
        ["ЭМД-УЛ"] = "Электронная модель детали Удостоверяющий лист",
        ["МС-УЛ"] = "Электронная модель сборочной единицы Информационно-удостоверяющий лист",
        ["ТУ-ЛУ"] = "Технические условия Лист утверждения",
        ["НДЗ-УЛ"] = "Загрузочный диск Информационно-удостоверяющий лист",
        ["НД-УЛ"] = "Текст программы Информационно-удостоверяющий лист",
        ["Д23-УЛ"] = "Технические требования Лист утверждения",
        ["ВЭ-ЛУ"] = "Ведомость эксплуатационных документов Лист утверждения",
        ["ПС-ЛУ"] = "Паспорт Лист утверждения",
        ["ЭСБ-УЛ"] = "Электронная модель сборочной единицы Удостоверяющий лист",
        // === ЛИСТЫ УТВЕРЖДЕНИЯ ===
        ["-ЛУ"] = "Лист утверждения", ["-УЛ"] = "Информационно-удостоверяющий лист",
        // === ДОПОЛНИТЕЛЬНЫЕ ===
        ["ТЧ"] = "Чертеж теоретический", ["ЭМ"] = "Схема электрическая монтажная",
        ["ПЭ"] = "Перечень элементов (общий)", ["ПМ"] = "Программа и методика испытаний",
        ["Д33"] = "Ведомость документов", ["Д42"] = "Текст программы", ["Д50"] = "Описание применения",
        ["ЗИП-О"] = "Ведомость ЗИП одиночного изделия", ["И"] = "Инструкция", ["А0"] = "Альбом чертежей",
        ["А1"] = "Спецификация альбома чертежей", ["А2"] = "Ведомость альбома чертежей",
        ["А3"] = "Пояснительная записка альбома чертежей", ["А4"] = "Технические условия альбома чертежей",
        ["А5"] = "Программа и методика испытаний альбома чертежей", ["А6"] = "Таблицы альбома чертежей",
        ["А7"] = "Расчеты альбома чертежей", ["А8"] = "Документы прочие альбома чертежей",
        ["А9"] = "Инструкция альбома чертежей", ["РК"] = "Руководство по ремонту", ["КД"] = "Комплект документов",
        ["ДЭ"] = "Документация эксплуатационная", ["МС"] = "Схема монтажная",
        ["ВКР"] = "Ведомость ремонтных комплектов запасных частей, инструмента, принадлежностей и материалов",
        ["УЛ"] = "Информационно-удостоверяющий лист", ["НДЗ"] = "Загрузочный диск",
        ["НД"] = "Текст программы (на носителе)", ["ОП"] = "Описание программы",
    };

    // ══════════════════════════════════════════════════════════════════════════
    //  ПД — программная документация (ЕСПД, ГОСТ 19.101 / 19.103)
    // ──────────────────────────────────────────────────────────────────────────
    //  Тип документа задаётся 2-значным «кодом вида» ВВ в номере
    //  КОДО.РРРРР-ИИ ВВ ДД (см. шпаргалку в summary класса). Номер БЕЗ кода вида
    //  (КОДО.РРРРР-ИИ) — это сама программа → PdBaseTypeName. Носители …НД/…НДЗ
    //  описаны кодами НД/НДЗ в KdNameMapping (тип берётся оттуда как у КД-кода).

    /// <summary>Наименование документа для номера без кода вида (сама программа/спецификация).</summary>
    public const string PdBaseTypeName = "Спецификация";

    /// <summary>Наименование для кодов вида 90–99 (прочие документы, ГОСТ 19.101).</summary>
    public const string PdOtherTypeName = "Прочие документы";

    /// <summary>Коды видов программных документов (ГОСТ 19.101, поле «ВВ») → наименование.</summary>
    public static readonly Dictionary<string, string> PdVidCodes = new()
    {
        ["05"] = "Ведомость держателей подлинников",
        ["12"] = "Текст программы",
        ["13"] = "Описание программы",
        ["20"] = "Ведомость эксплуатационных документов",
        ["30"] = "Формуляр",
        ["31"] = "Описание применения",
        ["32"] = "Руководство системного программиста",
        ["33"] = "Руководство программиста",
        ["34"] = "Руководство оператора",
        ["35"] = "Описание языка",
        ["46"] = "Руководство по техническому обслуживанию",
        ["51"] = "Программа и методика испытаний",
        ["81"] = "Пояснительная записка",
        // 90–99 — прочие документы (обрабатываются диапазоном, см. PdOtherTypeName).
    };

    // Разбор ПД-номера ДЛЯ ТИПА (ResolvePdType): базовый номер + опц. код вида «ВВ» (за ним номер
    // документа ДД), опц. часть «-Ч», опц. лист «-ЛУ/-УЛ». Носители …НД/…НДЗ сюда НЕ подходят —
    // их тип даёт код НД/НДЗ из KdNameMapping, поэтому здесь они (намеренно) не матчатся.
    private static readonly Regex PdDesignationRx = new(
        @"^(?<base>[A-ZА-Я]{4}\.\d{5}-\d{2})(?:\s+(?<vid>\d{2})\s+\d{2})?(?:-\d)?(?:-(?:ЛУ|УЛ))?$",
        RegexOptions.Compiled);

    // Разбор ДЛЯ БАЗЫ ИЗДЕЛИЯ (GetPdProgramBase): то же, но ВКЛЮЧАЯ носители …НД/…НДЗ(-УЛ) —
    // изделие у носителей общее с программой, поэтому база = КОДО.РРРРР-ИИ (с редакцией!).
    private static readonly Regex PdProgramBaseRx = new(
        @"^(?<base>[A-ZА-Я]{4}\.\d{5}-\d{2})(?:\s+\d{2}\s+\d{2})?(?:НДЗ|НД)?(?:-\d)?(?:-(?:ЛУ|УЛ))?$",
        RegexOptions.Compiled);

    /// <summary>Тип программного документа (ГОСТ 19.101) по его децимальному номеру; "" — если это не ПД.</summary>
    public static string ResolvePdType(string? designation)
    {
        if (string.IsNullOrEmpty(designation)) return "";
        var m = PdDesignationRx.Match(designation);
        if (!m.Success) return "";
        var vid = m.Groups["vid"].Value;
        if (vid.Length == 0) return PdBaseTypeName;                       // без кода вида → спецификация
        if (PdVidCodes.TryGetValue(vid, out var name)) return name;       // известный вид
        if (int.TryParse(vid, out var n) && n is >= 90 and <= 99) return PdOtherTypeName;
        return "";                                                        // неизвестный код вида
    }

    /// <summary>Базовый номер программы КОДО.РРРРР-ИИ (без вида/номера/части/листа, но с редакцией);
    /// "" — если это не ПД. Матчит и носители …НД/…НДЗ — у них изделие общее с программой.</summary>
    public static string GetPdProgramBase(string? designation)
    {
        if (string.IsNullOrEmpty(designation)) return "";
        var m = PdProgramBaseRx.Match(designation);
        return m.Success ? m.Groups["base"].Value : "";
    }

    // Все известные НАЗВАНИЯ ВИДОВ документов (КД + ПД) — имя изделия не может им совпадать
    // (иначе тип «протёк» бы в слот изделия: напр. файл «…12 01 Текст программы.pdf»).
    private static readonly HashSet<string> KnownDocTypeNames = BuildKnownDocTypeNames();

    private static HashSet<string> BuildKnownDocTypeNames()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var v in KdNameMapping.Values) set.Add(v);
        foreach (var v in PdVidCodes.Values) set.Add(v);
        set.Add(PdBaseTypeName);
        set.Add(PdOtherTypeName);
        return set;
    }

    /// <summary>Является ли строка названием вида документа (а не именем изделия).</summary>
    public static bool IsDocTypeName(string? name) => name is not null && KnownDocTypeNames.Contains(name);

    /// <summary>Порядок кодов КД для сортировки (с производными -ЛУ/-УЛ = order+0.1/+0.2).</summary>
    public static readonly Dictionary<string, double> KdOrder = BuildKdOrder();

    private static Dictionary<string, double> BuildKdOrder()
    {
        var b = new Dictionary<string, double>
        {
            ["СПЕЦИФИКАЦИЯ"] = 0,
            ["ТП"] = 1, ["ПЗ"] = 2, ["Е1"] = 3, ["ВО"] = 4,
            ["СБ"] = 10, ["ГЧ"] = 11, ["МЭ"] = 12, ["МЧ"] = 13, ["УЧ"] = 14,
            ["Э0"] = 15, ["Э1"] = 16, ["Э2"] = 17, ["Э3"] = 18, ["Э4"] = 19, ["Э5"] = 20, ["Э6"] = 21, ["Э7"] = 23,
            ["ПЭ0"] = 24, ["ПЭ1"] = 25, ["ПЭ2"] = 26, ["ПЭ3"] = 27, ["ПЭ4"] = 28, ["ПЭ5"] = 29, ["ПЭ6"] = 30, ["ПЭ7"] = 31,
            ["С7"] = 32, ["ПС7"] = 33, ["ВС"] = 34, ["ВД"] = 35, ["ВП"] = 36, ["ДП"] = 37, ["ВДЭ"] = 38, ["ВЭД"] = 39,
            ["ТБ"] = 40, ["Д23"] = 41, ["Д27"] = 42, ["Д1"] = 43,
            ["ТУ"] = 44, ["ТУ1"] = 45, ["ТУ2"] = 46, ["ТУ3"] = 47, ["ТУ4"] = 48, ["ТУ5"] = 49, ["ТУ6"] = 50,
            ["ТУ7"] = 51, ["ТУ8"] = 52, ["ТУ9"] = 53, ["РР"] = 54,
            ["ВЭ"] = 60, ["РЭ"] = 61, ["РЭ1"] = 62, ["РЭ2"] = 63, ["РЭ3"] = 64, ["РЭ4"] = 65, ["РЭ5"] = 66, ["РЭ6"] = 67,
            ["РЭ7"] = 68, ["РЭ8"] = 69, ["РЭ9"] = 70, ["РЭ10"] = 71, ["РЭ11"] = 72, ["РЭ12"] = 73, ["РЭ13"] = 74,
            ["РЭ14"] = 75, ["РЭ15"] = 76, ["РО"] = 77, ["ФО"] = 78, ["ФО1"] = 79, ["ФО2"] = 80, ["ФО3"] = 81,
            ["ФО4"] = 82, ["ФО5"] = 83,
            ["ИС"] = 96, ["ИС1"] = 97, ["ИС2"] = 98, ["ИС3"] = 99, ["ИС4"] = 100, ["ИС5"] = 101, ["ИС6"] = 102,
            ["ИС7"] = 103, ["ИС8"] = 104, ["ИС9"] = 105,
        };
        var ext = new Dictionary<string, double>(b);
        foreach (var (code, order) in b)
            if (Array.IndexOf(SuffixCodes, code) < 0)
            {
                ext[$"{code}-ЛУ"] = order + 0.1;
                ext[$"{code}-УЛ"] = order + 0.2;
            }
        return ext;
    }

    // Коды КД (без суффиксов), отсортированные по длине убыв. — для срезания кода с конца обозначения.
    // ПД-номера (в т.ч. носители НД/НДЗ) обрабатываются отдельно: их коды-носители уже есть в
    // KdNameMapping, а «программные» части номера — через PdDesignationRx / GetPdProgramBase.
    private static readonly string[] AllCodesByLenDesc = BuildAllCodes();

    private static string[] BuildAllCodes()
    {
        return KdNameMapping.Keys
                  .Where(c => Array.IndexOf(SuffixCodes, c) < 0)
                  .Distinct()
                  .OrderByDescending(c => c.Length)
                  .ToArray();
    }

    /// <summary>Базовый номер обозначения без кода документа и без суффикса исполнения.</summary>
    public static string GetBaseDesignation(string? designation)
    {
        if (string.IsNullOrEmpty(designation)) return "";

        // ПД (ГОСТ 19.103): базовый номер программы = КОДО.РРРРР-ИИ (без вида/номера/части/листа).
        var pdBase = GetPdProgramBase(designation);
        if (pdBase.Length > 0) return pdBase;

        var clean = designation;
        if (clean.EndsWith("-ЛУ") || clean.EndsWith("-УЛ")) clean = clean[..^3];

        var temp = clean;
        foreach (var code in AllCodesByLenDesc)
        {
            if (temp.EndsWith($"-{code}")) { temp = temp[..^($"-{code}".Length)]; break; }
            if (temp.EndsWith(code) && temp != code) { temp = temp[..^code.Length]; break; }
        }

        var m = Regex.Match(temp, @"(\.[0-9]{3,4})-\d+$");
        if (m.Success) temp = temp[..(m.Groups[1].Index + m.Groups[1].Length)];

        return temp.TrimEnd('-');
    }

    /// <summary>Извлекает базовый номер и код документа из обозначения.</summary>
    public static (string Base, string? Code) ExtractBaseAndCode(string? designation)
    {
        if (string.IsNullOrEmpty(designation)) return (designation ?? "", null);

        string? found = null;
        var baseStr = designation;

        foreach (var code in AllCodesByLenDesc)
        {
            if (designation.EndsWith(code)) { found = code; baseStr = designation[..^code.Length]; break; }
            if (designation.EndsWith($"-{code}")) { found = code; baseStr = designation[..^($"-{code}".Length)]; break; }
        }

        if (found is null)
        {
            var m = Regex.Match(designation, @"^([A-ZА-Я]{4}\.\d{6}\.\d{3,4}(?:-\d+)?)");
            if (m.Success)
            {
                baseStr = m.Groups[1].Value;
                var remainder = designation[baseStr.Length..].TrimStart('-');
                if (remainder.Length > 0 && Array.IndexOf(AllCodesByLenDesc, remainder) >= 0)
                    found = remainder;
                else if (remainder.Length > 0)
                    foreach (var code in AllCodesByLenDesc)
                        if (remainder.StartsWith(code)) { found = code; baseStr = designation[..^remainder.Length]; break; }
            }
        }

        return (baseStr.TrimEnd('-'), found);
    }

    /// <summary>Полное наименование одной строкой: [название изделия] + [тип документа] + [суффикс].</summary>
    public static string GetDocumentFullName(
        string? designation, string fileName, IReadOnlyDictionary<string, string>? allDesignations,
        Dictionary<string, string> cache)
        => JoinParts(GetDocumentFullNameParts(designation, fileName, allDesignations, cache), " ");

    /// <summary>То же наименование, но каждая часть (изделие / тип / лист) — с новой строки.</summary>
    public static string GetDocumentFullNameMultiline(
        string? designation, string fileName, IReadOnlyDictionary<string, string>? allDesignations,
        Dictionary<string, string> cache)
        => JoinParts(GetDocumentFullNameParts(designation, fileName, allDesignations, cache), "\n");

    private static string JoinParts((string Product, string DocType, string Suffix) p, string sep)
        => string.Join(sep, new[] { p.Product, p.DocType, p.Suffix }.Where(s => s.Length > 0)).Trim();

    /// <summary>
    /// Три части наименования по отдельности: [название изделия], [тип документа], [суффикс листа].
    /// Пустые части = "" и пропускаются при склейке; склейка пробелом эквивалентна прежнему
    /// «полному наименованию» — отсюда же и однострочный, и многострочный варианты.
    /// </summary>
    public static (string Product, string DocType, string Suffix) GetDocumentFullNameParts(
        string? designation, string fileName, IReadOnlyDictionary<string, string>? allDesignations,
        Dictionary<string, string> cache)
    {
        if (string.IsNullOrEmpty(designation)) return (fileName, "", "");

        // Исключения (удостоверяющие листы) — приоритетная обработка.
        // ВАЖНО: голые "-ЛУ"/"-УЛ" сюда НЕ берём — это не конкретная комбинация «код+лист»,
        // а общий суффикс. Его должна обработать обычная ветка ниже, чтобы тип основного
        // документа (напр. «Сборочный чертеж») попал в наименование вместе с «Лист утверждения».
        // Конкретные же комбинации (Д42-УЛ, ТУ-ЛУ…) остаются приоритетом — у них имена-переопределения.
        var exceptions = KdNameMapping.Keys
            .Where(c => (c.EndsWith("-УЛ") || c.EndsWith("-ЛУ")) && Array.IndexOf(SuffixCodes, c) < 0)
            .OrderByDescending(c => c.Length).ToArray();

        foreach (var exc in exceptions)
        {
            if (!designation.EndsWith(exc)) continue;
            var docType = KdNameMapping[exc];
            // У спец. комбинаций тип и лист хранятся одной строкой — разделяем по известному окончанию.
            var (excType, excSheet) = SplitSheetSuffix(docType);
            var head = designation[..^exc.Length];
            // ПД (в т.ч. носители НД-УЛ/НДЗ-УЛ): база изделия = программный номер КОДО.РРРРР-ИИ (с
            // редакцией). Для КД — прежняя логика: базовый номер минус исполнение «-\d».
            var baseDes = GetPdProgramBase(head);
            if (baseDes.Length == 0)
            {
                baseDes = GetBaseDesignation(head);
                if (baseDes.Length > 0) baseDes = Regex.Replace(baseDes, @"-\d+$", "");
            }

            var prod = "";
            if (baseDes.Length > 0 && cache.TryGetValue(baseDes, out var c1)) prod = c1;
            else if (baseDes.Length > 0 && allDesignations is not null && allDesignations.TryGetValue(baseDes, out var c2))
            { prod = c2; cache[baseDes] = prod; }
            else if (baseDes.Length > 0 && fileName.Length > 0)
            {
                var t = fileName.Replace(".pdf", "").Trim().Replace(designation, "").Trim();
                t = Regex.Replace(t, @"[()\[\]_\-]+$", "").Trim();
                if (t.Length > 0) prod = t;
            }

            if (prod.Length > 0 && docType.Contains(prod)) prod = "";
            return (prod, excType, excSheet);
        }

        // Обычная обработка.
        var suffixText = "";
        var cleanDes = designation;
        if (designation.EndsWith("-ЛУ")) { suffixText = "Лист утверждения"; cleanDes = designation[..^3]; }
        else if (designation.EndsWith("-УЛ")) { suffixText = "Информационно-удостоверяющий лист"; cleanDes = designation[..^3]; }

        cleanDes = Regex.Replace(cleanDes, @"-\d+(?=[A-ZА-Я])", "");

        var (baseDesignation, docCode) = ExtractBaseAndCode(cleanDes);
        if (baseDesignation.Length > 0) baseDesignation = Regex.Replace(baseDesignation, @"-\d+$", "");
        var docType2 = docCode is not null && KdNameMapping.TryGetValue(docCode, out var dt) ? dt : "";

        // Для ПД (в т.ч. НОСИТЕЛЕЙ НД/НДЗ) база изделия = программный номер КОДО.РРРРР-ИИ (с редакцией).
        // Иначе у носителей тип берётся кодом КД, а номер обрезается до КОДО.РРРРР — и имя изделия из
        // спецификации (лежит под ключом с редакцией) не подтягивается. ПД-гейтовано → КД/ТД не трогает.
        var pdProgramBase = GetPdProgramBase(designation);
        if (pdProgramBase.Length == 0) pdProgramBase = GetPdProgramBase(cleanDes);
        if (pdProgramBase.Length > 0) baseDesignation = pdProgramBase;

        if (docType2.Length == 0 && cleanDes.Length > 0)
        {
            foreach (var code in KdNameMapping.Keys.Where(c => Array.IndexOf(SuffixCodes, c) < 0).OrderByDescending(c => c.Length))
            {
                if (cleanDes.Contains($"-{code}")) { docType2 = KdNameMapping.GetValueOrDefault(code, ""); break; }
                if (cleanDes.EndsWith(code) && code.Length < cleanDes.Length) { docType2 = KdNameMapping.GetValueOrDefault(code, ""); break; }
            }
        }

        // ПД (ГОСТ 19.101): тип — по коду вида документа в децимальном номере (для видовых документов).
        if (docType2.Length == 0)
        {
            var pdType = ResolvePdType(cleanDes);
            if (pdType.Length == 0) pdType = ResolvePdType(designation);
            if (pdType.Length > 0) docType2 = pdType;
        }

        var product = "";
        if (baseDesignation.Length > 0 && cache.TryGetValue(baseDesignation, out var p1)) product = p1;
        else if (baseDesignation.Length > 0 && allDesignations is not null && allDesignations.TryGetValue(baseDesignation, out var p2))
        { product = p2; cache[baseDesignation] = product; }
        else if (cleanDes.Length > 0 && allDesignations is not null && allDesignations.TryGetValue(cleanDes, out var p3))
        { product = p3; if (baseDesignation.Length > 0) cache[baseDesignation] = product; }
        else if (allDesignations is not null && allDesignations.TryGetValue(designation, out var p4))
        { product = p4; if (baseDesignation.Length > 0) cache[baseDesignation] = product; }

        if (product.Length == 0 && fileName.Length > 0)
        {
            var bn = fileName.Replace(".pdf", "").Trim();
            foreach (var des in new[] { designation, cleanDes })
                if (des.Length > 0 && bn.Contains(des)) bn = bn.Replace(des, "").Trim();
            bn = bn.Replace("-ЛУ", "").Replace("-УЛ", "").Trim();
            bn = Regex.Replace(bn, @"[()\[\]_\-]+$", "").Trim();
            bn = Regex.Replace(bn, @"-\d+", "").Trim();
            // Если тип документа уже прописан в имени файла — не дублируем его в наименовании.
            if (docType2.Length > 0 && bn.Contains(docType2)) bn = bn.Replace(docType2, "");
            bn = Regex.Replace(bn, @"\s{2,}", " ").Trim();
            if (bn.Length > 0) product = bn;
        }

        // Изделие не может быть названием вида документа — иначе тип «протёк» в слот изделия
        // (частый случай для ПД: имя файла содержит и номер, и словами тот же тип документа).
        if (docType2.Length > 0 && IsDocTypeName(product)) product = "";

        // Раскладка по трём частям (склейка пробелом эквивалентна прежнему «полному наименованию»).
        string prodPart, typePart;
        if (docType2.Length > 0)
        {
            typePart = docType2;
            prodPart = product.Length > 0 && !docType2.Contains(product) ? product : "";
        }
        else if (product.Length > 0) { prodPart = product; typePart = ""; }
        else { prodPart = fileName.Length > 0 ? fileName.Replace(".pdf", "") : ""; typePart = ""; }

        var joined = string.Join(" ", new[] { prodPart, typePart }.Where(s => s.Length > 0));
        var sheetPart = suffixText.Length > 0 && !joined.Contains(suffixText) && !docType2.Contains(suffixText)
            ? suffixText : "";

        return (prodPart, typePart, sheetPart);
    }

    // Названия удостоверяющих листов (от длинного к короткому) — для отделения «листа» от типа документа.
    private static readonly string[] SheetSuffixNames =
        ["Информационно-удостоверяющий лист", "Удостоверяющий лист", "Лист утверждения"];

    // Делит «<тип> <лист>» на (тип, лист), если строка оканчивается известным названием листа.
    private static (string Type, string Suffix) SplitSheetSuffix(string docType)
    {
        foreach (var s in SheetSuffixNames)
            if (docType.Length > s.Length && docType.EndsWith(s, StringComparison.Ordinal))
                return (docType[..^s.Length].Trim(), s);
        return (docType, "");
    }

    // ── Извлечение номеров из имени файла ──────────────────────────────────────

    private static readonly string[] KdCodesExtract =
    [
        "ТП","ПЗ","Е1","ВО","СБ","ГЧ","МЭ","МЧ","УЧ","Э0","Э1","Э2","Э3","Э4","Э5","Э6","Э7",
        "ПЭ0","ПЭ1","ПЭ2","ПЭ3","ПЭ4","ПЭ5","ПЭ6","ПЭ7","С7","ПС7","ВС","ВД","ВП","ДП","ВДЭ","ВЭД","ТБ",
        "Д23","Д27","Д70","Д1","ТУ","ТУ1","ТУ2","ТУ3","ТУ4","ТУ5","ТУ6","ТУ7","ТУ8","ТУ9","РР","ВЭ","РЭ",
        "РЭ1","РЭ2","РЭ3","РЭ4","РЭ5","РЭ6","РЭ7","РЭ8","РЭ9","РЭ10","РЭ11","РЭ12","РЭ13","РЭ14","РЭ15",
        "РО","ФО","ФО1","ФО2","ФО3","ФО4","ФО5","ФО6","ФО7","ФО8","ФО9","ПС","ЭТ","ЗИ","И3","ЭД","ВЗ","НЗ","НМ",
        "Д90","УП","ИС","ИС1","ИС2","ИС3","ИС4","ИС5","ИС6","ИС7","ИС8","ИС9","И10","РЛЭ","ВКЭ","НЭ","АЭ","РК",
        "КД","ДЭ","МС","ВКР","Д33","Д42","Д50","ОП","НД","НДЗ",
        "Э3-УЛ","Д33-УЛ","Д42-УЛ","Д42.1-УЛ","Д42.2-УЛ","Д50-УЛ","МД-УЛ","ЭМД-УЛ","МС-УЛ","Д23-УЛ","НДЗ-УЛ",
        "НД-УЛ","ТУ-ЛУ","ВЭ-ЛУ","ПС-ЛУ","ЭСБ-УЛ",
    ];

    private static readonly string[] PdCodesExtract =
    [
        "ТЗ","ПЗ","ПО","ПР","СП","ТК","РО","РП","РС","РТ","РЭ","ИО","ИП","ИС","ИТ","ВЭ","ВД","ВМ","ВЗ","ЗИ",
        "ОП","ОО","ОА","ОС","ОД","ЛУ","ЛС","АИ","АТ","АВ","РБ","ИБ","МП","БД","АП","ТМ","ТЗ-ЛУ","ПЗ-ЛУ","ПО-ЛУ",
        "ПР-ЛУ","РО-ЛУ","РП-ЛУ","ОП-ЛУ","ВЭ-ЛУ","НД","НДЗ","НД-УЛ","НДЗ-УЛ",
    ];

    private const RegexOptions Verbose = RegexOptions.IgnorePatternWhitespace | RegexOptions.IgnoreCase;

    /// <summary>Номер КД по ГОСТ 2.201 (с суффиксами -ЛУ/-УЛ).</summary>
    public static string ExtractNumbersKd(string text)
    {
        var codes = KdCodesExtract.Distinct().OrderByDescending(c => c.Length);
        var kd = string.Join("|", codes.Select(Regex.Escape));

        var withHyphen =
            @"\b [A-ZА-Я]{4}\.\d{6}\.\d{3,4} (?:-\d{2}\.\d{2})? (?:-\d+)? (?:-" + kd + @")? (?:-ЛУ)? (?:-УЛ)? \b";
        var m = Regex.Match(text, withHyphen, Verbose);
        if (m.Success) return Regex.Replace(m.Value, @"\s+", " ").Trim();

        var without =
            @"\b [A-ZА-Я]{4}\.\d{6}\.\d{3,4} (?:-\d{2}\.\d{2})? (?:-\d+)? (?:" + kd + @")? (?:-ЛУ)? (?:-УЛ)? (?!\d) \b";
        m = Regex.Match(text, without, Verbose);
        if (m.Success) return Regex.Replace(m.Value, @"\s+", " ").Trim();

        return "";
    }

    /// <summary>Номер ПД по ГОСТ 19.101.</summary>
    public static string ExtractNumbersPd(string text)
    {
        var pd = string.Join("|", PdCodesExtract.OrderByDescending(c => c.Length));

        string[] patterns =
        [
            @"\b ( [A-ZА-Я]{4}\.\d{5} -\d{2} \s+\d{2} \s+\d{2} (?:-\d)? (?:-ЛУ)? ) \b",
            @"\b ( [A-ZА-Я]{4}\.\d{5} -\d{2} \s+(?:" + pd + @") \s+\d{2} (?:-\d)? (?:-ЛУ)? ) \b",
            @"\b ( [A-ZА-Я]{4}\.\d{5} -\d{2} (?:-ЛУ)? ) \b",
            @"\b ( [A-ZА-Я]{4}\.\d{5} -\d{2} (?:" + pd + @") (?:-УЛ)? ) \b",
            @"\b ( [A-ZА-Я]{4}\.\d{5} -\d{2} (?:" + pd + @") \s+\d{2} (?:-\d)? (?:-ЛУ)? ) \b",
        ];

        foreach (var p in patterns)
        {
            var m = Regex.Match(text, p, Verbose);
            if (m.Success) return Regex.Replace(m.Groups[1].Value, @"\s+", " ").Trim();
        }
        return "";
    }

    /// <summary>Номер ТД по строгому формату АААА.00000.00000 (без приставок).</summary>
    public static string ExtractNumbersTd(string text)
    {
        var m = Regex.Match(text, @"([A-ZА-Я]{4}\.\d{5}\.\d{5})");
        if (m.Success) return m.Groups[1].Value;
        var m2 = Regex.Match(text, @"([A-ZА-Я]{4}\.\d{5}\.\d{5})(?:[A-ZА-Я]+)?");
        return m2.Success ? m2.Groups[1].Value : "";
    }

    /// <summary>Разбор имени файла → (КД, ПД, ТД, название документа).</summary>
    public static (string Kd, string Pd, string Td, string Name) ParseFilename(string filename)
    {
        var baseName = filename.Length >= 4 ? filename[..^4] : filename;   // убираем .pdf

        var kd = ExtractNumbersKd(baseName);
        var pd = ExtractNumbersPd(baseName);
        var td = ExtractNumbersTd(baseName);

        var name = baseName;
        if (td.Length > 0) name = name.Replace(td, "").Trim();
        if (pd.Length > 0) name = name.Replace(pd, "").Trim();
        if (kd.Length > 0) name = name.Replace(kd, "").Trim();
        name = name.Replace("-ЛУ", "").Replace("-УЛ", "").Trim();
        name = Regex.Replace(name, @"[()\[\]]", "").Trim();
        name = string.Join(" ", name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return (kd, pd, td, name);
    }
}
