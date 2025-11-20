using System;
using System.Security.Cryptography;
using System.Text;

namespace TruckScale.Pos.Security
{
    public static class CryptoHelper
    {
        private const string Prefix = "dpapi:";

        public static string Protect(string plainText)
        {
            if (string.IsNullOrEmpty(plainText))
                return string.Empty;

            var bytes = Encoding.UTF8.GetBytes(plainText);
            var protectedBytes = ProtectedData.Protect(
                bytes,
                null,
                DataProtectionScope.CurrentUser); // o LocalMachine si quieres compartir entre usuarios

            return Prefix + Convert.ToBase64String(protectedBytes);
        }

        public static string Unprotect(string stored)
        {
            if (string.IsNullOrWhiteSpace(stored))
                return string.Empty;

            if (!stored.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
                return stored; // por si hay texto plano viejo

            var b64 = stored.Substring(Prefix.Length);
            var bytes = Convert.FromBase64String(b64);
            var unprotected = ProtectedData.Unprotect(
                bytes,
                null,
                DataProtectionScope.CurrentUser);

            return Encoding.UTF8.GetString(unprotected);
        }
    }
}
