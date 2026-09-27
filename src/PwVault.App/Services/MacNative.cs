using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PwVault.App.Services;

/// <summary>Objective-C ランタイムの最小限の呼び出し（macOS の API を .NET から直接使うため）。</summary>
[SupportedOSPlatform("macos")]
internal static partial class ObjC
{
    private const string Lib = "/usr/lib/libobjc.A.dylib";

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr objc_getClass(string name);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr sel_registerName(string name);

    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    public static partial IntPtr Send(IntPtr receiver, IntPtr selector);

    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    public static partial IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr arg1);

    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    public static partial IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr arg1, IntPtr arg2);

    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    public static partial long SendLong(IntPtr receiver, IntPtr selector);

    [LibraryImport(Lib)]
    public static partial IntPtr objc_autoreleasePoolPush();

    [LibraryImport(Lib)]
    public static partial void objc_autoreleasePoolPop(IntPtr pool);

    /// <summary>.NET の文字列から NSString（自動解放）を作る。</summary>
    public static unsafe IntPtr NSString(string value)
    {
        fixed (char* chars = value)
            return Send(objc_getClass("NSString"), sel_registerName("stringWithCharacters:length:"), (IntPtr)chars, (IntPtr)value.Length);
    }
}

/// <summary>
/// macOS のクリップボード（NSPasteboard）。
/// コピーするときに「機密」の印（nspasteboard.org の取り決め: org.nspasteboard.ConcealedType）を付け、
/// 対応したクリップボード履歴アプリに記録されないようにする。
/// </summary>
[SupportedOSPlatform("macos")]
internal static class MacPasteboard
{
    private const string TextType = "public.utf8-plain-text";
    private const string ConcealedType = "org.nspasteboard.ConcealedType";

    private static IntPtr General => ObjC.Send(ObjC.objc_getClass("NSPasteboard"), ObjC.sel_registerName("generalPasteboard"));

    public static long ChangeCount
    {
        get
        {
            var pool = ObjC.objc_autoreleasePoolPush();
            try { return ObjC.SendLong(General, ObjC.sel_registerName("changeCount")); }
            finally { ObjC.objc_autoreleasePoolPop(pool); }
        }
    }

    public static void SetConcealedText(string text)
    {
        var pool = ObjC.objc_autoreleasePoolPush();
        try
        {
            var pb = General;
            ObjC.SendLong(pb, ObjC.sel_registerName("clearContents"));
            var setString = ObjC.sel_registerName("setString:forType:");
            ObjC.Send(pb, setString, ObjC.NSString(text), ObjC.NSString(TextType));
            ObjC.Send(pb, setString, ObjC.NSString(""), ObjC.NSString(ConcealedType));
        }
        finally { ObjC.objc_autoreleasePoolPop(pool); }
    }

    public static void Clear()
    {
        var pool = ObjC.objc_autoreleasePoolPush();
        try { ObjC.SendLong(General, ObjC.sel_registerName("clearContents")); }
        finally { ObjC.objc_autoreleasePoolPop(pool); }
    }

    /// <summary>テスト用: 今のクリップボードの文字列。</summary>
    public static string? GetText()
    {
        var pool = ObjC.objc_autoreleasePoolPush();
        try
        {
            var ns = ObjC.Send(General, ObjC.sel_registerName("stringForType:"), ObjC.NSString(TextType));
            if (ns == IntPtr.Zero) return null;
            var utf8 = ObjC.Send(ns, ObjC.sel_registerName("UTF8String"));
            return Marshal.PtrToStringUTF8(utf8);
        }
        finally { ObjC.objc_autoreleasePoolPop(pool); }
    }

    public static bool HasConcealedMarker()
    {
        var pool = ObjC.objc_autoreleasePoolPush();
        try
        {
            var types = ObjC.Send(General, ObjC.sel_registerName("types"));
            var contains = ObjC.Send(types, ObjC.sel_registerName("containsObject:"), ObjC.NSString(ConcealedType));
            return (contains & 0xFF) != 0;
        }
        finally { ObjC.objc_autoreleasePoolPop(pool); }
    }
}

/// <summary>macOS の画面ロック状態（CGSessionCopyCurrentDictionary の CGSSessionScreenIsLocked）。</summary>
[SupportedOSPlatform("macos")]
internal static partial class MacSession
{
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [LibraryImport(CoreGraphics)]
    private static partial IntPtr CGSessionCopyCurrentDictionary();

    [LibraryImport(CoreFoundation)]
    private static partial IntPtr CFDictionaryGetValue(IntPtr dict, IntPtr key);

    [LibraryImport(CoreFoundation)]
    private static partial IntPtr CFStringCreateWithCString(IntPtr allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string cStr, uint encoding);

    [LibraryImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool CFBooleanGetValue(IntPtr boolean);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(IntPtr obj);

    private const uint kCFStringEncodingUTF8 = 0x08000100;

    public static bool IsScreenLocked()
    {
        var dict = CGSessionCopyCurrentDictionary();
        if (dict == IntPtr.Zero) return false;
        var key = CFStringCreateWithCString(IntPtr.Zero, "CGSSessionScreenIsLocked", kCFStringEncodingUTF8);
        try
        {
            var value = CFDictionaryGetValue(dict, key);
            return value != IntPtr.Zero && CFBooleanGetValue(value);
        }
        finally
        {
            CFRelease(key);
            CFRelease(dict);
        }
    }
}
