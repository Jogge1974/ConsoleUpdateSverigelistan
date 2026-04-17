namespace SverigelistanScraperConsole.Models;

public sealed class EventorWebLoginDiagnostics
{
    public bool HasAspNetSessionCookie { get; set; }
    public bool HasAuthCookie { get; set; }
    public int InitialStatusCode { get; set; }
    public int LoginStatusCode { get; set; }
    public string? LoginResponseUrl { get; set; }
    public string? RedirectLocation { get; set; }
    public string[] ResponseCookieNames { get; set; } = [];
    public bool Success { get; set; }
}
