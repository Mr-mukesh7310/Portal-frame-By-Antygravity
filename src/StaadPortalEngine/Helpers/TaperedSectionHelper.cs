using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace StaadPortalEngine.Helpers
{
    public static class TaperedSectionHelper
    {
        public static bool IsTapered(string? property)
        {
            if (string.IsNullOrWhiteSpace(property)) return false;
            return property.ToUpperInvariant().Contains("TAPERED");
        }

        public static string InterpolateTaperedProperty(string? prop, double tStart, double tEnd)
        {
            if (string.IsNullOrWhiteSpace(prop)) return prop ?? string.Empty;
            if (!IsTapered(prop)) return prop;

            // If start and end represent the full member (0.0 to 1.0), return original
            if (Math.Abs(tStart - 0.0) < 1e-5 && Math.Abs(tEnd - 1.0) < 1e-5) return prop;

            var rawTokens = prop.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
            int taperedIdx = -1;
            for (int i = 0; i < rawTokens.Length; i++)
            {
                if (rawTokens[i].Equals("TAPERED", StringComparison.OrdinalIgnoreCase))
                {
                    taperedIdx = i;
                    break;
                }
            }

            if (taperedIdx == -1) return prop;

            string prefix = string.Join(" ", rawTokens.Take(taperedIdx + 1));

            var paramTokens = rawTokens.Skip(taperedIdx + 1).ToList();
            var values = new List<double>();

            for (int i = 0; i < paramTokens.Count; i++)
            {
                if (double.TryParse(paramTokens[i], NumberStyles.Any, CultureInfo.InvariantCulture, out double v))
                {
                    values.Add(v);
                }
                else
                {
                    break;
                }
            }

            if (values.Count < 5) return prop;

            double dStartOrig = values[0];
            double tw = values[1];
            double bfTop = values[2];
            double tfTop = values[3];
            double dEndOrig = values[4];

            string f2Str = paramTokens.Count > 1 ? paramTokens[1] : tw.ToString("0.00##", CultureInfo.InvariantCulture);
            string f3Str = paramTokens.Count > 2 ? paramTokens[2] : bfTop.ToString("0.00##", CultureInfo.InvariantCulture);
            string f4Str = paramTokens.Count > 3 ? paramTokens[3] : tfTop.ToString("0.00##", CultureInfo.InvariantCulture);

            double dStartNew = Math.Round(dStartOrig + tStart * (dEndOrig - dStartOrig), 4);
            double dEndNew = Math.Round(dStartOrig + tEnd * (dEndOrig - dStartOrig), 4);

            string dStartStr = dStartNew.ToString("0.00##", CultureInfo.InvariantCulture);
            string dEndStr = dEndNew.ToString("0.00##", CultureInfo.InvariantCulture);

            if (values.Count >= 7)
            {
                double bfBot = values[5];
                double tfBot = values[6];
                string f6Str = paramTokens.Count > 5 ? paramTokens[5] : bfBot.ToString("0.00##", CultureInfo.InvariantCulture);
                string f7Str = paramTokens.Count > 6 ? paramTokens[6] : tfBot.ToString("0.00##", CultureInfo.InvariantCulture);

                return $"{prefix} {dStartStr} {f2Str} {f3Str} {f4Str} {dEndStr} {f6Str} {f7Str}";
            }
            else
            {
                return $"{prefix} {dStartStr} {f2Str} {f3Str} {f4Str} {dEndStr}";
            }
        }
    }
}
