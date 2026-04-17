namespace SverigelistanScraperConsole.Models.Dto;

public sealed class RunnerRankingTableResultDto
{
    public bool HasResultsTable { get; set; }
    public string? Message { get; set; }
    public string? PageTitle { get; set; }
    public string? SourceUrl { get; set; }
    public int PersonId { get; set; }
    public string[] Headers { get; set; } = [];
    public List<RunnerRankingTableRowDto> Rows { get; set; } = [];
    public bool Success { get; set; }
}
