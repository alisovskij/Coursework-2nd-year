using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WebCrawler
{
    /// <summary>
    /// Веб-краулер. Выполняет обход сайта в ширину (BFS), собирает метаданные
    /// страниц и сохраняет результаты в текстовый файл. Не зависит от консоли:
    /// прогресс и статусные сообщения передаются через IProgress, что позволяет
    /// использовать класс как из консоли, так и из WinForms-интерфейса.
    /// </summary>
    public class Crawler : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly CrawlerConfig _config;
        private readonly HashSet<string> _visited = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<PageInfo> _results = new();

        private static readonly Regex _titleRegex =
            new(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        private static readonly Regex _linkRegex =
            new(@"<a\s[^>]*href\s*=\s*[""']([^""'#][^""']*)[""']", RegexOptions.IgnoreCase);
        private static readonly Regex _h1Regex =
            new(@"<h1[^>]*>(.*?)</h1>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        private static readonly Regex _metaDescRegex =
            new(@"<meta[^>]*name\s*=\s*[""']description[""'][^>]*content\s*=\s*[""'](.*?)[""']",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
        private static readonly Regex _metaDescRegexAlt =
            new(@"<meta[^>]*content\s*=\s*[""'](.*?)[""'][^>]*name\s*=\s*[""']description[""']",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
        private static readonly Regex _emailRegex =
            new(@"[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}", RegexOptions.IgnoreCase);
        private static readonly Regex _phoneRegex =
            new(@"(?:\+?\d[\d\-\s().]{7,}\d)", RegexOptions.IgnoreCase);

        /// <summary>Результаты обхода (только для чтения).</summary>
        public IReadOnlyList<PageInfo> Results => _results;

        public Crawler(CrawlerConfig? config = null)
        {
            _config = config ?? new CrawlerConfig();
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            _httpClient.DefaultRequestHeaders.Add("User-Agent",
                "WebCrawler/1.0 (Educational; C# Course Project)");
        }

        /// <summary>
        /// Запускает обход с заданного URL.
        /// </summary>
        /// <param name="startUrl">Стартовый адрес.</param>
        /// <param name="progress">Канал обновлений прогресса (по странице на шаг).</param>
        /// <param name="status">Канал текстовых статусных сообщений.</param>
        /// <param name="ct">Токен отмены.</param>
        public async Task CrawlAsync(
            string startUrl,
            IProgress<CrawlUpdate>? progress = null,
            IProgress<string>? status = null,
            CancellationToken ct = default)
        {
            if (!Uri.TryCreate(startUrl, UriKind.Absolute, out Uri? startUri))
            {
                status?.Report("[ERROR] Некорректный стартовый URL.");
                return;
            }

            _visited.Clear();
            _results.Clear();

            string baseDomain = startUri.Host;
            status?.Report($"[INFO] Старт обхода: {startUrl}");
            status?.Report($"[INFO] Домен: {baseDomain} | Глубина: {_config.MaxDepth} | Лимит страниц: {_config.MaxPages}");

            var queue = new Queue<(string url, int depth)>();
            queue.Enqueue((startUrl, 0));
            _visited.Add(NormalizeUrl(startUrl));

            while (queue.Count > 0 && _results.Count < _config.MaxPages)
            {
                ct.ThrowIfCancellationRequested();

                var (url, depth) = queue.Dequeue();
                if (depth > _config.MaxDepth)
                    continue;

                var (pageInfo, html) = await FetchPageAsync(url, baseDomain, ct);
                pageInfo.Depth = depth;
                _results.Add(pageInfo);

                progress?.Report(new CrawlUpdate { Page = pageInfo, TotalCount = _results.Count });

                // Если страница успешна и не достигнута предельная глубина — собираем ссылки
                if (pageInfo.StatusCode == 200 && html != null && depth < _config.MaxDepth)
                {
                    foreach (var link in ExtractLinks(html, url))
                    {
                        if (!Uri.TryCreate(link, UriKind.Absolute, out Uri? linkUri))
                            continue;

                        if (_config.StayOnDomain &&
                            !linkUri.Host.Equals(baseDomain, StringComparison.OrdinalIgnoreCase))
                            continue;

                        string ext = Path.GetExtension(linkUri.AbsolutePath).ToLowerInvariant();
                        if (!_config.AllowedExtensions.Contains(ext))
                            continue;

                        string normalized = NormalizeUrl(link);
                        if (_visited.Add(normalized))
                            queue.Enqueue((link, depth + 1));
                    }
                }

                await Task.Delay(_config.DelayMs, ct);
            }

            status?.Report($"[INFO] Обход завершён. Страниц обработано: {_results.Count}");
            SaveResults();
            status?.Report($"[INFO] Результаты сохранены в: {_config.OutputFile}");
        }

        /// <summary>
        /// Загружает страницу один раз и возвращает её метаданные вместе с HTML.
        /// HTML переиспользуется для извлечения ссылок — повторного запроса нет.
        /// </summary>
        private async Task<(PageInfo info, string? html)> FetchPageAsync(string url, string baseDomain, CancellationToken ct)
        {
            var info = new PageInfo { Url = url, CrawledAt = DateTime.Now };
            string? html = null;

            try
            {
                var response = await _httpClient.GetAsync(url, ct);
                info.StatusCode = (int)response.StatusCode;
                info.ContentLength = response.Content.Headers.ContentLength ?? 0;

                if (response.IsSuccessStatusCode)
                {
                    html = await response.Content.ReadAsStringAsync(ct);
                    info.ContentLength = html.Length;

                    var titleMatch = _titleRegex.Match(html);
                    if (titleMatch.Success)
                        info.Title = CleanText(titleMatch.Groups[1].Value);

                    info.LinksFound = _linkRegex.Matches(html).Count;

                    ExtractSeoData(info, html, url, baseDomain);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // отмена пользователем — пробрасываем наверх
            }
            catch (TaskCanceledException)
            {
                info.Error = "Timeout";
                info.StatusCode = 408;
            }
            catch (HttpRequestException ex)
            {
                info.Error = ex.Message;
                info.StatusCode = -1;
            }
            catch (Exception ex)
            {
                info.Error = ex.Message;
                info.StatusCode = -1;
            }

            return (info, html);
        }

        /// <summary>
        /// Извлекает абсолютные ссылки из готового HTML (без сетевого запроса).
        /// </summary>
        private static List<string> ExtractLinks(string html, string pageUrl)
        {
            var links = new List<string>();
            if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out Uri? baseUri))
                return links;

            foreach (Match match in _linkRegex.Matches(html))
            {
                string href = match.Groups[1].Value.Trim();
                if (Uri.TryCreate(baseUri, href, out Uri? absolute))
                    links.Add(absolute.GetLeftPart(UriPartial.Query));
            }

            return links.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// Извлекает SEO- и контактные данные из HTML: H1, meta description,
        /// e-mail, телефоны, а также подсчитывает внутренние/внешние ссылки.
        /// </summary>
        private static void ExtractSeoData(PageInfo info, string html, string pageUrl, string baseDomain)
        {
            // H1 — берём первый заголовок первого уровня
            var h1Match = _h1Regex.Match(html);
            if (h1Match.Success)
                info.H1 = CleanText(h1Match.Groups[1].Value);

            // Meta description — поддерживаем оба порядка атрибутов name/content
            var metaMatch = _metaDescRegex.Match(html);
            if (!metaMatch.Success)
                metaMatch = _metaDescRegexAlt.Match(html);
            if (metaMatch.Success)
                info.MetaDescription = CleanText(metaMatch.Groups[1].Value);

            // E-mail — собираем уникальные адреса (в т.ч. из mailto:)
            var emails = _emailRegex.Matches(html)
                .Select(m => m.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(10);
            info.Emails = string.Join("; ", emails);

            // Телефоны — извлекаем в первую очередь из ссылок tel:
            var phones = ExtractPhones(html);
            info.Phones = string.Join("; ", phones);

            // Внутренние/внешние ссылки относительно стартового домена
            if (Uri.TryCreate(pageUrl, UriKind.Absolute, out Uri? baseUri))
            {
                int internalCount = 0, externalCount = 0;
                foreach (Match match in _linkRegex.Matches(html))
                {
                    string href = match.Groups[1].Value.Trim();
                    if (!Uri.TryCreate(baseUri, href, out Uri? abs))
                        continue;
                    if (abs.Host.Equals(baseDomain, StringComparison.OrdinalIgnoreCase))
                        internalCount++;
                    else
                        externalCount++;
                }
                info.InternalLinks = internalCount;
                info.ExternalLinks = externalCount;
            }
        }

        /// <summary>
        /// Извлекает телефонные номера: сначала из ссылок tel:, затем из текста.
        /// </summary>
        private static List<string> ExtractPhones(string html)
        {
            var result = new List<string>();

            // tel:-ссылки — самый надёжный источник
            foreach (Match m in Regex.Matches(html, @"tel:([+\d\-\s().]+)", RegexOptions.IgnoreCase))
            {
                string phone = m.Groups[1].Value.Trim();
                if (phone.Length >= 7 && !result.Contains(phone))
                    result.Add(phone);
            }

            // Если tel:-ссылок нет — пробуем найти номера в тексте страницы
            if (result.Count == 0)
            {
                string text = CleanText(html);
                foreach (Match m in _phoneRegex.Matches(text))
                {
                    string phone = m.Value.Trim();
                    int digits = phone.Count(char.IsDigit);
                    if (digits is >= 7 and <= 15 && !result.Contains(phone))
                        result.Add(phone);
                    if (result.Count >= 10)
                        break;
                }
            }

            return result;
        }

        /// <summary>
        /// Сохраняет результаты обхода и сводную статистику в текстовый файл.
        /// </summary>
        private void SaveResults()
        {
            File.WriteAllText(_config.OutputFile, BuildReport(), Encoding.UTF8);
        }

        /// <summary>
        /// Формирует текст отчёта (используется и для файла, и для показа в UI).
        /// </summary>
        public string BuildReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== WEB CRAWLER RESULTS ===");
            sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Total pages: {_results.Count}");
            sb.AppendLine($"Successful: {_results.Count(r => r.StatusCode == 200)}");
            sb.AppendLine($"Errors: {_results.Count(r => r.StatusCode != 200)}");
            sb.AppendLine(new string('=', 60));
            sb.AppendLine();

            int i = 1;
            foreach (var page in _results)
            {
                sb.AppendLine($"[{i++}] {page.Url}");
                sb.AppendLine($"    Title:   {page.Title}");
                sb.AppendLine($"    H1:      {page.H1}");
                sb.AppendLine($"    Meta:    {page.MetaDescription}");
                sb.AppendLine($"    Status:  {page.StatusCode}");
                sb.AppendLine($"    Size:    {page.ContentLength} bytes");
                sb.AppendLine($"    Links:   {page.LinksFound} (внутр. {page.InternalLinks} / внеш. {page.ExternalLinks})");
                if (!string.IsNullOrEmpty(page.Emails))
                    sb.AppendLine($"    Email:   {page.Emails}");
                if (!string.IsNullOrEmpty(page.Phones))
                    sb.AppendLine($"    Phones:  {page.Phones}");
                sb.AppendLine($"    Time:    {page.CrawledAt:HH:mm:ss}");
                if (page.Error != null)
                    sb.AppendLine($"    Error:   {page.Error}");
                sb.AppendLine();
            }

            sb.AppendLine(new string('=', 60));
            sb.AppendLine("STATISTICS");
            double avgSize = _results.Any(r => r.ContentLength > 0)
                ? _results.Where(r => r.ContentLength > 0).Average(r => r.ContentLength) : 0;
            double avgLinks = _results.Any(r => r.LinksFound > 0)
                ? _results.Where(r => r.LinksFound > 0).Average(r => r.LinksFound) : 0;
            sb.AppendLine($"Avg content size: {avgSize:F0} bytes");
            sb.AppendLine($"Avg links/page:   {avgLinks:F1}");

            sb.AppendLine("\nTop 5 pages by links found:");
            foreach (var p in _results.OrderByDescending(r => r.LinksFound).Take(5))
                sb.AppendLine($"  {p.LinksFound,4} links - {TruncateUrl(p.Url, 50)}");

            return sb.ToString();
        }

        /// <summary>
        /// Формирует CSV-представление результатов (разделитель — точка с запятой,
        /// что удобно для русской локали Excel). Кодировка — UTF-8 c BOM.
        /// </summary>
        public string BuildCsv()
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(';', new[]
            {
                "#", "URL", "Title", "H1", "MetaDescription", "Status", "Size",
                "Links", "InternalLinks", "ExternalLinks", "Email", "Phones",
                "Depth", "CrawledAt", "Error"
            }));

            int i = 1;
            foreach (var p in _results)
            {
                sb.AppendLine(string.Join(';', new[]
                {
                    Csv(i++.ToString()),
                    Csv(p.Url),
                    Csv(p.Title),
                    Csv(p.H1),
                    Csv(p.MetaDescription),
                    Csv(p.StatusCode.ToString()),
                    Csv(p.ContentLength.ToString()),
                    Csv(p.LinksFound.ToString()),
                    Csv(p.InternalLinks.ToString()),
                    Csv(p.ExternalLinks.ToString()),
                    Csv(p.Emails),
                    Csv(p.Phones),
                    Csv(p.Depth.ToString()),
                    Csv(p.CrawledAt.ToString("yyyy-MM-dd HH:mm:ss")),
                    Csv(p.Error ?? "")
                }));
            }

            return sb.ToString();
        }

        /// <summary>Сохраняет результаты в CSV-файл (UTF-8 с BOM для Excel).</summary>
        public void SaveCsv(string path)
        {
            File.WriteAllText(path, BuildCsv(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }

        /// <summary>Экранирует значение поля по правилам CSV (RFC 4180).</summary>
        private static string Csv(string value)
        {
            value ??= "";
            if (value.Contains('"') || value.Contains(';') || value.Contains('\n') || value.Contains('\r'))
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            return value;
        }

        private static string NormalizeUrl(string url)
        {
            try
            {
                var uri = new Uri(url);
                return uri.GetLeftPart(UriPartial.Path).TrimEnd('/').ToLowerInvariant();
            }
            catch { return url.ToLowerInvariant(); }
        }

        private static string CleanText(string html) =>
            Regex.Replace(html, "<[^>]+>", "").Trim();

        private static string TruncateUrl(string url, int maxLen) =>
            url.Length <= maxLen ? url : url.Substring(0, maxLen - 3) + "...";

        public void Dispose() => _httpClient.Dispose();
    }
}
