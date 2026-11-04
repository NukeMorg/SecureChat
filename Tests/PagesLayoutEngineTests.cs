using SecureChat.Core;
using SecureChat.Core.Printing;
using Xunit;

namespace SecureChat.Tests;

/// <summary>
/// TDD-тесты разбивки на страницы для PrintDocument (Лаба 4).
/// Семантика Split: стоимость сообщения — 1..maxLinesPerMessage строк (~80 символов/строка);
/// новая страница, когда message-бюджет исчерпан; пустой ввод / capacity ≤ 0 → страниц нет.
/// </summary>
public class PagesLayoutEngineTests
{
    private static ChatExportRow Row(string id, string text) =>
        new() { Id = id, Sender = "u", Text = text, TimestampUtc = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc) };

    private static string Text(int length) => new('x', length);

    [Fact]
    public void Split_EmptyRows_ReturnsNoPages()
    {
        Assert.Empty(PagesLayoutEngine.Split(new List<ChatExportRow>(), 44));
    }

    [Fact]
    public void Split_NonPositiveCapacity_ReturnsNoPages()
    {
        var rows = new List<ChatExportRow> { Row("m1", "hello") };
        Assert.Empty(PagesLayoutEngine.Split(rows, 0));
        Assert.Empty(PagesLayoutEngine.Split(rows, -5));
    }

    [Fact]
    public void Split_ShortMessages_FillOnePage()
    {
        var rows = new List<ChatExportRow>
        {
            Row("m1", "привет"),
            Row("m2", "как дела"),
            Row("m3", "ок")
        };

        var pages = PagesLayoutEngine.Split(rows, linesPerPage: 3);

        Assert.Single(pages);
        Assert.Equal(3, pages[0].Count);
    }

    [Fact]
    public void Split_PageBreak_WhenNextMessageDoesNotFit()
    {
        // стоимость каждого сообщения — 2 строки (81..160 символов), capacity 3
        var rows = new List<ChatExportRow>
        {
            Row("m1", Text(100)),
            Row("m2", Text(100))
        };

        var pages = PagesLayoutEngine.Split(rows, linesPerPage: 3);

        Assert.Equal(2, pages.Count);
        Assert.Single(pages[0]);
        Assert.Single(pages[1]);
    }

    [Fact]
    public void Split_LongMessage_OccupiesOwnPage()
    {
        // 300 символов → cost 4 > capacity 2: сообщение не может быть разрезано — на своей странице
        var rows = new List<ChatExportRow>
        {
            Row("m1", Text(300)),
            Row("m2", "short")
        };

        var pages = PagesLayoutEngine.Split(rows, linesPerPage: 2);

        Assert.Equal(2, pages.Count);
        Assert.Single(pages[0]);
        Assert.Single(pages[1]);
        Assert.Equal("m1", pages[0][0].Id);
        Assert.Equal("m2", pages[1][0].Id);
    }

    [Fact]
    public void Split_PreservesMessageOrder()
    {
        var rows = new List<ChatExportRow>();
        for (int i = 0; i < 10; i++)
            rows.Add(Row($"m{i:00}", Text(60))); // cost 1, capacity 3 → 4 страницы

        var pages = PagesLayoutEngine.Split(rows, 3);

        Assert.Equal(4, pages.Count);
        var flat = pages.SelectMany(p => p).Select(r => r.Id).ToList();
        Assert.Equal(rows.Select(r => r.Id), flat);
    }

    [Fact]
    public void EstimateLines_ByEightyCharacters_CappedAtMax()
    {
        Assert.Equal(1, PagesLayoutEngine.EstimateLines("", 4));
        Assert.Equal(1, PagesLayoutEngine.EstimateLines(Text(80), 4));
        Assert.Equal(2, PagesLayoutEngine.EstimateLines(Text(81), 4));
        Assert.Equal(3, PagesLayoutEngine.EstimateLines(Text(240), 4));
        Assert.Equal(4, PagesLayoutEngine.EstimateLines(Text(1000), 4)); // cap
        Assert.Equal(2, PagesLayoutEngine.EstimateLines(Text(1000), 2)); // max как параметр
    }
}
