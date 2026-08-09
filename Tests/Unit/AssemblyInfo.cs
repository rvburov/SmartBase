// Модульные тесты гоняются последовательно: AppSettings.AttachStore кладёт хранилище
// настроек в статическое поле, одно на процесс. AutostartTests и NotificationSettingsTests —
// разные классы, значит разные коллекции xUnit, значит по умолчанию параллельные прогоны:
// один тест подменял store другому между Attach и проверкой, и
// Autostart_and_background_default_off_and_round_trip_as_deltas падал примерно в четверти
// запусков (Assert.True(store.Has("autostartEnabled")) — запись уходила в чужой store).
//
// Полный прогон занимает ~70 мс, так что последовательность ничего не стоит. Альтернатива —
// собрать классы в одну [Collection] — держалась бы только до следующего теста, который
// тронет статику: их тут хватает (AppSettings, CurrentUser, FontScale).
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
