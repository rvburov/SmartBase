using System;
using System.Reflection;
using Avalonia.Controls;

namespace SmartBase.Views;

/// <summary>
/// Стартовый сплэш-экран (в стиле установщика: название + версия). Показывается сразу, пока в
/// фоне монтируются базы данных — убирает «пустой/зависший» запуск на больших БД (первое открытие
/// может включать разовую миграцию/бэкафилл). Полосу двигает единый
/// <see cref="Controls.Components.ProgressIndicator"/> в smooth-режиме; долгие этапы репортят
/// живую долю (процент миграции — по росту WAL, см. DatabaseManager), по готовности — добег
/// до 100% и запуск главного окна.
/// </summary>
public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
        // Версия — из сборки, как в окне установщика (единый источник).
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = v is null ? "" : $"Версия {v.Major}.{v.Minor}.{v.Build}";
        // Стартовая ступень маленькая: реальные сегменты шкалы выставит SetStage по этапам.
        Indicator.StartSmooth("Подготовка…", cap: 0.05);
    }

    /// <summary>Этап запуска со своим сегментом шкалы: полоса плавно идет к <paramref name="cap"/>.
    /// Перевод строки в сообщении делит его на фазу (жирная строка) и доп. строку под ней —
    /// туда долгие этапы кладут живую оценку времени («осталось ≈ 2 мин»).</summary>
    public void SetStage(double cap, string message)
    {
        var nl = message.IndexOf('\n');
        Indicator.SetInfo(nl < 0 ? "" : message[(nl + 1)..]);
        Indicator.SetStage(cap, nl < 0 ? message : message[..nl]);
    }

    /// <summary>Работа завершена: плавно добежать до 100% и затем вызвать <paramref name="onDone"/>
    /// (там показывается главное окно и закрывается сплэш).</summary>
    public void Finish(string message, Action onDone)
    {
        Indicator.SetInfo("");
        Indicator.FinishSmooth(message, onDone);
    }

    /// <summary>Показать ошибку запуска вместо прогресса (проценты скрываются).</summary>
    public void SetError(string message)
    {
        Indicator.SetInfo("");
        Indicator.SetError(message);
    }
}
