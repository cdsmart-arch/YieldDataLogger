using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace YieldDataLogger.Api.Data;

/// <summary>
/// Design-time factory for `dotnet ef` commands. The runtime DI only registers the
/// DbContext when Storage:Sql:Enabled = true, so the EF tooling needs this shim to
/// instantiate a context for generating migrations. The connection string here is only
/// used when the EF tools actually hit the database (e.g. `dotnet ef database update`);
/// migrations generation itself doesn't touch SQL Server.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<YieldDbContext>
{
    public YieldDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<YieldDbContext>()
            .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=YieldDataLogger;Trusted_Connection=True;TrustServerCertificate=True;Encrypt=False")
            .Options;
        return new YieldDbContext(options);
    }
}
