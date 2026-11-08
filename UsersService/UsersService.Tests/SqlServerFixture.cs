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
/// <para>
/// Two databases: <see cref="ConnectionString"/> is used by the in-process API, whose outbox delivery service
/// delivers (and then deletes) every outbox row in it. <see cref="NoDeliveryConnectionString"/> is used by tests that
/// run <see cref="Registration.RegistrationService"/> with outbox delivery disabled and assert on the outbox tables,
/// so no delivery service can ever pick up (and delete) their rows.
/// </para>
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    private readonly Lazy<UsersApiFactory> _api;

    public SqlServerFixture() => _api = new Lazy<UsersApiFactory>(() => new UsersApiFactory(ConnectionString));

    public string ConnectionString { get; private set; } = string.Empty;

    public string NoDeliveryConnectionString { get; private set; } = string.Empty;

    /// <summary>One in-process Users API per test run, created on first use, sharing this database.</summary>
    public UsersApiFactory Api => _api.Value;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionString = WithCatalog("UsersTests");
        NoDeliveryConnectionString = WithCatalog("UsersTestsNoDelivery");

        await using var db = CreateContext();
        await db.Database.MigrateAsync();
        await using var noDelivery = CreateNoDeliveryContext();
        await noDelivery.Database.MigrateAsync();
    }

    private string WithCatalog(string catalog) =>
        new SqlConnectionStringBuilder(_container.GetConnectionString()) { InitialCatalog = catalog }.ConnectionString;

    public UsersDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<UsersDbContext>().UseSqlServer(ConnectionString).Options);

    public UsersDbContext CreateNoDeliveryContext() =>
        new(new DbContextOptionsBuilder<UsersDbContext>().UseSqlServer(NoDeliveryConnectionString).Options);

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
