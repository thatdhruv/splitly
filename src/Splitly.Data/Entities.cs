using Microsoft.AspNetCore.Identity;
using Splitly.Domain.Splitting;

namespace Splitly.Data;

public class AppUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = "";
}

public enum MemberRole { Member = 0, Owner = 1 }

public class Group
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Name { get; set; } = "";
    /// <summary>ISO 4217 code. Balances are always expressed in this currency.</summary>
    public string BaseCurrency { get; set; } = "USD";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<GroupMember> Members { get; set; } = [];
}

public class GroupMember
{
    public Guid GroupId { get; set; }
    public Guid UserId { get; set; }
    public MemberRole Role { get; set; }
    public DateTimeOffset JoinedAt { get; set; } = DateTimeOffset.UtcNow;
    public Group? Group { get; set; }
    public AppUser? User { get; set; }
}

public class GroupInvite
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid GroupId { get; set; }
    /// <summary>SHA-256 of the token; the raw token is only ever shown once, in the invite link.</summary>
    public string TokenHash { get; set; } = "";
    public Guid CreatedBy { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public int MaxUses { get; set; } = 1;
    public int Uses { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public class Expense
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid GroupId { get; set; }
    public string Description { get; set; } = "";
    public string Currency { get; set; } = "USD";
    public decimal Amount { get; set; }
    /// <summary>Rate locked at <see cref="Date"/>: 1 unit of <see cref="Currency"/> = Rate units of the group's base currency.</summary>
    public decimal ExchangeRate { get; set; } = 1m;
    public decimal BaseAmount { get; set; }
    public DateOnly Date { get; set; }
    public Guid PaidBy { get; set; }
    public SplitType SplitType { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Touched on every edit so the parent row always changes and the rowversion check fires, even when only shares change.</summary>
    public DateTimeOffset ModifiedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? ReceiptId { get; set; }
    public Guid? RecurringExpenseId { get; set; }
    public bool IsDeleted { get; set; }
    /// <summary>SQL Server rowversion used for optimistic concurrency.</summary>
    public byte[] RowVersion { get; set; } = [];
    public List<ExpenseShare> Shares { get; set; } = [];
}

public class ExpenseShare
{
    public Guid ExpenseId { get; set; }
    public Guid UserId { get; set; }
    /// <summary>Amount owed, in the expense's currency.</summary>
    public decimal Amount { get; set; }
    /// <summary>Amount owed, in the group's base currency.</summary>
    public decimal BaseAmount { get; set; }
}

public class Payment
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid GroupId { get; set; }
    public Guid FromUserId { get; set; }
    public Guid ToUserId { get; set; }
    /// <summary>In the group's base currency.</summary>
    public decimal Amount { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum ReceiptStatus { Uploaded = 0, Processing = 1, NeedsReview = 2, Ready = 3, Failed = 4, Finalized = 5 }

public class Receipt
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid GroupId { get; set; }
    public Guid UploadedBy { get; set; }
    public string BlobName { get; set; } = "";
    public string ContentType { get; set; } = "image/jpeg";
    public ReceiptStatus Status { get; set; }
    public string? FailureReason { get; set; }
    /// <summary>Why the parser asked for review (e.g. items don't add up to the printed total). Cleared when a member edits the receipt.</summary>
    public string? Note { get; set; }
    public string? Merchant { get; set; }
    /// <summary>The grand total printed on the receipt, if the parser found one; used to sanity-check the items.</summary>
    public decimal? StatedTotal { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? ProcessingStartedAt { get; set; }
    public Guid? ExpenseId { get; set; }
    public string Currency { get; set; } = "USD";
    public decimal Tax { get; set; }
    public decimal Tip { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public byte[] RowVersion { get; set; } = [];
    public List<ReceiptItem> Items { get; set; } = [];
}

public class ReceiptItem
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ReceiptId { get; set; }
    public string Description { get; set; } = "";
    public decimal Price { get; set; }
    /// <summary>Extraction confidence, 0..1. Items below the threshold are flagged for review.</summary>
    public double Confidence { get; set; } = 1;
    public bool NeedsReview { get; set; }
    public List<ItemClaim> Claims { get; set; } = [];
}

public class ItemClaim
{
    public Guid ReceiptItemId { get; set; }
    public Guid UserId { get; set; }
}

public enum Recurrence { Daily = 0, Weekly = 1, Monthly = 2, Yearly = 3 }

public class RecurringExpense
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid GroupId { get; set; }
    public string Description { get; set; } = "";
    public string Currency { get; set; } = "USD";
    public decimal Amount { get; set; }
    public Guid PaidBy { get; set; }
    public SplitType SplitType { get; set; }
    public Guid CreatedBy { get; set; }
    public Recurrence Recurrence { get; set; }
    public DateOnly NextRunDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public bool IsActive { get; set; } = true;
    public List<RecurringShare> Shares { get; set; } = [];
}

public class RecurringShare
{
    public Guid RecurringExpenseId { get; set; }
    public Guid UserId { get; set; }
    /// <summary>Exact amount or percentage, depending on the split type; null for equal.</summary>
    public decimal? Value { get; set; }
}

/// <summary>Cached daily FX rate: 1 unit of <see cref="Currency"/> in <see cref="BaseCurrency"/> on <see cref="Date"/>.</summary>
public class ExchangeRateEntry
{
    public DateOnly Date { get; set; }
    public string BaseCurrency { get; set; } = "";
    public string Currency { get; set; } = "";
    public decimal Rate { get; set; }
}
