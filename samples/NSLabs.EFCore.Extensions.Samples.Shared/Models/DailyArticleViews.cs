namespace NSLabs.EFCore.Extensions.Samples.Models;

public class DailyArticleViews
{
    public int Id { get; set; }

    public int ArticleId { get; set; }

    public DateTime Date { get; set; }

    public int Views { get; set; }
}
