using HtmlAgilityPack;

namespace BrainApp.Core.Skills;

public static class SkillHelpers
{
    public static string ExtractTextFromHtml(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        foreach (var node in doc.DocumentNode.SelectNodes("//script|//style|//head") ?? Enumerable.Empty<HtmlNode>())
            node.Remove();

        return doc.DocumentNode.InnerText.Trim();
    }
}
