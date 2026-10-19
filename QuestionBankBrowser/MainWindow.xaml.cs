using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuestionBankBrowser;

public partial class MainWindow : Window
{
    private DatabaseService? _db;
    private readonly List<QuestionItem> _items = [];
    private bool _loadingFilters;
    private QuestionItem? _currentQuestion;
    private int _pageSize = 100;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            string baseDir = AppContext.BaseDirectory;
            string configPath = Path.Combine(
                baseDir,
                "questionbank.config.json");

            string dbPath = Path.Combine(
                baseDir,
                "QuestionBank.db");

            // 读取配置文件
            if (File.Exists(configPath))
            {
                try
                {
                    var cfg = JsonSerializer.Deserialize<AppConfig>(
                        await File.ReadAllTextAsync(configPath),
                        new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        });

                    if (!string.IsNullOrWhiteSpace(cfg?.DatabasePath))
                    {
                        dbPath = Path.Combine(
                            baseDir,
                            cfg.DatabasePath);
                    }

                    if (cfg?.PageSize is > 0)
                    {
                        _pageSize = cfg.PageSize.Value;
                    }
                }
                catch
                {
                    // 配置错误时使用默认值
                }
            }

            // 检查数据库
            if (!File.Exists(dbPath))
            {
                MessageBox.Show(
                    $"找不到题库数据库：\n\n{dbPath}\n\n" +
                    "请先运行题库导入程序，把 QuestionBank.db 放到这里。",
                    "题库数据库不存在",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                Close();
                return;
            }

            // 打开数据库
            _db = new DatabaseService(dbPath);
            _db.Open();

            // 初始化 WebView2
            await QuestionWebView.EnsureCoreWebView2Async();

            QuestionWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            QuestionWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;

            // 加载筛选项
            LoadFilters();

            // 首次搜索
            Search();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.ToString(),
                "启动失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            Close();
        }
    }

    private void LoadFilters()
    {
        if (_db == null)
            return;

        _loadingFilters = true;

        try
        {
            var options = _db.LoadFilterOptions();

            // 年份
            YearCombo.Items.Clear();
            YearCombo.Items.Add(
                new FilterItem<int?>(
                    null,
                    "全部年份"));

            foreach (var year in options.Years)
            {
                YearCombo.Items.Add(
                    new FilterItem<int?>(
                        year,
                        year.ToString()));
            }

            YearCombo.SelectedIndex = 0;

            // 题型
            TypeCombo.Items.Clear();
            TypeCombo.Items.Add(
                new FilterItem<string?>(
                    null,
                    "全部题型"));

            foreach (var type in options.Types)
            {
                TypeCombo.Items.Add(
                    new FilterItem<string?>(
                        type,
                        type));
            }

            TypeCombo.SelectedIndex = 0;

            // 来源
            SourceCombo.Items.Clear();
            SourceCombo.Items.Add(
                new FilterItem<string?>(
                    null,
                    "全部来源"));

            foreach (var source in options.Sources)
            {
                SourceCombo.Items.Add(
                    new FilterItem<string?>(
                        source,
                        source));
            }

            SourceCombo.SelectedIndex = 0;
        }
        finally
        {
            _loadingFilters = false;
        }
    }

    private void Search()
    {
        if (_db == null)
            return;

        try
        {
            string text = SearchBox.Text.Trim();

            int? year =
                GetSelectedValue<int?>(YearCombo);

            string? type =
                GetSelectedValue<string?>(TypeCombo);

            string? source =
                GetSelectedValue<string?>(SourceCombo);

            int total = _db.Count(
                text,
                year,
                type,
                source);

            _items.Clear();

            _items.AddRange(
                _db.Search(
                    text,
                    year,
                    type,
                    source,
                    _pageSize,
                    0));

            QuestionList.ItemsSource = null;
            QuestionList.ItemsSource = _items;

            CountText.Text =
                $"共 {total:N0} 道，当前显示 {_items.Count:N0} 道";

            if (_items.Count > 0)
            {
                QuestionList.SelectedIndex = 0;
            }
            else
            {
                _currentQuestion = null;
                CopyImageButton.IsEnabled = false;
                PreviewTitle.Text = "";
                PreviewMeta.Text = "";

                StatusText.Text =
                    "没有找到符合条件的题目。";

                QuestionWebView.NavigateToString(
                    EmptyHtml("没有找到题目。"));
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                "搜索失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void ShowQuestion(QuestionItem item)
    {
        if (_db == null)
            return;

        PreviewTitle.Text =
            $"{item.Year} {item.Source} 第 {item.QuestionNo} 题";
        _currentQuestion = item;
        CopyImageButton.IsEnabled = true;

        PreviewMeta.Text =
            $"{item.Type}    ID: {item.Id}";

        var choices = _db.GetChoices(item.Id);
        var images = _db.GetImages(item.Id);

        string html =
            BuildQuestionHtml(
                item,
                choices,
                images);

        QuestionWebView.NavigateToString(html);

        StatusText.Text =
            $"题目 ID：{item.Id}    " +
            $"{item.Type}    " +
            $"{item.Year} {item.Source}";

        Title =
            $"高中题库浏览器 - " +
            $"{item.Year} {item.Source} 第 {item.QuestionNo} 题";
    }

    private string BuildQuestionHtml(
        QuestionItem item,
        List<ChoiceItem> choices,
        List<ImageItem> images)
    {
        string baseDir =
            AppContext.BaseDirectory;

        // ------------------------------------------------------------
        // 题干
        // ------------------------------------------------------------

        string content =
            HtmlEncode(item.Content);

        // Markdown 图片转为本地 file URI
        content =
            ConvertMarkdownImages(
                content,
                images,
                baseDir);

        // 基本 Markdown 换行
        content =
            content.Replace(
                "\r\n",
                "\n");

        content =
            content.Replace(
                "\n\n",
                "<div class=\"para-gap\"></div>");

        content =
            content.Replace(
                "\n",
                "<br>");

        // ------------------------------------------------------------
        // 选项
        // ------------------------------------------------------------

        string choiceHtml = "";

        if (choices.Count > 0)
        {
            choiceHtml =
                "<div class=\"choices\">";

            foreach (var c in choices)
            {
                string mark =
                    c.IsCorrect
                        ? " <span class=\"correct\">✓</span>"
                        : "";

                choiceHtml +=
                    "<div class=\"choice\">" +
                    $"<b>{HtmlEncode(c.Key)}.</b>&nbsp;" +
                    $"{ConvertInlineMath(HtmlEncode(c.Content))}" +
                    $"{mark}" +
                    "</div>";
            }

            choiceHtml +=
                "</div>";
        }

        // ------------------------------------------------------------
        // 答案
        // ------------------------------------------------------------

        string answerHtml = "";

        if (!string.IsNullOrWhiteSpace(item.Answer))
        {
            answerHtml =
                "<div class=\"answer\">" +
                "<b>答案：</b>" +
                ConvertInlineMath(
                    HtmlEncode(item.Answer)) +
                "</div>";
        }

        // ------------------------------------------------------------
        // 解析
        // ------------------------------------------------------------

        string analysisHtml = "";

        if (!string.IsNullOrWhiteSpace(item.Analysis))
        {
            string analysis =
                HtmlEncode(item.Analysis)
                    .Replace("\r\n", "\n")
                    .Replace("\n", "<br>");

            analysisHtml =
                "<details>" +
                "<summary>解析</summary>" +
                "<div class=\"analysis\">" +
                ConvertInlineMath(analysis) +
                "</div>" +
                "</details>";
        }

        string body =
            ConvertInlineMath(content);

        // ------------------------------------------------------------
        // HTML 模板
        //
        // 注意：
        // 这里故意不用 $"""。
        // CSS / JavaScript 中的 { } 可以直接使用。
        // 页面内容使用 {{BODY}} 等占位符，
        // 最后通过 Replace() 注入。
        // ------------------------------------------------------------

        string html = """
<!doctype html>
<html>
<head>
<meta charset="utf-8">
<meta http-equiv="X-UA-Compatible" content="IE=edge">

<style>
html, body {
    margin: 0;
    padding: 0;
    background: #ffffff;
    color: #222;
    font-family: "Microsoft YaHei", "Segoe UI", Arial, sans-serif;
    font-size: 18px;
    line-height: 1.85;
}

body {
    padding: 24px 34px 50px 34px;
}

.question {
    max-width: 1100px;
    margin: auto;
}

.para-gap {
    height: 8px;
}

.choice {
    margin: 7px 0;
}

.choices {
    margin: 20px 0 26px 18px;
}

.answer {
    margin-top: 24px;
    padding: 12px 16px;
    background: #f5f7fa;
    border-radius: 6px;
}

details {
    margin-top: 22px;
    padding: 10px 15px;
    background: #fafafa;
    border: 1px solid #e4e4e4;
    border-radius: 6px;
}

summary {
    cursor: pointer;
    font-weight: bold;
}

.analysis {
    margin-top: 12px;
}

img.question-image {
    max-width: 100%;
    height: auto;
    display: block;
    margin: 14px auto;
}

.correct {
    color: #16803c;
    font-weight: bold;
}
</style>

<script>
window.MathJax = {
    tex: {
        inlineMath: [['$', '$'], ['\\(', '\\)']],
        displayMath: [['$$', '$$'], ['\\[', '\\]']]
    },
    svg: {
        fontCache: 'global'
    }
};
</script>

<script>
(function() {
    var s = document.createElement('script');
    s.src = 'https://cdn.jsdelivr.net/npm/mathjax@3/es5/tex-svg.js';
    s.async = true;
    document.head.appendChild(s);
})();
</script>

</head>

<body>

<div class="question">
{{BODY}}
{{CHOICES}}
{{ANSWER}}
{{ANALYSIS}}
</div>

</body>
</html>
""";

        // ------------------------------------------------------------
        // 注入动态内容
        // ------------------------------------------------------------

        html = html.Replace(
            "{{BODY}}",
            body);

        html = html.Replace(
            "{{CHOICES}}",
            choiceHtml);

        html = html.Replace(
            "{{ANSWER}}",
            answerHtml);

        html = html.Replace(
            "{{ANALYSIS}}",
            analysisHtml);

        return html;
    }

    private static string ConvertMarkdownImages(
        string htmlEncodedContent,
        List<ImageItem> images,
        string baseDir)
    {
        // HtmlEncode 后的原始 Markdown 图片仍然保留
        // ![alt](path)
        //
        // 方括号、圆括号不需要 HTML 编码。
        // 这里只处理数据库中登记过的图片。

        string converted = Regex.Replace(htmlEncodedContent, @"!\[(.*?)\]\((.*?)\)", match =>
        {
            string alt = System.Net.WebUtility.HtmlDecode(match.Groups[1].Value);
            string markdownPath = System.Net.WebUtility.HtmlDecode(match.Groups[2].Value).Replace('\\', '/');
            var image = images.FirstOrDefault(candidate =>
                string.Equals(candidate.FilePath.Replace('\\', '/'), markdownPath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidate.OriginalName, Path.GetFileName(markdownPath), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidate.AltText, alt, StringComparison.Ordinal));
            if (image == null)
                return match.Value;

            string? file = ResolveImagePath(baseDir, image.FilePath);
            if (file == null)
                return $"<span class=\"missing-image\">[图片文件不存在：{HtmlEncode(image.OriginalName)}]</span>";

            string mimeType = Path.GetExtension(file).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                ".svg" => "image/svg+xml",
                _ => "image/png"
            };
            string dataUri = $"data:{mimeType};base64,{Convert.ToBase64String(File.ReadAllBytes(file))}";
            return $"<img class=\"question-image\" src=\"{dataUri}\" alt=\"{HtmlEncode(image.AltText)}\">";
        });

        // 部分导入数据只在题干中保存了图片文件名，没有 Markdown 图片语法。
        foreach (var image in images)
        {
            if (string.IsNullOrWhiteSpace(image.OriginalName) ||
                !converted.Contains(image.OriginalName, StringComparison.OrdinalIgnoreCase))
                continue;

            string? file = ResolveImagePath(baseDir, image.FilePath);
            if (file == null) continue;
            string mimeType = Path.GetExtension(file).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                ".svg" => "image/svg+xml",
                _ => "image/png"
            };
            string dataUri = $"data:{mimeType};base64,{Convert.ToBase64String(File.ReadAllBytes(file))}";
            converted = converted.Replace(image.OriginalName,
                $"<img class=\"question-image\" src=\"{dataUri}\" alt=\"{HtmlEncode(image.AltText)}\">",
                StringComparison.OrdinalIgnoreCase);
        }

        return converted;
    }

    private static string? ResolveImagePath(string baseDir, string storedPath)
    {
        if (string.IsNullOrWhiteSpace(storedPath))
            return null;

        var candidates = new List<string>();
        if (Path.IsPathRooted(storedPath))
            candidates.Add(storedPath);
        else
        {
            candidates.Add(Path.Combine(baseDir, storedPath));
            string normalized = storedPath.Replace('\\', '/');
            if (normalized.StartsWith("Images/", StringComparison.OrdinalIgnoreCase))
                candidates.Add(Path.Combine(baseDir, normalized[7..]));
            else
                candidates.Add(Path.Combine(baseDir, "Images", storedPath));
        }

        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
    }

    private static string ConvertInlineMath(string html)
    {
        // WebView2 / MathJax 需要原始反斜杠。
        // 数据库中的字符串本身已经保存为单个反斜杠，
        // 因此这里不做额外转义。
        return html;
    }

    private static string HtmlEncode(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        return System.Net.WebUtility.HtmlEncode(text);
    }

    private static string EmptyHtml(string message)
    {
        // 同样使用普通 raw string，
        // 不使用 $"""，避免 CSS 大括号解析问题。

        string html = """
<!doctype html>
<html>
<head>
<meta charset="utf-8">

<style>
body {
    font-family: "Microsoft YaHei";
    padding: 50px;
    color: #777;
    text-align: center;
}
</style>

</head>

<body>
{{MESSAGE}}
</body>

</html>
""";

        return html.Replace(
            "{{MESSAGE}}",
            HtmlEncode(message));
    }

    private void SearchButton_Click(
        object sender,
        RoutedEventArgs e)
        => Search();

    private async void CopyImageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentQuestion == null || QuestionWebView.CoreWebView2 == null)
            return;

        CopyImageButton.IsEnabled = false;
        try
        {
            var item = _currentQuestion;
            string heading = $"{item.Year?.ToString() ?? "年份未知"}　{item.Source}　第 {item.QuestionNo} 题";
            string headingJson = JsonSerializer.Serialize(heading);
            string prepareScript = """
                (() => {
                  window.__questionCopyState = {
                    details: Array.from(document.querySelectorAll('details')).map(x => x.open)
                  };
                  document.querySelectorAll('details').forEach(x => x.open = true);
                  const header = document.createElement('div');
                  header.id = '__question_copy_header';
                  header.textContent = HEADER_TEXT;
                  header.style.cssText = 'color:#1677d2;font-size:22px;font-weight:700;margin:0 0 20px 0;line-height:1.5';
                  document.querySelector('.question').prepend(header);
                  return true;
                })()
                """.Replace("HEADER_TEXT", headingJson);
            await QuestionWebView.CoreWebView2.ExecuteScriptAsync(prepareScript);
            await QuestionWebView.CoreWebView2.ExecuteScriptAsync("""
                (async () => {
                  if (document.fonts?.ready) await document.fonts.ready;
                  await Promise.all(Array.from(document.images).map(i => i.decode?.().catch(() => {}) ?? Promise.resolve()));
                  if (window.MathJax?.typesetPromise) await window.MathJax.typesetPromise();
                  await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
                  return true;
                })()
                """);

            string metricsJson = await QuestionWebView.CoreWebView2.CallDevToolsProtocolMethodAsync(
                "Page.getLayoutMetrics", "{}");
            using var metricsDoc = JsonDocument.Parse(metricsJson);
            var size = metricsDoc.RootElement.GetProperty("cssContentSize");
            double width = size.GetProperty("width").GetDouble();
            double height = size.GetProperty("height").GetDouble();
            string captureArgs = JsonSerializer.Serialize(new
            {
                format = "png",
                fromSurface = true,
                captureBeyondViewport = true,
                clip = new { x = 0, y = 0, width, height, scale = 1 }
            });
            string captureJson = await QuestionWebView.CoreWebView2.CallDevToolsProtocolMethodAsync(
                "Page.captureScreenshot", captureArgs);
            using var captureDoc = JsonDocument.Parse(captureJson);
            byte[] png = Convert.FromBase64String(captureDoc.RootElement.GetProperty("data").GetString()!);
            using var stream = new MemoryStream(png);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            Clipboard.SetImage(bitmap);
            StatusText.Text = "题目图片已复制到剪贴板。";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "复制题目图片失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            try
            {
                await QuestionWebView.CoreWebView2.ExecuteScriptAsync("""
                    (() => {
                      document.getElementById('__question_copy_header')?.remove();
                      if (window.__questionCopyState) {
                        document.querySelectorAll('details').forEach((x, i) => x.open = !!window.__questionCopyState.details[i]);
                        delete window.__questionCopyState;
                      }
                    })()
                    """);
            }
            catch { }
            CopyImageButton.IsEnabled = _currentQuestion != null;
        }
    }

    private void ClearButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        SearchBox.Clear();

        YearCombo.SelectedIndex = 0;
        TypeCombo.SelectedIndex = 0;
        SourceCombo.SelectedIndex = 0;

        Search();
    }

    private void SearchBox_KeyDown(
        object sender,
        KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Search();
            e.Handled = true;
        }
    }

    private void FilterChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_loadingFilters || !IsLoaded || _db == null)
            return;

        if (ReferenceEquals(sender, YearCombo))
        {
            UpdateSourcesForSelectedYear();
        }
        else if (ReferenceEquals(sender, SourceCombo))
        {
            UpdateYearsForSelectedSource();
        }

        Search();
    }

    private void UpdateSourcesForSelectedYear()
    {
        if (_db == null) return;
        string? selected = GetSelectedValue<string?>(SourceCombo);
        var sources = _db.LoadSourcesForYear(GetSelectedValue<int?>(YearCombo));
        if (selected != null && !sources.Contains(selected))
            selected = null;

        _loadingFilters = true;
        try
        {
            SourceCombo.Items.Clear();
            SourceCombo.Items.Add(new FilterItem<string?>(null, "全部来源"));
            foreach (string source in sources)
                SourceCombo.Items.Add(new FilterItem<string?>(source, source));
            SelectFilterValue(SourceCombo, selected);
        }
        finally { _loadingFilters = false; }
    }

    private void UpdateYearsForSelectedSource()
    {
        if (_db == null) return;
        int? selected = GetSelectedValue<int?>(YearCombo);
        var years = _db.LoadYearsForSource(GetSelectedValue<string?>(SourceCombo));
        if (selected.HasValue && !years.Contains(selected.Value))
            selected = null;

        _loadingFilters = true;
        try
        {
            YearCombo.Items.Clear();
            YearCombo.Items.Add(new FilterItem<int?>(null, "全部年份"));
            foreach (int year in years)
                YearCombo.Items.Add(new FilterItem<int?>(year, year.ToString()));
            SelectFilterValue(YearCombo, selected);
        }
        finally { _loadingFilters = false; }
    }

    private static void SelectFilterValue<T>(ComboBox combo, T value)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is FilterItem<T> item && EqualityComparer<T>.Default.Equals(item.Value, value))
            {
                combo.SelectedIndex = i;
                return;
            }
        }
        combo.SelectedIndex = 0;
    }

    private void ReloadFiltersButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        LoadFilters();
        Search();
    }

    private void QuestionList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (QuestionList.SelectedItem is QuestionItem item)
        {
            ShowQuestion(item);
        }
    }

    private void PreviousButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (QuestionList.SelectedIndex > 0)
        {
            QuestionList.SelectedIndex--;
        }
    }

    private void NextButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (QuestionList.SelectedIndex <
            QuestionList.Items.Count - 1)
        {
            QuestionList.SelectedIndex++;
        }
    }

    private static T? GetSelectedValue<T>(
        ComboBox combo)
    {
        if (combo.SelectedItem is FilterItem<T> item)
        {
            return item.Value;
        }

        return default;
    }

    private sealed class FilterItem<T>
    {
        public T Value { get; }

        public string Text { get; }

        public FilterItem(
            T value,
            string text)
        {
            Value = value;
            Text = text;
        }

        public override string ToString()
            => Text;
    }

    private sealed class AppConfig
    {
        public string? DatabasePath { get; set; }

        public string? ImagesFolder { get; set; }

        public int? PageSize { get; set; }
    }

    protected override void OnClosed(EventArgs e)
    {
        _db?.Dispose();

        base.OnClosed(e);
    }
}
