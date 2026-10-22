using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using SmartBase.Controls.Components;
using SmartBase.Controls.Dialogs;
using SmartBase.Controls.Widgets;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Repositories;
using SmartBase.Logging;
using SmartBase.Utils;
using SmartBase.ViewModels.ConstructorDb;

namespace SmartBase.Views.ConstructorDb;

/// <summary>
/// Копирование, очистка и вставка значений выделенных ячеек — три действия, общие для трех
/// контекстных меню (ячейки, строки, столбца) и горячих клавиш.
///
/// Все они работают по ОДНОМУ прямоугольнику: меню строки и столбца перед вызовом просто
/// расширяют выделение на всю строку или весь столбец, поэтому отдельной логики «скопировать
/// строку» и «скопировать столбец» не существует.
///
/// Запись всегда идет пакетным путем репозитория (одна транзакция, одна строка журнала со
/// счетом): обычная правка ячейки дает около 18 значений в секунду, и диапазон в пару тысяч
/// ячеек подвесил бы окно на минуты. Большой пакет пишется в фоне, на отдельном подключении.
/// </summary>
public partial class TableView
{
    /// <summary>Сколько ячеек очищается молча. Больше — спрашиваем, если включена настройка
    /// «Спрашивать подтверждение при удалении»: стереть полсотни значений одним нажатием и
    /// заметить это потом — дороже, чем лишний вопрос.</summary>
    private const int ClearConfirmThreshold = 10;

    /// <summary>До скольких значений пакет пишется сразу, на общем подключении. Больше — в фоне с
    /// окном прогресса: на сетевой папке запись десятков тысяч значений — это секунды.</summary>
    private const int SyncWriteCells = 5_000;

    // ── Копирование ───────────────────────────────────────────────────────────

    /// <summary>Значения выделенных ячеек в буфер обмена, в общем формате табличных редакторов
    /// (табуляция между столбцами, перевод строки между строками).</summary>
    public async Task<bool> CopyCellsAsync()
    {
        FollowSelectionIdentity();
        if (_cellRange is not { } range || _repository is null) return false;

        var columns = range.VisibleColumns(VisibleDataColumns());
        if (columns.Count == 0 || TooManyCells((long)range.RowCount * columns.Count)) return false;
        if (await RowsOfRangeAsync(range.Top, range.RowCount, FieldIdsOf(columns)) is not { Count: > 0 } rows) return false;

        // Связь уходит в буфер названиями связанных записей через разделитель столбца — так ее
        // показывает ячейка, и так она вставляется обратно. Названия порциями на столбец.
        var recordIds = rows.Select(r => r.RecordId).ToList();
        var links     = new Dictionary<int, Dictionary<int, List<(RelationTarget Target, string Display)>>>();
        foreach (var c in columns)
            if (FieldTypeInfo.IsRelation(_colDefs[c].FieldType) && int.TryParse(_colDefs[c].Key, out var relationField))
                links[c] = LinksOf(relationField, recordIds);

        string Text(TableRowVm row, int c) =>
            links.TryGetValue(c, out var byRecord)
                ? string.Join(_colDefs[c].Relation?.SeparatorString ?? ", ",
                              byRecord.GetValueOrDefault(row.RecordId)?.Select(l => l.Display) ?? [])
                : CellText(row, _colDefs[c]);

        var matrix = rows
            .Select(row => (IReadOnlyList<string>)columns.Select(c => Text(row, c)).ToList())
            .ToList();

        var text = TableClipboardFormat.Build(matrix);

        // След в журнале безопасности — ДО буфера: копирование выносит данные наружу так же, как
        // выгрузка в файл, и копирования, о котором не осталось записи, быть не должно.
        if (!CopyTrace.Ready) { AppNotifications.Error(CopyTrace.NoJournal); return false; }
        if (!NoteCopy(columns, matrix.Count, text)) { AppNotifications.Error(CopyTrace.NotWritten); return false; }

        var relationCells = new List<RelationClipboardCell>();
        for (var r = 0; r < rows.Count; r++)
            for (var c = 0; c < columns.Count; c++)
                if (links.TryGetValue(columns[c], out var byRecord))
                    relationCells.Add(new(r, c, byRecord.GetValueOrDefault(rows[r].RecordId)?.Select(l => l.Target).ToList() ?? []));
        var payload = relationCells.Count == 0 ? null : new RelationClipboard(1, text,
            RelationDbResolver.DbIdFor(_repository), _repository.RelationDatabaseIdentity,
            _repository.GetRelationDatabaseIdentities(relationCells.SelectMany(c => c.Targets)), relationCells);
        return await SetClipboardTextAsync(text, payload);
    }

    // Связи столбца для многих записей — порциями: список id уходит в запрос параметром.
    private Dictionary<int, List<(RelationTarget Target, string Display)>> LinksOf(int fieldId, List<int> recordIds)
    {
        var all = new Dictionary<int, List<(RelationTarget Target, string Display)>>();
        if (_repository is null) return all;

        foreach (var chunk in recordIds.Chunk(5_000))
            foreach (var (recordId, list) in _repository.GetRelationLinksBatch(fieldId, chunk.ToList()))
                all[recordId] = list;
        return all;
    }

    private bool NoteCopy(List<int> columns, int rows, string text)
    {
        if (_repository is null) return false;

        // Метка — самая строгая среди базы и скопированных столбцов, как у выгрузки.
        var label = _repository.GetDatabaseLabel();
        foreach (var c in columns)
            if (_fields.FirstOrDefault(f => f.Id?.ToString() == _colDefs[c].Key) is { } field)
                label = Confidentiality.Strictest(label, Confidentiality.FromConfig(field.ConfigJson));

        return CopyTrace.Note(DatabaseId?.Invoke() ?? 0, _repository.CollectionName(_collectionId),
                              [.. columns.Select(c => _colDefs[c].Header)], rows, rows * columns.Count,
                              text, label, DateTimeOffset.Now);
    }

    /// <summary>Вырезать: скопировать значения и сразу очистить ячейки. Отменяется Ctrl+Z, как
    /// очистка; если скопировать не удалось, ничего не стирается.</summary>
    public async Task CutCellsAsync()
    {
        if (!Can(PermAction.EditRecord)) { PermissionNotice.Show(this, PermAction.EditRecord); return; }
        if (!await CopyCellsAsync()) return;
        await ClearCellsAsync("Вырезание");
    }

    // Буфер может быть занят другой программой — на Windows это штатный отказ. Без своего
    // перехвата исключение всплыло бы глобальным «Непредвиденная ошибка», хотя случилось
    // всего лишь «не удалось скопировать».
    private const string RelationClipboardFormat = "application/x-smartbase-relation-cells+json";

    private async Task<bool> SetClipboardTextAsync(string text, RelationClipboard? payload = null)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return false;

        try
        {
            if (payload is null) await clipboard.SetTextAsync(text);
            else
            {
                var data = new DataObject();
                data.Set(DataFormats.Text, text);
                data.Set(RelationClipboardFormat, JsonSerializer.Serialize(payload));
                await clipboard.SetDataObjectAsync(data);
            }
            return true;
        }
        catch (Exception)
        {
            AppNotifications.Error("Не удалось скопировать: буфер обмена занят другой программой.");
            return false;
        }
    }

    private async Task<string?> GetClipboardTextAsync()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return null;

        try { return await clipboard.GetTextAsync(); }
        catch (Exception)
        {
            AppNotifications.Error("Не удалось прочитать буфер обмена: он занят другой программой.");
            return null;
        }
    }

    // ── Очистка ───────────────────────────────────────────────────────────────

    /// <summary>Стереть значения выделенных ячеек. Сами записи и столбцы остаются — их удаляет
    /// пункт «Удалить».</summary>
    public async Task ClearCellsAsync(string operation = "Очистка")
    {
        if (!HasCellSelection || _repository is null) return;
        if (!Can(PermAction.EditRecord)) { PermissionNotice.Show(this, PermAction.EditRecord); return; }

        if (await SelectedCellsAsync() is not { } cells) return;

        var targets = cells
            .Where(t => TablePastePlanner.IsWritable(PasteColumnOf(t.Column, users: null, structured: true)))
            .ToList();
        if (targets.Count == 0) return;

        // Больше, чем запоминает отмена, — спрашиваем всегда: вернуть такую очистку Ctrl+Z нельзя, и
        // настройка «не спрашивать» на это не рассчитана.
        var noUndo = targets.Count > ConstructorRepository.UndoChangesLimit;
        if (noUndo || (targets.Count > ClearConfirmThreshold && ConstructorSettings.ConfirmDelete))
        {
            if (VisualRoot is not Window win) return;
            var dlg = new ConfirmDialog(
                $"Очистить {targets.Count:N0} {CellsPlural(targets.Count)}? Записи и столбцы останутся на месте." +
                (noUndo ? " Отменить очистку будет нельзя: столько изменений не запоминается." : ""),
                confirmText: "Очистить");
            if (await dlg.ShowDialog<bool>(win) != true) return;
        }

        // Стертое «Название» становится «Без названия» — как в любом другом месте правки названия.
        await WriteCellsAsync(targets.Select(t => (t.Row, t.Column,
                                                   Value: t.Column.Key == TableRowVm.TitleKey ? TitleText.Placeholder : ""))
                                     .ToList(), operation);
    }

    // ── Вставка ───────────────────────────────────────────────────────────────

    /// <summary>Вставить значения из буфера. Выделенная ячейка — левый верхний угол блока,
    /// дальше он разливается вниз и вправо.</summary>
    public async Task PasteCellsAsync()
    {
        FollowSelectionIdentity();
        if (_cellRange is not { } range || _repository is null) return;
        if (!Can(PermAction.EditRecord)) { PermissionNotice.Show(this, PermAction.EditRecord); return; }

        var text = await GetClipboardTextAsync();
        if (text is null) return;

        RelationClipboard? structured = null;
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard
                && await clipboard.GetDataAsync(RelationClipboardFormat) is string raw)
            {
                structured = JsonSerializer.Deserialize<RelationClipboard>(raw);
                if (structured is not { Version: 1, Cells: not null, DatabaseIdentities: not null }
                    || structured.Text != text || structured.Cells.Any(c => c.Row < 0 || c.Column < 0 || c.Targets is null)
                    || structured.Cells.Select(c => (c.Row, c.Column)).Distinct().Count() != structured.Cells.Count)
                    throw new InvalidOperationException("Неверный формат связей в буфере обмена.");
                // Проверяем все базы до записи первого значения или ребра.
                structured = structured with { Cells = structured.Cells.Select(c => c with
                    { Targets = _repository.RebaseRelationClipboardTargets(structured, c.Targets) }).ToList() };
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        { AppNotifications.Error(ex.Message); return; }

        var values = text.Length == 0 && structured?.Cells.Any(c => c.Row == 0 && c.Column == 0) == true
            ? (IReadOnlyList<IReadOnlyList<string>>)[(IReadOnlyList<string>)[""]]
            : TableClipboardFormat.Parse(text);
        if (values.Count == 0) return;

        // Размножение блока по выделению разливает его на всю высоту выделения — проверяем объем
        // ДО планирования: план на весь столбец большой коллекции сам по себе занял бы память и
        // секунды.
        if (TooManyRows(Math.Max(values.Count, range.RowCount))) return;

        // Вставка идет только по ВИДИМЫМ столбцам — тем же, что копируются: скрытый столбец места в
        // блоке не занимает (PlanInGrid), иначе скопированный блок ложился бы обратно со сдвигом на
        // чужие столбцы. В плане столбец — уже позиция в сетке.
        var visible = VisibleDataColumns();
        List<string>? users = null;
        var columns = visible.Select(c => PasteColumnOf(_colDefs[c], users ??= UserOptions(), structured is not null)).ToList();
        var plan    = TablePastePlanner.PlanInGrid(values, range, visible, columns, VisibleRowCount);

        if (TooManyCells(plan.Cells.Count)) return;

        // Подтверждение — только когда отменить вставку будет нельзя: остальное возвращается Ctrl+Z,
        // и вопрос на каждую вставку был бы рутиной, которую перестают читать.
        if (plan.Cells.Count > ConstructorRepository.UndoChangesLimit)
        {
            if (VisualRoot is not Window win) return;
            var dlg = new ConfirmDialog(
                $"Вставить {plan.Cells.Count:N0} {ValuesPlural(plan.Cells.Count)}? Отменить вставку будет нельзя: " +
                "столько изменений не запоминается.",
                confirmText: "Вставить");
            if (await dlg.ShowDialog<bool>(win) != true) return;
        }

        var writes = new List<(TableRowVm Row, TableColumnDef Column, string Value)>();
        var identities = new Dictionary<(int RecordId, int FieldId), IReadOnlyList<RelationTarget>>();
        var copiedLinks = structured?.Cells.ToDictionary(c => (c.Row, c.Column));
        if (plan.Cells.Count > 0)
        {
            // Вставке нужны только id записей — значения полей не читаются.
            var top    = plan.Cells.Min(c => c.Row);
            var bottom = plan.Cells.Max(c => c.Row);
            if (await RowsOfRangeAsync(top, bottom - top + 1, []) is not { } rows) return;

            var byPosition = new Dictionary<int, TableRowVm>(rows.Count);
            foreach (var row in rows) byPosition[row.RowNumber - 1] = row;

            foreach (var cell in plan.Cells)
                if (byPosition.TryGetValue(cell.Row, out var row))
                {
                    var column = _colDefs[cell.Column];
                    writes.Add((row, column, cell.StorageValue));
                    var sourceColumn = (visible.IndexOf(cell.Column) - visible.FindIndex(c => c >= range.Left)) % values.Max(v => v.Count);
                    var sourceRow = (cell.Row - range.Top) % values.Count;
                    if (FieldTypeInfo.IsRelation(column.FieldType) && int.TryParse(column.Key, out var fid)
                        && copiedLinks?.TryGetValue((sourceRow, sourceColumn), out var copied) == true)
                        identities[(row.RecordId, fid)] = copied.Targets;
                }
        }

        var outcome = await WriteCellsAsync(writes, "Вставка", identities);
        ReportPaste(outcome, plan.Skipped);
    }

    // Список допущенных пользователей для столбцов «Пользователь»: вставка сверяет с ним имена,
    // как выпадающий список правки. Читается один раз на вставку.
    private List<string> UserOptions() => _repository?.GetUserOptions() ?? [];

    // Столбец таблицы глазами вставки: планировщик не должен знать про ViewModels.
    private static PasteColumn PasteColumnOf(TableColumnDef def, List<string>? users, bool structured = false) =>
        new(def.Key, def.FieldType,
            // IsReadOnly у колонки означает «нет текстового редактора», а не «значение менять
            // нельзя»: чекбокс правится кликом и из буфера заполняется штатно («да», «1», «✓»).
            // Без этой оговорки вставка и очистка молча обходили бы все колонки-галочки.
            IsReadOnly: def.IsReadOnly && def.FieldType != "checkbox" || def.Relation?.Auto is not null,
            Options: def.FieldType == "user" ? users : def.Options,
            DecimalSeparator: def.NumberFormat.DecimalSeparator,
            IsTitle: def.Key == TableRowVm.TitleKey,
            NumberFormat: def.NumberFormat,
            Separator: def.Separator,
            // Связь без настроенной цели или с целью в другой базе по названию не найти.
            IsCrossDatabaseRelation: FieldTypeInfo.IsRelation(def.FieldType) && (def.Relation is null || !structured && def.Relation.IsCrossDb));

    // ── Запись ────────────────────────────────────────────────────────────────

    /// <summary>Итог записи ячеек: сколько изменилось, сколько связей не нашлось по названию и
    /// сколько ячеек-связей пропущено условием выбора записей.</summary>
    private readonly record struct WriteOutcome(int Changed, int RelationNotFound, int RelationAmbiguous,
                                                int RelationOutsideCondition = 0);

    /// <summary>Итог одного пакета — и значения, и связи; для отмены нужны оба.</summary>
    private sealed record WriteBatch(ConstructorRepository.CellBulkResult Values,
                                     ConstructorRepository.LinkBulkResult Links,
                                     int RelationNotFound, int RelationAmbiguous);

    // Одним пакетом: значения полей, названия записей и связи разделяются здесь — «Название» живет
    // в самой записи, связи — ребрами, а не текстом. operation — имя действия для журнала и отмены.
    private async Task<WriteOutcome> WriteCellsAsync(
        IReadOnlyList<(TableRowVm Row, TableColumnDef Column, string Value)> writes, string operation,
        IReadOnlyDictionary<(int RecordId, int FieldId), IReadOnlyList<RelationTarget>>? identities = null)
    {
        if (writes.Count == 0 || _repository is null) return default;
        var repository = _repository;
        // Порог медленности: запись значений ячеек (очистка, вставка, вырезание) дольше Diag.UiQuery.
        using var slow = Diag.Measure($"{operation.ToLowerInvariant()} ячеек", Diag.UiQuery,
                                      $"{DbErrors.Display(repository.DbPath)}, коллекция {_collectionId}, ячеек {writes.Count}");

        var cells     = new List<ConstructorRepository.CellWrite>();
        var titles    = new List<ConstructorRepository.TitleWrite>();
        var relations = new List<(int RecordId, int FieldId, string Text, IReadOnlyList<RelationTarget>? Targets)>();

        foreach (var (row, column, value) in writes)
        {
            if (column.Key == TableRowVm.TitleKey) titles.Add(new(row.RecordId, value));
            else if (!int.TryParse(column.Key, out var fieldId)) continue;
            else if (FieldTypeInfo.IsRelation(column.FieldType)) relations.Add((row.RecordId, fieldId, value,
                value.Length == 0 ? [] : identities?.GetValueOrDefault((row.RecordId, fieldId))));
            else cells.Add(new ConstructorRepository.CellWrite(row.RecordId, fieldId, value));
        }

        var batch = writes.Count <= SyncWriteCells
            ? WriteNow(repository, cells, titles, relations, operation)
            : await WithProgressAsync($"{operation}: запись в базу…",
                                      (_, _) => WriteInBackgroundAsync(repository, cells, titles, relations, operation),
                                      cancellable: false);
        if (batch is null) return default;

        RememberForUndo(operation, batch.Values, batch.Links);
        var changed = batch.Values.Changed + batch.Links.Changed;
        if (changed > 0) ReloadRows();   // строки перечитываем целиком: правок много и они разные
        return new WriteOutcome(changed, batch.RelationNotFound, batch.RelationAmbiguous + batch.Links.Conflicts,
                                batch.Links.ConditionRefused);
    }

    // Небольшой пакет — сразу, на общем подключении. null — значения не записались (база занята или
    // недоступна, человеку уже сказано).
    private WriteBatch? WriteNow(ConstructorRepository repository,
                                 List<ConstructorRepository.CellWrite> cells, List<ConstructorRepository.TitleWrite> titles,
                                 List<(int RecordId, int FieldId, string Text, IReadOnlyList<RelationTarget>? Targets)> relations, string operation)
    {
        var values = ConstructorRepository.CellBulkResult.Nothing;
        if (cells.Count > 0 || titles.Count > 0)
        {
            try { values = repository.SetCellsBulk(_collectionId, cells, titles, operation); }
            catch (Exception ex) when (DbErrors.Handle(ex, repository.DbPath)) { return null; }
        }

        // Связи — вторым пакетом: значения к этому моменту уже записаны, и сбой здесь не должен
        // лишить их отмены.
        var links = ConstructorRepository.LinkBulkResult.Nothing;
        var (notFound, ambiguous) = (0, 0);
        if (relations.Count > 0)
        {
            try
            {
                var found = relations.Where(r => r.Targets is null).GroupBy(r => r.FieldId).ToDictionary(
                    g => g.Key, g => repository.ResolveRelationTitles(g.Key, [.. g.Select(r => r.Text).Distinct()]));
                (var linkWrites, notFound, ambiguous) = LinkWritesOf(relations, found);
                links = repository.SetLinksBulk(_collectionId, linkWrites, operation);
            }
            catch (Exception ex) when (DbErrors.Handle(ex, repository.DbPath)) { }
        }

        return new WriteBatch(values, links, notFound, ambiguous);
    }

    // Большой пакет — в фоне, на отдельном подключении: окно остается живым, прогресс виден.
    private async Task<WriteBatch?> WriteInBackgroundAsync(ConstructorRepository repository,
                                                          List<ConstructorRepository.CellWrite> cells, List<ConstructorRepository.TitleWrite> titles,
                                                          List<(int RecordId, int FieldId, string Text, IReadOnlyList<RelationTarget>? Targets)> relations, string operation)
    {
        var values = ConstructorRepository.CellBulkResult.Nothing;
        if (cells.Count > 0 || titles.Count > 0)
        {
            try { values = await repository.SetCellsBulkIsolatedAsync(_collectionId, cells, titles, operation); }
            catch (Exception ex) when (DbErrors.Handle(ex, repository.DbPath)) { return null; }
        }

        var links = ConstructorRepository.LinkBulkResult.Nothing;
        var (notFound, ambiguous) = (0, 0);
        if (relations.Count > 0)
        {
            try
            {
                var found = new Dictionary<int, Dictionary<string, ConstructorRepository.TitleResolution>>();
                foreach (var byField in relations.Where(r => r.Targets is null).GroupBy(r => r.FieldId))
                    found[byField.Key] = await repository.ResolveRelationTitlesIsolatedAsync(
                        byField.Key, [.. byField.Select(r => r.Text).Distinct()]);
                (var linkWrites, notFound, ambiguous) = LinkWritesOf(relations, found);
                links = await repository.SetLinksBulkIsolatedAsync(_collectionId, linkWrites, operation);
            }
            catch (Exception ex) when (DbErrors.Handle(ex, repository.DbPath)) { }
        }

        return new WriteBatch(values, links, notFound, ambiguous);
    }

    // Найденные по названиям связи — в записи ребер; ненайденные и тезки идут в счет пропуска.
    private static (List<ConstructorRepository.LinkWrite> Writes, int NotFound, int Ambiguous) LinkWritesOf(
        List<(int RecordId, int FieldId, string Text, IReadOnlyList<RelationTarget>? Targets)> relations,
        IReadOnlyDictionary<int, Dictionary<string, ConstructorRepository.TitleResolution>> found)
    {
        var writes    = new List<ConstructorRepository.LinkWrite>();
        var notFound  = 0;
        var ambiguous = 0;

        foreach (var (recordId, fieldId, text, targets) in relations)
        {
            if (targets is not null) { writes.Add(ConstructorRepository.LinkWrite.FromTargets(recordId, fieldId, targets)); continue; }

            if (!found.TryGetValue(fieldId, out var byText) || !byText.TryGetValue(text, out var resolution))
            {
                notFound++;
                continue;
            }

            switch (resolution.Match)
            {
                case ConstructorRepository.TitleMatch.NotFound:  notFound++;  break;
                case ConstructorRepository.TitleMatch.Ambiguous: ambiguous++; break;
                default: writes.Add(new(recordId, fieldId, resolution.Ids)); break;
            }
        }

        return (writes, notFound, ambiguous);
    }

    private static void ReportPaste(WriteOutcome outcome, IReadOnlyDictionary<PasteSkipReason, int> skipped)
    {
        // Итог говорит, ПОЧЕМУ пропущено: «пропущено 6» без причины заставляло гадать, что не так
        // со вставкой, и перепроверять ячейки по одной.
        var reasons = skipped
            .Where(kv => kv.Value > 0)
            .Select(kv => (Count: kv.Value, Phrase: SkipPhrase(kv.Key)))
            .ToList();
        if (outcome.RelationNotFound > 0)  reasons.Add((outcome.RelationNotFound,  "связей не нашлись по названию"));
        if (outcome.RelationAmbiguous > 0) reasons.Add((outcome.RelationAmbiguous, "связей с неоднозначным названием"));
        if (outcome.RelationOutsideCondition > 0)
            reasons.Add((outcome.RelationOutsideCondition, "связей не подходят под условие выбора записей"));

        var skippedTotal = reasons.Sum(r => r.Count);
        if (outcome.Changed == 0 && skippedTotal == 0) return;

        var message = outcome.Changed > 0
            ? $"Вставлено {outcome.Changed:N0} {ValuesPlural(outcome.Changed)}"
            : "Вставлять было нечего";
        if (skippedTotal > 0)
            message += $", пропущено {skippedTotal:N0}: " +
                       string.Join(", ", reasons.OrderByDescending(r => r.Count).Select(r => $"{r.Count:N0} {r.Phrase}"));

        AppNotifications.Info(message + ".");
    }

    private static string SkipPhrase(PasteSkipReason reason) => reason switch
    {
        PasteSkipReason.WrongFormat    => "не подходят типу столбца",
        PasteSkipReason.NotInOptions   => "нет в списке допустимых",
        PasteSkipReason.ComputedColumn => "в столбцах, которые не правятся",
        PasteSkipReason.NotTextColumn  => "в файлах и связях с другой базой",
        PasteSkipReason.OutsideTable   => "за краем таблицы",
        _                              => "по другой причине",
    };

    // Действия с буфером запускаются из клавиш и пунктов меню, где результата ждать некому.
    // async void здесь нарочно: непредвиденное исключение уйдет в общий обработчик приложения
    // (журнал и окно ошибки), а не пропадет молча в незамеченной задаче.
    private async void Fire(Func<Task> action) => await action();

    // ── Пункты меню ───────────────────────────────────────────────────────────

    /// <summary>
    /// Группа «Копировать / Вставить / Очистить значения» — одна и та же в меню ячейки, строки и
    /// столбца. <paramref name="select"/> выделяет то, над чем меню открыли: для ячейки это уже
    /// сделано правым кликом, для строки и столбца — расширяет прямоугольник на всю строку или
    /// весь столбец. Дальше все три действия работают по общему выделению.
    /// Справа у пунктов — их клавиши: так сочетания узнают там же, где ищут действие.
    /// </summary>
    private void AddCellValueItems(ContextMenu menu, Action select)
    {
        menu.Items.Add(new Separator());

        var copy = new MenuItem
        {
            Header       = MenuIcons.CopyValuesHeader(),
            InputGesture = MenuGesture("table.copy"),
        };
        copy.Click += (_, _) => { select(); Fire(CopyCellsAsync); };
        menu.Items.Add(copy);

        var paste = new MenuItem
        {
            Header       = MenuIcons.PasteValuesHeader(),
            InputGesture = MenuGesture("table.paste"),
        };
        Guarded(paste, PermAction.EditRecord, () => { select(); Fire(PasteCellsAsync); });
        menu.Items.Add(paste);

        // Пустой буфер — вставлять нечего: пункт гаснет при открытии меню, а не молчит по нажатию.
        // Буфер спрашивается на КАЖДОЕ открытие: его могли заполнить в другой программе. Занятый
        // буфер считается непустым — пусть нажатие скажет, что он занят, а не пункт молча погаснет.
        OnMenuOpening(menu, async () =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
            try { paste.IsEnabled = !string.IsNullOrWhiteSpace(await clipboard.GetTextAsync())
                    || await clipboard.GetDataAsync(RelationClipboardFormat) is string; }
            catch (Exception) { paste.IsEnabled = true; }
        });

        var clear = new MenuItem
        {
            Header       = MenuIcons.ClearValuesHeader(),
            InputGesture = MenuGesture("table.clearCells"),
        };
        Guarded(clear, PermAction.EditRecord, () => { select(); Fire(() => ClearCellsAsync()); });
        menu.Items.Add(clear);
    }

    // Подсказка в пункте меню — действующее сочетание действия, включая переназначенное
    // («Настройки → Сочетания клавиш»): подпись и нажатие расходиться не должны.
    private KeyGesture? MenuGesture(string actionId) =>
        ShortcutSettings.PrimaryGesture(actionId)?.ToKeyGesture(CommandModifiers());

    // Модификатор команд платформы: Ctrl, а на macOS — Cmd. Подсказка в меню должна совпадать с
    // тем, что реально нажимают.
    private KeyModifiers CommandModifiers() =>
        TopLevel.GetTopLevel(this)?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;

    // ── Меню самой ячейки ─────────────────────────────────────────────────────
    //
    // Строится на правом нажатии по ячейке и открывается у указателя (OpenMenu в GridInput).

    /// <summary>
    /// Меню ячейки: только то, что относится к значениям и их виду, — про саму запись есть свое
    /// меню на столбце «№». Выделение к этому моменту уже стоит (правый клик его поставил),
    /// поэтому пункты работают по нему и ничего не переустанавливают.
    /// </summary>
    private ContextMenu BuildCellContextMenu(TableRowVm row, TableColumnDef def)
    {
        var menu = new ContextMenu();

        // Шапка со свойством: меню ячейки и меню записи должны различаться с первого взгляда.
        menu.Items.Add(MakeMenuHeaderItem(MenuIcons.PropertyMenuHeader(def.Header, def.FieldType)));

        AddCellValueItems(menu, () => { });

        menu.Items.Add(new Separator());
        AddCellColorItems(menu, row, def);
        AddCellTextFormatItems(menu, row, def);

        return menu;
    }

    // ── Цвет выделенных ячеек ─────────────────────────────────────────────────
    //
    // «Фон ячейки» и «Цвет текста ячейки» живут в меню самой ячейки: правый клик по ячейке
    // открывает именно его, и в меню записи (столбец «№») адресата-ячейки нет. Цвет ложится на
    // ВСЕ выделенные ячейки — как копирование и очистка. Отметка текущего цвета — у той ячейки, по
    // которой кликнули; состав пересчитывается при открытии, потому что цвет могли сменить.
    private void AddCellColorItems(ContextMenu menu, TableRowVm row, TableColumnDef def)
    {
        var host = new MenuItem { Header = MenuIcons.ColorHeader() };
        MenuIcons.FixSubmenuGap(host);

        OnMenuOpening(menu, () =>
        {
            host.Items.Clear();
            host.IsEnabled = Can(PermAction.EditRecord);

            var current = row.CellStyle(def.Key);
            host.Items.Add(ColorMenu.Build("Фон ячейки", current.Bg,
                hex => Fire(() => ApplySelectedCellsStyleAsync(StyleAspect.Background, hex))));
            host.Items.Add(ColorMenu.Build("Цвет текста ячейки", current.Fg,
                hex => Fire(() => ApplySelectedCellsStyleAsync(StyleAspect.Text, hex))));
        });

        menu.Items.Add(host);
    }

    private Task ApplySelectedCellsStyleAsync(StyleAspect aspect, string? hex) =>
        ApplySelectedCellsStyleAsync([(aspect, hex)]);

    /// <summary>Выделить все ячейки строки — для пунктов меню строки.</summary>
    private void SelectWholeRow(Func<TableRowVm?> row)
    {
        if (row() is not { IsAddRow: false } vm) return;
        var index = vm.RowNumber - 1;
        SetCellSelection(new CellRange(index, FirstDataColumn, index, LastDataColumn));
    }

    /// <summary>Выделить все ячейки столбца — для пунктов меню заголовка.</summary>
    private void SelectWholeColumn(string columnKey)
    {
        var index = _colDefs.FindIndex(d => d.Key == columnKey);
        if (!IsDataColumn(index)) return;

        var rows = VisibleRowCount;
        if (rows <= 0) return;
        SetCellSelection(new CellRange(0, index, rows - 1, index));
    }

    private static string CellsPlural(int n) => Plural(n, "ячейку", "ячейки", "ячеек");
    private static string ValuesPlural(int n) => Plural(n, "значение", "значения", "значений");

    private static string Plural(int n, string one, string few, string many)
    {
        var mod100 = n % 100;
        if (mod100 is >= 11 and <= 14) return many;
        return (n % 10) switch { 1 => one, 2 or 3 or 4 => few, _ => many };
    }
}
