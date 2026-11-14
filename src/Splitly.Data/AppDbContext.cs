using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Splitly.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<Group> Groups => Set<Group>();
    public DbSet<GroupMember> GroupMembers => Set<GroupMember>();
    public DbSet<GroupInvite> GroupInvites => Set<GroupInvite>();
    public DbSet<Expense> Expenses => Set<Expense>();
    public DbSet<ExpenseShare> ExpenseShares => Set<ExpenseShare>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<Receipt> Receipts => Set<Receipt>();
    public DbSet<ReceiptItem> ReceiptItems => Set<ReceiptItem>();
    public DbSet<ItemClaim> ItemClaims => Set<ItemClaim>();
    public DbSet<RecurringExpense> RecurringExpenses => Set<RecurringExpense>();
    public DbSet<ExchangeRateEntry> ExchangeRates => Set<ExchangeRateEntry>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<AppUser>().Property(u => u.DisplayName).HasMaxLength(100);

        // Ids are assigned in code (Guid v7). Without this, EF treats a child that already has a key, when it is added
        // through a navigation, as an existing row and issues an UPDATE instead of an INSERT.
        b.Entity<Group>().Property(x => x.Id).ValueGeneratedNever();
        b.Entity<GroupInvite>().Property(x => x.Id).ValueGeneratedNever();
        b.Entity<Expense>().Property(x => x.Id).ValueGeneratedNever();
        b.Entity<Payment>().Property(x => x.Id).ValueGeneratedNever();
        b.Entity<Receipt>().Property(x => x.Id).ValueGeneratedNever();
        b.Entity<ReceiptItem>().Property(x => x.Id).ValueGeneratedNever();
        b.Entity<RecurringExpense>().Property(x => x.Id).ValueGeneratedNever();

        b.Entity<Group>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.Property(x => x.BaseCurrency).HasMaxLength(3).IsFixedLength().IsRequired();
            e.HasMany(x => x.Members).WithOne(m => m.Group).HasForeignKey(m => m.GroupId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<GroupMember>(e =>
        {
            e.HasKey(x => new { x.GroupId, x.UserId });
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.UserId);
        });

        b.Entity<GroupInvite>(e =>
        {
            e.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasOne<Group>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Expense>(e =>
        {
            e.Property(x => x.Description).HasMaxLength(200).IsRequired();
            e.Property(x => x.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
            e.Property(x => x.Amount).HasPrecision(19, 4);
            e.Property(x => x.BaseAmount).HasPrecision(19, 4);
            e.Property(x => x.ExchangeRate).HasPrecision(19, 8);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.HasOne<Group>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Shares).WithOne().HasForeignKey(s => s.ExpenseId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.GroupId, x.Date });
            // Backstop for idempotency: a recurring expense can produce at most one expense per date, even if two workers race.
            e.HasIndex(x => new { x.RecurringExpenseId, x.Date }).IsUnique().HasFilter("[RecurringExpenseId] IS NOT NULL");
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        b.Entity<ExpenseShare>(e =>
        {
            e.HasKey(x => new { x.ExpenseId, x.UserId });
            e.Property(x => x.Amount).HasPrecision(19, 4);
            e.Property(x => x.BaseAmount).HasPrecision(19, 4);
        });

        b.Entity<Payment>(e =>
        {
            e.Property(x => x.Amount).HasPrecision(19, 4);
            e.HasOne<Group>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.GroupId);
        });

        b.Entity<Receipt>(e =>
        {
            e.Property(x => x.BlobName).HasMaxLength(300).IsRequired();
            e.Property(x => x.ContentType).HasMaxLength(100);
            e.Property(x => x.Currency).HasMaxLength(3).IsFixedLength();
            e.Property(x => x.Tax).HasPrecision(19, 4);
            e.Property(x => x.Tip).HasPrecision(19, 4);
            e.Property(x => x.StatedTotal).HasPrecision(19, 4);
            e.Property(x => x.Merchant).HasMaxLength(200);
            e.Property(x => x.Note).HasMaxLength(500);
            e.Property(x => x.FailureReason).HasMaxLength(500);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.HasOne<Group>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Items).WithOne().HasForeignKey(i => i.ReceiptId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.Status);
        });

        b.Entity<ReceiptItem>(e =>
        {
            e.Property(x => x.Description).HasMaxLength(200);
            e.Property(x => x.Price).HasPrecision(19, 4);
            e.HasMany(x => x.Claims).WithOne().HasForeignKey(c => c.ReceiptItemId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ItemClaim>().HasKey(x => new { x.ReceiptItemId, x.UserId });

        b.Entity<RecurringExpense>(e =>
        {
            e.Property(x => x.Description).HasMaxLength(200).IsRequired();
            e.Property(x => x.Currency).HasMaxLength(3).IsFixedLength();
            e.Property(x => x.Amount).HasPrecision(19, 4);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.HasOne<Group>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Shares).WithOne().HasForeignKey(s => s.RecurringExpenseId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.IsActive, x.NextRunDate });
        });

        b.Entity<RecurringShare>(e =>
        {
            e.HasKey(x => new { x.RecurringExpenseId, x.UserId });
            e.Property(x => x.Value).HasPrecision(19, 4);
        });

        b.Entity<ExchangeRateEntry>(e =>
        {
            e.HasKey(x => new { x.Date, x.BaseCurrency, x.Currency });
            e.Property(x => x.BaseCurrency).HasMaxLength(3).IsFixedLength();
            e.Property(x => x.Currency).HasMaxLength(3).IsFixedLength();
            e.Property(x => x.Rate).HasPrecision(19, 8);
        });
    }
}
