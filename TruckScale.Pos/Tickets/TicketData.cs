using System;
using System.Windows.Media;
using System.Globalization;
namespace TruckScale.Pos.Tickets
{
    public sealed class TicketData
    {
        // Header
        public string CompanyName { get; set; } =
            "McAllen Foreign Trade Zone Certified Public Truck Scale";
        public string CompanyAddress { get; set; } =
            "6401 S. 33rd Street · McAllen, Texas 78503";
        public string CompanyPhone { get; set; } =
            "(956) 682-4306 · (956) 882-9111 Fax";
        public string CertificateNumber { get; set; } =
            "Certificate No. 036231";
        public string WebSite { get; set; } =
            "Website: http://www.mftz.org    Email: fizinfo@mftz.org";
               
        public string ReweighWarning { get; set; } =
            "ALL RE-WEIGHS MUST BE DONE WITHIN 12 HOURS OF ORIGINAL WEIGH TIME.";

        public string TicketNumber { get; set; } = "";
        public string Transaction { get; set; } = "";
        public DateTime Date { get; set; }

        public string Weigher { get; set; } = "";

        public string DriverName { get; set; } = "";
        public string DriverLicense { get; set; } = "";
        public string Plates { get; set; } = "";
        public string TractorNumber { get; set; } = "";
        public string TrailerNumber { get; set; } = "";

        public string Product { get; set; } = "";
        public decimal WeighFee { get; set; }

        public double Scale1 { get; set; }
        public double Scale2 { get; set; }
        public double Scale3 { get; set; }
        public double TotalGross { get; set; }

        public bool IsReweigh { get; set; } = false;

        public string TicketUid { get; set; } = "";
        public string QrPayload { get; set; } = "";
        //texto que se imprime en "Reweigh Form / Date & Time"
        public string ReweighDateTimeText
        {
            get
            {
                if (!IsReweigh) return string.Empty;   // si NO es reweigh, no imprimimos nada
                // Formato US: mm/dd/yyyy hh:mm AM/PM
                return Date.ToString("MM/dd/yyyy h:mm tt", CultureInfo.InvariantCulture);
            }
        }
    }
}
