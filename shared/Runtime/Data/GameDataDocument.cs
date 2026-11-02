using System;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace ElementalLegion.Shared.Data
{
    public sealed class GameDataDocument
    {
        public JObject Root { get; }
        public string Sha256 { get; }

        public GameDataDocument(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new ArgumentException("Game data must be a JSON object.", nameof(json));

            string normalized = json.Replace("\r\n", "\n").Replace("\r", "\n");
            Root = JObject.Parse(normalized);
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(normalized));
                Sha256 = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
