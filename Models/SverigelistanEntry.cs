using System;

namespace SverigelistanScraperConsole.Models;

public class SverigelistanEntry
{
    public string Gender { get; set; } = string.Empty;
    public int Rank { get; set; }
    public string Name { get; set; } = string.Empty;
    public int? RunnerId { get; set; }
    public int? BirthYear { get; set; }
    public string Club { get; set; } = string.Empty;
    public int? ClubId { get; set; }
    public decimal Points { get; set; }
    public int PageIndex { get; set; }
    public DateTime Updated { get; set; }
}
