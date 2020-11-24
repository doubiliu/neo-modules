using System;
using System.Collections.Generic;
using System.Text;

namespace Neo.Plugins.util
{
    public static class Helper
    {
        public static string ParseToString(this IDictionary<string, string> parameters)
        {
            IEnumerator<KeyValuePair<string, string>> dem = parameters.GetEnumerator();
            StringBuilder query = new StringBuilder("");
            while (dem.MoveNext())
            {
                string key = dem.Current.Key;
                string value = dem.Current.Value;
                if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(value))
                {
                    query.Append(key).Append("=").Append(value).Append("&");
                }
            }
            string content = query.ToString().Substring(0, query.Length - 1);
            return content;
        }
    }
}
