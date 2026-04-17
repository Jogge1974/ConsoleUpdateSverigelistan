# SverigelistanScraper Console

This project packages the original `SverigelistanScraper` logic as a Windows console app.

It can:

- `fetch` the Sverigelistan pages, defaulting to pages `1..40`.
- `test-login` against Eventor and return login diagnostics.
- `runner-ranking` for a specific person id.
- `fetch` also enriches birth years from MySQL and replaces the Sverigelistan rows in MySQL and Supabase.

## Build

```powershell
cd C:\Workspace\OL-Kollen-Sverigelistan-Update\consol-application
dotnet build -c Release
```

Because the project is configured with `win-x64` and an app host, the build output includes an `.exe`.

## Config

Copy `appsettings.json.example` to `appsettings.json`, or provide the same values through environment variables.

The scraper accepts these keys:

- `Eventor:WebUsername`
- `Eventor:WebPassword`
- `EVENTOR_WEB_USERNAME`
- `EVENTOR_WEB_PASSWORD`
- `ConnectionStrings:MySql`
- `MYSQL_CONNECTION_STRING`
- `SUPABASE_URL`
- `SUPABASE_SERVICE_ROLE_KEY`

Supabase skrivs nu via REST mot `https://<project>.supabase.co/rest/v1/`, så du behöver inte längre någon direkt Postgres-anslutning.

Optional defaults for command and page range can be set under:

- `Scraper:Command`
- `Scraper:StartPageIndex`
- `Scraper:EndPageIndex`
- `Scraper:PersonId`
- `Scraper:OutputPath`

## Examples

Fetch the full list:

```powershell
.\bin\Release\net8.0\win-x64\SverigelistanScraper.exe fetch --output .\output\sverigelistan.json
```

Test the login:

```powershell
.\bin\Release\net8.0\win-x64\SverigelistanScraper.exe test-login --output .\output\login.json
```

Fetch a runner ranking:

```powershell
.\bin\Release\net8.0\win-x64\SverigelistanScraper.exe runner-ranking --person-id 12345 --output .\output\runner.json
```

## Scheduler

For Windows Task Scheduler, point the task at the `.exe` and pass the command you want to run.

If you want the task to run unattended, use environment variables or keep `appsettings.json` next to the exe.
