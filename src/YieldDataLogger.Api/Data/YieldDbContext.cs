using Microsoft.EntityFrameworkCore;
using YieldDataLogger.Api.Data.Entities;

namespace YieldDataLogger.Api.Data;

/// <summary>
/// EF Core context for the optional SQL backend. Only registered when
/// Storage:Sql:Enabled is true. The only entity is PriceTicks; the instrument catalog
/// lives in instruments.json (see InstrumentCatalog), not in the database.
/// </summary>
public sealed class YieldDbContext : DbContext
{
    public YieldDbContext(DbContextOptions<YieldDbContext> options) : base(options) { }

    public DbSet<PriceTickEntity> PriceTicks => Set<PriceTickEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PriceTickEntity>(b =>
        {
            b.ToTable("PriceTicks");
            b.HasKey(e => new { e.Symbol, e.TsUnix });
            b.Property(e => e.Symbol).IsRequired().HasMaxLength(32);
            b.Property(e => e.Source).IsRequired().HasMaxLength(16);
            b.Property(e => e.TsUnix);
            b.Property(e => e.Price);
        });
    }
}
