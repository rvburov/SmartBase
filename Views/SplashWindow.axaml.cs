using System;
using System.Reflection;
using Avalonia.Controls;

namespace DataBase.Views;

/// <summary>
/// Стартовый сплэш-экран (в стиле установщика: название + версия). Показывается сразу, пока в
/// фоне монтируются базы данных — убирает «пустой/зависший» запуск на больших БД (первое открытие
/// может включать разовую миграцию/бэкафилл). Полосу двигает единый
/// <see cref="Controls.Components.ProgressIndicator"/> в smooth-режиме: плавно и непрерывно
/// (реальный процент EF-миграции недоступен), по готовности — добег до 100% и запуск главного окна.
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

    /// <summary>Этап запуска со своим сегментом шкалы: полоса плавно идёт к <paramref name="cap"/>.</summary>
    public void SetStage(double cap, string message) => Indicator.SetStage(cap, message);

    /// <summary>Работа завершена: плавно добежать до 100% и затем вызвать <paramref name="onDone"/>
    /// (там показывается главное окно и закрывается сплэш).</summary>
    public void Finish(string message, Action onDone) => Indicator.FinishSmooth(message, onDone);

    /// <summary>Показать ошибку запуска вместо прогресса (проценты скрываются).</summary>
    public void SetError(string message) => Indicator.SetError(message);
}
