using System;

namespace WebCrawler
{
    /// <summary>
    /// Информация об одной обойдённой странице.
    /// </summary>
    public class PageInfo
    {
        public string Url { get; set; } = "";
        public string Title { get; set; } = "(no title)";
        public int StatusCode { get; set; }
        public long ContentLength { get; set; }
        public DateTime CrawledAt { get; set; }
        public int LinksFound { get; set; }
        public int Depth { get; set; }
        public string? Error { get; set; }

        // --- SEO / контактные данные, извлечённые со страницы ---
        public string H1 { get; set; } = "";
        public string MetaDescription { get; set; } = "";
        public string Emails { get; set; } = "";
        public string Phones { get; set; } = "";
        public int InternalLinks { get; set; }
        public int ExternalLinks { get; set; }
    }

    /// <summary>
    /// Настройки краулера.
    /// </summary>
    public class CrawlerConfig
    {
        public int MaxDepth { get; set; } = 3;
        public int MaxPages { get; set; } = 100;
        public int DelayMs { get; set; } = 500;
        public bool StayOnDomain { get; set; } = true;
        public string OutputFile { get; set; } = "crawl_results.txt";
        public string[] AllowedExtensions { get; set; } = { ".html", ".htm", "", ".php", ".asp", ".aspx" };
    }

    /// <summary>
    /// Одно обновление прогресса, передаваемое в UI через IProgress.
    /// </summary>
    public class CrawlUpdate
    {
        public PageInfo Page { get; set; } = null!;
        public int TotalCount { get; set; }
    }
}
