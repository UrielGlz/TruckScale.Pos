public class TransportAccount
{
    public int IdCustomer { get; set; }
    public string AccountNumber { get; set; } = "";
    public string AccountName { get; set; } = "";
    public string AccountAddress { get; set; } = "";
    public string AccountCountry { get; set; } = "";
    public string AccountState { get; set; } = "";

    public string Display => $"{AccountNumber} — {AccountName}";
}
