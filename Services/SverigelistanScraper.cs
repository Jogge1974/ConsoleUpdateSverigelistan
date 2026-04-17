using System.Globalization;
using System.Net;
using System.Net.Http;
using HtmlAgilityPack;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SverigelistanScraperConsole.Models;
using SverigelistanScraperConsole.Models.Dto;

namespace SverigelistanScraperConsole.Services;

public interface ISverigelistanScraper
{
    Task<List<SverigelistanEntry>> FetchAsync(int startPageIndex = 1, int endPageIndex = 2);
    Task<EventorWebLoginDiagnostics> TestWebLoginAsync();
    Task<RunnerRankingTableResultDto> FetchRunnerRankingAsync(int personId);
}

public sealed class SverigelistanScraper : ISverigelistanScraper
{
    private const string BaseUrl = "https://eventor.orientering.se/Ranking/ol?pageIndex=";
    private const string RankingProbeUrl = BaseUrl + "1";
    private static readonly Uri LoginUri = new("https://eventor.orientering.se/Login");
    private readonly IConfiguration _configuration;
    private readonly ILogger<SverigelistanScraper> _logger;

    public SverigelistanScraper(IConfiguration configuration, ILogger<SverigelistanScraper> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<List<SverigelistanEntry>> FetchAsync(int startPageIndex = 1, int endPageIndex = 2)
    {
        using var authenticatedClient = await CreateAuthenticatedClientAsync();

        var results = new List<SverigelistanEntry>();
        var culture = CultureInfo.GetCultureInfo("sv-SE");

        for (var page = startPageIndex; page <= endPageIndex; page++)
        { 
            try
            {
                var url = BaseUrl + page;
                var html = await authenticatedClient.GetStringAsync(url);

                var doc = new HtmlDocument();
                doc.LoadHtml(html);

                var tables = doc.DocumentNode.SelectNodes("//table[contains(@class,'manualStriped') and contains(@class,'paged')]");
                if (tables == null || tables.Count == 0)
                {
                    continue;
                }

                for (var t = 0; t < tables.Count && t < 2; t++)
                {
                    var table = tables[t];
                    var gender = t == 0 ? "D" : "H";
                    var rows = table.SelectNodes(".//tr[td]");
                    if (rows == null)
                    {
                        continue;
                    }

                    foreach (var row in rows)
                    {
                        var cells = row.SelectNodes("./td");
                        if (cells == null || cells.Count < 4)
                        {
                            continue;
                        }

                        var rank = 0;
                        int.TryParse(InnerText(cells[0]), out rank);

                        string name = string.Empty;
                        int? runnerId = null;
                        var nameLink = cells[1].SelectSingleNode(".//a[@href]");
                        if (nameLink != null)
                        {
                            name = HtmlEntity.DeEntitize(nameLink.InnerText.Trim())!;
                            var href = nameLink.GetAttributeValue("href", "");
                            var idStr = href.Split('/')[^1];
                            if (int.TryParse(idStr, out var parsedRunnerId))
                            {
                                runnerId = parsedRunnerId;
                            }
                        }
                        else
                        {
                            name = HtmlEntity.DeEntitize(InnerText(cells[1]))!;
                        }

                        string club = string.Empty;
                        int? clubId = null;
                        var clubLink = cells[2].SelectSingleNode(".//a[@href]");
                        if (clubLink != null)
                        {
                            club = HtmlEntity.DeEntitize(clubLink.InnerText.Trim())!;
                            var href = clubLink.GetAttributeValue("href", "");
                            var parts = href.Split('/');
                            if (parts.Length > 0 && int.TryParse(parts[^1], out var parsedClubId))
                            {
                                clubId = parsedClubId;
                            }
                        }
                        else
                        {
                            club = HtmlEntity.DeEntitize(InnerText(cells[2]))!;
                        }

                        var points = 0m;
                        var pointsText = InnerText(cells[3]).Replace("\u00A0", " ").Trim();
                        decimal.TryParse(pointsText, NumberStyles.Any, culture, out points);

                        results.Add(new SverigelistanEntry
                        {
                            BirthYear = null,
                            Club = club,
                            ClubId = clubId,
                            Gender = gender,
                            Name = name,
                            PageIndex = page,
                            Points = points,
                            Rank = rank,
                            RunnerId = runnerId,
                        });
                    }
                }
            }
            catch
            {
                // Skip a failed page fetch and continue with remaining pages.
            }

            if (page < endPageIndex)
            {
                var delayMilliseconds = Random.Shared.Next(5_000, 30_001);
                var delaySeconds = delayMilliseconds / 1000d;
                _logger.LogInformation(
                    "Waiting {DelaySeconds:F1}s before next page. CurrentPage={CurrentPage} NextPage={NextPage}",
                    delaySeconds,
                    page,
                    page + 1);
                await Task.Delay(delayMilliseconds);
            }
        }

        return results;
    }

    public async Task<EventorWebLoginDiagnostics> TestWebLoginAsync()
    {
        var loginResult = await PerformLoginAsync();
        var responseCookies = loginResult.CookieContainer.GetCookies(LoginUri).Cast<Cookie>().ToList();

        return new EventorWebLoginDiagnostics
        {
            HasAspNetSessionCookie = responseCookies.Any(cookie => cookie.Name.Equals("ASP.NET_SessionId", StringComparison.OrdinalIgnoreCase)),
            HasAuthCookie = responseCookies.Any(cookie =>
                cookie.Name.Contains("Auth", StringComparison.OrdinalIgnoreCase)
                || cookie.Name.Contains("Forms", StringComparison.OrdinalIgnoreCase)
                || cookie.Name.Contains("Persistent", StringComparison.OrdinalIgnoreCase)),
            InitialStatusCode = loginResult.InitialStatusCode,
            LoginResponseUrl = loginResult.LoginResponseUrl,
            LoginStatusCode = loginResult.LoginStatusCode,
            RedirectLocation = loginResult.RedirectLocation,
            ResponseCookieNames = responseCookies.Select(cookie => cookie.Name).Distinct().OrderBy(name => name).ToArray(),
            Success = loginResult.Success,
        };
    }

    public async Task<RunnerRankingTableResultDto> FetchRunnerRankingAsync(int personId)
    {
        using var authenticatedClient = await CreateAuthenticatedClientAsync();
        var sourceUrl = $"https://eventor.orientering.se/Ranking/ol/Runner/Index/{personId}";
        var html = await authenticatedClient.GetStringAsync(sourceUrl);

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var result = new RunnerRankingTableResultDto
        {
            Message = null,
            PageTitle = doc.DocumentNode.SelectSingleNode("//title") is { } titleNode ? InnerText(titleNode) : null,
            PersonId = personId,
            SourceUrl = sourceUrl,
        };

        var table = doc.DocumentNode.SelectSingleNode("//table[@id='resultsTable']");
        if (table == null)
        {
            result.HasResultsTable = false;
            result.Success = false;
            result.Message = IsLoginPage(html)
                ? "Eventors inloggningssida returnerades i stallet for resultsTable."
                : "Inget resultsTable hittades i svaret. Personen eller organisationen har sannolikt inte behorighet, eller sa saknas rankingdata.";
            return result;
        }

        result.HasResultsTable = true;
        result.Success = true;
        result.Headers = ExtractTableHeaders(table);
        result.Rows = ExtractTableRows(table);

        if (result.Rows.Count == 0)
        {
            result.Message = "resultsTable hittades, men inga rader kunde lasas ut.";
        }

        return result;
    }

    private async Task<HttpClient> CreateAuthenticatedClientAsync()
    {
        var loginResult = await PerformLoginAsync();

        if (!loginResult.Success)
        {
            loginResult.Client.Dispose();
            throw new InvalidOperationException("Webbinloggning mot Eventor misslyckades. Kontrollera anvandarnamn/losenord eller att Eventors loginflode inte har andrats.");
        }

        return loginResult.Client;
    }

    private async Task<(HttpClient Client, CookieContainer CookieContainer, int InitialStatusCode, int LoginStatusCode, string? LoginResponseUrl, string? RedirectLocation, bool Success)> PerformLoginAsync()
    {
        var username = _configuration["Eventor:WebUsername"]
                       ?? _configuration["Eventor:Username"]
                       ?? _configuration["EVENTOR_WEB_USERNAME"];
        var password = _configuration["Eventor:WebPassword"]
                       ?? _configuration["Eventor:Password"]
                       ?? _configuration["EVENTOR_WEB_PASSWORD"];

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException("Eventor webbinloggning saknar anvandarnamn eller losenord i konfigurationen.");
        }

        var cookieContainer = new CookieContainer();
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            CookieContainer = cookieContainer,
            UseCookies = true,
        };

        var client = new HttpClient(handler, disposeHandler: true);
        ApplyDefaultHeaders(client);

        var initialResponse = await client.GetAsync(LoginUri);
        var initialStatusCode = (int)initialResponse.StatusCode;
        initialResponse.EnsureSuccessStatusCode();

        var form = new List<KeyValuePair<string, string>>
        {
            new("PersonUsername", username),
            new("PersonPassword", password),
            new("PersonPersistentLogin", "true"),
            new("PersonPersistentLogin", "false"),
            new("PersonLogin", "Logga in"),
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, LoginUri)
        {
            Content = new FormUrlEncodedContent(form),
        };
        request.Headers.Referrer = LoginUri;

        using var loginResponse = await client.SendAsync(request);
        var redirectLocation = loginResponse.Headers.Location?.ToString();
        var probeResponse = await client.GetAsync(RankingProbeUrl);
        var probeHtml = await probeResponse.Content.ReadAsStringAsync();
        var success = IsSuccessfulLogin(loginResponse.StatusCode, redirectLocation, cookieContainer, probeResponse.RequestMessage?.RequestUri?.ToString(), probeHtml);

        return (
            client,
            cookieContainer,
            initialStatusCode,
            (int)loginResponse.StatusCode,
            loginResponse.RequestMessage?.RequestUri?.ToString(),
            redirectLocation,
            success
        );
    }

    private static void ApplyDefaultHeaders(HttpClient client)
    {
        if (!client.DefaultRequestHeaders.Contains("User-Agent"))
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) OL-manager/1.0");
        }

        if (!client.DefaultRequestHeaders.Contains("Accept"))
        {
            client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        }

        if (!client.DefaultRequestHeaders.Contains("Accept-Language"))
        {
            client.DefaultRequestHeaders.Add("Accept-Language", "sv-SE,sv;q=0.9,en;q=0.8");
        }
    }

    private static bool IsSuccessfulLogin(HttpStatusCode statusCode, string? redirectLocation, CookieContainer cookieContainer, string? probeUrl, string probeHtml)
    {
        var cookies = cookieContainer.GetCookies(LoginUri).Cast<Cookie>().ToList();
        var hasSessionCookie = cookies.Any(cookie => cookie.Name.Equals("ASP.NET_SessionId", StringComparison.OrdinalIgnoreCase));
        var redirectedAwayFromLogin = !string.IsNullOrWhiteSpace(redirectLocation) && !redirectLocation.StartsWith("/Login", StringComparison.OrdinalIgnoreCase);
        var directSuccess = (int)statusCode >= 200 && (int)statusCode < 300 && cookies.Count > 0;
        var probeReachedRanking = !string.IsNullOrWhiteSpace(probeUrl)
                                  && probeUrl.Contains("/Ranking/ol", StringComparison.OrdinalIgnoreCase)
                                  && !IsLoginPage(probeHtml);

        return probeReachedRanking || (hasSessionCookie && (redirectedAwayFromLogin || directSuccess));
    }

    private static bool IsLoginPage(string html)
    {
        return html.Contains("PersonUsername", StringComparison.OrdinalIgnoreCase)
               && html.Contains("PersonPassword", StringComparison.OrdinalIgnoreCase)
               && html.Contains("PersonLogin", StringComparison.OrdinalIgnoreCase);
    }

    private static string InnerText(HtmlNode? node)
    {
        if (node is null)
        {
            return string.Empty;
        }

#pragma warning disable CS8602
        return HtmlEntity.DeEntitize(node!.InnerText ?? string.Empty).Trim();
#pragma warning restore CS8602
    }

    private static string[] ExtractTableHeaders(HtmlNode table)
    {
        var headerNodes = table.SelectNodes(".//thead//tr[1]//th|.//thead//tr[1]//td")
                          ?? table.SelectNodes(".//tr[1]//th|.//tr[1]//td");

        if (headerNodes == null)
        {
            return [];
        }

        return headerNodes.Select(InnerText).Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
    }

    private static List<RunnerRankingTableRowDto> ExtractTableRows(HtmlNode table)
    {
        var rows = new List<RunnerRankingTableRowDto>();
        var bodyRows = table.SelectNodes(".//tbody//tr[td]") ?? table.SelectNodes(".//tr[td]");

        if (bodyRows == null)
        {
            return rows;
        }

        foreach (var row in bodyRows)
        {
            var cells = row.SelectNodes("./th|./td");
            if (cells == null || cells.Count == 0)
            {
                continue;
            }

            var detailLink = row.SelectSingleNode(".//a[@href]")?.GetAttributeValue("href", string.Empty);

            rows.Add(new RunnerRankingTableRowDto
            {
                DetailLink = string.IsNullOrWhiteSpace(detailLink) ? null : detailLink,
                Cells = cells.Select(InnerText).ToArray(),
            });
        }

        return rows;
    }
}
