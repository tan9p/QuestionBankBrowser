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
    private bool _startupStarted;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // WPF may raise Loaded again when the window is reattached to the visual tree.
        if (_startupStarted)
            return;

        _startupStarted = true;

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

            // WebView2 的用户数据目录必须可写。安装目录通常位于 Program Files，
            // 因此将缓存、Cookie 和其他配置放到当前 Windows 用户的 LocalAppData。
            string webViewDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "QuestionBankBrowser",
                "WebView2");
            Directory.CreateDirectory(webViewDataDir);

            CoreWebView2Environment webViewEnvironment =
                await CoreWebView2Environment.CreateAsync(userDataFolder: webViewDataDir);
            await QuestionWebView.EnsureCoreWebView2Async(webViewEnvironment);


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
                CopyLatexButton.IsEnabled = false;
                CopyOfficeButton.IsEnabled = false;
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
        CopyLatexButton.IsEnabled = true;
        CopyOfficeButton.IsEnabled = true;

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

        var previewTables = new List<string>();
        string contentWithTables = ReplaceHtmlTables(
            item.Content,
            rows => RenderPreviewTable(rows, images, baseDir),
            previewTables);
        string content = HtmlEncode(contentWithTables);
        for (int i = 0; i < previewTables.Count; i++)
            content = content.Replace($"__QB_TABLE_{i}__", previewTables[i], StringComparison.Ordinal);

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

table.question-table {
    border-collapse: collapse;
    margin: 14px auto;
    max-width: 100%;
}

table.question-table td, table.question-table th {
    border: 1px solid #777;
    padding: 6px 10px;
    text-align: center;
    vertical-align: middle;
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

    private void CopyLatexButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentQuestion == null || _db == null)
            return;

        try
        {
            string source = BuildLatexSource(_currentQuestion, _db.GetChoices(_currentQuestion.Id), _db.GetImages(_currentQuestion.Id));
            Clipboard.SetText(source, TextDataFormat.UnicodeText);
            StatusText.Text = "LaTeX 源码已复制。图片使用本机绝对路径，移动题库或分享源码时请同时复制对应图片文件。";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "复制 LaTeX 源码失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private string RenderPreviewTable(List<List<HtmlTableCell>> rows, List<ImageItem> images, string baseDir)
    {
        var html = new System.Text.StringBuilder("<table class=\"question-table\"><tbody>");
        foreach (var row in rows)
        {
            html.Append("<tr>");
            foreach (var cell in row)
            {
                string tag = cell.IsHeader ? "th" : "td";
                html.Append('<').Append(tag);
                AppendSpanAttribute(html, "rowspan", cell.RowSpan);
                AppendSpanAttribute(html, "colspan", cell.ColSpan);
                html.Append('>');
                string text = CleanHtmlCellText(cell.Content).Replace("\r\n", "\n").Replace("\n", "<br>");
                html.Append(ConvertMarkdownImages(HtmlEncode(text), images, baseDir));
                html.Append("</").Append(tag).Append('>');
            }
            html.Append("</tr>");
        }
        return html.Append("</tbody></table>").ToString();
    }

    private string ConvertHtmlTablesToLatex(string? text)
    {
        var tables = new List<string>();
        string converted = ReplaceHtmlTables(text, RenderLatexTable, tables);
        for (int i = 0; i < tables.Count; i++)
            converted = converted.Replace($"__QB_TABLE_{i}__", tables[i], StringComparison.Ordinal);
        return converted;
    }

    private static string RenderLatexTable(List<List<HtmlTableCell>> rows)
    {
        if (rows.Count == 0) return "";
        int columns = GetTableColumnCount(rows);
        var output = new System.Text.StringBuilder();
        if (rows.SelectMany(row => row).Any(cell => cell.RowSpan > 1))
            output.AppendLine("% Requires \\usepackage{multirow}");
        output.Append("\\begin{tabular}{").Append(string.Concat(Enumerable.Repeat("|c", columns))).Append("|}\n\\hline\n");
        var occupiedUntil = new int[columns];
        for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var cells = new List<string>();
            int column = 0;
            foreach (var cell in rows[rowIndex])
            {
                while (column < columns && occupiedUntil[column] > rowIndex)
                {
                    cells.Add("");
                    column++;
                }
                if (column >= columns) break;

                string content = EscapeLatexCell(CleanHtmlCellText(cell.Content));
                int span = Math.Min(cell.ColSpan, columns - column);
                if (cell.RowSpan > 1)
                {
                    content = $"\\multirow{{{cell.RowSpan}}}{{*}}{{{content}}}";
                    for (int i = 0; i < span; i++) occupiedUntil[column + i] = rowIndex + cell.RowSpan;
                }
                if (span > 1)
                    content = $"\\multicolumn{{{span}}}{{|c|}}{{{content}}}";
                cells.Add(content);
                column += span;
            }
            while (cells.Count < columns) cells.Add("");
            output.Append(string.Join(" & ", cells)).Append(" \\\\\n\\hline\n");
        }
        output.Append("\\end{tabular}");
        return output.ToString();
    }

    private static string EscapeLatexCell(string text)
    {
        var output = new System.Text.StringBuilder();
        var matches = Regex.Matches(text, @"\$\$.*?\$\$|\\{1,2}\[.*?\\{1,2}\]|\\{1,2}\(.*?\\{1,2}\)|(?<!\\)\$(?!\$).*?(?<!\\)\$(?!\$)", RegexOptions.Singleline);
        int position = 0;
        foreach (Match match in matches)
        {
            output.Append(EscapeLatexPlainText(text[position..match.Index]));
            output.Append(match.Value);
            position = match.Index + match.Length;
        }
        output.Append(EscapeLatexPlainText(text[position..]));
        return output.ToString();
    }

    private static string EscapeLatexPlainText(string text)
    {
        var output = new System.Text.StringBuilder();
        foreach (char ch in text)
        {
            output.Append(ch switch
            {
                '#' => @"\#", '$' => @"\$", '%' => @"\%", '&' => @"\&",
                '_' => @"\_", '{' => @"\{", '}' => @"\}",
                '~' => @"\textasciitilde{}", '^' => @"\textasciicircum{}",
                '\\' => @"\textbackslash{}", _ => ch.ToString()
            });
        }
        return output.ToString();
    }

    private string BuildOfficeTable(List<List<HtmlTableCell>> rows, List<ImageItem> images, List<(string Token, string Tex, bool Display, string Source)> math)
    {
        var html = new System.Text.StringBuilder("<table border=\"1\" cellpadding=\"4\" cellspacing=\"0\"><tbody>");
        foreach (var row in rows)
        {
            html.Append("<tr>");
            foreach (var cell in row)
            {
                string tag = cell.IsHeader ? "th" : "td";
                html.Append('<').Append(tag);
                AppendSpanAttribute(html, "rowspan", cell.RowSpan);
                AppendSpanAttribute(html, "colspan", cell.ColSpan);
                html.Append('>').Append(OfficeText(CleanHtmlCellText(cell.Content), images, math));
                html.Append("</").Append(tag).Append('>');
            }
            html.Append("</tr>");
        }
        return html.Append("</tbody></table>").ToString();
    }

    private static string ReplaceHtmlTables(string? text, Func<List<List<HtmlTableCell>>, string> render, List<string> renderedTables)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        return Regex.Replace(text, @"<table\b[^>]*>(.*?)</table\s*>", match =>
        {
            var rows = new List<List<HtmlTableCell>>();
            foreach (Match rowMatch in Regex.Matches(match.Groups[1].Value, @"<tr\b[^>]*>(.*?)</tr\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                var cells = new List<HtmlTableCell>();
                foreach (Match cellMatch in Regex.Matches(rowMatch.Groups[1].Value, @"<(td|th)\b([^>]*)>(.*?)</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
                {
                    cells.Add(new HtmlTableCell(
                        cellMatch.Groups[3].Value,
                        ReadSpan(cellMatch.Groups[2].Value, "rowspan"),
                        ReadSpan(cellMatch.Groups[2].Value, "colspan"),
                        string.Equals(cellMatch.Groups[1].Value, "th", StringComparison.OrdinalIgnoreCase)));
                }
                if (cells.Count > 0) rows.Add(cells);
            }

            if (rows.Count == 0) return "";
            string token = $"__QB_TABLE_{renderedTables.Count}__";
            renderedTables.Add(render(rows));
            return token;
        }, RegexOptions.IgnoreCase | RegexOptions.Singleline);
    }

    private static int ReadSpan(string attributes, string name)
    {
        var match = Regex.Match(attributes, $@"\b{name}\s*=\s*[""']?(\d+)", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out int value) ? Math.Clamp(value, 1, 50) : 1;
    }

    private static void AppendSpanAttribute(System.Text.StringBuilder html, string name, int value)
    {
        if (value > 1) html.Append(' ').Append(name).Append("=\"").Append(value).Append('"');
    }

    private static string CleanHtmlCellText(string? text)
    {
        string clean = Regex.Replace(text ?? "", @"<br\s*/?>", " ", RegexOptions.IgnoreCase);
        clean = Regex.Replace(clean, @"<[^>]*>", " ");
        return System.Net.WebUtility.HtmlDecode(clean).Trim();
    }

    private static int GetTableColumnCount(List<List<HtmlTableCell>> rows)
    {
        var occupiedUntil = new List<int>();
        int maxColumns = 0;
        for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            int column = 0;
            foreach (var cell in rows[rowIndex])
            {
                while (column < occupiedUntil.Count && occupiedUntil[column] > rowIndex) column++;
                int span = cell.ColSpan;
                while (occupiedUntil.Count < column + span) occupiedUntil.Add(0);
                if (cell.RowSpan > 1)
                    for (int i = 0; i < span; i++) occupiedUntil[column + i] = rowIndex + cell.RowSpan;
                column += span;
            }
            maxColumns = Math.Max(maxColumns, column);
        }
        return Math.Max(1, maxColumns);
    }

    private sealed record HtmlTableCell(string Content, int RowSpan, int ColSpan, bool IsHeader);

    private async void CopyOfficeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentQuestion == null || _db == null || QuestionWebView.CoreWebView2 == null)
            return;

        CopyOfficeButton.IsEnabled = false;
        try
        {
            var item = _currentQuestion;
            var choices = _db.GetChoices(item.Id);
            var images = _db.GetImages(item.Id);
            string plainText = BuildLatexSource(item, choices, images);
            plainText = Regex.Replace(plainText, @"\\begin\{figure\}.*?\\end\{figure\}", " ", RegexOptions.Singleline);
            plainText = Regex.Replace(plainText, @"!\[.*?\]\(.*?\)", " ");
            plainText = Regex.Replace(plainText, @"\s+", " ").Trim();
            var math = new List<(string Token, string Tex, bool Display, string Source)>();
            string fragment = BuildOfficeFragment(item, choices, images, math);
            int sourceFormulaCount = 0;
            string formulasJson = JsonSerializer.Serialize(math.Select(x => new { tex = x.Tex, display = x.Display }));
            string mathmlJson;
            if (math.Count == 0)
            {
                mathmlJson = "{\"error\":null,\"results\":[]}";
            }
            else
            {
                bool mathJaxReady = false;
                for (int attempt = 0; attempt < 100; attempt++)
                {
                    string readyJson = await QuestionWebView.CoreWebView2.ExecuteScriptAsync(
                        "Boolean(window.MathJax && typeof window.MathJax.tex2mml === 'function')");
                    if (string.Equals(readyJson, "true", StringComparison.OrdinalIgnoreCase))
                    {
                        mathJaxReady = true;
                        break;
                    }
                    await Task.Delay(100);
                }

                if (!mathJaxReady)
                    throw new InvalidOperationException("MathJax 尚未加载，无法把公式转换为 Office 公式。请确认网络连接后重试。");

                string mathmlScript = """
                    (formulas => {
                      const results = formulas.map(f => {
                        try { return window.MathJax.tex2mml(f.tex, {display: f.display}); }
                        catch (_) { return null; }
                      });
                      return {error: null, results};
                    })
                    """;
                mathmlJson = await QuestionWebView.CoreWebView2.ExecuteScriptAsync($"{mathmlScript}({formulasJson})");
            }
            using (var mathmlDoc = JsonDocument.Parse(mathmlJson))
            {
                var root = mathmlDoc.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("results", out var results))
                    throw new InvalidOperationException("公式转换返回了无效结果，无法复制 Office 公式。");
                if (root.TryGetProperty("error", out var errorElement) && errorElement.ValueKind == JsonValueKind.String)
                    throw new InvalidOperationException(errorElement.GetString());
                for (int i = 0; i < math.Count; i++)
                {
                    string? mathml = results.ValueKind == JsonValueKind.Array && i < results.GetArrayLength() &&
                                     results[i].ValueKind == JsonValueKind.String
                        ? results[i].GetString()
                        : null;
                    bool conversionSucceeded = !string.IsNullOrWhiteSpace(mathml) &&
                        mathml.Contains("<math", StringComparison.OrdinalIgnoreCase) &&
                        !mathml.Contains("<merror", StringComparison.OrdinalIgnoreCase) &&
                        !mathml.Contains("misplaced &", StringComparison.OrdinalIgnoreCase);
                    string replacement;
                    if (conversionSucceeded)
                    {
                        replacement = mathml!;
                    }
                    else
                    {
                        replacement = $"<code>{HtmlEncode(math[i].Source)}</code>";
                        sourceFormulaCount++;
                    }
                    fragment = fragment.Replace(math[i].Token, replacement, StringComparison.Ordinal);
                }
            }

            string html = BuildCfHtml(fragment);
            var data = new DataObject();
            data.SetData(DataFormats.Html, html);
            data.SetData(DataFormats.UnicodeText, plainText);
            data.SetData(DataFormats.Text, plainText);
            Clipboard.SetDataObject(data, true);
            StatusText.Text = sourceFormulaCount == 0
                ? "可编辑题目块已复制。公式已转换为 Office 公式。"
                : $"可编辑题目块已复制；{sourceFormulaCount} 个无法转换的公式保留为 LaTeX 源码。";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "复制 Word/PPT 块失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            CopyOfficeButton.IsEnabled = _currentQuestion != null;
        }
    }

    private string BuildLatexSource(QuestionItem item, List<ChoiceItem> choices, List<ImageItem> images)
    {
        string heading = $"% {item.Year?.ToString() ?? "年份未知"} {item.Source} 第 {item.QuestionNo} 题";
        var lines = new List<string> { "% 表格或图片需要 graphicx/multirow 宏包。", heading, $"% 题型：{item.Type}", "", ConvertHtmlTablesToLatex(item.Content) };
        if (choices.Count > 0)
        {
            lines.Add("");
            foreach (var choice in choices)
                lines.Add($"{choice.Key}. {ConvertHtmlTablesToLatex(choice.Content)}" + (choice.IsCorrect ? "（正确）" : ""));
        }
        if (!string.IsNullOrWhiteSpace(item.Answer))
        {
            lines.Add("");
            lines.Add("答案：" + ConvertHtmlTablesToLatex(item.Answer));
        }
        if (!string.IsNullOrWhiteSpace(item.Analysis))
        {
            lines.Add("");
            lines.Add("解析：" + ConvertHtmlTablesToLatex(item.Analysis));
        }

        string source = string.Join(Environment.NewLine, lines);
        string baseDir = AppContext.BaseDirectory;
        source = Regex.Replace(source, @"!\[(?<alt>.*?)\]\((?<path>.*?)\)", match =>
        {
            string markdownPath = System.Net.WebUtility.HtmlDecode(match.Groups["path"].Value).Replace('\\', '/');
            string alt = match.Groups["alt"].Value;
            var image = images.FirstOrDefault(candidate =>
                string.Equals(candidate.FilePath.Replace('\\', '/'), markdownPath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidate.OriginalName, Path.GetFileName(markdownPath), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidate.AltText, alt, StringComparison.Ordinal));
            string? path = image == null ? null : ResolveImagePath(baseDir, image.FilePath);
            if (path == null) return match.Value;
            string latexPath = EscapeLatexPath(path.Replace('\\', '/'));
            return $"\\begin{{figure}}[htbp]{Environment.NewLine}\\centering{Environment.NewLine}\\includegraphics[width=\\linewidth]{{{latexPath}}}{Environment.NewLine}\\end{{figure}}";
        });
        return source;
    }

    private static string EscapeLatexPath(string path)
        => path.Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("#", "\\#", StringComparison.Ordinal)
            .Replace("&", "\\&", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal)
            .Replace("$", "\\$", StringComparison.Ordinal);

    private string BuildOfficeFragment(QuestionItem item, List<ChoiceItem> choices, List<ImageItem> images, List<(string Token, string Tex, bool Display, string Source)> math)
    {
        var html = new System.Text.StringBuilder();
        html.Append("<div style=\"font-family:Microsoft YaHei,Arial,sans-serif;font-size:12pt;line-height:1.6;color:#222\">");
        html.Append("<span>")
            .Append(HtmlEncode($"({item.Year?.ToString() ?? "年份未知"}{item.Source}{item.QuestionNo})"))
            .Append(" </span>");
        if (!string.IsNullOrWhiteSpace(item.Content))
            html.Append("<span>").Append(OfficeText(item.Content, images, math)).Append(" </span>");
        if (choices.Count > 0)
        {
            foreach (var choice in choices)
            {
                html.Append(" <b>").Append(HtmlEncode(choice.Key)).Append(".</b> ");
                html.Append(OfficeText(choice.Content, images, math));
            }
        }
        AppendOfficeSection(html, "答案", item.Answer, images, math);
        AppendOfficeSection(html, "解析", item.Analysis, images, math);
        html.Append("</div>");
        return html.ToString();
    }

    private void AppendOfficeSection(System.Text.StringBuilder html, string label, string? text, List<ImageItem> images, List<(string Token, string Tex, bool Display, string Source)> math)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        html.Append(" <b>").Append(label).Append("：</b>").Append(OfficeText(text, images, math)).Append(' ');
    }

    private string OfficeText(string? text, List<ImageItem> images, List<(string Token, string Tex, bool Display, string Source)> math)
    {
        var officeTables = new List<string>();
        string textWithTables = ReplaceHtmlTables(text, rows => BuildOfficeTable(rows, images, math), officeTables);
        string encoded = HtmlEncode(textWithTables);
        encoded = Regex.Replace(encoded, @"!\[(.*?)\]\((.*?)\)", match =>
        {
            string markdownPath = System.Net.WebUtility.HtmlDecode(match.Groups[2].Value).Replace('\\', '/');
            string alt = System.Net.WebUtility.HtmlDecode(match.Groups[1].Value);
            var image = images.FirstOrDefault(candidate => string.Equals(candidate.FilePath.Replace('\\', '/'), markdownPath, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(candidate.OriginalName, Path.GetFileName(markdownPath), StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(candidate.AltText, alt, StringComparison.Ordinal));
            string? file = image == null ? null : ResolveImagePath(AppContext.BaseDirectory, image.FilePath);
            return file == null ? match.Value : OfficeImage(file, image!.AltText);
        });
        foreach (var image in images)
        {
            if (string.IsNullOrWhiteSpace(image.OriginalName) || !encoded.Contains(image.OriginalName, StringComparison.OrdinalIgnoreCase)) continue;
            string? file = ResolveImagePath(AppContext.BaseDirectory, image.FilePath);
            if (file != null) encoded = encoded.Replace(image.OriginalName, OfficeImage(file, image.AltText), StringComparison.OrdinalIgnoreCase);
        }
        encoded = Regex.Replace(encoded, @"\$\$(?<tex>.+?)\$\$|\\{1,2}\[(?<tex>.+?)\\{1,2}\]|\\{1,2}\((?<tex>.+?)\\{1,2}\)|(?<!\\)\$(?!\$)(?<tex>.+?)(?<!\\)\$(?!\$)", match =>
        {
            string tex = System.Net.WebUtility.HtmlDecode(match.Groups["tex"].Value);
            bool display = match.Value.StartsWith("$$", StringComparison.Ordinal) || Regex.IsMatch(match.Value, @"^\\{1,2}\[");
            string token = $"__QB_MATH_{math.Count}__";
            math.Add((token, tex, display, System.Net.WebUtility.HtmlDecode(match.Value)));
            return token;
        }, RegexOptions.Singleline);
        encoded = Regex.Replace(encoded, @"\s*\r?\n\s*", " ").Replace("\t", " ");
        for (int i = 0; i < officeTables.Count; i++)
            encoded = encoded.Replace($"__QB_TABLE_{i}__", officeTables[i], StringComparison.Ordinal);
        return encoded;
    }

    private static string OfficeImage(string path, string? alt)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        string mime = extension switch { ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp", ".svg" => "image/svg+xml", _ => "image/png" };
        return $"<img src=\"data:{mime};base64,{Convert.ToBase64String(File.ReadAllBytes(path))}\">";
    }

    private static string BuildCfHtml(string fragment)
    {
        const string startMarker = "<!--StartFragment-->";
        const string endMarker = "<!--EndFragment-->";
        string before = "<!DOCTYPE html><html><head><meta charset=\"utf-8\"></head><body>" + startMarker;
        string after = endMarker + "</body></html>";
        string html = before + fragment + after;
        string header = "Version:0.9\r\nStartHTML:0000000000\r\nEndHTML:0000000000\r\nStartFragment:0000000000\r\nEndFragment:0000000000\r\nStartSelection:0000000000\r\nEndSelection:0000000000\r\n";
        int startHtml = System.Text.Encoding.UTF8.GetByteCount(header);
        int startFragment = startHtml + System.Text.Encoding.UTF8.GetByteCount(before);
        int endFragment = startFragment + System.Text.Encoding.UTF8.GetByteCount(fragment);
        int endHtml = endFragment + System.Text.Encoding.UTF8.GetByteCount(after);
        header = $"Version:0.9\r\nStartHTML:{startHtml:D10}\r\nEndHTML:{endHtml:D10}\r\nStartFragment:{startFragment:D10}\r\nEndFragment:{endFragment:D10}\r\nStartSelection:{startFragment:D10}\r\nEndSelection:{endFragment:D10}\r\n";
        return header + html;
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
