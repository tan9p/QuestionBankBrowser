namespace QuestionBankBrowser;

public sealed class QuestionItem
{
    public long Id { get; set; }
    public string Type { get; set; } = "";
    public int? Year { get; set; }
    public string Source { get; set; } = "";
    public string QuestionNo { get; set; } = "";
    public string Content { get; set; } = "";
    public string Answer { get; set; } = "";
    public string Analysis { get; set; } = "";
    public string ExamName { get; set; } = "";
}

public sealed class ChoiceItem
{
    public string Key { get; set; } = "";
    public string Content { get; set; } = "";
    public int SortOrder { get; set; }
    public bool IsCorrect { get; set; }
}

public sealed class ImageItem
{
    public string FilePath { get; set; } = "";
    public string OriginalName { get; set; } = "";
    public string AltText { get; set; } = "";
    public int SortOrder { get; set; }
}

public sealed class FilterOptions
{
    public List<int> Years { get; } = [];
    public List<string> Types { get; } = [];
    public List<string> Sources { get; } = [];
}
