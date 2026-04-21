using Microsoft.EntityFrameworkCore;
using YieldDataLogger.Api.Data.Entities;

namespace YieldDataLogger.Api.Data;

public sealed class YieldDbContext : DbContext
{
    public YieldDbContext(DbContextOptions<YieldDbContext> options) : base(options) { }

    public DbSet<PriceTickEntity> PriceTicks => Set<PriceTickEntity>();
    public DbSet<InstrumentEntity> Instruments => Set<InstrumentEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PriceTickEntity>(b =>
        {
            b.ToTable("PriceTicks");
            // Composite PK -> clustered by default on SQL Server, which gives us fast
            // range scans for "history for X between A and B". Ordering Symbol first
            // means all rows for one symbol live together on disk.
            b.HasKey(e => new { e.Symbol, e.TsUnix });
            b.Property(e => e.Symbol).IsRequired().HasMaxLength(32);
            b.Property(e => e.Source).IsRequired().HasMaxLength(16);
            b.Property(e => e.TsUnix);
            b.Property(e => e.Price);
        });

        modelBuilder.Entity<InstrumentEntity>(b =>
        {
            b.ToTable("Instruments");
            b.HasKey(e => e.CanonicalSymbol);
            b.Property(e => e.CanonicalSymbol).HasMaxLength(32);
            b.Property(e => e.CnbcSymbol).HasMaxLength(32);
            b.Property(e => e.Category).HasMaxLength(64);

            b.HasIndex(e => e.InvestingPid).IsUnique().HasFilter("[InvestingPid] IS NOT NULL");
            b.HasIndex(e => e.CnbcSymbol).IsUnique().HasFilter("[CnbcSymbol] IS NOT NULL");
        });
    }
}
