using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Helm.Keyboard.Tip.Interop;
using Microsoft.Win32;

namespace Helm.Keyboard.Tip;

/// <summary>
/// The V/E sign next to ENG in the taskbar (the input mode button, like 中/英 for Chinese keyboards): V while typing
/// Vietnamese, E while typing English. A click flips the switch.
/// </summary>
[GeneratedComClass]
internal sealed unsafe partial class ModeButton(Action flip) : ITfLangBarItem, ITfLangBarItemButton, ITfSource
{
    private const string Description = "Helm Keyboard";
    private const int IconSize = 32;

    private bool _vietnamese;
    private ITfLangBarItemSink? _sink;

    /// <summary>Shows the switch as it is now.</summary>
    public void Show(bool vietnamese)
    {
        _vietnamese = vietnamese;
        _sink?.OnUpdate(TsfIds.TF_LBI_BTNALL);
    }

    public int GetInfo(TfLangBarItemInfo* info)
    {
        info->Service = Exports.Clsid;
        info->Item = TsfIds.InputModeItem;
        info->Style = TsfIds.TF_LBI_STYLE_BTN_BUTTON | TsfIds.TF_LBI_STYLE_SHOWNINTRAY;
        info->Sort = 0;
        Description.AsSpan().CopyTo(new Span<char>(info->Description, 31));
        info->Description[Description.Length] = '\0';
        return TsfIds.S_OK;
    }

    public int GetStatus(out uint status)
    {
        status = 0;
        return TsfIds.S_OK;
    }

    public int Show(int show) => TsfIds.S_OK;

    public int GetTooltipString(out nint tooltip)
    {
        tooltip = Marshal.StringToBSTR(_vietnamese ? "Tiếng Việt (Ctrl+Shift)" : "English (Ctrl+Shift)");
        return TsfIds.S_OK;
    }

    public int OnClick(int click, TfPoint point, nint area)
    {
        try
        {
            flip();
        }
        catch (Exception ex)
        {
            Log.Error("V/E button", ex);
        }
        return TsfIds.S_OK;
    }

    public int InitMenu(nint menu) => TsfIds.S_OK;

    public int OnMenuSelect(uint id) => TsfIds.S_OK;

    /// <summary>The letter, drawn in the taskbar's text color; whoever asks owns the icon.</summary>
    public int GetIcon(out nint icon)
    {
        try
        {
            icon = DrawIcon(_vietnamese ? "V" : "E", TaskbarIsLight() ? 0x000000u : 0xFFFFFFu);
            return icon == 0 ? TsfIds.E_FAIL : TsfIds.S_OK;
        }
        catch (Exception ex)
        {
            Log.Error("V/E icon", ex);
            icon = 0;
            return TsfIds.E_FAIL;
        }
    }

    public int GetText(out nint text)
    {
        text = Marshal.StringToBSTR(_vietnamese ? "V" : "E");
        return TsfIds.S_OK;
    }

    // ITfSource: the taskbar listens here for the switch to change.

    public int AdviseSink(in Guid riid, nint sink, out uint cookie)
    {
        cookie = TsfIds.TF_INVALID_COOKIE;
        if (riid != TsfIds.LangBarItemSink) return TsfIds.CONNECT_E_CANNOTCONNECT;
        if (_sink is not null) return TsfIds.CONNECT_E_ADVISELIMIT;
        _sink = ComInterfaceMarshaller<ITfLangBarItemSink>.ConvertToManaged((void*)sink);
        cookie = 1;
        return TsfIds.S_OK;
    }

    public int UnadviseSink(uint cookie)
    {
        if (cookie != 1 || _sink is null) return TsfIds.CONNECT_E_CANNOTCONNECT;
        _sink = null;
        return TsfIds.S_OK;
    }

    private static bool TaskbarIsLight() =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0)
            is int light && light != 0;

    /// <summary>
    /// Draws <paramref name="letter"/> white on black, then turns that into the icon's see-through-ness: the letter
    /// in <paramref name="color"/> (0xRRGGBB) on nothing.
    /// </summary>
    private static nint DrawIcon(string letter, uint color)
    {
        var header = new Native.BitmapInfoHeader
        {
            Size = (uint)sizeof(Native.BitmapInfoHeader), Width = IconSize, Height = -IconSize, Planes = 1, BitCount = 32,
        };
        var dc = Native.CreateCompatibleDC(0);
        var bitmap = Native.CreateDIBSection(dc, &header, 0, out var bits, 0, 0);
        var mask = Native.CreateBitmap(IconSize, IconSize, 1, 1, null);
        var font = Native.CreateFont(-IconSize + 4, 0, 0, 0, 700, 0, 0, 0, 1, 0, 0, Native.ANTIALIASED_QUALITY, 0, "Segoe UI");
        try
        {
            if (dc == 0 || bitmap == 0 || bits == 0 || mask == 0) return 0;
            var oldBitmap = Native.SelectObject(dc, bitmap);
            var oldFont = Native.SelectObject(dc, font);
            Native.SetTextColor(dc, 0xFFFFFF);
            Native.SetBkMode(dc, Native.TRANSPARENT);
            var rect = new Native.Rect { Right = IconSize, Bottom = IconSize };
            fixed (char* text = letter)
                Native.DrawText(dc, text, letter.Length, &rect, Native.DT_CENTER | Native.DT_VCENTER | Native.DT_SINGLELINE);
            Native.GdiFlush();
            Native.SelectObject(dc, oldFont);
            Native.SelectObject(dc, oldBitmap);

            // Pixels are 0xAARRGGBB; the gray the text left is how much of the letter covers the pixel.
            var pixels = new Span<uint>((void*)bits, IconSize * IconSize);
            foreach (ref var pixel in pixels)
            {
                var cover = Math.Max(pixel & 0xFF, Math.Max((pixel >> 8) & 0xFF, (pixel >> 16) & 0xFF));
                pixel = (cover << 24) | (color & 0xFFFFFF);
            }
            var info = new Native.IconInfo { IsIcon = 1, Mask = mask, Color = bitmap };
            return Native.CreateIconIndirect(&info);
        }
        finally
        {
            if (font != 0) Native.DeleteObject(font);
            if (mask != 0) Native.DeleteObject(mask);
            if (bitmap != 0) Native.DeleteObject(bitmap);
            if (dc != 0) Native.DeleteDC(dc);
        }
    }
}
