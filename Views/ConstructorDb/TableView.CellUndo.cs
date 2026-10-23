using SmartBase.Controls.Dialogs;
using SmartBase.Controls.Widgets;
using SmartBase.Core.Models;
using SmartBase.Database.Repositories;
using SmartBase.Utils;

namespace SmartBase.Views.ConstructorDb;

/// <summary>
/// Отмена (Ctrl+Z) вставки, очистки и вырезания значений ячеек.
///
/// Пакетная запись и так читает старые значения, чтобы не писать совпадающие, — она возвращает их
/// в итоге (<see cref="ConstructorRepository.CellBulkResult.Changes"/>), и отмена просто пишет их
/// обратно. Обратно — с проверкой: ячейку, которую после вставки уже поправил сосед или сам
/// человек в панели записи, отмена не трогает (<c>ExpectedOld</c>), иначе Ctrl+Z молча стер бы
/// чужую свежую правку.
///
/// Шаги живут в памяти этой таблицы и этой коллекции: переход в другую коллекцию их забывает —
/// отмена «из прошлого», которую человек уже не видит, скорее навредила бы.
///
/// Вставка, досоздавшая записи и столбцы (<c>PasteGrowth</c>), отменяется вместе с ними: после
/// возврата значений заведенное убирается — кроме того, что человек успел заполнить после вставки.
/// </summary>
public partial class TableView
{
    /// <summary>Сколько последних действий можно отменить подряд.</summary>
    private const int UndoDepth = 20;

    private sealed record UndoStep(int CollectionId, string Operation,
                                   IReadOnlyList<ConstructorRepository.CellChange> Changes,
                                   IReadOnlyList<ConstructorRepository.LinkChange> Links,
                                   PasteGrowth Growth);

    private readonly List<UndoStep> _undoSteps = [];

    private bool HasUndo => _undoSteps.Count > 0;

    private void ForgetUndo() => _undoSteps.Clear();

    // Шаг запоминается, только если его можно вернуть целиком: итог без старых значений (больше
    // UndoChangesLimit) отменить нельзя, и об этом человека спросили до записи.
    private void RememberForUndo(string operation, ConstructorRepository.CellBulkResult values,
                                 ConstructorRepository.LinkBulkResult links, PasteGrowth? growth = null)
    {
        growth ??= PasteGrowth.None;
        if (values.Changed + links.Changed == 0 && growth.IsEmpty) return;
        if (values.ChangesTruncated || links.ChangesTruncated
            || values.Changed + links.Changes.Sum(c => (long)Math.Max(1, c.OldTargets.Count + c.NewTargets.Count))
                > ConstructorRepository.UndoChangesLimit) return;

        _undoSteps.Add(new UndoStep(_collectionId, operation, values.Changes, links.Changes, growth));
        if (_undoSteps.Count > UndoDepth) _undoSteps.RemoveAt(0);
    }

    /// <summary>Отменить последнее действие со значениями ячеек.</summary>
    public async Task UndoLastCellChangeAsync()
    {
        if (_repository is null || _undoSteps.Count == 0) return;
        if (!Can(PermAction.EditRecord)) { PermissionNotice.Show(this, PermAction.EditRecord); return; }
        var repository = _repository;

        var step = _undoSteps[^1];
        _undoSteps.RemoveAt(_undoSteps.Count - 1);
        if (step.CollectionId != _collectionId) { ForgetUndo(); return; }

        var cells = step.Changes
            .Where(c => c.FieldId is not null)
            .Select(c => new ConstructorRepository.CellWrite(c.RecordId, c.FieldId!.Value, c.OldValue, ExpectedOld: c.NewValue))
            .ToList();
        var titles = step.Changes
            .Where(c => c.FieldId is null)
            .Select(c => new ConstructorRepository.TitleWrite(c.RecordId, c.OldValue, ExpectedOld: c.NewValue))
            .ToList();
        var links = step.Links
            .Select(l => ConstructorRepository.LinkWrite.FromTargets(l.RecordId, l.FieldId, l.OldTargets,
                ConstructorRepository.LinkTargetsKey(l.NewTargets), l.Config))
            .ToList();
        var journal = $"Отмена: {step.Operation.ToLowerInvariant()}";

        // Большой шаг возвращается в фоне, с окном прогресса, — как и писался.
        var large = cells.Count + titles.Count + links.Count > SyncWriteCells;

        var values = ConstructorRepository.CellBulkResult.Nothing;
        var edges  = ConstructorRepository.LinkBulkResult.Nothing;
        try
        {
            if (cells.Count > 0 || titles.Count > 0)
                values = large
                    ? await WithProgressAsync($"{Undone(step.Operation)}: запись в базу…",
                          (_, _) => repository.SetCellsBulkIsolatedAsync(_collectionId, cells, titles, journal),
                          cancellable: false) ?? values
                    : repository.SetCellsBulk(_collectionId, cells, titles, journal);

            if (links.Count > 0)
                edges = large
                    ? await repository.SetLinksBulkIsolatedAsync(_collectionId, links, journal)
                    : repository.SetLinksBulk(_collectionId, links, journal);
        }
        catch (Exception ex) when (DbErrors.Handle(ex, repository.DbPath))
        {
            // База была занята. Шаг возвращается, Ctrl+Z можно нажать снова: вернувшееся уже
            // повтор пропустит по ожидаемому значению, а оставшееся вернет.
            _undoSteps.Add(step);
            if (values.Changed > 0) ReloadRows();
            return;
        }

        var changed   = values.Changed + edges.Changed;
        var conflicts = values.Conflicts + edges.Conflicts;
        if (changed > 0) ReloadRows();

        // Досозданное вставкой — уже без значений: убираем записи и столбцы, в которые после
        // вставки никто ничего не вписал. Без права удалять записи или свойства они остаются:
        // создать их право было, а убрать — нет.
        var (removedRecords, removedFields) = (0, 0);
        var growthRecords = Can(PermAction.DeleteRecord) ? step.Growth.Records : [];
        var growthFields  = Can(PermAction.DeleteField)  ? step.Growth.Fields  : [];
        if (growthRecords.Count + growthFields.Count > 0)
        {
            try
            {
                (removedRecords, removedFields) = repository.RemoveUntouchedPasteGrowth(
                    _collectionId, growthRecords, growthFields);
            }
            catch (Exception ex) when (DbErrors.Handle(ex, repository.DbPath)) { }
            if (removedFields > 0) ReloadColumns();
            if (removedRecords > 0) ReloadRowsStructural(-removedRecords);
            if (removedRecords + removedFields > 0) TableStructureChanged?.Invoke(this, EventArgs.Empty);
        }

        var message = changed + removedRecords + removedFields > 0
            ? $"{Undone(step.Operation)}: возвращено {changed:N0} {ValuesPlural(changed)}"
            : $"{Undone(step.Operation)}: возвращать нечего";
        if (removedRecords > 0)
            message += $", убрано {removedRecords:N0} {Plural(removedRecords, "запись", "записи", "записей")}";
        if (removedFields > 0)
            message += $", убрано {removedFields:N0} {Plural(removedFields, "столбец", "столбца", "столбцов")}";
        if (conflicts > 0)
            message += $"; {conflicts:N0} {ValuesPlural(conflicts)} уже поменяли после этого — их не трогали";
        AppNotifications.Info(message + ".");
    }

    private static string Undone(string operation) => operation switch
    {
        "Вставка"   => "Вставка отменена",
        "Очистка"   => "Очистка отменена",
        "Вырезание" => "Вырезание отменено",
        _           => "Действие отменено",
    };
}
