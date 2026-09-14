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
/// </summary>
public partial class TableView
{
    /// <summary>Сколько последних действий можно отменить подряд.</summary>
    private const int UndoDepth = 20;

    private sealed record UndoStep(int CollectionId, string Operation, IReadOnlyList<ConstructorRepository.CellChange> Changes);

    private readonly List<UndoStep> _undoSteps = [];

    private bool HasUndo => _undoSteps.Count > 0;

    private void ForgetUndo() => _undoSteps.Clear();

    // Шаг запоминается, только если его можно вернуть целиком: итог без старых значений (больше
    // UndoChangesLimit) отменить нельзя, и об этом человека спросили до записи.
    private void RememberForUndo(string operation, ConstructorRepository.CellBulkResult result)
    {
        if (result.Changed == 0 || result.ChangesTruncated) return;

        _undoSteps.Add(new UndoStep(_collectionId, operation, result.Changes));
        if (_undoSteps.Count > UndoDepth) _undoSteps.RemoveAt(0);
    }

    /// <summary>Отменить последнее действие со значениями ячеек.</summary>
    public void UndoLastCellChange()
    {
        if (_repository is null || _undoSteps.Count == 0) return;
        if (!Can(PermAction.EditRecord)) { PermissionNotice.Show(this, PermAction.EditRecord); return; }

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

        ConstructorRepository.CellBulkResult result;
        try { result = _repository.SetCellsBulk(_collectionId, cells, titles, $"Отмена: {step.Operation.ToLowerInvariant()}"); }
        catch (Exception ex) when (DbErrors.Handle(ex, _repository.DbPath))
        {
            _undoSteps.Add(step);   // база была занята — шаг не потерян, Ctrl+Z можно нажать снова
            return;
        }

        if (result.Changed > 0) ReloadRows();

        var message = result.Changed > 0
            ? $"{Undone(step.Operation)}: возвращено {result.Changed:N0} {ValuesPlural(result.Changed)}"
            : $"{Undone(step.Operation)}: возвращать нечего";
        if (result.Conflicts > 0)
            message += $"; {result.Conflicts:N0} {ValuesPlural(result.Conflicts)} уже поменяли после этого — их не трогали";
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
