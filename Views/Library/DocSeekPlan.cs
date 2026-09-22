using SmartBase.Core.Models;
using SmartBase.Database.Repositories;

namespace SmartBase.Views.Library;

/// <summary>
/// Как дойти до куска потока ленты самым коротким путем. Чистая арифметика без базы и без
/// Avalonia — ее проверяют <c>DocSeekPlanTests</c>.
///
/// Цена пути — сколько строк база пропустит, прежде чем отдать нужные: смещение (OFFSET)
/// шагает по каждой из них. Путей четыре:
/// <list type="bullet">
/// <item>от начала потока — пропустить <c>local</c> строк;</item>
/// <item>от конца — поток читается в обратном порядке, пропустить все, что ниже куска;</item>
/// <item>после известного документа выше куска — keyset-курсор, пропустить только разрыв;</item>
/// <item>перед известным документом ниже куска — то же при прокрутке вверх.</item>
/// </list>
///
/// Прокрутка подряд всегда находит соседа вплотную, и страница идет за ноль пропущенных строк —
/// на любой глубине (замер на 125 000 документов: 1,3 мс против 455 мс смещением в конце).
/// Прыжок ползунком соседа не имеет, и тогда считается от ближнего края или от ближнего
/// известного документа. Дороже всего остается середина длинного потока, но худший случай
/// вдвое дешевле прежнего: раньше им был конец, теперь конец — такой же ближний край, как начало.
/// </summary>
public static class DocSeekPlan
{
    /// <param name="local">Номер первого документа куска внутри потока.</param>
    /// <param name="take">Сколько документов в куске.</param>
    /// <param name="docs">Сколько документов в потоке всего.</param>
    /// <param name="known">Документы потока, чье место в порядке уже известно: номер → курсор.</param>
    public static ConstructorRepository.DocSeek Choose(int local, int take, int docs,
                                                       IReadOnlyDictionary<int, DocCursor> known)
    {
        var best = ConstructorRepository.DocSeek.FromStart(local);

        // От конца — только если поток знает свой размер правильно: кусок, вылезающий за конец,
        // от конца не посчитать.
        var fromEnd = docs - (local + take);
        if (fromEnd >= 0 && fromEnd < best.Skip)
            best = new(ConstructorRepository.DocSeekOrigin.End, fromEnd);

        foreach (var (at, cursor) in known)
        {
            if (at < local)
            {
                var skip = local - at - 1;
                if (skip < best.Skip) best = new(ConstructorRepository.DocSeekOrigin.After, skip, cursor);
            }
            else if (at >= local + take)
            {
                var skip = at - (local + take);
                if (skip < best.Skip) best = new(ConstructorRepository.DocSeekOrigin.Before, skip, cursor);
            }
        }
        return best;
    }
}
