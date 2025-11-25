namespace TruckScale.Pos
{
    public static class PosSession
    {
        public static int UserId { get; set; }
        public static string Username { get; set; } = "";
        public static string FullName { get; set; } = "";
        public static string RoleCode { get; set; } = "";

        public static bool IsLoggedIn => UserId > 0;
    }
}
