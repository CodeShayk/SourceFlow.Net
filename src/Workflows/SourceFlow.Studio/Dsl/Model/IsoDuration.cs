using System;
using System.Globalization;
using System.Text;

namespace SourceFlow.Studio.Dsl.Model
{
    /// <summary>
    /// ISO-8601 durations without years and months (their length varies): <c>P[nW][nD][T[nH][nM][n[.n]S]]</c>, for
    /// example <c>PT30S</c>, <c>PT5M</c>, <c>P1D</c>, <c>P1DT12H</c>.
    /// </summary>
    public static class IsoDuration
    {
        /// <summary>Parses <paramref name="text"/>; false when it is not a supported duration.</summary>
        public static bool TryParse(string? text, out TimeSpan duration)
        {
            duration = TimeSpan.Zero;
            if (string.IsNullOrEmpty(text) || text![0] != 'P' || text.Length < 3)
            {
                return false;
            }

            decimal seconds = 0;
            bool time = false, any = false;
            char last = ' ';
            int i = 1;
            while (i < text.Length)
            {
                if (text[i] == 'T')
                {
                    if (time)
                    {
                        return false;
                    }

                    time = true;
                    last = 'T';
                    i++;
                    continue;
                }

                int start = i;
                while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.'))
                {
                    i++;
                }

                if (i == start || i >= text.Length
                    || !decimal.TryParse(text.Substring(start, i - start), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal value))
                {
                    return false;
                }

                char unit = text[i++];
                bool fraction = text.IndexOf('.', start, i - 1 - start) >= 0;
                if (fraction && !(time && unit == 'S'))
                {
                    return false;   // only seconds may have a fraction
                }

                decimal factor;
                if (!time && unit == 'W' && last == ' ')
                {
                    factor = 7 * 86400;
                }
                else if (!time && unit == 'D' && (last == ' ' || last == 'W'))
                {
                    factor = 86400;
                }
                else if (time && unit == 'H' && last == 'T')
                {
                    factor = 3600;
                }
                else if (time && unit == 'M' && (last == 'T' || last == 'H'))
                {
                    factor = 60;
                }
                else if (time && unit == 'S' && (last == 'T' || last == 'H' || last == 'M'))
                {
                    factor = 1;
                }
                else
                {
                    return false;
                }

                last = unit;
                any = true;
                seconds += value * factor;
                if (seconds > (decimal)TimeSpan.MaxValue.TotalSeconds / 2)
                {
                    return false;
                }
            }

            if (!any || last == 'T')
            {
                return false;
            }

            duration = TimeSpan.FromTicks((long)(seconds * TimeSpan.TicksPerSecond));
            return true;
        }

        /// <summary>Formats <paramref name="duration"/> in the shortest ISO-8601 form, for example <c>P1DT2H</c>.</summary>
        public static string Format(TimeSpan duration)
        {
            if (duration < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(duration), "Durations are not negative.");
            }

            var builder = new StringBuilder("P");
            if (duration.Days > 0)
            {
                builder.Append(duration.Days.ToString(CultureInfo.InvariantCulture)).Append('D');
            }

            TimeSpan rest = duration - TimeSpan.FromDays(duration.Days);
            if (rest > TimeSpan.Zero || duration == TimeSpan.Zero)
            {
                builder.Append('T');
                if (rest.Hours > 0)
                {
                    builder.Append(rest.Hours.ToString(CultureInfo.InvariantCulture)).Append('H');
                }

                if (rest.Minutes > 0)
                {
                    builder.Append(rest.Minutes.ToString(CultureInfo.InvariantCulture)).Append('M');
                }

                decimal seconds = rest.Seconds + (rest.Ticks % TimeSpan.TicksPerSecond) / (decimal)TimeSpan.TicksPerSecond;
                if (seconds > 0 || duration == TimeSpan.Zero)
                {
                    builder.Append(seconds.ToString("0.#######", CultureInfo.InvariantCulture)).Append('S');
                }
            }

            return builder.ToString();
        }
    }
}
