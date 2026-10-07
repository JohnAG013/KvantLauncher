using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace KVANTLauncher.Services;

/// <summary>
/// Проверка SHA-256 всех скачиваемых файлов по trusted_assets.json.
/// Несовпадение хеша = отказ от файла, удаление файла и запись в logs/launcher_errors.log.
/// </summary>
public sealed class TrustedSource
{
    [JsonProperty("url")] public string Url { get; set; } = "";
    [JsonProperty("sha256")] public string Sha256 { get; set; } = "";
    [JsonProperty("size")] public long Size { get; set; }
}

public sealed class TrustedAsset
{
    [JsonProperty("id")] public string Id { get; set; } = "";
    /// <summary>pinned — хеш зашит в trusted_assets.json; remote-sha256 — хеш берётся с checksumUrl;
    /// adoptium-api — ссылка и хеш берутся из официального API Adoptium.</summary>
    [JsonProperty("kind")] public string Kind { get; set; } = "pinned";
    [JsonProperty("sources")] public List<TrustedSource> Sources { get; set; } = new();
    /// <summary>Шаблон URL, возвращающий текст с хешем. {url} подставляется на адрес файла.</summary>
    [JsonProperty("checksumUrl")] public string ChecksumUrl { get; set; } = "";
    [JsonProperty("requireHash")] public bool RequireHash { get; set; } = true;
    [JsonProperty("minSize")] public long MinSize { get; set; }
}

public static class DownloadVerifier
{
    private static readonly object _lock = new();
    private static Dictionary<string, TrustedAsset>? _assets;

    private static string LogPath => LauncherLog.LogFilePath;

    public static void Log(string message) => LauncherLog.Error(message);

    /// <summary>Загружает trusted_assets.json (лежит рядом с exe).</summary>
    public static IReadOnlyDictionary<string, TrustedAsset> Load()
    {
        lock (_lock)
        {
            if (_assets != null) return _assets;

            string file = System.IO.Path.Combine(AppContext.BaseDirectory, "trusted_assets.json");
            try
            {
                if (!File.Exists(file))
                {
                    Log($"SECURITY: trusted_assets.json не найден ({file}) — проверка хешей невозможна");
                    _assets = new Dictionary<string, TrustedAsset>(StringComparer.OrdinalIgnoreCase);
                    return _assets;
                }

                var parsed = JsonConvert.DeserializeObject<TrustedAssetsFile>(File.ReadAllText(file));
                _assets = (parsed?.Assets ?? new List<TrustedAsset>())
                    .ToDictionary(a => a.Id, a => a, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Log($"SECURITY: не удалось разобрать trusted_assets.json — {ex.Message}");
                _assets = new Dictionary<string, TrustedAsset>(StringComparer.OrdinalIgnoreCase);
            }
            return _assets;
        }
    }

    private sealed class TrustedAssetsFile
    {
        [JsonProperty("assets")] public List<TrustedAsset> Assets { get; set; } = new();
    }

    public static TrustedAsset? GetAsset(string assetId)
    {
        Load().TryGetValue(assetId, out var asset);
        return asset;
    }

    /// <summary>SHA-256 файла (нижний регистр, без дефисов).</summary>
    public static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(stream);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (byte b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    /// <summary>
    /// Проверка по зашитому хешу: источник должен быть указан в trusted_assets.json,
    /// размер и SHA-256 должны совпасть. Иначе — отказ (false) + лог.
    /// </summary>
    public static bool VerifyPinned(string assetId, string url, string filePath)
    {
        var asset = GetAsset(assetId);
        if (asset == null)
        {
            Log($"SECURITY: отказ — актив {assetId} отсутствует в trusted_assets.json ({filePath})");
            Reject(filePath);
            return false;
        }

        long size = new FileInfo(filePath).Length;
        if (size < asset.MinSize)
        {
            Log($"SECURITY: отказ — {assetId}: файл подозрительно мал ({size} байт, минимум {asset.MinSize})");
            Reject(filePath);
            return false;
        }

        var source = asset.Sources.FirstOrDefault(s =>
            string.Equals(NormalizeUrl(s.Url), NormalizeUrl(url), StringComparison.OrdinalIgnoreCase));
        if (source == null)
        {
            Log($"SECURITY: отказ — {assetId}: источник не входит в список доверенных: {url}");
            Reject(filePath);
            return false;
        }

        string actual = ComputeSha256(filePath);
        if (!string.Equals(actual, source.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            Log($"SECURITY: отказ — {assetId}: SHA-256 не совпал!\n  источник: {url}\n  ожидалось: {source.Sha256}\n  получено:  {actual}");
            Reject(filePath);
            return false;
        }

        Log($"SECURITY: {assetId}: SHA-256 подтверждён ({actual[..16]}...)");
        return true;
    }

    /// <summary>Проверка против заранее полученного ожидаемого хеша (например, из API Adoptium).</summary>
    public static bool VerifyAgainstExpected(string assetId, string expectedSha256, string filePath, long expectedSize = 0)
    {
        var asset = GetAsset(assetId);
        long size = new FileInfo(filePath).Length;
        if (asset != null && size < asset.MinSize)
        {
            Log($"SECURITY: отказ — {assetId}: файл подозрительно мал ({size} байт)");
            Reject(filePath);
            return false;
        }

        string actual = ComputeSha256(filePath);
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            Log($"SECURITY: отказ — {assetId}: SHA-256 не совпал!\n  ожидалось: {expectedSha256}\n  получено:  {actual}");
            Reject(filePath);
            return false;
        }

        if (expectedSize > 0 && Math.Abs(size - expectedSize) > 0)
        {
            Log($"SECURITY: отказ — {assetId}: размер {size} не совпал с заявленным {expectedSize}");
            Reject(filePath);
            return false;
        }

        Log($"SECURITY: {assetId}: SHA-256 подтверждён ({actual[..16]}...)");
        return true;
    }

    /// <summary>
    /// Проверка через официальный источник чексумм (checksumUrl, например maven: файл.sha256).
    /// Если источник чексумм недоступен: при requireHash=true — отказ, иначе — предупреждение в лог.
    /// </summary>
    public static async Task<bool> VerifyRemoteChecksumAsync(string assetId, string fileUrl, string filePath, HttpClient? client = null)
    {
        var asset = GetAsset(assetId);
        if (asset == null)
        {
            Log($"SECURITY: отказ — актив {assetId} отсутствует в trusted_assets.json");
            Reject(filePath);
            return false;
        }

        long size = new FileInfo(filePath).Length;
        if (size < asset.MinSize)
        {
            Log($"SECURITY: отказ — {assetId}: файл подозрительно мал ({size} байт)");
            Reject(filePath);
            return false;
        }

        string actual = ComputeSha256(filePath);
        string template = string.IsNullOrEmpty(asset.ChecksumUrl) ? "{url}.sha256" : asset.ChecksumUrl;

        // 1. Официальный SHA-256 рядом с файлом (maven-стиль: файл.sha256)
        string sha256Url = template.Replace("{url}", fileUrl);
        string? expected = await TryFetchChecksumAsync(client, sha256Url);
        if (expected != null && expected.Length == 64)
        {
            if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                Log($"SECURITY: {assetId}: SHA-256 подтверждён по {sha256Url}");
                return true;
            }
            Log($"SECURITY: отказ — {assetId}: SHA-256 не совпал с официальным чексуммом!\n  источник: {sha256Url}\n  ожидалось: {expected}\n  получено:  {actual}");
            Reject(filePath);
            return false;
        }

        // 2. Официальный SHA-1 рядом с файлом (файл.sha1)
        string? expectedSha1 = await TryFetchChecksumAsync(client, fileUrl + ".sha1");
        if (expectedSha1 != null && expectedSha1.Length == 40)
        {
            string actualSha1 = ComputeSha1(filePath);
            if (string.Equals(actualSha1, expectedSha1, StringComparison.OrdinalIgnoreCase))
            {
                Log($"SECURITY: {assetId}: официальный SHA-1 подтверждён по {fileUrl}.sha1");
                return true;
            }
            Log($"SECURITY: отказ — {assetId}: SHA-1 не совпал (ожидалось {expectedSha1}, получено {actualSha1})");
            Reject(filePath);
            return false;
        }

        // Официальных чексуммов нет
        if (asset.RequireHash)
        {
            Log($"SECURITY: отказ — {assetId}: официальный чексумм недоступен, а актив требует проверку хеша ({fileUrl})");
            Reject(filePath);
            return false;
        }

        Log($"SECURITY: ПРЕДУПРЕЖДЕНИЕ — {assetId}: официальных SHA-256/SHA-1 нет ({fileUrl}), " +
            $"файл принят по TLS, вычисленный SHA-256: {actual} (сравните с официальным при публикации)");
        return true;
    }

    /// <summary>
    /// Проверка уже лежащего на диске файла: его SHA-256 должен совпадать хотя бы с одним доверенным источником.
    /// Нет совпадения — файл удаляется (false), чтобы лаунчер перекачал чистую копию.
    /// </summary>
    public static bool VerifyExisting(string assetId, string filePath)
    {
        var asset = GetAsset(assetId);
        if (asset == null || !File.Exists(filePath))
        {
            if (asset == null) Log($"SECURITY: отказ — актив {assetId} отсутствует в trusted_assets.json");
            return false;
        }

        long size = new FileInfo(filePath).Length;
        if (size < asset.MinSize || asset.Sources.Count == 0)
        {
            Log($"SECURITY: отказ — {assetId}: локальный файл не проходит проверку (размер {size})");
            Reject(filePath);
            return false;
        }

        string actual = ComputeSha256(filePath);
        if (asset.Sources.Any(s => string.Equals(actual, s.Sha256, StringComparison.OrdinalIgnoreCase)))
            return true;

        Log($"SECURITY: отказ — {assetId}: локальный файл {filePath} имеет неподтверждённый SHA-256 {actual}, он будет перекачан");
        Reject(filePath);
        return false;
    }

    private static string ComputeSha1(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var sha = SHA1.Create();
        byte[] hash = sha.ComputeHash(stream);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (byte b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    private static async Task<string?> TryFetchChecksumAsync(HttpClient? client, string url)
    {
        try
        {
            using var c = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            string text = await c.GetStringAsync(url);
            // Ответ вида "9c7f43...  authlib-injector.jar" или просто hex
            string token = text.Trim().Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            token = token.TrimStart('/');
            if (token.Length is 32 or 40 or 64 && token.All(c => Uri.IsHexDigit(c)))
                return token.ToLowerInvariant();
        }
        catch { /* чексуммов нет */ }
        return null;
    }

    private static string NormalizeUrl(string url)
    {
        // Отбрасываем query/fragment: CDN часто добавляет подпись
        int cut = url.IndexOfAny(new[] { '?', '#' });
        return cut >= 0 ? url[..cut] : url;
    }

    private static void Reject(string filePath)
    {
        try { if (File.Exists(filePath)) File.Delete(filePath); }
        catch (Exception ex) { Log($"SECURITY: не удалось удалить отклонённый файл {filePath}: {ex.Message}"); }
    }
}