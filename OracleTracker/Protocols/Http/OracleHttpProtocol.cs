using Neo.Ledger;
using Neo.Network.P2P.Payloads;
using Neo.Persistence;
using Neo.SmartContract.Native;
using Neo.SmartContract.Native.Tokens;
using OracleTracker;
using OracleTracker.Protocols;
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using JArray = Newtonsoft.Json.Linq.JArray;
using JObject = Newtonsoft.Json.Linq.JObject;

namespace Neo.Oracle.Protocols.Https
{
    internal class OracleHttpProtocol : IOracleProtocol
    {
        public int Timeout => 5000;
        public bool AllowPrivateHost { get; internal set; } = false;

        public readonly string[] AllowedFormats = new string[] { "application/json" };

        public OracleResponseAttribute Process(OracleRequest request)
        {
            Log($"Downloading HTTPS request: url={request.Url}", LogLevel.Debug);
            Uri.TryCreate(request.Url, UriKind.Absolute, out var uri);
            if (!AllowPrivateHost && IsInternal(Dns.GetHostEntry(uri.Host)))
            {
                // Don't allow private host in order to prevent SSRF
                LogError(uri, "PolicyError");
                return OraclePreHandler.CreateError(request.RequestTxHash);
            }

            using var handler = new HttpClientHandler
            {
                // TODO: Accept all certificates
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };
            using var client = new HttpClient(handler);

            client.DefaultRequestHeaders.Add("Accept", string.Join(",", AllowedFormats));

            Task<HttpResponseMessage> result = client.GetAsync(uri);

            if (!result.Wait(Timeout))
            {
                // Timeout
                LogError(uri, "Timeout");
                return OraclePreHandler.CreateError(request.RequestTxHash);
            }

            if (!result.Result.IsSuccessStatusCode)
            {
                // Error with response
                LogError(uri, "ResponseError");
                return OraclePreHandler.CreateError(request.RequestTxHash);
            }

            if (!AllowedFormats.Contains(result.Result.Content.Headers.ContentType.MediaType))
            {
                // Error with the ContentType
                LogError(uri, "ContentType it's not allowed");
                return OraclePreHandler.CreateError(request.RequestTxHash);
            }

            string ret;
            var taskRet = result.Result.Content.ReadAsStringAsync();

            if (!taskRet.Wait(Timeout))
            {
                // Timeout
                LogError(uri, "Timeout");
                return OraclePreHandler.CreateError(request.RequestTxHash);
            }
            else
            {
                // Good response
                ret = taskRet.Result;
            }
            // Filter
            using var snapshot = Blockchain.Singleton.GetSnapshot();
            if (!Filter(snapshot, ret, request.FilterPath, out var output, out long FilterFee))
            {
                LogError(uri, "FilterError");
                return OraclePreHandler.CreateError(request.RequestTxHash);
            }

            return OraclePreHandler.CreateResult(request.RequestTxHash, Encoding.UTF8.GetBytes(output), FilterFee);
        }

        private bool Filter(StoreView snapshot, string input, string filterArgs, out string result, out long gasCost)
        {
            if (filterArgs is null)
            {
                result = input;
                gasCost = 0;
            }
            try
            {
                JObject beforeObject = JObject.Parse(input);
                JArray afterObjects = new JArray(beforeObject.SelectTokens(filterArgs).ToArray());
                result = afterObjects.ToString();
                gasCost = (input.Length - result.Length) * NativeContract.Policy.GetFeePerByte(snapshot);
            }
            catch
            {
                result = null;
                gasCost = 0;
                return false;
            }
            return true;
        }

        private static void LogError(Uri url, string error)
        {
            Log($"{error} at {url.ToString()}", LogLevel.Error);
        }

        private static void Log(string line, LogLevel level)
        {
            Utility.Log(nameof(OracleHttpProtocol), level, line);
        }

        internal static bool IsInternal(IPHostEntry entry)
        {
            foreach (var ip in entry.AddressList)
            {
                if (IsInternal(ip)) return true;
            }
            return false;
        }

        /// <summary>
        ///       ::1          -   IPv6  loopback
        ///       10.0.0.0     -   10.255.255.255  (10/8 prefix)
        ///       127.0.0.0    -   127.255.255.255  (127/8 prefix)
        ///       172.16.0.0   -   172.31.255.255  (172.16/12 prefix)
        ///       192.168.0.0  -   192.168.255.255 (192.168/16 prefix)
        /// </summary>
        /// <param name="ipAddress">Address</param>
        /// <returns>True if it was an internal address</returns>
        internal static bool IsInternal(IPAddress ipAddress)
        {
            if (IPAddress.IsLoopback(ipAddress)) return true;
            if (IPAddress.Broadcast.Equals(ipAddress)) return true;
            if (IPAddress.Any.Equals(ipAddress)) return true;
            if (IPAddress.IPv6Any.Equals(ipAddress)) return true;
            if (IPAddress.IPv6Loopback.Equals(ipAddress)) return true;

            var ip = ipAddress.GetAddressBytes();
            switch (ip[0])
            {
                case 10:
                case 127: return true;
                case 172: return ip[1] >= 16 && ip[1] < 32;
                case 192: return ip[1] == 168;
                default: return false;
            }
        }
    }
}
