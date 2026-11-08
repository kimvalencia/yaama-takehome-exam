using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MsSql;
using UsersService.Data;
using UsersService.Tests.Api;

namespace UsersService.Tests;

/// <summary>
/// Starts one real SQL Server container for the whole test run and applies the EF migrations.
/// Shared through the <see cref="SqlServerCollection"/> collection so service tests and the
/// WebApplicationFactory-based API tests reuse the same database.
/// Tests isolate themselves by using fresh email addresses, so no reset is needed.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    private readonly Lazy<UsersApiFactory> _api;

    public SqlServerFixture() => _api = new Lazy<UsersApiFactory>(() => new UsersApiFactory(ConnectionString));

    public string ConnectionString { get; private set; } = string.Empty;

    /// <summary>One in-process Users API per test run, created on first use, sharing this database.</summary>
    public UsersApiFactory Api => _api.Value;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionString = new SqlConnectionStringBuilder(_container.GetConnectionString())
        {
            InitialCatalog = "UsersTests",
        }.ConnectionString;

        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public UsersDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<UsersDbContext>().UseSqlServer(ConnectionString).Options);

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
