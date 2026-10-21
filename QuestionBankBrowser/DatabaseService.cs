using Microsoft.Data.Sqlite;
using System.Text;
using System.Text.RegularExpressions;

using System.IO;

namespace QuestionBankBrowser;

public sealed class DatabaseService
{
    private readonly string _dbPath;
    private SqliteConnection? _connection;

    public DatabaseService(string dbPath)
    {
        _dbPath = Path.GetFullPath(dbPath);
    }

    public bool Exists => File.Exists(_dbPath);

    public void Open()
    {
        if (!Exists)
            throw new FileNotFoundException("找不到题库数据库。", _dbPath);

        // 浏览器只查询题库。以只读模式打开后，可直接运行在 Program Files 等
        // 没有写权限的安装目录中。
        _connection = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly;Foreign Keys=True");
        _connection.Open();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys=ON;";
        cmd.ExecuteNonQuery();
    }

    public FilterOptions LoadFilterOptions()
    {
        EnsureOpen();
        var result = new FilterOptions();

        using (var cmd = _connection!.CreateCommand())
        {
            cmd.CommandText = "SELECT DISTINCT Year FROM Questions WHERE Year IS NOT NULL ORDER BY Year DESC";
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
                result.Years.Add(rd.GetInt32(0));
        }

        using (var cmd = _connection!.CreateCommand())
        {
            cmd.CommandText = "SELECT DISTINCT Type FROM Questions WHERE Type IS NOT NULL AND Type<>'' ORDER BY Type";
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
                result.Types.Add(rd.GetString(0));
        }

        using (var cmd = _connection!.CreateCommand())
        {
            cmd.CommandText = """
                SELECT DISTINCT COALESCE(Source,'')
                FROM Questions
                WHERE Source IS NOT NULL AND Source<>''
                ORDER BY Source
                """;
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
                result.Sources.Add(rd.GetString(0));
        }

        return result;
    }

    public List<int> LoadYearsForSource(string? source)
    {
        EnsureOpen();
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = string.IsNullOrWhiteSpace(source)
            ? "SELECT DISTINCT Year FROM Questions WHERE Year IS NOT NULL ORDER BY Year DESC"
            : "SELECT DISTINCT Year FROM Questions WHERE Year IS NOT NULL AND Source=$source ORDER BY Year DESC";
        if (!string.IsNullOrWhiteSpace(source))
            cmd.Parameters.AddWithValue("$source", source);

        var years = new List<int>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            years.Add(reader.GetInt32(0));
        return years;
    }

    public List<string> LoadSourcesForYear(int? year)
    {
        EnsureOpen();
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = year.HasValue
            ? "SELECT DISTINCT Source FROM Questions WHERE Source IS NOT NULL AND Source<>'' AND Year=$year ORDER BY Source"
            : "SELECT DISTINCT Source FROM Questions WHERE Source IS NOT NULL AND Source<>'' ORDER BY Source";
        if (year.HasValue)
            cmd.Parameters.AddWithValue("$year", year.Value);

        var sources = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            sources.Add(reader.GetString(0));
        return sources;
    }

    public List<QuestionItem> Search(
        string text,
        int? year,
        string? type,
        string? source,
        int limit = 100,
        int offset = 0)
    {
        EnsureOpen();

        text = text.Trim();

        bool useFts = !string.IsNullOrWhiteSpace(text);
        bool looksLikeChinese = text.Any(c => c >= 0x3400 && c <= 0x9FFF);

        // 中文题库对 FTS5 的 tokenizer 依赖较大，因此中文搜索采用
        // “FTS5 + LIKE”双路径：先保证搜索结果稳定，再利用 FTS5 做相关性排序。
        var sql = new StringBuilder();

        if (useFts && !looksLikeChinese)
        {
            sql.Append("""
                SELECT q.Id, q.Type, q.Year, q.Source, q.QuestionNo,
                       q.Content, q.Answer, q.Analysis,
                       COALESCE(e.Name,'') AS ExamName
                FROM Questions q
                LEFT JOIN Exams e ON e.Id=q.ExamId
                JOIN QuestionSearch s ON s.rowid=q.Id
                WHERE QuestionSearch MATCH $fts
                """);
        }
        else if (useFts)
        {
            sql.Append("""
                SELECT q.Id, q.Type, q.Year, q.Source, q.QuestionNo,
                       q.Content, q.Answer, q.Analysis,
                       COALESCE(e.Name,'') AS ExamName
                FROM Questions q
                LEFT JOIN Exams e ON e.Id=q.ExamId
                WHERE (q.Content LIKE $like
                    OR COALESCE(q.Analysis,'') LIKE $like
                    OR EXISTS (
                        SELECT 1 FROM QuestionChoices c
                        WHERE c.QuestionId=q.Id AND c.Content LIKE $like
                    ))
                """);
        }
        else
        {
            sql.Append("""
                SELECT q.Id, q.Type, q.Year, q.Source, q.QuestionNo,
                       q.Content, q.Answer, q.Analysis,
                       COALESCE(e.Name,'') AS ExamName
                FROM Questions q
                LEFT JOIN Exams e ON e.Id=q.ExamId
                WHERE 1=1
                """);
        }

        if (year.HasValue)
            sql.Append(" AND q.Year=$year");

        if (!string.IsNullOrWhiteSpace(type))
            sql.Append(" AND q.Type=$type");

        if (!string.IsNullOrWhiteSpace(source))
            sql.Append(" AND q.Source=$source");

        if (useFts && !looksLikeChinese)
            sql.Append(" ORDER BY bm25(QuestionSearch), q.Year DESC, q.Id DESC");
        else
            sql.Append(" ORDER BY q.Year DESC, q.Id DESC");

        sql.Append(" LIMIT $limit OFFSET $offset");

        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql.ToString();

        if (useFts && !looksLikeChinese)
            cmd.Parameters.AddWithValue("$fts", BuildFtsQuery(text));
        else if (useFts)
            cmd.Parameters.AddWithValue("$like", $"%{text}%");

        if (year.HasValue)
            cmd.Parameters.AddWithValue("$year", year.Value);

        if (!string.IsNullOrWhiteSpace(type))
            cmd.Parameters.AddWithValue("$type", type);

        if (!string.IsNullOrWhiteSpace(source))
            cmd.Parameters.AddWithValue("$source", source);

        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);

        var result = new List<QuestionItem>();
        using var reader = cmd.ExecuteReader();

        while (reader.Read())
        {
            result.Add(new QuestionItem
            {
                Id = reader.GetInt64(0),
                Type = reader.IsDBNull(1) ? "" : reader.GetString(1),
                Year = reader.IsDBNull(2) ? null : reader.GetInt32(2),
                Source = reader.IsDBNull(3) ? "" : reader.GetString(3),
                QuestionNo = reader.IsDBNull(4) ? "" : reader.GetString(4),
                Content = reader.IsDBNull(5) ? "" : reader.GetString(5),
                Answer = reader.IsDBNull(6) ? "" : reader.GetString(6),
                Analysis = reader.IsDBNull(7) ? "" : reader.GetString(7),
                ExamName = reader.IsDBNull(8) ? "" : reader.GetString(8)
            });
        }

        return result;
    }

    public int Count(string text, int? year, string? type, string? source)
    {
        EnsureOpen();
        text = text.Trim();
        bool useFts = !string.IsNullOrWhiteSpace(text);
        bool looksLikeChinese = text.Any(c => c >= 0x3400 && c <= 0x9FFF);

        var sql = new StringBuilder("SELECT COUNT(*) FROM Questions q WHERE 1=1 ");

        if (useFts && !looksLikeChinese)
            sql.Append(" AND q.Id IN (SELECT rowid FROM QuestionSearch WHERE QuestionSearch MATCH $fts) ");
        else if (useFts)
            sql.Append("""
                AND (
                    q.Content LIKE $like
                    OR COALESCE(q.Analysis,'') LIKE $like
                    OR EXISTS (
                        SELECT 1 FROM QuestionChoices c
                        WHERE c.QuestionId=q.Id AND c.Content LIKE $like
                    )
                )
                """);

        if (year.HasValue)
            sql.Append(" AND q.Year=$year");

        if (!string.IsNullOrWhiteSpace(type))
            sql.Append(" AND q.Type=$type");

        if (!string.IsNullOrWhiteSpace(source))
            sql.Append(" AND q.Source=$source");

        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql.ToString();

        if (useFts && !looksLikeChinese)
            cmd.Parameters.AddWithValue("$fts", BuildFtsQuery(text));
        else if (useFts)
            cmd.Parameters.AddWithValue("$like", $"%{text}%");

        if (year.HasValue)
            cmd.Parameters.AddWithValue("$year", year.Value);

        if (!string.IsNullOrWhiteSpace(type))
            cmd.Parameters.AddWithValue("$type", type);

        if (!string.IsNullOrWhiteSpace(source))
            cmd.Parameters.AddWithValue("$source", source);

        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public List<ChoiceItem> GetChoices(long questionId)
    {
        EnsureOpen();
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = """
            SELECT ChoiceKey, Content, SortOrder, IsCorrect
            FROM QuestionChoices
            WHERE QuestionId=$id
            ORDER BY SortOrder, Id
            """;
        cmd.Parameters.AddWithValue("$id", questionId);

        var list = new List<ChoiceItem>();
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            list.Add(new ChoiceItem
            {
                Key = rd.GetString(0),
                Content = rd.IsDBNull(1) ? "" : rd.GetString(1),
                SortOrder = rd.GetInt32(2),
                IsCorrect = rd.GetInt32(3) != 0
            });
        }
        return list;
    }

    public List<ImageItem> GetImages(long questionId)
    {
        EnsureOpen();
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = """
            SELECT FilePath, OriginalName, AltText, SortOrder
            FROM QuestionImages
            WHERE QuestionId=$id
            ORDER BY SortOrder, Id
            """;
        cmd.Parameters.AddWithValue("$id", questionId);

        var list = new List<ImageItem>();
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            list.Add(new ImageItem
            {
                FilePath = rd.IsDBNull(0) ? "" : rd.GetString(0),
                OriginalName = rd.IsDBNull(1) ? "" : rd.GetString(1),
                AltText = rd.IsDBNull(2) ? "" : rd.GetString(2),
                SortOrder = rd.GetInt32(3)
            });
        }
        return list;
    }

    public string? GetScalar(string sql)
    {
        EnsureOpen();
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        var v = cmd.ExecuteScalar();
        return v == null || v == DBNull.Value ? null : v.ToString();
    }

    public void Dispose() => _connection?.Dispose();

    private void EnsureOpen()
    {
        if (_connection == null)
            throw new InvalidOperationException("数据库尚未打开。");
    }

    private static string BuildFtsQuery(string input)
    {
        var tokens = RegexTokenizer(input);
        if (tokens.Count == 0)
            return "\"\"";

        // 每个词都必须出现；短语和特殊字符统一加引号。
        return string.Join(" ", tokens.Select(t => $"\"{t.Replace("\"", "\"\"")}\""));
    }

    private static List<string> RegexTokenizer(string input)
    {
        return Regex.Matches(input, @"[\p{L}\p{N}_]+")
            .Select(m => m.Value)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
    }
}
