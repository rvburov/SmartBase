using Avalonia.Controls;
using Avalonia.Threading;
using SmartBase.Controls.DrawnGrid;
using SmartBase.Controls.Widgets;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Repositories;

namespace SmartBase.Views.ConstructorDb;

/// <summary>
/// Блок «Ссылаются на эту запись»: какие записи ссылаются на показанную — по каждому
/// свойству-связи своей базы и подключенных других баз, нацеленному на ее коллекцию. Карточки
/// встают хвостом списка свойств после «Добавить свойство»: подпись раздела, под ней «Счета ·
/// Клиент» с числом записей и первыми названиями. Читается в фоне
/// (<see cref="RelationBacklinkReader"/>) после показа записи: свои карточки стоят на месте, когда
/// приходит ответ.
///
/// Карточка ссылок не выделяется и меню не дает: нажатие открывает список источников
/// (<see cref="RelationBacklinkBox"/>), строка списка переводит к записи-источнику. Ответ, пришедший
/// во время ввода в свойстве или при открытом списке, ждет, пока редактор освободится: набираемое
/// не теряется, фокус не уходит. Пустые карточки прячет «Скрыть пустые свойства», раздел без
/// видимых карточек уходит вместе с подписью.
/// </summary>
public partial class RecordPageView
{
    private RelationBacklinkReader? _backlinkReader;
    private IReadOnlyList<RelationBacklinkSection>? _backlinks;
    // Ответ, который ждет свободного редактора.
    private (RelationBacklinkReader Reader, IReadOnlyList<RelationBacklinkSection> Sections)? _pendingBacklinks;
    // Из какого ответа и при каком «Скрыть пустые свойства» построены карточки — быстрая проверка
    // UpdatePropertyCards.
    private IReadOnlyList<RelationBacklinkSection>? _cardsBacklinks;
    private bool _cardsHideEmpty;
    private CancellationTokenSource? _backlinksCts;
    private bool _backlinksAgain;

    /// <summary>Момент, на который блок ссылок соответствует базам (как <see cref="DataStamp"/>).</summary>
    public long BacklinksStamp { get; private set; }

    /// <summary>Чтение ссылок еще идет (стенды ждут ответа).</summary>
    internal bool BacklinksLoading => _backlinksCts is not null;

    /// <summary>Панель показывает запись этой базы — тем же репозиторием. База, отключенная и
    /// подключенная снова, — новый репозиторий: прежний закрыт, и панель его не перечитывает.</summary>
    public bool Shows(ConstructorRepository? repo) => _vm is not null && repo is not null && ReferenceEquals(_repo, repo);

    /// <summary>Коммит в этой базе может изменить блок ссылок: база записи или прочитанная другая
    /// база. Пока блок не прочитан (первое чтение, сбой), годится любая подключенная база.</summary>
    public bool BacklinksWatch(ConstructorRepository? repo) =>
        repo is not null && _vm is not null && (Shows(repo) || (_backlinkReader?.Reads(repo.DbPath) ?? true));

    /// <summary>Перечитать блок ссылок: коммит в базе-источнике, подключили или отключили базу,
    /// сменились права.</summary>
    public void RefreshBacklinks() => LoadBacklinks();

    private void ResetBacklinks()
    {
        _backlinksCts?.Cancel();
        _backlinksCts = null;
        _backlinksAgain = false;
        _backlinkReader = null;
        _backlinks = null;
        _pendingBacklinks = null;
        _cardsBacklinks = null;
    }

    private void LoadBacklinks()
    {
        if (_vm is null || _repo is null) return;
        // Чтение уже идет — повторим после него: отменять почти готовое незачем.
        if (_backlinksCts is not null) { _backlinksAgain = true; return; }
        RelationBacklinkReader reader;
        try { reader = RelationBacklinkReader.Capture(_repo, _vm.RecordId, _vm.CollectionId); }
        catch (Exception ex) when (ex is ObjectDisposedException or System.Data.Common.DbException or InvalidOperationException)
        {
            // База записи только что отключена: ее репозиторий закрыт, читать нечего.
            Serilog.Log.Warning(ex, "Ссылки на запись не прочитаны: база записи закрыта");
            return;
        }
        var cts = _backlinksCts = new CancellationTokenSource();
        var stamp = ChangeBus.Now();
        _ = RunAsync();

        async Task RunAsync()
        {
            IReadOnlyList<RelationBacklinkSection>? sections = null;
            try { sections = await Task.Run(() => reader.Load(cts.Token), cts.Token); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Serilog.Log.Warning(ex, "Не удалось прочитать ссылки на запись"); }
            var current = ReferenceEquals(_backlinksCts, cts);
            cts.Dispose();
            if (!current) return;   // показана другая запись
            _backlinksCts = null;
            if (sections is not null)
            {
                BacklinksStamp = stamp;
                ApplyBacklinks(reader, sections);
            }
            if (_backlinksAgain)
            {
                _backlinksAgain = false;
                LoadBacklinks();
            }
        }
    }

    private void ApplyBacklinks(RelationBacklinkReader reader, IReadOnlyList<RelationBacklinkSection> sections)
    {
        // Тот же ответ — карточки и открытый список не трогаем.
        if (_backlinks is not null && RelationBacklinkSection.Same(_backlinks, sections))
        {
            _backlinkReader = reader;
            _pendingBacklinks = null;
            return;
        }
        if (PropertiesHost.IsEditorBusy)
        {
            _pendingBacklinks = (reader, sections);
            return;
        }
        _pendingBacklinks = null;
        _backlinkReader = reader;
        _backlinks = sections;
        UpdatePropertyCards();
    }

    // Редактор освободился (снят или потерял фокус) — отложенный ответ встает на место.
    private void SchedulePendingBacklinks()
    {
        if (_pendingBacklinks is null) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_pendingBacklinks is { } pending && !PropertiesHost.IsEditorBusy)
                ApplyBacklinks(pending.Reader, pending.Sections);
        }, DispatcherPriority.Background);
    }

    // Хвост списка свойств: разделы ссылок после «Добавить свойство».
    private IEnumerable<PropertyCard> BacklinkCards()
    {
        _cardsBacklinks = _backlinks;
        _cardsHideEmpty = FooterBar.HideEmptyProperties;
        if (_backlinks is null) yield break;
        foreach (var section in _backlinks)
        {
            var groups = section.Groups.Where(g => !_cardsHideEmpty || g.Sources > 0).ToList();
            if (groups.Count == 0 && !section.Failed) continue;
            yield return new PropertyCard { Name = RelationBacklinkText.Header(section.BaseName), IconKey = "", Kind = PropertyCardKind.Section };
            if (section.Failed)
                yield return new PropertyCard
                {
                    Name = RelationBacklinkText.FailedName, IconKey = "IconFieldRelation", Kind = PropertyCardKind.Backlink,
                    FieldType = "relation", Muted = true, Display = static () => RelationBacklinkText.FailedValue,
                };
            foreach (var group in groups)
                yield return new PropertyCard
                {
                    Name = RelationBacklinkText.Name(group), IconKey = "IconFieldRelation", Kind = PropertyCardKind.Backlink,
                    FieldType = "relation", Tag = group, Placeholder = RelationBacklinkText.None,
                    Display = () => group.Sources == 0 ? "" : RelationBacklinkText.Value(group),
                };
        }
    }

    private RelationBacklinkBox? MakeBacklinkBox(RelationBacklinkGroup group)
    {
        if (_backlinkReader is not { } reader) return null;
        var box = new RelationBacklinkBox(reader, group);
        box.Navigate += (_, t) => NavigateTo(t.SourceDbId, t.RecordId);
        box.ListClosed += (_, _) => Dispatcher.UIThread.Post(ApplyDeferredReload, DispatcherPriority.Background);
        return box;
    }

    // Открытый список источников — долгий просмотр (поиск, подгрузка): перечитывание панели по
    // чужой правке закрыло бы его на полуслове. Оно ждет, пока список закроют.
    private bool _reloadAfterList;

    private bool DeferReloadForList()
    {
        if (PropertiesHost.CurrentEditor is not RelationBacklinkBox { IsListOpen: true }) return false;
        _reloadAfterList = true;
        return true;
    }

    private void ApplyDeferredReload()
    {
        if (!_reloadAfterList || PropertiesHost.CurrentEditor is RelationBacklinkBox { IsListOpen: true }) return;
        _reloadAfterList = false;
        ReloadFromDb();
    }

    // Нажатие на карточку ссылок (кроме ручки ⋮): выделение свойств снимается, открывается список
    // источников. Сама карточка в набор не входит — Delete и сочетания свойств до нее не дотянутся.
    private void PressBacklink(int index, PropertyPointerEventArgs e)
    {
        e.Pointer.Handled = true;
        if (_cards[index].Tag is not RelationBacklinkGroup) return;   // база не прочитана — открывать нечего
        ClearPropertySelection();
        if (_titleSelected)
        {
            _titleSelected = false;
            PropertiesHost.RefreshValues();
        }
        // Фокус уходит из поля другого свойства: занятый редактор иначе не уступил бы места.
        PropertiesHost.Surface.Focus();
        (PropertiesHost.OpenEditor(index) as RelationBacklinkBox)?.OpenList();
    }

    // Переход к записи. База 0 — «в базе ЭТОЙ записи», а она не обязательно активная: панель могла
    // быть открыта межбазовым переходом. Подставляем базу панели (_dbId), иначе внутрибазовая связь
    // чужой записи открывалась бы в АКТИВНОЙ базе — чужая или пустая.
    private void NavigateTo(int dbId, int recordId)
    {
        var db = dbId > 0 ? dbId : _dbId;
        if (db > 0) CrossDbRelatedRecordRequested?.Invoke(this, (db, recordId));
        else RelatedRecordRequested?.Invoke(this, recordId);
    }
}
