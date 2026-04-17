using System.Text;
using System.Text.Json;
using System.Collections;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SverigelistanScraperConsole.Services;

Console.OutputEncoding = Encoding.UTF8;

var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables()
    .Build();

var options = CliOptions.Parse(args, configuration);

if (options.ShowHelp)
{
    PrintUsage();
    return 0;
}

var fileLogger = string.IsNullOrWhiteSpace(options.OutputPath) ? null : new StreamWriter(Path.ChangeExtension(options.OutputPath, ".log"), append: true, Encoding.UTF8) { AutoFlush = true };

using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.SetMinimumLevel(LogLevel.Information);

    if (fileLogger is null)
    {
        builder.AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
        });
    }
    else
    {
        builder.AddProvider(new TextFileLoggerProvider(fileLogger));
    }
});

var logger = loggerFactory.CreateLogger("SverigelistanScraper");
var scraper = new SverigelistanScraper(configuration, loggerFactory.CreateLogger<SverigelistanScraper>());
var repository = new SverigelistanRepository(configuration, loggerFactory.CreateLogger<SverigelistanRepository>());
var jsonOptions = new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
};

try
{
    switch (options.Command)
    {
        case CliCommand.Fetch:
        {
            var entries = await scraper.FetchAsync(options.StartPageIndex, options.EndPageIndex);
            entries = await repository.PopulateBirthYearsAsync(entries);
            foreach (var entry in entries)
            {
                entry.Updated = DateTime.Today;
            }

            await repository.ReplaceAllAsync(entries);
            await WriteResultAsync(entries, options.OutputPath, jsonOptions);
            Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} OK. Fetched and loaded {entries.Count} entries.");
            return 0;
        }

        case CliCommand.TestLogin:
        {
            var diagnostics = await scraper.TestWebLoginAsync();
            await WriteResultAsync(diagnostics, options.OutputPath, jsonOptions);
            if (!diagnostics.Success)
            {
                Console.Error.WriteLine($"Login success: {diagnostics.Success}");
            }
            return diagnostics.Success ? 0 : 2;
        }

        case CliCommand.RunnerRanking:
        {
            if (options.PersonId is null)
            {
                Console.Error.WriteLine("Missing --person-id for runner-ranking.");
                PrintUsage();
                return 1;
            }

            var ranking = await scraper.FetchRunnerRankingAsync(options.PersonId.Value);
            await WriteResultAsync(ranking, options.OutputPath, jsonOptions);
            if (!ranking.Success)
            {
                Console.Error.WriteLine($"Runner ranking success: {ranking.Success}");
            }
            return ranking.Success ? 0 : 3;
        }

        default:
            PrintUsage();
            return 1;
    }
}
catch (Exception ex)
{
    var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
    Console.Error.WriteLine($"{timestamp} ERROR: {ex.Message}");

    if (!string.IsNullOrWhiteSpace(options.OutputPath))
    {
        var errorPath = Path.ChangeExtension(options.OutputPath, ".error.txt");
        await File.AppendAllTextAsync(errorPath, $"{timestamp}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}", Encoding.UTF8);
    }

    return 1;
}

static async Task WriteResultAsync<T>(T result, string? outputPath, JsonSerializerOptions jsonOptions)
{
    object payload = result!;
    var entryCount = 0;
    var isEnumerableResult = false;

    if (result is IEnumerable enumerable and not string)
    {
        isEnumerableResult = true;
        var list = enumerable.Cast<object?>().ToList();
        payload = list;
        entryCount = list.Count;
    }

    var json = JsonSerializer.Serialize(payload, jsonOptions);

    if (string.IsNullOrWhiteSpace(outputPath))
    {
        Console.WriteLine(json);
        return;
    }

    var fullPath = Path.GetFullPath(outputPath);
    var directory = Path.GetDirectoryName(fullPath);
    if (!string.IsNullOrWhiteSpace(directory))
    {
        Directory.CreateDirectory(directory);
    }

    var summary = isEnumerableResult
        ? $"Fetched {entryCount} entries."
        : "Result written.";

    await File.AppendAllTextAsync(fullPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {summary}{Environment.NewLine}", Encoding.UTF8);
    // await File.AppendAllTextAsync(Path.ChangeExtension(fullPath, ".summary.txt"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {summary}{Environment.NewLine}", Encoding.UTF8);
}

static void PrintUsage()
{
    Console.WriteLine("""
SverigelistanScraperConsole

Usage:
  SverigelistanScraper [fetch] [--start-page N] [--end-page N] [--output path]
  SverigelistanScraper test-login [--output path]
  SverigelistanScraper runner-ranking --person-id N [--output path]

Configuration:
  appsettings.json or environment variables can provide defaults for:
    Scraper:Command
    Scraper:StartPageIndex
    Scraper:EndPageIndex
    Scraper:PersonId
    Scraper:OutputPath
    Eventor:WebUsername
    Eventor:WebPassword
    EVENTOR_WEB_USERNAME
    EVENTOR_WEB_PASSWORD

  Command-line arguments override appsettings values.
""");
}

sealed class TextFileLoggerProvider : ILoggerProvider
{
    private readonly StreamWriter _writer;

    public TextFileLoggerProvider(StreamWriter writer)
    {
        _writer = writer;
    }

    public ILogger CreateLogger(string categoryName) => new TextFileLogger(_writer, categoryName);

    public void Dispose()
    {
        _writer.Dispose();
    }
}

sealed class TextFileLogger : ILogger
{
    private readonly StreamWriter _writer;
    private readonly string _categoryName;

    public TextFileLogger(StreamWriter writer, string categoryName)
    {
        _writer = writer;
        _categoryName = categoryName;
    }

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        _writer.WriteLine($"{timestamp} {logLevel,11}: {_categoryName} {message}");
        if (exception is not null)
        {
            _writer.WriteLine(exception);
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose()
        {
        }
    }
}

internal enum CliCommand
{
    Fetch,
    TestLogin,
    RunnerRanking,
    Help,
}

internal sealed record CliOptions(
    CliCommand Command,
    int StartPageIndex,
    int EndPageIndex,
    int? PersonId,
    string? OutputPath,
    bool ShowHelp)
{
    public static CliOptions Parse(string[] args, IConfiguration configuration)
    {
        var defaultCommand = ParseCommand(configuration["Scraper:Command"] ?? "fetch");
        var defaultStartPage = ParseOptionalInt(configuration["Scraper:StartPageIndex"], 1, "Scraper:StartPageIndex");
        var defaultEndPage = ParseOptionalInt(configuration["Scraper:EndPageIndex"], 40, "Scraper:EndPageIndex");
        var defaultPersonId = ParseOptionalNullableInt(configuration["Scraper:PersonId"], "Scraper:PersonId");
        var defaultOutputPath = configuration["Scraper:OutputPath"];

        var index = 0;
        var command = defaultCommand;

        if (args.Length > 0 && !args[0].StartsWith('-'))
        {
            command = ParseCommand(args[0]);
            index = 1;
        }

        var startPage = defaultStartPage;
        var endPage = defaultEndPage;
        int? personId = defaultPersonId;
        string? outputPath = defaultOutputPath;
        var showHelp = command == CliCommand.Help;

        while (index < args.Length)
        {
            var arg = args[index];

            switch (arg)
            {
                case "--start-page":
                    startPage = ParseRequiredInt(args, ref index, arg);
                    break;
                case "--end-page":
                    endPage = ParseRequiredInt(args, ref index, arg);
                    break;
                case "--person-id":
                    personId = ParseRequiredInt(args, ref index, arg);
                    break;
                case "--output":
                    outputPath = ParseRequiredString(args, ref index, arg);
                    break;
                case "--help":
                case "-h":
                case "/?":
                    showHelp = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{arg}'.");
            }

            index += 1;
        }

        if (command == CliCommand.RunnerRanking && personId is null)
        {
            throw new ArgumentException("runner-ranking requires --person-id.");
        }

        return new CliOptions(command, startPage, endPage, personId, outputPath, showHelp);
    }

    private static CliCommand ParseCommand(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "fetch" => CliCommand.Fetch,
            "test-login" => CliCommand.TestLogin,
            "runner-ranking" => CliCommand.RunnerRanking,
            "help" => CliCommand.Help,
            _ => throw new ArgumentException($"Unknown command '{value}'."),
        };
    }

    private static int ParseRequiredInt(string[] args, ref int index, string optionName)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Missing value for {optionName}.");
        }

        index += 1;
        if (!int.TryParse(args[index], out var parsed))
        {
            throw new ArgumentException($"Invalid integer value '{args[index]}' for {optionName}.");
        }

        return parsed;
    }

    private static int ParseOptionalInt(string? value, int fallback, string optionName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (!int.TryParse(value, out var parsed))
        {
            throw new ArgumentException($"Invalid integer value '{value}' for {optionName}.");
        }

        return parsed;
    }

    private static int? ParseOptionalNullableInt(string? value, string optionName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!int.TryParse(value, out var parsed))
        {
            throw new ArgumentException($"Invalid integer value '{value}' for {optionName}.");
        }

        return parsed;
    }

    private static string ParseRequiredString(string[] args, ref int index, string optionName)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Missing value for {optionName}.");
        }

        index += 1;
        return args[index];
    }
}
