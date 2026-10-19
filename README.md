# QuestionBankBrowser

WPF + .NET 10 + SQLite + FTS5 + WebView2 的本地高考题库浏览器。

## 使用方法

1. 使用 Visual Studio 2022/2026 打开 `QuestionBankBrowser.sln`。
2. 还原 NuGet 包。
3. 将`Data`文件夹中的 `QuestionBank.db`和`images` 放到编译输出目录（例如 `bin\Release\net9.0-windows\`）或程序 exe 所在目录：
   `QuestionBankBrowser\QuestionBank.db`
4. 生成并运行。

程序默认从自身运行目录读取：

- QuestionBank.db
- Images\
- questionbank.config.json

## 功能

- 题干全文搜索
- 中文搜索的 LIKE 兼容路径
- FTS5 搜索与 BM25 排序
- 年份筛选
- 题型筛选
- 来源筛选
- 题目列表
- 上一题 / 下一题
- 选择题选项显示
- 题目图片显示
- LaTeX 数学公式显示
- 答案/解析显示
- 自适应窗口
- 可拖动左右栏宽度

## 注意

当前数学公式预览使用 MathJax CDN，因此首次显示数学公式需要网络。
如果希望完全离线运行，可将 MathJax 下载到 `viewer\mathjax` 后改为本地加载。

## 数据库兼容

本浏览器针对前面提供的 QuestionBankImporter 数据库结构编写。
