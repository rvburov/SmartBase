using SmartBase.Core.Models;

namespace SmartBase.Database.Repositories;

/// <summary>
/// Все, что таблица читает из базы при открытии коллекции, одним снимком: коллекция, поля,
/// подписи системных полей, порядок/видимость/стили/ширины колонок, видимость/высоты/стили
/// строк, число записей и настройки вида. Снимок читается в фоне на собственном контексте
/// (<see cref="ConstructorRepository.ReadOpenSnapshotAsync"/>), а UI-поток лишь строит по нему
/// колонки и строки. Раньше эти десять с лишним запросов шли с UI-потока по одному, и на медленной
/// сетевой папке открытие коллекции замораживало окно на секунды («Интерфейс не отвечал 6,7 с —
/// выполнялось: открытие коллекции»). Отсутствующая коллекция — <see cref="Collection"/> = null.
/// </summary>
public sealed record CollectionOpenSnapshot(
    DbCollection?                                Collection,
    (bool ColResize, bool RowResize, string ViewMode) UiSettings,
    List<DbField>                                Fields,
    Dictionary<string, string>                   SystemNames,
    Dictionary<string, int>                      ColumnOrder,
    Dictionary<string, (bool Visible, int Level)> ColumnDisplay,
    Dictionary<string, RowStyle>                 ColumnStyles,
    Dictionary<string, double>                   ColumnWidths,
    Dictionary<int, (bool Visible, int Level)>   RecordDisplay,
    Dictionary<int, int>                         RowHeights,
    Dictionary<int, RowStyle>                    RowStyles,
    int                                          RecordCount,
    int                                          VisibleRecordCount);
