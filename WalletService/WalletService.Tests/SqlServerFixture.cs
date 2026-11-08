using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MsSql;
using WalletService.Data;
using WalletService.Tests.Api;

namespace WalletService.Tests;

/// <summary>
/// Starts one real SQL Server container for the whole test run and applies the EF migrations.
/// Shared through the <see cref="SqlServerCollection"/> collection so any test class
/// (including WebApplicationFactory-based API tests) can reuse the same database.
/// Tests isolate themselves by using fresh player ids and provider ids, so no reset is needed.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionString = new SqlConnectionStringBuilder(_container.GetConnectionString())
        {
            InitialCatalog = "WalletTests",
        }.ConnectionString;

        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public WalletDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<WalletDbContext>().UseSqlServer(ConnectionString).Options);

    private readonly Lazy<WalletApiFactory> _api;

    public SqlServerFixture() => _api = new Lazy<WalletApiFactory>(() => new WalletApiFactory(ConnectionString));

    /// <summary>One in-process Wallet API per test run, created on first use, sharing this database.</summary>
    public WalletApiFactory Api => _api.Value;

    public async Task DisposeAsync()
    {
        if (_api.IsValueCreated) await _api.Value.DisposeAsync();
        await _container.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "SqlServer";
}
