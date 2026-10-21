# QuestionBankBrowser

WPF + .NET 9 + SQLite + FTS5 + WebView2 的本地高考题库浏览器。

## 使用方法

1. 使用 Visual Studio 2022/2026 打开 `QuestionBankBrowser.sln`。
2. 还原 NuGet 包。
3. 将之前导入生成的 `QuestionBank.db` 放到编译输出目录（例如 `bin\Debug\net10.0-windows\`）或程序 exe 所在目录：
   `QuestionBankBrowser\QuestionBank.db`
4. 将图片目录 `Images` 放到：
   `QuestionBankBrowser\Images`
   或者直接把数据库和 Images 放在编译输出目录。
5. 生成并运行。

程序默认从自身运行目录读取：

- QuestionBank.db
- Images\
- questionbank.config.json

WebView2 缓存和用户配置保存在 `%LOCALAPPDATA%\QuestionBankBrowser\WebView2`，避免安装在 `Program Files` 时因安装目录不可写而无法启动。题库数据库以只读方式打开。

## 制作安装包

1. 在 Visual Studio 中生成 `Release` 配置。
2. 使用 Inno Setup 打开 `installer\QuestionBankBrowser.iss` 并编译。安装脚本从仓库中的 Release 输出读取文件，不依赖个人电脑上的绝对路径。
3. 将 `QuestionBank.db`（以及题目所需图片目录）放在 `QuestionBankBrowser\bin\Release\net9.0-windows\` 后再编译安装包。

安装包版本为 1.0.5。安装时会检查 x64 .NET 9 Desktop Runtime；未安装时，安装程序优先从微软直连地址下载并静默安装，失败后再尝试微软 aka.ms 下载地址。该步骤需要网络连接和管理员权限。

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
- 可复制题目为图片、LaTeX源码、Office文档块

## 注意

当前数学公式预览使用 MathJax CDN，因此首次显示数学公式需要网络。
如果希望完全离线运行，可将 MathJax 下载到 `viewer\mathjax` 后改为本地加载。

## 数据库兼容

本浏览器针对前面提供的 QuestionBankImporter 数据库结构编写。

## 未来增加的功能

- 题目的标签机制
- 支持自主导入题目
- 更精致的高考真题版本
