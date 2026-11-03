namespace Helm.Core.Text;

/// <summary>A day of the Vietnamese lunar calendar (âm lịch); <see cref="Leap"/> for a day of a leap month.</summary>
public readonly record struct LunarDate(int Day, int Month, int Year, bool Leap = false);

/// <summary>
/// The Vietnamese lunar calendar (UTC+7), for anniversaries kept in lunar days (giỗ, Tết): Hồ Ngọc Đức's algorithm,
/// from the astronomical new moons and the sun's longitude. Good for 1900–2100.
/// </summary>
public static class LunarCalendar
{
    private const double Zone = 7;

    public static LunarDate ToLunar(DateOnly day)
    {
        var dayNumber = JulianDay(day.Day, day.Month, day.Year);
        var k = (int)Math.Floor((dayNumber - 2415021.076998695) / 29.530588853);
        var monthStart = NewMoon(k + 1);
        if (monthStart > dayNumber) monthStart = NewMoon(k);
        var a11 = Month11(day.Year);
        var b11 = a11;
        int year;
        if (a11 >= monthStart)
        {
            year = day.Year;
            a11 = Month11(day.Year - 1);
        }
        else
        {
            year = day.Year + 1;
            b11 = Month11(day.Year + 1);
        }
        var lunarDay = dayNumber - monthStart + 1;
        var diff = (monthStart - a11) / 29;
        var leap = false;
        var month = diff + 11;
        if (b11 - a11 > 365)
        {
            var leapDiff = LeapMonthOffset(a11);
            if (diff >= leapDiff)
            {
                month = diff + 10;
                leap = diff == leapDiff;
            }
        }
        if (month > 12) month -= 12;
        if (month >= 11 && diff < 4) year -= 1;
        return new LunarDate(lunarDay, month, year, leap);
    }

    /// <summary>The solar day of a lunar one; null when that month is not a leap month in that year.</summary>
    public static DateOnly? ToSolar(LunarDate lunar)
    {
        int a11, b11;
        if (lunar.Month < 11)
        {
            a11 = Month11(lunar.Year - 1);
            b11 = Month11(lunar.Year);
        }
        else
        {
            a11 = Month11(lunar.Year);
            b11 = Month11(lunar.Year + 1);
        }
        var k = (int)Math.Floor(0.5 + (a11 - 2415021.076998695) / 29.530588853);
        var off = lunar.Month - 11;
        if (off < 0) off += 12;
        if (b11 - a11 > 365)
        {
            var leapOff = LeapMonthOffset(a11);
            var leapMonth = leapOff - 2;
            if (leapMonth < 0) leapMonth += 12;
            if (lunar.Leap && lunar.Month != leapMonth) return null;
            if (lunar.Leap || off >= leapOff) off += 1;
        }
        else if (lunar.Leap)
        {
            return null;
        }
        return FromJulianDay(NewMoon(k + off) + lunar.Day - 1);
    }

    /// <summary>
    /// The first solar day on or after <paramref name="from"/> that falls on lunar <paramref name="day"/>/<paramref name="month"/>
    /// (the plain month, not a leap one). A day 30 in a month of 29 days is kept on its 29th.
    /// </summary>
    public static DateOnly Next(int day, int month, DateOnly from)
    {
        var year = ToLunar(from).Year;
        for (var y = year - 1; y <= year + 1; y++)
        {
            if (ToSolar(new LunarDate(day, month, y)) is not { } solar) continue;
            // No day 30 that month: the solar day found is already the next month's first.
            if (day == 30 && ToLunar(solar).Day != 30) solar = solar.AddDays(-1);
            if (solar >= from) return solar;
        }
        return ToSolar(new LunarDate(Math.Min(day, 29), month, year + 2)) ?? from;
    }

    private static int JulianDay(int dd, int mm, int yy)
    {
        var a = (14 - mm) / 12;
        var y = yy + 4800 - a;
        var m = mm + 12 * a - 3;
        var jd = dd + (153 * m + 2) / 5 + 365 * y + y / 4 - y / 100 + y / 400 - 32045;
        return jd < 2299161 ? dd + (153 * m + 2) / 5 + 365 * y + y / 4 - 32083 : jd;
    }

    private static DateOnly FromJulianDay(int jd)
    {
        int b, c;
        if (jd > 2299160)
        {
            var a = jd + 32044;
            b = (4 * a + 3) / 146097;
            c = a - b * 146097 / 4;
        }
        else
        {
            b = 0;
            c = jd + 32082;
        }
        var d = (4 * c + 3) / 1461;
        var e = c - 1461 * d / 4;
        var m = (5 * e + 2) / 153;
        return new DateOnly(b * 100 + d - 4800 + m / 10, m + 3 - 12 * (m / 10), e - (153 * m + 2) / 5 + 1);
    }

    /// <summary>The day number of the k-th new moon after 1900-01-01, in the zone.</summary>
    private static int NewMoon(int k)
    {
        var t = k / 1236.85;
        var t2 = t * t;
        var t3 = t2 * t;
        const double dr = Math.PI / 180;
        var jd1 = 2415020.75933 + 29.53058868 * k + 0.0001178 * t2 - 0.000000155 * t3;
        jd1 += 0.00033 * Math.Sin((166.56 + 132.87 * t - 0.009173 * t2) * dr);
        var m = 359.2242 + 29.10535608 * k - 0.0000333 * t2 - 0.00000347 * t3;
        var mpr = 306.0253 + 385.81691806 * k + 0.0107306 * t2 + 0.00001236 * t3;
        var f = 21.2964 + 390.67050646 * k - 0.0016528 * t2 - 0.00000239 * t3;
        var c1 = (0.1734 - 0.000393 * t) * Math.Sin(m * dr) + 0.0021 * Math.Sin(2 * dr * m);
        c1 = c1 - 0.4068 * Math.Sin(mpr * dr) + 0.0161 * Math.Sin(dr * 2 * mpr);
        c1 -= 0.0004 * Math.Sin(dr * 3 * mpr);
        c1 = c1 + 0.0104 * Math.Sin(dr * 2 * f) - 0.0051 * Math.Sin(dr * (m + mpr));
        c1 = c1 - 0.0074 * Math.Sin(dr * (m - mpr)) + 0.0004 * Math.Sin(dr * (2 * f + m));
        c1 = c1 - 0.0004 * Math.Sin(dr * (2 * f - m)) - 0.0006 * Math.Sin(dr * (2 * f + mpr));
        c1 = c1 + 0.0010 * Math.Sin(dr * (2 * f - mpr)) + 0.0005 * Math.Sin(dr * (2 * mpr + m));
        var deltaT = t < -11
            ? 0.001 + 0.000839 * t + 0.0002261 * t2 - 0.00000845 * t3 - 0.000000081 * t * t3
            : -0.000278 + 0.000265 * t + 0.000262 * t2;
        return (int)Math.Floor(jd1 + c1 - deltaT + 0.5 + Zone / 24);
    }

    /// <summary>The sun's longitude at the start of the day, in 30° sectors (0–11).</summary>
    private static int SunLongitude(int dayNumber)
    {
        var t = (dayNumber - 2451545.5 - Zone / 24) / 36525;
        var t2 = t * t;
        const double dr = Math.PI / 180;
        var m = 357.52910 + 35999.05030 * t - 0.0001559 * t2 - 0.00000048 * t * t2;
        var l0 = 280.46645 + 36000.76983 * t + 0.0003032 * t2;
        var dl = (1.914600 - 0.004817 * t - 0.000014 * t2) * Math.Sin(dr * m);
        dl += (0.019993 - 0.000101 * t) * Math.Sin(dr * 2 * m) + 0.000290 * Math.Sin(dr * 3 * m);
        var l = (l0 + dl) * dr;
        l -= Math.PI * 2 * Math.Floor(l / (Math.PI * 2));
        return (int)Math.Floor(l / Math.PI * 6);
    }

    /// <summary>The first day of the 11th lunar month (the one with the winter solstice) of a year.</summary>
    private static int Month11(int year)
    {
        var off = JulianDay(31, 12, year) - 2415021;
        var k = (int)Math.Floor(off / 29.530588853);
        var nm = NewMoon(k);
        return SunLongitude(nm) >= 9 ? NewMoon(k - 1) : nm;
    }

    private static int LeapMonthOffset(int a11)
    {
        var k = (int)Math.Floor((a11 - 2415021.076998695) / 29.530588853 + 0.5);
        int last;
        var i = 1;
        var arc = SunLongitude(NewMoon(k + i));
        do
        {
            last = arc;
            i++;
            arc = SunLongitude(NewMoon(k + i));
        } while (arc != last && i < 14);
        return i - 1;
    }
}
