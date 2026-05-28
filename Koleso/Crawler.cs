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

                var (pageInfo, html) = await FetchPageAsync(url, ct);
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
        private async Task<(PageInfo info, string? html)> FetchPageAsync(string url, CancellationToken ct)
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
                sb.AppendLine($"    Status:  {page.StatusCode}");
                sb.AppendLine($"    Size:    {page.ContentLength} bytes");
                sb.AppendLine($"    Links:   {page.LinksFound}");
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
