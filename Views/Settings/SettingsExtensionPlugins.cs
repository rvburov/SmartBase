using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using SmartBase.Controls.Dialogs;
using SmartBase.Extensions;

namespace SmartBase.Views.Settings;

/// <summary>
/// «Настройки → Расширения»: подключаемые расширения (плагины) — список установленных,
/// «Установить из файла…» и «Удалить». Плагин живет в своей папке вместе со всеми
/// библиотеками (<see cref="ExtensionPlugins"/>); установка, обновление и удаление
/// выполняются при следующем запуске программы — загруженные сборки не выгружаются.
/// </summary>
public partial class SettingsView
{
    // Итог последнего действия (установили, отменили…) — строкой под заголовком блока.
    private string? _pluginNotice;

    private void BuildExtensionPluginsBlock()
    {
        ExtensionPluginsBlock.Children.Clear();

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var text = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 32, 0) };
        text.Children.Add(new TextBlock { Classes = { "title" }, Text = "Подключаемые расширения" });
        text.Children.Add(new TextBlock
        {
            Classes = { "desc" },
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Text = "Расширения, установленные отдельно от программы, — каждое в своей папке вместе со всеми " +
                   "своими библиотеками и моделями. Удаление убирает расширение и только его файлы; документы и " +
                   "шаблоны пользователя не затрагиваются. Изменения вступают в силу после перезапуска программы.",
        });
        if (_pluginNotice is not null)
            text.Children.Add(new TextBlock
            {
                Classes = { "desc" },
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                FontWeight = Avalonia.Media.FontWeight.SemiBold,
                Text = _pluginNotice,
            });
        head.Children.Add(text);
        var install = new Button
        {
            Classes = { "dialog" }, Content = "Установить из файла…", VerticalAlignment = VerticalAlignment.Center,
            Width = double.NaN, Padding = new(14, 0),
        };
        install.Click += async (_, _) => await InstallPluginFromFile();
        Grid.SetColumn(install, 1);
        head.Children.Add(install);
        ExtensionPluginsBlock.Children.Add(new Border { Classes = { "settingRow" }, Child = head });

        var installed = ExtensionPlugins.Installed;
        if (installed.Count == 0)
        {
            ExtensionPluginsBlock.Children.Add(new Border
            {
                Classes = { "settingRow" },
                Child = new TextBlock { Classes = { "desc" }, Text = $"Не установлено ни одного. Папка расширений: {ExtensionPlugins.Root}" },
            });
        }
        foreach (var plugin in installed) ExtensionPluginsBlock.Children.Add(BuildPluginRow(plugin));
        ExtensionPluginsBlock.Children.Add(new Border { Classes = { "separator", "h" } });
    }

    private Border BuildPluginRow(InstalledPlugin plugin)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var info = new StackPanel { Spacing = 4, Margin = new(0, 0, 32, 0) };
        info.Children.Add(new TextBlock { Classes = { "title" }, Text = $"{plugin.Name}  ·  версия {plugin.Version}" });
        var state = plugin.State switch
        {
            PluginState.Loaded => "Работает",
            PluginState.PendingInstall => "Будет установлено после перезапуска программы",
            PluginState.PendingRemoval => "Будет удалено после перезапуска программы",
            _ => $"Не загружено: {plugin.Problem}",
        };
        info.Children.Add(new TextBlock { Classes = { "desc" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Text = state });
        info.Children.Add(new TextBlock { Classes = { "desc" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Text = plugin.Folder });
        row.Children.Add(info);

        var button = new Button { Classes = { "dialog" }, VerticalAlignment = VerticalAlignment.Center, Width = double.NaN, Padding = new(14, 0) };
        if (plugin.State == PluginState.PendingRemoval)
        {
            button.Content = "Не удалять";
            button.Click += (_, _) =>
            {
                ExtensionPlugins.CancelRemoval(plugin.Id);
                _pluginNotice = $"Удаление «{plugin.Name}» отменено.";
                BuildExtensionPluginsBlock();
            };
        }
        else
        {
            button.Content = "Удалить";
            button.Click += async (_, _) =>
            {
                if (VisualRoot is not Window owner) return;
                var ok = await new ConfirmDialog(
                    $"Удалить расширение «{plugin.Name}» вместе с его библиотеками?\n\nПапка {plugin.Folder} " +
                    "будет удалена при следующем запуске программы. Документы и шаблоны не затрагиваются.",
                    confirmText: "Удалить", cancelText: "Отмена").ShowDialog<bool>(owner);
                if (!ok) return;
                ExtensionPlugins.ScheduleRemoval(plugin.Id);
                _pluginNotice = $"«{plugin.Name}» будет удалено после перезапуска программы.";
                BuildExtensionPluginsBlock();
            };
        }
        Grid.SetColumn(button, 1);
        row.Children.Add(button);
        return new Border { Classes = { "settingRow" }, Child = row };
    }

    private async Task InstallPluginFromFile()
    {
        if (VisualRoot is not TopLevel top) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Архив расширения",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Архив расширения (.zip)") { Patterns = ["*.zip"] }],
        });
        var path = files.FirstOrDefault()?.Path.LocalPath;
        if (path is null) return;

        var result = await Task.Run(() => ExtensionPlugins.Install(path));
        _pluginNotice = result.Message;
        BuildExtensionPluginsBlock();
    }
}
