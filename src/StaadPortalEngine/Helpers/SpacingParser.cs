using System;
using System.Collections.Generic;

namespace StaadPortalEngine.Helpers
{
    public static class SpacingParser
    {
        /// <summary>
        /// Parses bay spacing expressions like "5@6+5@4", "5@6, 5@4", "6.0, 7.5, 7.5, 6.0", "4*6", or "0" for single mid frame.
        /// </summary>
        public static List<double> ParseBaySpacings(string input)
        {
            var result = new List<double>();
            if (string.IsNullOrWhiteSpace(input)) return new List<double> { 6.0, 6.0, 6.0, 6.0 };

            var trimmed = input.Trim();
            if (trimmed == "0" || trimmed == "0.0" || (double.TryParse(trimmed, out double singleZero) && singleZero == 0.0 && !trimmed.Contains('@') && !trimmed.Contains('*')))
            {
                return new List<double>();
            }

            var tokens = input.Split(new[] { '+', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var rawToken in tokens)
            {
                var t = rawToken.Trim();
                if (t.Contains('@') || t.Contains('*') || t.Contains('x') || t.Contains('X'))
                {
                    char sep = t.Contains('@') ? '@' : (t.Contains('*') ? '*' : (t.Contains('x') ? 'x' : 'X'));
                    var parts = t.Split(sep);
                    if (parts.Length == 2 && int.TryParse(parts[0].Trim(), out int count) && double.TryParse(parts[1].Trim(), out double spacing))
                    {
                        for (int i = 0; i < count; i++)
                        {
                            if (spacing > 0) result.Add(spacing);
                        }
                    }
                }
                else if (double.TryParse(t, out double val) && val > 0)
                {
                    result.Add(val);
                }
            }

            if (result.Count == 0 && (trimmed == "0" || trimmed == "0.0" || (double.TryParse(trimmed, out double zeroVal) && zeroVal == 0.0)))
            {
                return new List<double>();
            }

            return result.Count > 0 ? result : new List<double> { 6.0, 6.0, 6.0, 6.0 };
        }

        /// <summary>
        /// Parses gable wind post definition which can be a count (e.g. "3") or a spacing expression (e.g. "1@5+2@4+1@5" or "4@6").
        /// </summary>
        public static List<double> ParseGablePostOffsets(string input, double totalWidth)
        {
            var offsets = new List<double>();
            if (string.IsNullOrWhiteSpace(input)) return offsets;

            var t = input.Trim();

            // Case 1: Simple integer count e.g. "3"
            if (int.TryParse(t, out int postCount) && !t.Contains('@') && !t.Contains('*') && !t.Contains('+') && !t.Contains(','))
            {
                if (postCount <= 0) return offsets;
                double spacing = totalWidth / (postCount + 1);
                for (int i = 1; i <= postCount; i++)
                {
                    offsets.Add(Math.Round(i * spacing, 4));
                }
                return offsets;
            }

            // Case 2: Spacing expression e.g. "1@5+2@4+1@5" or "4@6"
            var bayList = ParseBaySpacings(input);
            double currentX = 0.0;
            for (int i = 0; i < bayList.Count; i++)
            {
                currentX += bayList[i];
                if (currentX < totalWidth - 0.01)
                {
                    offsets.Add(Math.Round(currentX, 4));
                }
            }

            return offsets;
        }

        /// <summary>
        /// Parses a list of 0-indexed bay numbers e.g. "0, 1, 2", "1-3", "0; 2; 4".
        /// </summary>
        public static List<int> ParseBayIndices(string input)
        {
            var indices = new List<int>();
            if (string.IsNullOrWhiteSpace(input)) return indices;

            var tokens = input.Split(new[] { ',', ';', ' ', '+' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var token in tokens)
            {
                var t = token.Trim();
                if (t.Contains('-') && !t.StartsWith("-"))
                {
                    var range = t.Split('-');
                    if (range.Length == 2 && int.TryParse(range[0].Trim(), out int start) && int.TryParse(range[1].Trim(), out int end))
                    {
                        for (int i = Math.Min(start, end); i <= Math.Max(start, end); i++)
                        {
                            if (!indices.Contains(i)) indices.Add(i);
                        }
                        continue;
                    }
                }

                if (int.TryParse(t, out int val) && val >= 0)
                {
                    if (!indices.Contains(val)) indices.Add(val);
                }
            }
            indices.Sort();
            return indices;
        }
    }
}
