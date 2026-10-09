using Microsoft.EntityFrameworkCore;
using SecureFact.Platform.Persistence;
using SecureFact.Platform.Tenancy;
using SecureFact.Subscriptions.Domain;

namespace SecureFact.Subscriptions.Infrastructure;

internal sealed class SubscriptionsDbContext(DbContextOptions<SubscriptionsDbContext> options, IDataScope scope) : TenantDbContext(options, scope)
{
    public const string Schema = "subscription";

    public DbSet<PlanPrice> Prices => Set<PlanPrice>();

    public DbSet<BillingPolicy> Policies => Set<BillingPolicy>();

    public DbSet<Charge> Charges => Set<Charge>();

    public DbSet<Payment> Payments => Set<Payment>();

    public DbSet<CommissionSchedule> Schedules => Set<CommissionSchedule>();

    public DbSet<CommissionTier> Tiers => Set<CommissionTier>();

    public DbSet<CommissionEntry> Entries => Set<CommissionEntry>();

    public DbSet<CommissionSettlement> Settlements => Set<CommissionSettlement>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasDefaultSchema(Schema);

        // Prices and the billing policy are platform data: every scope reads them (a tenant sees what it pays), only the platform scope writes (RLS global_read / platform_write), and a trigger refuses
        // any edit or deletion, so a published version stays as it was published.
        modelBuilder.Entity<PlanPrice>(b =>
        {
            b.ToTable("plan_price");
            b.HasKey(p => p.Id);
            b.Property(p => p.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(p => p.PlanId).HasColumnName("plan_id");
            b.Property(p => p.Version).HasColumnName("version");
            b.Property(p => p.EffectiveFrom).HasColumnName("effective_from");
            b.Property(p => p.MonthlyFee).HasColumnName("monthly_fee").HasColumnType("numeric(14,2)");
            b.Property(p => p.IncludedDocuments).HasColumnName("included_documents");
            b.Property(p => p.OverageUnitPrice).HasColumnName("overage_unit_price").HasColumnType("numeric(14,4)");
            b.Property(p => p.Note).HasColumnName("note").HasMaxLength(300);
            b.Property(p => p.CreatedAt).HasColumnName("created_at");
            b.HasIndex(p => new { p.PlanId, p.Version }).IsUnique();
            b.HasIndex(p => new { p.PlanId, p.EffectiveFrom }).IsUnique();
        });

        modelBuilder.Entity<BillingPolicy>(b =>
        {
            b.ToTable("billing_policy");
            b.HasKey(p => p.Id);
            b.Property(p => p.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(p => p.Version).HasColumnName("version");
            b.Property(p => p.EffectiveFrom).HasColumnName("effective_from");
            b.Property(p => p.DueDays).HasColumnName("due_days");
            b.Property(p => p.SuspendAfterDays).HasColumnName("suspend_after_days");
            b.Property(p => p.Note).HasColumnName("note").HasMaxLength(300);
            b.Property(p => p.CreatedAt).HasColumnName("created_at");
            b.HasIndex(p => p.Version).IsUnique();
            b.HasIndex(p => p.EffectiveFrom).IsUnique();
        });

        modelBuilder.Entity<Charge>(b =>
        {
            b.ToTable("charge");
            b.HasKey(c => c.Id);
            b.Property(c => c.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(c => c.TenantName).HasColumnName("tenant_name").HasMaxLength(120).IsRequired();
            b.Property(c => c.Period).HasColumnName("period");
            b.Property(c => c.PlanId).HasColumnName("plan_id");
            b.Property(c => c.PlanCode).HasColumnName("plan_code").HasMaxLength(40).IsRequired();
            b.Property(c => c.PlanName).HasColumnName("plan_name").HasMaxLength(80).IsRequired();
            b.Property(c => c.PriceId).HasColumnName("price_id");
            b.Property(c => c.MonthlyFee).HasColumnName("monthly_fee").HasColumnType("numeric(14,2)");
            b.Property(c => c.IncludedDocuments).HasColumnName("included_documents");
            b.Property(c => c.DocumentsIssued).HasColumnName("documents_issued");
            b.Property(c => c.OverageDocuments).HasColumnName("overage_documents");
            b.Property(c => c.OverageUnitPrice).HasColumnName("overage_unit_price").HasColumnType("numeric(14,4)");
            b.Property(c => c.OverageAmount).HasColumnName("overage_amount").HasColumnType("numeric(14,2)");
            b.Property(c => c.NetAmount).HasColumnName("net_amount").HasColumnType("numeric(14,2)");
            b.Property(c => c.TaxRate).HasColumnName("tax_rate").HasColumnType("numeric(7,4)");
            b.Property(c => c.TaxAmount).HasColumnName("tax_amount").HasColumnType("numeric(14,2)");
            b.Property(c => c.TotalAmount).HasColumnName("total_amount").HasColumnType("numeric(14,2)");
            b.Property(c => c.Currency).HasColumnName("currency").HasMaxLength(3).IsRequired();
            b.Property(c => c.IssuedOn).HasColumnName("issued_on");
            b.Property(c => c.DueOn).HasColumnName("due_on");
            b.Property(c => c.SuspendOn).HasColumnName("suspend_on");
            b.Property(c => c.ResellerId).HasColumnName("reseller_id");
            b.Property(c => c.CommissionScheduleId).HasColumnName("commission_schedule_id");
            b.Property(c => c.VoidedAt).HasColumnName("voided_at");
            b.Property(c => c.VoidReason).HasColumnName("void_reason").HasMaxLength(300);
            b.Property(c => c.CreatedAt).HasColumnName("created_at");
            b.Property(c => c.Version).IsRowVersion();
            b.Ignore(c => c.IsVoid);
            b.HasIndex(c => new { c.TenantId, c.Period }).IsUnique();
            b.HasIndex(c => c.DueOn);
            ConfigureTenantOwned(b);
        });

        modelBuilder.Entity<Payment>(b =>
        {
            b.ToTable("payment");
            b.HasKey(p => p.Id);
            b.Property(p => p.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(p => p.ChargeId).HasColumnName("charge_id");
            b.Property(p => p.Amount).HasColumnName("amount").HasColumnType("numeric(14,2)");
            b.Property(p => p.Method).HasColumnName("method").HasConversion<string>().HasMaxLength(20);
            b.Property(p => p.Reference).HasColumnName("reference").HasMaxLength(100);
            b.Property(p => p.PaidOn).HasColumnName("paid_on");
            b.Property(p => p.Note).HasColumnName("note").HasMaxLength(300);
            b.Property(p => p.ReversesPaymentId).HasColumnName("reverses_payment_id");
            b.Property(p => p.RecordedAt).HasColumnName("recorded_at");
            b.HasIndex(p => p.ChargeId);
            b.HasIndex(p => p.ReversesPaymentId).IsUnique().HasFilter("reverses_payment_id IS NOT NULL");
            ConfigureTenantOwned(b);
        });

        // Commissions belong to the platform and its resellers, not to a tenant: only the platform scope reads or writes them (RLS platform_only), and the services filter by reseller, as in ADR-043.
        modelBuilder.Entity<CommissionSchedule>(b =>
        {
            b.ToTable("commission_schedule");
            b.HasKey(s => s.Id);
            b.Property(s => s.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(s => s.Version).HasColumnName("version");
            b.Property(s => s.EffectiveFrom).HasColumnName("effective_from");
            b.Property(s => s.Note).HasColumnName("note").HasMaxLength(300);
            b.Property(s => s.CreatedAt).HasColumnName("created_at");
            b.HasIndex(s => s.Version).IsUnique();
            b.HasIndex(s => s.EffectiveFrom).IsUnique();
        });

        modelBuilder.Entity<CommissionTier>(b =>
        {
            b.ToTable("commission_tier");
            b.HasKey(t => t.Id);
            b.Property(t => t.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(t => t.ScheduleId).HasColumnName("schedule_id");
            b.Property(t => t.MinAccounts).HasColumnName("min_accounts");
            b.Property(t => t.Rate).HasColumnName("rate").HasColumnType("numeric(5,4)");
            b.HasIndex(t => new { t.ScheduleId, t.MinAccounts }).IsUnique();
        });

        modelBuilder.Entity<CommissionEntry>(b =>
        {
            b.ToTable("commission_entry");
            b.HasKey(e => e.Id);
            b.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(e => e.ResellerId).HasColumnName("reseller_id");
            b.Property(e => e.TenantId).HasColumnName("tenant_id");
            b.Property(e => e.TenantName).HasColumnName("tenant_name").HasMaxLength(120).IsRequired();
            b.Property(e => e.ChargeId).HasColumnName("charge_id");
            b.Property(e => e.ChargePeriod).HasColumnName("charge_period");
            b.Property(e => e.PaymentId).HasColumnName("payment_id");
            b.Property(e => e.Month).HasColumnName("month");
            b.Property(e => e.BaseAmount).HasColumnName("base_amount").HasColumnType("numeric(14,2)");
            b.Property(e => e.Rate).HasColumnName("rate").HasColumnType("numeric(5,4)");
            b.Property(e => e.Amount).HasColumnName("amount").HasColumnType("numeric(14,2)");
            b.Property(e => e.ScheduleId).HasColumnName("schedule_id");
            b.Property(e => e.CreatedAt).HasColumnName("created_at");
            b.HasIndex(e => e.PaymentId).IsUnique();
            b.HasIndex(e => new { e.ResellerId, e.Month });
        });

        modelBuilder.Entity<CommissionSettlement>(b =>
        {
            b.ToTable("commission_settlement");
            b.HasKey(s => s.Id);
            b.Property(s => s.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(s => s.ResellerId).HasColumnName("reseller_id");
            b.Property(s => s.Month).HasColumnName("month");
            b.Property(s => s.Total).HasColumnName("total").HasColumnType("numeric(14,2)");
            b.Property(s => s.Entries).HasColumnName("entries");
            b.Property(s => s.SettledOn).HasColumnName("settled_on");
            b.Property(s => s.Reference).HasColumnName("reference").HasMaxLength(100);
            b.Property(s => s.Note).HasColumnName("note").HasMaxLength(300);
            b.Property(s => s.CreatedAt).HasColumnName("created_at");
            b.HasIndex(s => new { s.ResellerId, s.Month }).IsUnique();
        });
    }
}
