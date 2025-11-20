namespace TruckScale.Pos.Config
{
    public class AppConfig
    {
        public string MainDbStrCon { get; set; } = "";
        public string LocalDbStrCon { get; set; } = "";
    }

    // Para mapear directo el JSON (con nombres exactos)
    internal class RawConfig
    {
        public string main_db_str_con { get; set; } = "";
        public string local_db_str_con { get; set; } = "";
    }
}
