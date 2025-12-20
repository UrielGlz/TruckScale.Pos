public sealed class TransportAccount
{
    // Customers
    public int IdCustomer { get; set; }
    public string? AccountNumber { get; set; }
    public string? AccountName { get; set; }
    public string? AccountAddress { get; set; }
    public string? AccountCountry { get; set; }
    public string? AccountState { get; set; }
    public bool HasCredit { get; set; }          // customers.has_credit

    // Customer credit
    public string? CreditType { get; set; }      // POSTPAID / PREPAID (string para no acoplar enum)
    public decimal CreditLimit { get; set; }
    public decimal CurrentBalance { get; set; }
    public decimal AvailableCredit { get; set; }
    public DateTime? ExpiryDate { get; set; }
    public bool IsSuspended { get; set; }
    public string? SuspensionReason { get; set; }

    public DateTime? LastPaymentDate { get; set; }
    public int PaymentTermsDays { get; set; }

    // UI
    public string Display
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(AccountName) ? "(No name)" : AccountName.Trim();
            var num = string.IsNullOrWhiteSpace(AccountNumber) ? "" : $" • {AccountNumber.Trim()}";
            return $"{name}{num}";
        }
    }
}
