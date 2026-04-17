using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using SverigelistanScraperConsole.Models;

namespace SverigelistanScraperConsole.Services;

public interface ISverigelistanRepository
{
    Task<List<SverigelistanEntry>> PopulateBirthYearsAsync(List<SverigelistanEntry> items);
    Task ReplaceAllAsync(List<SverigelistanEntry> items);
}

public sealed class SverigelistanRepository : ISverigelistanRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ILogger<SverigelistanRepository> _logger;
    private readonly string _mySqlConnectionString;
    private readonly string _supabaseUrl;
    private readonly string _supabaseServiceRoleKey;
    private readonly HttpClient _httpClient;

    public SverigelistanRepository(IConfiguration configuration, ILogger<SverigelistanRepository> logger)
    {
        _logger = logger;
        _mySqlConnectionString =
            configuration.GetConnectionString("MySql") ??
            configuration.GetConnectionString("DefaultConnection") ??
            configuration["MYSQL_CONNECTION_STRING"] ??
            string.Empty;

        _supabaseUrl =
            configuration["SUPABASE_URL"] ??
            configuration.GetConnectionString("SupabaseUrl") ??
            string.Empty;

        _supabaseServiceRoleKey =
            configuration["SUPABASE_SERVICE_ROLE_KEY"] ??
            configuration["SUPABASE_ANON_KEY"] ??
            string.Empty;

        _httpClient = new HttpClient();
    }

    public async Task<List<SverigelistanEntry>> PopulateBirthYearsAsync(List<SverigelistanEntry> items)
    {
        EnsureMySqlConfigured();

        _logger.LogInformation("Loading birth years from MySQL for {ItemCount} entries.", items.Count);

        using var conn = new MySqlConnection(_mySqlConnectionString);
        await conn.OpenAsync();

        var birthYearsByRunnerId = new Dictionary<int, int?>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT `Id`, `BirthYear` FROM `OLM_PersonregisterEventor`";

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var id = reader.GetInt32(0);
                var birthYear = reader.IsDBNull(1) ? (int?)null : reader.GetInt32(1);
                birthYearsByRunnerId[id] = birthYear;
            }
        }

        foreach (var item in items)
        {
            if (item.RunnerId.HasValue
                && birthYearsByRunnerId.TryGetValue(item.RunnerId.Value, out var birthYear)
                && birthYear.HasValue)
            {
                item.BirthYear = birthYear.Value;
            }
        }

        return items;
    }

    public async Task ReplaceAllAsync(List<SverigelistanEntry> items)
    {
        EnsureMySqlConfigured();
        EnsureSupabaseConfigured();

        await ReplaceAllInMySqlAsync(items);
        await ReplaceAllInSupabaseAsync(items);
    }

    private async Task ReplaceAllInMySqlAsync(IReadOnlyList<SverigelistanEntry> items)
    {
        _logger.LogInformation("Replacing Sverigelistan in MySQL. ItemCount={ItemCount}", items.Count);

        using var conn = new MySqlConnection(_mySqlConnectionString);
        await conn.OpenAsync();

        using var tx = await conn.BeginTransactionAsync();
        var updatedDate = DateTime.Today.AddDays(1 - DateTime.Today.Day);
        var updatedYearAgoDate = updatedDate.AddYears(-1).AddMonths(1);

        using (var clearCmd = conn.CreateCommand())
        {
            clearCmd.Transaction = (MySqlTransaction)tx;
            clearCmd.CommandText = "DELETE FROM OLM_Sverigelistan WHERE Updated = '0000-00-00' or Updated >= @updatedDate or Updated < @updatedYearAgoDate";
            clearCmd.Parameters.AddWithValue("@updatedDate", updatedDate.ToString("yyyy-MM-dd"));
            clearCmd.Parameters.AddWithValue("@updatedYearAgoDate", updatedYearAgoDate.ToString("yyyy-MM-dd"));
            await clearCmd.ExecuteNonQueryAsync();
        }

        const int batchSize = 500;
        for (var i = 0; i < items.Count; i += batchSize)
        {
            var take = Math.Min(batchSize, items.Count - i);
            using var cmd = conn.CreateCommand();
            cmd.Transaction = (MySqlTransaction)tx;

            var sb = new StringBuilder();
            sb.Append("INSERT INTO `OLM_Sverigelistan` (`Gender`, `Rank`, `Name`, `RunnerId`, `Club`, `ClubId`, `Points`, `PageIndex`, `BirthYear`, `Updated`) VALUES ");

            for (var j = 0; j < take; j++)
            {
                if (j > 0)
                {
                    sb.Append(',');
                }

                var idx = i + j;
                sb.Append($"(@g{j}, @r{j}, @n{j}, @rid{j}, @c{j}, @cid{j}, @p{j}, @pi{j}, @by{j}, @u{j})");

                var it = items[idx];
                cmd.Parameters.AddWithValue($"@g{j}", it.Gender);
                cmd.Parameters.AddWithValue($"@r{j}", it.Rank);
                cmd.Parameters.AddWithValue($"@n{j}", it.Name);
                cmd.Parameters.AddWithValue($"@rid{j}", (object?)it.RunnerId ?? DBNull.Value);
                cmd.Parameters.AddWithValue($"@c{j}", it.Club);
                cmd.Parameters.AddWithValue($"@cid{j}", (object?)it.ClubId ?? DBNull.Value);
                cmd.Parameters.AddWithValue($"@p{j}", it.Points);
                cmd.Parameters.AddWithValue($"@pi{j}", it.PageIndex);
                cmd.Parameters.AddWithValue($"@by{j}", (object?)it.BirthYear ?? DBNull.Value);
                cmd.Parameters.AddWithValue($"@u{j}", DateTime.Today.ToString("yyyy-MM-dd"));
            }

            cmd.CommandText = sb.ToString();
            await cmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
        _logger.LogInformation("MySQL replacement completed. ItemCount={ItemCount}", items.Count);
    }

    private async Task ReplaceAllInSupabaseAsync(List<SverigelistanEntry> items)
    {
        _logger.LogInformation("Replacing Sverigelistan in Supabase via REST. ItemCount={ItemCount}", items.Count);

        var updatedDate = DateTime.Today.AddDays(1 - DateTime.Today.Day);
        var updatedYearAgoDate = updatedDate.AddYears(-1).AddMonths(1);
        var tablePath = $"{_supabaseUrl.TrimEnd('/')}/rest/v1/Sverigelistan";

        await DeleteSupabaseRowsAsync(tablePath, updatedDate, updatedYearAgoDate);
        await InsertSupabaseRowsAsync(tablePath, items);

        _logger.LogInformation("Supabase replacement completed. ItemCount={ItemCount}", items.Count);
    }

    private async Task DeleteSupabaseRowsAsync(string tablePath, DateTime updatedDate, DateTime updatedYearAgoDate)
    {
        var deleteUrl =
            $"{tablePath}?Updated=gte.{Uri.EscapeDataString(updatedDate.ToString("yyyy-MM-dd"))}" +
            $"&Updated=lt.{Uri.EscapeDataString(updatedYearAgoDate.ToString("yyyy-MM-dd"))}";

        using var request = new HttpRequestMessage(HttpMethod.Delete, deleteUrl);
        ApplySupabaseHeaders(request);
        request.Headers.Add("Prefer", "return=minimal");

        using var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Supabase delete failed with {(int)response.StatusCode}: {body}");
        }
    }

    private async Task InsertSupabaseRowsAsync(string tablePath, IReadOnlyList<SverigelistanEntry> items)
    {
        const int batchSize = 500;
        for (var i = 0; i < items.Count; i += batchSize)
        {
            var take = Math.Min(batchSize, items.Count - i);
            var batch = new List<Dictionary<string, object?>>(take);

            for (var j = 0; j < take; j++)
            {
                var it = items[i + j];
                batch.Add(new Dictionary<string, object?>
                {
                    ["Gender"] = it.Gender,
                    ["Rank"] = it.Rank,
                    ["Name"] = it.Name,
                    ["RunnerId"] = it.RunnerId,
                    ["Club"] = it.Club,
                    ["ClubId"] = it.ClubId,
                    ["Points"] = it.Points,
                    ["PageIndex"] = it.PageIndex,
                    ["BirthYear"] = it.BirthYear,
                    ["Updated"] = DateOnly.FromDateTime(it.Updated == default ? DateTime.Today : it.Updated),
                });
            }

            var json = JsonSerializer.Serialize(batch, JsonOptions);
            using var request = new HttpRequestMessage(HttpMethod.Post, tablePath)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            ApplySupabaseHeaders(request);
            request.Headers.Add("Prefer", "return=minimal");

            using var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                throw new InvalidOperationException($"Supabase insert failed with {(int)response.StatusCode}: {body}");
            }
        }
    }

    private void ApplySupabaseHeaders(HttpRequestMessage request)
    {
        request.Headers.Add("apikey", _supabaseServiceRoleKey);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _supabaseServiceRoleKey);
    }

    private void EnsureMySqlConfigured()
    {
        if (string.IsNullOrWhiteSpace(_mySqlConnectionString))
        {
            throw new InvalidOperationException("Missing MySql connection string. Set ConnectionStrings:MySql or MYSQL_CONNECTION_STRING.");
        }
    }

    private void EnsureSupabaseConfigured()
    {
        if (string.IsNullOrWhiteSpace(_supabaseUrl))
        {
            throw new InvalidOperationException("Missing Supabase URL. Set SUPABASE_URL or ConnectionStrings:SupabaseUrl.");
        }

        if (string.IsNullOrWhiteSpace(_supabaseServiceRoleKey))
        {
            throw new InvalidOperationException("Missing Supabase service role key. Set SUPABASE_SERVICE_ROLE_KEY or SUPABASE_ANON_KEY.");
        }
    }
}
