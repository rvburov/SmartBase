using SmartBase.Integrations;
using SmartBase.Utils;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Чтение источников политики с диска (<see cref="PolicySources"/>) и снимок последней
/// известной копии.
///
/// Главное, что здесь проверяется, — различие двух непохожих «файла нет»: папка на месте,
/// а файла в ней больше нет (источник отменили) и папки не видно вовсе (источник цел, но
/// недоступен). Спутать их нельзя ни в ту, ни в другую сторону: в первом случае снятую
/// политику стало бы невозможно снять, во втором выключенный на ночь сервер расширял бы
/// права.
///
/// Ветку реестра тесты не трогают: писать в <c>HKLM</c> из тестов нельзя, да и незачем —
/// вся логика вокруг нее общая с папками.
/// </summary>
public sealed class PolicySourcesTests : IDisposable
{
    private const string Policy = """{"mode":"external","hosts":["api.zavod.local"]}""";
    private const string Other  = """{"mode":"external","hosts":["api.drugoy.local"]}""";

    private sealed class FakeStore : ISettingsDatabase
    {
        private readonly Dictionary<string, string> _d = new();
        public string? Get(string key) => _d.TryGetValue(key, out var v) ? v : null;
        public void Set(string key, string value) => _d[key] = value;
        public void Remove(string key) => _d.Remove(key);
        public IReadOnlyList<string> KeysWithPrefix(string prefix) =>
            _d.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
    }

    private readonly string _root;

    public PolicySourcesTests()
    {
        AppSettings.AttachStore(new FakeStore());
        _root = Path.Combine(Path.GetTempPath(), "smartbase-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* уборка не важнее теста */ }
    }

    private string Folder(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Write(string folder, string json, string? signature = null)
    {
        File.WriteAllText(Path.Combine(folder, PolicySources.FileName), json);
        if (signature is not null)
            File.WriteAllText(Path.Combine(folder, PolicySources.FileName + PolicySources.SignatureExtension), signature);
    }

    // ── Обычное чтение ────────────────────────────────────────────────────────

    [Fact]
    public void Policy_file_is_read_from_the_folder()
    {
        var folder = Folder("ustanovka");
        Write(folder, Policy);

        var candidate = PolicySources.FromFolder("папка установки", "install", folder, signatureRequired: false);

        Assert.NotNull(candidate);
        Assert.Equal("папка установки", candidate!.Source);
        Assert.Equal(Policy, candidate.Json);
        Assert.False(candidate.SignatureRequired);
    }

    [Fact]
    public void Signature_next_to_the_file_is_picked_up()
    {
        var folder = Folder("razdacha");
        Write(folder, Policy, signature: "cGVjaGF0aw==");

        var candidate = PolicySources.FromFolder("папка раздачи", "share", folder, signatureRequired: true);

        Assert.NotNull(candidate);
        Assert.Equal("cGVjaGF0aw==", candidate!.Signature);
        Assert.True(candidate.SignatureRequired);
    }

    // ── Источник отменили ─────────────────────────────────────────────────────

    /// <summary>
    /// Папка на месте, файла нет — источник отменили. Снимок при этом забывается: иначе
    /// снятая политика действовала бы вечно и снять ее у службы ИТ не вышло бы никак.
    /// </summary>
    [Fact]
    public void Missing_file_in_an_existing_folder_removes_the_source_and_forgets_the_snapshot()
    {
        var folder = Folder("ustanovka");
        Write(folder, Policy);
        PolicySources.FromFolder("папка установки", "install", folder, signatureRequired: false);
        Assert.NotEqual("", AppSettings.LoadPolicySnapshot("install"));

        File.Delete(Path.Combine(folder, PolicySources.FileName));
        var candidate = PolicySources.FromFolder("папка установки", "install", folder, signatureRequired: false);

        Assert.Null(candidate);
        Assert.Equal("", AppSettings.LoadPolicySnapshot("install"));
    }

    // ── Источник недоступен ───────────────────────────────────────────────────

    /// <summary>
    /// Папки не видно — выключили сервер, отвалился диск. Действует последняя известная
    /// копия, и в имени источника это видно: человек на экране «Безопасность» должен
    /// понимать, что смотрит на вчерашнюю политику.
    /// </summary>
    [Fact]
    public void Unreachable_folder_falls_back_to_the_last_known_copy()
    {
        var folder = Folder("razdacha");
        Write(folder, Policy);
        PolicySources.FromFolder("папка раздачи", "share", folder, signatureRequired: true);

        Directory.Delete(folder, recursive: true);
        var candidate = PolicySources.FromFolder("папка раздачи", "share", folder, signatureRequired: true);

        Assert.NotNull(candidate);
        Assert.Equal(Policy, candidate!.Json);
        Assert.Contains(PolicySources.LastKnownSuffix, candidate.Source, StringComparison.Ordinal);
        Assert.True(candidate.SignatureRequired);
    }

    [Fact]
    public void Unreachable_folder_without_a_snapshot_gives_no_source()
    {
        var candidate = PolicySources.FromFolder(
            "папка раздачи", "share", Path.Combine(_root, "net-takoy-papki"), signatureRequired: true);

        Assert.Null(candidate);
    }

    /// <summary>
    /// Путь к папке раздачи задается в настройках, то есть его может стереть пользователь.
    /// Ограничения организации от этого сниматься не должны: пока снимок есть, источник
    /// считается недоступным, а не отмененным.
    /// </summary>
    [Fact]
    public void Cleared_folder_path_keeps_the_last_known_copy()
    {
        var folder = Folder("razdacha");
        Write(folder, Policy);
        PolicySources.FromFolder("папка раздачи", "share", folder, signatureRequired: true);

        var candidate = PolicySources.FromFolder("папка раздачи", "share", "", signatureRequired: true);

        Assert.NotNull(candidate);
        Assert.Equal(Policy, candidate!.Json);
        Assert.Contains(PolicySources.LastKnownSuffix, candidate.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void Cleared_folder_path_without_a_snapshot_gives_no_source()
    {
        Assert.Null(PolicySources.FromFolder("папка раздачи", "share", "", signatureRequired: true));
    }

    // ── Источник не читается ──────────────────────────────────────────────────

    /// <summary>
    /// Папку видно, а файл в ней не читается: нет прав, держит кто-то другой. Источник
    /// недоступен — и требование подписи с него НЕ снимается.
    ///
    /// Почему это важнее, чем кажется. Снимок пишется РАНЬШЕ, чем подпись проверена: чтение
    /// запоминает текст и отдает кандидата, а сверяет его уже <c>PolicyLoader</c>. Значит в
    /// снимке вполне может лежать неподписанный файл из папки раздачи. Сними здесь требование —
    /// и файл, отвергнутый при первом чтении, прошел бы со второго: достаточно, чтобы папка
    /// ответила отказом в доступе. Недоступность обязана сужать права, а не открывать то, что
    /// уже отвергли.
    /// </summary>
    [Fact]
    public void Unreadable_file_keeps_the_signature_requirement()
    {
        var folder = Folder("razdacha");
        Write(folder, Policy);   // подписи рядом нет

        var first = PolicySources.FromFolder("папка раздачи", "share", folder, signatureRequired: true);
        Assert.NotNull(first);
        Assert.True(first!.SignatureRequired);

        // Файл держат монопольно — прочитать его больше нельзя.
        using var held = new FileStream(Path.Combine(folder, PolicySources.FileName),
                                        FileMode.Open, FileAccess.Read, FileShare.None);

        var candidate = PolicySources.FromFolder("папка раздачи", "share", folder, signatureRequired: true);

        Assert.NotNull(candidate);
        Assert.Equal(Policy, candidate!.Json);
        Assert.Contains(PolicySources.LastKnownSuffix, candidate.Source, StringComparison.Ordinal);
        Assert.True(candidate.SignatureRequired);
    }

    /// <summary>Снимка нет — кандидат без текста: загрузчику этого хватит, чтобы запереть контур.</summary>
    [Fact]
    public void Unreadable_file_without_a_snapshot_gives_a_candidate_without_text()
    {
        var folder = Folder("razdacha");
        Write(folder, Policy);

        using var held = new FileStream(Path.Combine(folder, PolicySources.FileName),
                                        FileMode.Open, FileAccess.Read, FileShare.None);

        var candidate = PolicySources.FromFolder("папка раздачи", "share", folder, signatureRequired: true);

        Assert.NotNull(candidate);
        Assert.Null(candidate!.Json);
    }

    // ── Снимок обновляется ────────────────────────────────────────────────────

    [Fact]
    public void Snapshot_follows_the_file()
    {
        var folder = Folder("razdacha");

        Write(folder, Policy, signature: "cGVydmF5YQ==");
        PolicySources.FromFolder("папка раздачи", "share", folder, signatureRequired: true);

        Write(folder, Other, signature: "dnRvcmF5YQ==");
        PolicySources.FromFolder("папка раздачи", "share", folder, signatureRequired: true);

        Directory.Delete(folder, recursive: true);
        var candidate = PolicySources.FromFolder("папка раздачи", "share", folder, signatureRequired: true);

        Assert.NotNull(candidate);
        Assert.Equal(Other, candidate!.Json);
        Assert.Equal("dnRvcmF5YQ==", candidate.Signature);
    }

    // ── Сборка всех источников ────────────────────────────────────────────────

    /// <summary>
    /// Папке раздачи подпись обязательна, папке установки нет: в первую пишут без прав
    /// администратора, вторая защищена правами системы. Спутать эти два случая значит либо
    /// требовать подпись там, где ее некому ставить, либо принимать неподписанный файл
    /// из общей папки.
    /// </summary>
    [Fact]
    public void Share_folder_requires_a_signature_and_the_install_folder_does_not()
    {
        var install = Folder("ustanovka");
        var share   = Folder("razdacha");
        Write(install, Policy);
        Write(share, Policy, signature: "cGVjaGF0aw==");

        var candidates = PolicySources.Collect(install, share);

        var installCandidate = candidates.Single(c => c.Source.StartsWith("папка установки", StringComparison.Ordinal));
        var shareCandidate   = candidates.Single(c => c.Source.StartsWith("папка раздачи", StringComparison.Ordinal));

        Assert.False(installCandidate.SignatureRequired);
        Assert.True(shareCandidate.SignatureRequired);
    }

    /// <summary>
    /// Ни одного источника — пустой список, а не выдуманный «разрешающий» кандидат. Запрет
    /// на этот случай ставит загрузчик: пустой набор источников значит «ничего нельзя».
    /// </summary>
    [Fact]
    public void No_policy_anywhere_gives_no_candidates()
    {
        var candidates = PolicySources.Collect(Folder("pusto-1"), Folder("pusto-2"));

        Assert.DoesNotContain(candidates, c => c.Source.StartsWith("папка", StringComparison.Ordinal));
    }
}
