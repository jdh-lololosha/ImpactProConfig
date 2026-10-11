using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;

namespace ImpactProConfig.Services;

/// <summary>
/// Слой локализации поверх плоских словарей <c>Languages/*.json</c>.
///
/// Почему так, а не через .resx: словарь — это обычный JSON, который можно
/// править в любом редакторе и диффить как текст. Ключи точечные
/// (<c>Dpi.Levels</c>, <c>Sensor.LiftOff</c>) — по образцу из README.
///
/// Два способа чтения:
///  - XAML: <c>Text="{DynamicResource Dpi.Levels}"</c> — подхватывается
///    автоматически, при смене языка обновляется сам.
///  - код: <c>Loc.T("Dpi.Levels")</c> и <c>Loc.Array("Sleep.Options")</c>.
///
/// Смена языка в рантайме: словарь пересобирается и подменяется в
/// <see cref="Application.Resources.MergedDictionaries"/>. Именно подменой, а
/// не мутацией на месте — DynamicResource обязан увидеть новое значение.
/// </summary>
public static class Loc
{
    /// <summary>Словарь, который сейчас в цепочке ресурсов приложения.</summary>
    private static ResourceDictionary? _dict;

    /// <summary>Активный язык: "ru" или "en".</summary>
    private static string _current = "ru";

    /// <summary>Папка с файлами языков.</summary>
    private static string _dir = "";

    /// <summary>Файл с выбором языка пользователя.</summary>
    private static string _settingsFile = "";

    /// <summary>Активный язык.</summary>
    public static string Current => _current;

    /// <summary>Подписка на смену языка — кто держит строки в коде и хочет перерисоваться.</summary>
    public static event Action? LanguageChanged;

    /// <summary>
    /// Загружает словари и применяет сохранённый язык. Вызывается из
    /// <c>App.OnStartup</c> до создания окна, иначе DynamicResource в XAML
    /// не увидит словаря.
    /// </summary>
    public static void Initialize(string languagesDir)
    {
        _dir = languagesDir;
        _settingsFile = Path.Combine(App.DataDir, "language.txt");
        Apply(LoadSavedLanguage());
    }

    /// <summary>Переключает язык на лету и запоминает выбор.</summary>
    public static void SetLanguage(string lang)
    {
        if (lang != "ru" && lang != "en")
            return;
        if (lang == _current)
            return;

        Apply(lang);
        try
        {
            File.WriteAllText(_settingsFile, lang);
        }
        catch
        {
            // Не смогли запомнить — язык всё равно переключился до перезапуска.
        }
        LanguageChanged?.Invoke();
    }

    /// <summary>Строка по ключу. Если ключа нет — возвращает сам ключ, чтобы пропуск был виден.</summary>
    public static string T(string key) =>
        _dict is not null && _dict[key] is string s ? s : key;

    /// <summary>Массив строк по ключу (для списков и перечислений).</summary>
    public static string[] Array(string key) =>
        _dict is not null && _dict[key] is string[] a ? a : System.Array.Empty<string>();

    private static void Apply(string lang)
    {
        _current = lang;

        var path = Path.Combine(_dir, lang + ".json");
        var json = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);

        var dict = new ResourceDictionary();
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            switch (prop.Value.ValueKind)
            {
                case JsonValueKind.String:
                    dict[prop.Name] = prop.Value.GetString();
                    break;
                case JsonValueKind.Array:
                    dict[prop.Name] = prop.Value.EnumerateArray()
                        .Select(e => e.GetString() ?? string.Empty)
                        .ToArray();
                    break;
            }
        }

        var app = Application.Current;
        if (app is null)
        {
            _dict = dict;
            return;
        }

        // Подмена, а не мутация: убираем старый словарь из цепочки и вставляем
        // новый. Так DynamicResource гарантированно перечитает значения.
        if (_dict is not null)
            app.Resources.MergedDictionaries.Remove(_dict);
        _dict = dict;
        app.Resources.MergedDictionaries.Add(dict);
    }

    private static string LoadSavedLanguage()
    {
        try
        {
            if (File.Exists(_settingsFile))
            {
                var saved = File.ReadAllText(_settingsFile).Trim();
                if (saved is "ru" or "en")
                    return saved;
            }
        }
        catch
        {
            // Нет файла или не читается — остаёмся на русском.
        }
        return "ru";
    }
}
