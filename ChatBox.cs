using System;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace ShoutRunner;

public static unsafe class ChatBox
{
    private const int MaxBytes = 500;

    public static bool Send(string message)
    {
        if (string.IsNullOrEmpty(message)) return false;

        string sanitised;
        try { sanitised = Plugin.PluginInterface.Sanitizer.Sanitize(message); }
        catch { sanitised = message; }
        if (string.IsNullOrEmpty(sanitised)) return false;

        var bytes = Encoding.UTF8.GetByteCount(sanitised);
        if (bytes > MaxBytes)
        {
            Plugin.Log.Warning($"Chat message too long ({bytes} bytes, max {MaxBytes}), dropped.");
            return false;
        }

        var module = UIModule.Instance();
        if (module == null)
        {
            Plugin.Log.Warning("UIModule not available; chat send skipped.");
            return false;
        }

        Utf8String* utf = null;
        try
        {
            utf = Utf8String.FromString(sanitised);
            module->ProcessChatBoxEntry(utf, IntPtr.Zero, false);
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Chat send failed.");
            return false;
        }
        finally
        {
            if (utf != null) utf->Dtor(true);
        }
    }
}
